namespace DriveMonitor.Core.Tracing;

/// <summary>
/// Holds what <see cref="EtwFileIoSource"/> reports for a short window and passes it on to the sink in the
/// order the kernel logged it (QPC timestamp order), so that completion matching does not depend on the order
/// in which Windows delivered the events.
/// </summary>
/// <remarks>
/// <para>
/// A real-time session hands over each processor's buffer separately, when it is full or flushed, so events
/// arrive out of timestamp order by up to about the flush interval, and request addresses are recycled within
/// microseconds. Some mix-ups cannot be undone in arrival order: a completion of a newer use of an address can
/// arrive before the starts and the completion of the earlier uses, and a monitored start can arrive after the
/// completion of an unmonitored operation that reused its address, before that operation's own start. In
/// timestamp order each address strictly alternates start and end, so <see cref="IoCorrelator"/> pairs every
/// completion with the latest start before it; its early stores and reuse checks remain for events that arrive
/// later than the window.
/// </para>
/// <para>
/// An event is passed on once one logged at the same time or later has been held for the window. As long as no
/// event arrives more than a window after one logged later than it, everything logged before has arrived by then.
/// Events with equal timestamps keep their arrival order, which for one processor's events is the order they were
/// logged. An event logged before one that was already passed on is passed on at once: it is too late to be put in
/// order. Arrivals release what is due, and a timer does when nothing arrives, so a quiet system still publishes.
/// </para>
/// <para>
/// Events wait in a heap ordered by timestamp, O(log n) to add or take, and nothing is allocated per event: a
/// start's <see cref="PendingIo"/> and a path's string exist already, the rest are values. Arrival times are kept per
/// clock tick, each with the highest timestamp that had arrived by the end of it, so that record grows with the
/// window, not with the number of events. A cap bounds memory (48 bytes an event, so about 12 MB at the default
/// 250,000): beyond it the earliest logged event is passed on before its time and counted in <see cref="Overflows"/>.
/// </para>
/// <para>
/// Thread-safe. The sink is called under this object's lock, one event at a time, so it must not call back, and
/// nothing may call <see cref="Release"/> or <see cref="Dispose"/> while holding a lock that the sink takes.
/// <see cref="IFileIoSink.TryAccept"/> goes straight to the sink.
/// </para>
/// </remarks>
internal sealed class ReorderBuffer : IFileIoSink, IDisposable
{
    /// <summary>
    /// The shortest time the timer is set for. While events stream in, arrivals release them and the timer finds
    /// nothing to do; this keeps it from waking up at every clock tick.
    /// </summary>
    private const int MinTimerDelayMs = 50;

    private readonly IFileIoSink _sink;
    private readonly int _windowMs;
    private readonly Action<Exception> _onError;
    private readonly int _capacity;
    private readonly bool _releaseOnTimer;
    private readonly object _lock = new();
    private readonly PriorityQueue<Held, (long Qpc, long Arrival)> _held = new();

    // For each clock tick in which events arrived, oldest first: the highest timestamp that had arrived by its end.
    // The newest tick is kept apart, because events may still arrive in it.
    private readonly Queue<(long Tick, long LatestQpc)> _pastTicks = new();
    private long _newestTick = long.MinValue;
    private long _latestQpc = long.MinValue;

    /// <summary>Events logged at or before this have been passed on, or are being passed on now.</summary>
    private long _releaseUpTo = long.MinValue;

    private long _arrivals;
    private Timer? _timer;
    private bool _timerSet;
    private bool _disposed;
    private int _count;
    private long _overflows;

