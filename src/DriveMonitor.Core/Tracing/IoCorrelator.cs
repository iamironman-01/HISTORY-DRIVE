namespace DriveMonitor.Core.Tracing;

/// <summary>
/// Matches FileIo initiation events with their FileIo_OpEnd completions and FileIo_PathOperation
/// paths (same request address), and releases operations in initiation order once they are
/// complete or have waited too long.
/// </summary>
/// <remarks>
/// <para>
/// A real-time ETW session delivers per-CPU buffers independently, so a completion logged on
/// one CPU can arrive before the initiation logged on another. Request addresses are also
/// recycled within microseconds, so a completion can belong to an older or newer use of the same
/// address. The rule that resolves both: a completion C belongs to initiation B on the same
/// address only if C was logged at or after B, and among the candidates the closest one wins.
/// Completions and paths that match nothing yet are kept for a short while in case their
/// initiation is still in flight; that store also holds completions of operations on
/// unmonitored drives, which simply expire.
/// </para>
/// <para>Not thread-safe; the caller serialises access.</para>
/// </remarks>
internal sealed class IoCorrelator
{
    private readonly Dictionary<ulong, PendingIo> _pendingByIrp = new();
    private readonly Dictionary<ulong, PendingIo> _awaitingPathByIrp = new();
    private readonly Queue<PendingIo> _order = new();
    private readonly EarlyStore<(ulong Info, int Status)> _earlyCompletions;
    private readonly EarlyStore<string> _earlyPaths;
    private readonly int _holdMs;
    private readonly int _maxPending;

    /// <param name="holdMs">How long an operation may wait for its completion or path before it is released without it.</param>
    /// <param name="earlyKeepMs">How long an unmatched completion or path is kept for a late initiation.</param>
    /// <param name="maxPending">Hard cap on waiting operations; beyond it the oldest are released.</param>
    /// <param name="maxEarly">Hard cap on unmatched completions (and, separately, paths) kept.</param>
    public IoCorrelator(int holdMs = 2000, int earlyKeepMs = 2000, int maxPending = 200_000, int maxEarly = 500_000)
    {
        _holdMs = holdMs;
        _maxPending = maxPending;
        _earlyCompletions = new EarlyStore<(ulong, int)>(earlyKeepMs, maxEarly);
        _earlyPaths = new EarlyStore<string>(earlyKeepMs, maxEarly);
    }

    public int PendingCount => _order.Count;

    public int EarlyCompletionCount => _earlyCompletions.Count;

    public int EarlyPathCount => _earlyPaths.Count;

    public void Begin(PendingIo io, long nowTick)
    {
        io.ReceivedTick = nowTick;
        io.NextSameIrp = null;
        io.NextAwaitingPath = null;
        if (io.Irp == 0)
        {
            io.AwaitingPath = false; // nothing could ever be matched to it
        }
        else
        {
            if (_earlyCompletions.TryTake(io.Irp, io.TimestampQpc, out (ulong Info, int Status) completion))
            {
                io.Completed = true;
                io.NtStatus = completion.Status;
                io.CompletionInfo = completion.Info;
            }
            else
            {
                if (_pendingByIrp.TryGetValue(io.Irp, out PendingIo? head))
                    io.NextSameIrp = head;
                _pendingByIrp[io.Irp] = io;
            }

            if (io.AwaitingPath)
            {
                if (_earlyPaths.TryTake(io.Irp, io.TimestampQpc, out string? path))
                {
                    io.TargetPath = path;
                    io.AwaitingPath = false;
                }
                else
                {
                    if (_awaitingPathByIrp.TryGetValue(io.Irp, out PendingIo? head))
                        io.NextAwaitingPath = head;
                    _awaitingPathByIrp[io.Irp] = io;
                }
            }
        }

        _order.Enqueue(io);
    }

    /// <returns>True when the completion matched a waiting operation.</returns>
    public bool Complete(ulong irp, long timestampQpc, ulong info, int status, long nowTick)
    {
        if (irp == 0)
            return false;

        if (_pendingByIrp.TryGetValue(irp, out PendingIo? head))
        {
            PendingIo? best = LatestStartedBy(head, timestampQpc, static p => p.NextSameIrp);
            if (best is not null)
            {
                best.Completed = true;
                best.NtStatus = status;
                best.CompletionInfo = info;
                UnlinkPending(irp, best);
                return true;
            }
        }

        _earlyCompletions.Add(irp, timestampQpc, (info, status), nowTick);
        return false;
    }

    /// <summary>Attaches the path of a FileIo_PathOperation event to the operation it belongs to.</summary>
    /// <returns>True when the path matched an operation waiting for one.</returns>
    public bool Annotate(ulong irp, long timestampQpc, string path, long nowTick)
    {
        if (irp == 0)
            return false;

        if (_awaitingPathByIrp.TryGetValue(irp, out PendingIo? head))
        {
            PendingIo? best = LatestStartedBy(head, timestampQpc, static p => p.NextAwaitingPath);
            if (best is not null)
            {
                best.TargetPath = path;
                best.AwaitingPath = false;
                UnlinkAwaiting(irp, best);
                return true;
            }
        }

        // An operation that was not told to wait (it began before the first path event of the
        // session) can still take the path while it waits for its completion.
        if (_pendingByIrp.TryGetValue(irp, out PendingIo? pending)
            && LatestStartedBy(pending, timestampQpc, static p => p.NextSameIrp) is { } owner)
        {
            owner.TargetPath = path;
            return true;
        }

        _earlyPaths.Add(irp, timestampQpc, path, nowTick);
        return false;
    }

