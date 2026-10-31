using System.Runtime.InteropServices;

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
/// <para>
/// Every completion in the system passes through here, and recycling puts thousands of them on a
/// few hot addresses, so nothing walks a whole per-address chain. Each address's waiting operations
/// and stored values are doubly linked and sorted by timestamp: one leaves in constant time, a lookup
/// stops at the first entry that is early enough, and an insert rarely walks because events nearly
/// always arrive in timestamp order. A cap per address bounds the stored values that arrive out of order.
/// </para>
/// <para>Not thread-safe; the caller serialises access. Only <see cref="PendingCount"/> may be read from any thread.</para>
/// </remarks>
internal sealed class IoCorrelator
{
    private readonly Chains _pending = new(awaitingPath: false);
    private readonly Chains _awaitingPath = new(awaitingPath: true);
    private readonly Queue<PendingIo> _order = new();
    private readonly EarlyStore<(ulong Info, int Status)> _earlyCompletions;
    private readonly EarlyStore<string> _earlyPaths;
    private readonly int _holdMs;
    private readonly int _maxPending;
    private int _pendingCount;

    /// <param name="holdMs">How long an operation may wait for its completion or path before it is released without it.</param>
    /// <param name="earlyKeepMs">How long an unmatched completion or path is kept for a late initiation.</param>
    /// <param name="maxPending">Hard cap on waiting operations; beyond it the oldest are released.</param>
    /// <param name="maxEarly">Hard cap on unmatched completions (and, separately, paths) kept.</param>
    /// <param name="maxEarlyPerIrp">Cap on unmatched completions (and, separately, paths) kept for one request address; beyond it the earliest logged are dropped.</param>
    public IoCorrelator(int holdMs = 2000, int earlyKeepMs = 2000, int maxPending = 200_000, int maxEarly = 500_000, int maxEarlyPerIrp = 1024)
    {
        _holdMs = holdMs;
        _maxPending = maxPending;
        _earlyCompletions = new EarlyStore<(ulong, int)>(earlyKeepMs, maxEarly, maxEarlyPerIrp);
        _earlyPaths = new EarlyStore<string>(earlyKeepMs, maxEarly, maxEarlyPerIrp);
    }

    /// <summary>Operations not released yet. Unlike the rest of this class, safe to read from any thread.</summary>
    public int PendingCount => Volatile.Read(ref _pendingCount);

    public int EarlyCompletionCount => _earlyCompletions.Count;

    public int EarlyPathCount => _earlyPaths.Count;