    /// <param name="sink">Receives the events in timestamp order.</param>
    /// <param name="windowMs">How long an event is held at most; it goes sooner once one logged at the same time or later has been held that long.</param>
    /// <param name="onError">Told about an exception the sink threw; it cost the event the sink was given, the others are still passed on.</param>
    /// <param name="capacity">Events held at most; beyond it, the earliest logged one is passed on before its time.</param>
    /// <param name="releaseOnTimer">Whether a timer releases what falls due when nothing arrives (off only in tests that pass the time themselves).</param>
    public ReorderBuffer(IFileIoSink sink, int windowMs, Action<Exception> onError, int capacity = 250_000, bool releaseOnTimer = true)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(windowMs);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        _sink = sink;
        _windowMs = windowMs;
        _onError = onError;
        _capacity = capacity;
        _releaseOnTimer = releaseOnTimer;
    }

    private enum Kind : byte
    {
        Begin,
        ForeignBegin,
        Complete,
        PathOperation,
    }

    /// <summary>Events held now. Safe to read from any thread.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Events passed on before their time because the buffer was full. Safe to read from any thread.</summary>
    public long Overflows => Interlocked.Read(ref _overflows);

    bool IFileIoSink.TryAccept(ref string path, int processId, out char drive) => _sink.TryAccept(ref path, processId, out drive);

    void IFileIoSink.OnBegin(PendingIo io) => OnBegin(io, Environment.TickCount64);

    void IFileIoSink.OnForeignBegin(ulong irp, long timestampQpc) => OnForeignBegin(irp, timestampQpc, Environment.TickCount64);

    void IFileIoSink.OnComplete(ulong irp, long timestampQpc, ulong extraInfo, int ntStatus) =>
        OnComplete(irp, timestampQpc, extraInfo, ntStatus, Environment.TickCount64);

    void IFileIoSink.OnPathOperation(ulong irp, long timestampQpc, string path) => OnPathOperation(irp, timestampQpc, path, Environment.TickCount64);

    public void OnBegin(PendingIo io, long nowTick) => Add(new Held(Kind.Begin, 0, 0, 0, io), io.TimestampQpc, nowTick);

    public void OnForeignBegin(ulong irp, long timestampQpc, long nowTick) => Add(new Held(Kind.ForeignBegin, irp, 0, 0, null), timestampQpc, nowTick);

    public void OnComplete(ulong irp, long timestampQpc, ulong extraInfo, int ntStatus, long nowTick) =>
        Add(new Held(Kind.Complete, irp, extraInfo, ntStatus, null), timestampQpc, nowTick);

    public void OnPathOperation(ulong irp, long timestampQpc, string path, long nowTick) =>
        Add(new Held(Kind.PathOperation, irp, 0, 0, path), timestampQpc, nowTick);

    /// <summary>Passes on what is due at <paramref name="nowTick"/>, as each arrival and the timer do.</summary>
    public void Release(long nowTick)
    {
        lock (_lock)
        {
            if (!_disposed)
                ReleaseDue(nowTick);
        }
    }

    /// <summary>
    /// Passes on everything held, in timestamp order, and from then on passes events straight through. Called once
    /// the consumer has delivered its last event, so that the engine's final release finds them all.
    /// </summary>
    public void Dispose()
    {
        Timer? timer;
        lock (_lock)
        {
            if (_disposed)
                return;

            _pastTicks.Clear();
            while (_held.TryDequeue(out Held held, out (long Qpc, long Arrival) order))
                Pass(held, order.Qpc);
            Volatile.Write(ref _count, 0);
            _disposed = true;
            timer = _timer;
            _timer = null;
        }

        timer?.Dispose();
    }

    private void Add(in Held held, long timestampQpc, long nowTick)
    {
        lock (_lock)
        {
            if (_disposed)
            {
                // A consumer that outlived Stop: nothing would release the event from here.
                Pass(held, timestampQpc);
                return;
            }

            if (timestampQpc <= _releaseUpTo)
            {
                // Logged before an event that was already passed on, so too late to be put in order. Everything
                // still held was logged after it.
                Pass(held, timestampQpc);
            }
            else
            {
                NoteArrival(timestampQpc, nowTick);
                _held.Enqueue(held, (timestampQpc, _arrivals++));
                if (_held.Count > _capacity)
                {
                    // What was logged before it and arrives later is too late now, like any event that misses its window.
                    _held.TryDequeue(out Held earliest, out (long Qpc, long Arrival) order);
                    _releaseUpTo = Math.Max(_releaseUpTo, order.Qpc);
                    Interlocked.Increment(ref _overflows);
                    Pass(earliest, order.Qpc);
                }
            }

            ReleaseDue(nowTick);
            SetTimer(nowTick);
        }
    }

    /// <summary>Records that an event logged at <paramref name="timestampQpc"/> arrived in tick <paramref name="nowTick"/>.</summary>
    private void NoteArrival(long timestampQpc, long nowTick)
    {
        if (nowTick > _newestTick)
        {
            // The previous tick is over: by its end, nothing logged after _latestQpc had arrived.
            if (_newestTick != long.MinValue)
                _pastTicks.Enqueue((_newestTick, _latestQpc));
            _newestTick = nowTick;
        }

        if (timestampQpc > _latestQpc)
            _latestQpc = timestampQpc;
    }

    /// <summary>
    /// Passes on, in timestamp order, every event held that was logged at or before one that arrived at least the
    /// window before <paramref name="nowTick"/>.
    /// </summary>
    private void ReleaseDue(long nowTick)
    {
        while (_pastTicks.TryPeek(out (long Tick, long LatestQpc) past) && nowTick - past.Tick >= _windowMs)
        {
            _pastTicks.Dequeue();
            _releaseUpTo = Math.Max(_releaseUpTo, past.LatestQpc);
        }

        if (_newestTick != long.MinValue && nowTick - _newestTick >= _windowMs)
            _releaseUpTo = Math.Max(_releaseUpTo, _latestQpc);

        while (_held.TryPeek(out Held next, out (long Qpc, long Arrival) order) && order.Qpc <= _releaseUpTo)
        {
            _held.Dequeue();
            Pass(next, order.Qpc);
        }

        Volatile.Write(ref _count, _held.Count);
    }

    /// <summary>Sets the timer, unless it is set already, for when the oldest arrival tick still counted falls due.</summary>
    private void SetTimer(long nowTick)
    {
        if (!_releaseOnTimer || _timerSet || _held.Count == 0)
            return;

        long oldestTick = _pastTicks.TryPeek(out (long Tick, long LatestQpc) past) ? past.Tick : _newestTick;
        long delay = Math.Max(oldestTick + _windowMs - nowTick, MinTimerDelayMs);
        _timer ??= new Timer(static state => ((ReorderBuffer)state!).OnTimer(), this, Timeout.Infinite, Timeout.Infinite);
        _timer.Change(delay, Timeout.Infinite);
        _timerSet = true;
    }

    private void OnTimer()
    {
        lock (_lock)
        {
            _timerSet = false;
            if (_disposed)
                return;

            long now = Environment.TickCount64;
            ReleaseDue(now);
            SetTimer(now);
        }
    }

    /// <summary>Hands one event to the sink. An exception there is reported and costs only that event.</summary>
    private void Pass(in Held held, long timestampQpc)
    {
        try
        {
            switch (held.Kind)
            {
                case Kind.Begin:
                    _sink.OnBegin((PendingIo)held.Payload!);
                    break;
                case Kind.ForeignBegin:
                    _sink.OnForeignBegin(held.Irp, timestampQpc);
                    break;
                case Kind.Complete:
                    _sink.OnComplete(held.Irp, timestampQpc, held.Info, held.Status);
                    break;
                default:
                    _sink.OnPathOperation(held.Irp, timestampQpc, (string)held.Payload!);
                    break;
            }
        }
        catch (Exception ex)
        {
            _onError(ex);
        }
    }

    /// <summary>One event waiting to be passed on; its timestamp is its place in the heap.</summary>
    private readonly struct Held(Kind kind, ulong irp, ulong info, int status, object? payload)
    {
        /// <summary>A start's <see cref="PendingIo"/>, or the path of a path operation.</summary>
        public readonly object? Payload = payload;
        public readonly ulong Irp = irp;
        public readonly ulong Info = info;
        public readonly int Status = status;
        public readonly Kind Kind = kind;
    }
}