    /// <summary>
    /// Hands operations to <paramref name="sink"/> in initiation order: complete ones immediately,
    /// others once they waited longer than the hold time.
    /// </summary>
    public void Release(long nowTick, Action<PendingIo> sink, bool flushAll = false)
    {
        while (_order.TryPeek(out PendingIo? head))
        {
            // Without a request address nothing can ever be matched, so there is nothing to wait for.
            bool waiting = (!head.Completed && head.Irp != 0) || head.AwaitingPath;
            bool expired = nowTick - head.ReceivedTick >= _holdMs || _order.Count > _maxPending;
            if (waiting && !expired && !flushAll)
                break;

            _order.Dequeue();
            if (!head.Completed && head.Irp != 0)
                UnlinkPending(head.Irp, head);
            if (head.AwaitingPath)
            {
                UnlinkAwaiting(head.Irp, head);
                head.AwaitingPath = false;
            }

            head.NextSameIrp = null;
            head.NextAwaitingPath = null;
            sink(head);
        }

        _earlyCompletions.Purge(nowTick, flushAll);
        _earlyPaths.Purge(nowTick, flushAll);
    }

    /// <summary>The latest operation in a chain that started at or before <paramref name="timestampQpc"/>.</summary>
    private static PendingIo? LatestStartedBy(PendingIo head, long timestampQpc, Func<PendingIo, PendingIo?> next)
    {
        PendingIo? best = null;
        for (PendingIo? p = head; p is not null; p = next(p))
        {
            if (p.TimestampQpc <= timestampQpc && (best is null || p.TimestampQpc > best.TimestampQpc))
                best = p;
        }

        return best;
    }

    private void UnlinkPending(ulong irp, PendingIo item)
    {
        if (!_pendingByIrp.TryGetValue(irp, out PendingIo? head))
            return;

        if (ReferenceEquals(head, item))
        {
            if (item.NextSameIrp is null)
                _pendingByIrp.Remove(irp);
            else
                _pendingByIrp[irp] = item.NextSameIrp;
            item.NextSameIrp = null;
            return;
        }

        PendingIo previous = head;
        while (previous.NextSameIrp is { } next)
        {
            if (ReferenceEquals(next, item))
            {
                previous.NextSameIrp = item.NextSameIrp;
                item.NextSameIrp = null;
                return;
            }

            previous = next;
        }
    }

    private void UnlinkAwaiting(ulong irp, PendingIo item)
    {
        if (!_awaitingPathByIrp.TryGetValue(irp, out PendingIo? head))
            return;

        if (ReferenceEquals(head, item))
        {
            if (item.NextAwaitingPath is null)
                _awaitingPathByIrp.Remove(irp);
            else
                _awaitingPathByIrp[irp] = item.NextAwaitingPath;
            item.NextAwaitingPath = null;
            return;
        }

        PendingIo previous = head;
        while (previous.NextAwaitingPath is { } next)
        {
            if (ReferenceEquals(next, item))
            {
                previous.NextAwaitingPath = item.NextAwaitingPath;
                item.NextAwaitingPath = null;
                return;
            }

            previous = next;
        }
    }

    /// <summary>Values that arrived before the initiation they belong to, keyed by request address.</summary>
    private sealed class EarlyStore<T>(int keepMs, int capacity)
    {
        private readonly Dictionary<ulong, Entry> _byIrp = new();
        private readonly Queue<Entry> _order = new();

        private sealed class Entry
        {
            public ulong Irp;
            public long TimestampQpc;
            public T Value = default!;
            public long ReceivedTick;
            public bool Consumed;
            public Entry? Next;
        }

        public int Count => _order.Count;

        public void Add(ulong irp, long timestampQpc, T value, long nowTick)
        {
            var entry = new Entry { Irp = irp, TimestampQpc = timestampQpc, Value = value, ReceivedTick = nowTick };
            if (_byIrp.TryGetValue(irp, out Entry? head))
                entry.Next = head;
            _byIrp[irp] = entry;
            _order.Enqueue(entry);

            if (_order.Count > capacity)
                Purge(nowTick, flushAll: false, force: _order.Count - capacity);
        }

        /// <summary>Takes the earliest unconsumed value for <paramref name="irp"/> logged at or after <paramref name="beginQpc"/>.</summary>
        public bool TryTake(ulong irp, long beginQpc, out T value)
        {
            value = default!;
            if (!_byIrp.TryGetValue(irp, out Entry? head))
                return false;

            Entry? best = null;
            for (Entry? e = head; e is not null; e = e.Next)
            {
                if (!e.Consumed && e.TimestampQpc >= beginQpc && (best is null || e.TimestampQpc < best.TimestampQpc))
                    best = e;
            }

            if (best is null)
                return false;
            value = best.Value;
            Remove(best);
            return true;
        }

        public void Purge(long nowTick, bool flushAll, int force = 0)
        {
            while (_order.TryPeek(out Entry? oldest))
            {
                if (!oldest.Consumed && !flushAll && force <= 0 && nowTick - oldest.ReceivedTick < keepMs)
                    break;

                _order.Dequeue();
                force--;
                if (!oldest.Consumed)
                    Remove(oldest);
            }
        }

        private void Remove(Entry item)
        {
            item.Consumed = true;
            if (!_byIrp.TryGetValue(item.Irp, out Entry? head))
                return;

            if (ReferenceEquals(head, item))
            {
                if (item.Next is null)
                    _byIrp.Remove(item.Irp);
                else
                    _byIrp[item.Irp] = item.Next;
                return;
            }

            Entry previous = head;
            while (previous.Next is { } next)
            {
                if (ReferenceEquals(next, item))
                {
                    previous.Next = item.Next;
                    return;
                }

                previous = next;
            }
        }
    }
}