    public void Begin(PendingIo io, long nowTick)
    {
        io.ReceivedTick = nowTick;
        io.NextSameIrp = null;
        io.PrevSameIrp = null;
        io.NextAwaitingPath = null;
        io.PrevAwaitingPath = null;
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
                _pending.Insert(io);
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
                    _awaitingPath.Insert(io);
                }
            }
        }

        _order.Enqueue(io);
        Volatile.Write(ref _pendingCount, _order.Count);
    }

    /// <returns>True when the completion matched a waiting operation.</returns>
    public bool Complete(ulong irp, long timestampQpc, ulong info, int status, long nowTick)
    {
        if (irp == 0)
            return false;

        if (_pending.LatestStartedBy(irp, timestampQpc) is { } owner)
        {
            owner.Completed = true;
            owner.NtStatus = status;
            owner.CompletionInfo = info;
            _pending.Unlink(owner);
            return true;
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

        if (_awaitingPath.LatestStartedBy(irp, timestampQpc) is { } owner)
        {
            owner.TargetPath = path;
            owner.AwaitingPath = false;
            _awaitingPath.Unlink(owner);
            return true;
        }

        // An operation that was not told to wait (it began before the first path event of the
        // session) can still take the path while it waits for its completion.
        if (_pending.LatestStartedBy(irp, timestampQpc) is { } pending)
        {
            pending.TargetPath = path;
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
            Volatile.Write(ref _pendingCount, _order.Count);
            if (!head.Completed && head.Irp != 0)
                _pending.Unlink(head);
            if (head.AwaitingPath)
            {
                _awaitingPath.Unlink(head);
                head.AwaitingPath = false;
            }

            sink(head);
        }

        _earlyCompletions.Purge(nowTick, flushAll);
        _earlyPaths.Purge(nowTick, flushAll);
    }

    /// <summary>
    /// Waiting operations by request address. Each address's operations form a doubly linked chain
    /// sorted by start, latest first.
    /// </summary>
    /// <param name="awaitingPath">Which pair of links in <see cref="PendingIo"/> this chain uses.</param>
    private sealed class Chains(bool awaitingPath)
    {
        private readonly Dictionary<ulong, PendingIo> _latestByIrp = new();

        public void Insert(PendingIo io)
        {
            // Initiations nearly always arrive in start order, so this walk rarely moves.
            PendingIo? later = null;
            PendingIo? earlier = _latestByIrp.GetValueOrDefault(io.Irp);
            while (earlier is not null && earlier.TimestampQpc > io.TimestampQpc)
            {
                later = earlier;
                earlier = Earlier(earlier);
            }

            Earlier(io) = earlier;
            Later(io) = later;
            if (earlier is not null)
                Later(earlier) = io;
            if (later is not null)
                Earlier(later) = io;
            else
                _latestByIrp[io.Irp] = io;
        }

        /// <summary>Takes <paramref name="io"/> out of its chain in constant time; does nothing if it is not in one.</summary>
        public void Unlink(PendingIo io)
        {
            PendingIo? earlier = Earlier(io);
            PendingIo? later = Later(io);
            if (later is not null)
                Earlier(later) = earlier;
            else if (!_latestByIrp.TryGetValue(io.Irp, out PendingIo? latest) || !ReferenceEquals(latest, io))
                return;
            else if (earlier is null)
                _latestByIrp.Remove(io.Irp);
            else
                _latestByIrp[io.Irp] = earlier;

            if (earlier is not null)
                Later(earlier) = later;
            Earlier(io) = null;
            Later(io) = null;
        }

        /// <summary>The latest operation on <paramref name="irp"/> that started at or before <paramref name="timestampQpc"/>.</summary>
        public PendingIo? LatestStartedBy(ulong irp, long timestampQpc)
        {
            PendingIo? io = _latestByIrp.GetValueOrDefault(irp);
            while (io is not null && io.TimestampQpc > timestampQpc)
                io = Earlier(io);
            return io;
        }

        private ref PendingIo? Earlier(PendingIo io) => ref awaitingPath ? ref io.NextAwaitingPath : ref io.NextSameIrp;

        private ref PendingIo? Later(PendingIo io) => ref awaitingPath ? ref io.PrevAwaitingPath : ref io.PrevSameIrp;
    }

    /// <summary>Values that arrived before the initiation they belong to, keyed by request address.</summary>
    /// <remarks>
    /// Each value is on two doubly linked lists, so it leaves either in constant time: its address's
    /// chain, sorted by timestamp, which a lookup walks from the latest end only as far as the begin;
    /// and the list of all values in arrival order, which expiry and the overall cap work through.
    /// </remarks>
    private sealed class EarlyStore<T>(int keepMs, int capacity, int capacityPerIrp)
    {
        private readonly Dictionary<ulong, Chain> _byIrp = new();
        private Entry? _firstArrived;
        private Entry? _lastArrived;

        /// <summary>One address's values, from the earliest logged to the latest.</summary>
        private struct Chain
        {
            public Entry Earliest;
            public Entry Latest;
            public int Count;
        }

        private sealed class Entry(ulong irp, long timestampQpc, T value, long receivedTick)
        {
            public readonly ulong Irp = irp;
            public readonly long TimestampQpc = timestampQpc;
            public readonly T Value = value;
            public readonly long ReceivedTick = receivedTick;
            public Entry? Earlier;
            public Entry? Later;
            public Entry? ArrivedBefore;
            public Entry? ArrivedAfter;
        }

        public int Count { get; private set; }

        public void Add(ulong irp, long timestampQpc, T value, long nowTick)
        {
            var entry = new Entry(irp, timestampQpc, value, nowTick);
            ref Chain chain = ref CollectionsMarshal.GetValueRefOrAddDefault(_byIrp, irp, out bool exists);
            if (!exists)
            {
                chain.Earliest = entry;
                chain.Latest = entry;
            }
            else if (timestampQpc < chain.Earliest.TimestampQpc)
            {
                entry.Later = chain.Earliest;
                chain.Earliest.Earlier = entry;
                chain.Earliest = entry;
            }
            else
            {
                // Values nearly always arrive in timestamp order, so this walk from the latest rarely moves.
                Entry earlier = chain.Latest;
                while (earlier.TimestampQpc > timestampQpc)
                    earlier = earlier.Earlier!;

                entry.Earlier = earlier;
                entry.Later = earlier.Later;
                earlier.Later = entry;
                if (entry.Later is null)
                    chain.Latest = entry;
                else
                    entry.Later.Earlier = entry;
            }

            Entry? dropped = ++chain.Count > capacityPerIrp ? chain.Earliest : null;

            entry.ArrivedBefore = _lastArrived;
            if (_lastArrived is null)
                _firstArrived = entry;
            else
                _lastArrived.ArrivedAfter = entry;
            _lastArrived = entry;
            Count++;

            // Last, because removing can change the dictionary, which invalidates 'chain'.
            if (dropped is not null)
                Remove(dropped);
            if (Count > capacity)
                Remove(_firstArrived!);
        }

        /// <summary>Takes the earliest value for <paramref name="irp"/> logged at or after <paramref name="beginQpc"/>.</summary>
        public bool TryTake(ulong irp, long beginQpc, out T value)
        {
            value = default!;
            if (!_byIrp.TryGetValue(irp, out Chain chain))
                return false;

            // Only the values logged since the begin are visited.
            Entry? best = null;
            for (Entry? e = chain.Latest; e is not null && e.TimestampQpc >= beginQpc; e = e.Earlier)
                best = e;

            if (best is null)
                return false;
            value = best.Value;
            Remove(best);
            return true;
        }

        public void Purge(long nowTick, bool flushAll)
        {
            if (flushAll)
            {
                _byIrp.Clear();
                _firstArrived = null;
                _lastArrived = null;
                Count = 0;
                return;
            }

            while (_firstArrived is { } oldest && nowTick - oldest.ReceivedTick >= keepMs)
                Remove(oldest);
        }

        private void Remove(Entry item)
        {
            ref Chain chain = ref CollectionsMarshal.GetValueRefOrNullRef(_byIrp, item.Irp);
            if (--chain.Count == 0)
            {
                _byIrp.Remove(item.Irp);
            }
            else
            {
                if (item.Earlier is null)
                    chain.Earliest = item.Later!;
                else
                    item.Earlier.Later = item.Later;
                if (item.Later is null)
                    chain.Latest = item.Earlier!;
                else
                    item.Later.Earlier = item.Earlier;
            }

            if (item.ArrivedBefore is null)
                _firstArrived = item.ArrivedAfter;
            else
                item.ArrivedBefore.ArrivedAfter = item.ArrivedAfter;
            if (item.ArrivedAfter is null)
                _lastArrived = item.ArrivedBefore;
            else
                item.ArrivedAfter.ArrivedBefore = item.ArrivedBefore;
            Count--;
        }
    }
}
