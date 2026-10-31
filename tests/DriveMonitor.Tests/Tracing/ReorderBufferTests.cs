using System.Diagnostics;
using DriveMonitor.Core.Tracing;

namespace DriveMonitor.Tests.Tracing;

/// <summary>
/// Holding FileIo events for a window and passing them on in the order the kernel logged them, whatever order
/// Windows delivered them in.
/// </summary>
/// <remarks>"qpc" is when the kernel logged an event; "tick" is when the consumer received it.</remarks>
public sealed class ReorderBufferTests
{
    private const int WindowMs = 750;

    [Fact]
    public void Events_ArePassedOnInTimestampOrder_OnceTheWindowHasPassed()
    {
        var sink = new RecordingSink();
        using ReorderBuffer buffer = Create(sink);

        buffer.OnComplete(0x10, timestampQpc: 400, extraInfo: 4, ntStatus: 0, nowTick: 0);
        buffer.OnForeignBegin(0x10, timestampQpc: 300, nowTick: 0);
        buffer.OnComplete(0x10, timestampQpc: 200, extraInfo: 2, ntStatus: 0, nowTick: 0);
        buffer.OnForeignBegin(0x10, timestampQpc: 100, nowTick: 0);

        buffer.Release(nowTick: WindowMs - 1);
        Assert.Empty(sink.Calls);
        Assert.Equal(4, buffer.Count);

        buffer.Release(nowTick: WindowMs);
        Assert.Equal(new long[] { 100, 200, 300, 400 }, sink.Qpcs);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Event_GoesOnceOneLoggedNoEarlierHasWaitedTheWindow()
    {
        var sink = new RecordingSink();
        using ReorderBuffer buffer = Create(sink);

        buffer.OnForeignBegin(1, timestampQpc: 1000, nowTick: 0);
        buffer.OnForeignBegin(2, timestampQpc: 900, nowTick: 700); // logged earlier, delivered 700 ms later
        buffer.OnForeignBegin(3, timestampQpc: 1100, nowTick: 700); // logged later

        // The first has waited the window, so everything logged before it has arrived: it goes, after the one
        // logged before it, which waited only 50 ms.
        buffer.Release(nowTick: WindowMs);
        Assert.Equal(new long[] { 900, 1000 }, sink.Qpcs);

        buffer.Release(nowTick: 700 + WindowMs - 1);
        Assert.Equal(2, sink.Calls.Count);

        buffer.Release(nowTick: 700 + WindowMs);
        Assert.Equal(new long[] { 900, 1000, 1100 }, sink.Qpcs);
    }

    [Fact]
    public void Arrival_ReleasesWhatHasFallenDue()
    {
        var sink = new RecordingSink();
        using ReorderBuffer buffer = Create(sink);

        buffer.OnComplete(0x10, timestampQpc: 100, extraInfo: 1, ntStatus: 0, nowTick: 0);
        buffer.OnComplete(0x10, timestampQpc: 200, extraInfo: 2, ntStatus: 0, nowTick: WindowMs);

        Assert.Equal(new long[] { 100 }, sink.Qpcs);
        Assert.Equal(1, buffer.Count);
    }

    [Fact]
    public void EqualTimestamps_KeepTheirArrivalOrder()
    {
        var sink = new RecordingSink();
        using ReorderBuffer buffer = Create(sink);

        buffer.OnComplete(0x10, timestampQpc: 100, extraInfo: 1, ntStatus: 0, nowTick: 0);
        buffer.OnForeignBegin(0x20, timestampQpc: 100, nowTick: 0);
        buffer.OnBegin(Begin(0x30, qpc: 100), nowTick: 3);
        buffer.OnPathOperation(0x40, timestampQpc: 100, @"E:\b.txt", nowTick: 3);
        buffer.OnComplete(0x50, timestampQpc: 50, extraInfo: 5, ntStatus: 0, nowTick: 3);

        buffer.Release(nowTick: WindowMs);
        Assert.Equal(new ulong[] { 0x50, 0x10, 0x20, 0x30, 0x40 }, sink.Irps);

        // One more with the same timestamp, after the others were passed on: it still comes after them.
        buffer.OnComplete(0x60, timestampQpc: 100, extraInfo: 6, ntStatus: 0, nowTick: WindowMs + 1);
        Assert.Equal(new ulong[] { 0x50, 0x10, 0x20, 0x30, 0x40, 0x60 }, sink.Irps);
    }

    [Fact]
    public void Event_LoggedBeforeOneAlreadyPassedOn_IsPassedOnAtOnce()
    {
        // A flush delayed on a busy system: there is no order left to restore, and the correlator's own checks take over.
        var sink = new RecordingSink();
        using ReorderBuffer buffer = Create(sink);
        buffer.OnForeignBegin(1, timestampQpc: 1000, nowTick: 0);
        buffer.OnForeignBegin(2, timestampQpc: 2000, nowTick: 500);
        buffer.Release(nowTick: WindowMs);
        Assert.Equal(new long[] { 1000 }, sink.Qpcs);

        buffer.OnComplete(1, timestampQpc: 990, extraInfo: 0, ntStatus: 0, nowTick: WindowMs + 50);

        Assert.Equal(new long[] { 1000, 990 }, sink.Qpcs);
        Assert.Equal(1, buffer.Count); // the later one still waits for its window
        Assert.Equal(0, buffer.Overflows);
    }

    [Fact]
    public void EachKindOfEvent_IsPassedOnWithItsFields()
    {
        var sink = new RecordingSink();
        using ReorderBuffer buffer = Create(sink);
        PendingIo begin = Begin(0x10, qpc: 100);

        buffer.OnPathOperation(0x10, timestampQpc: 110, @"E:\renamed.txt", nowTick: 0);
        buffer.OnForeignBegin(0x20, timestampQpc: 130, nowTick: 0);
        buffer.OnComplete(0x10, timestampQpc: 120, extraInfo: 4096, ntStatus: unchecked((int)0xC0000022), nowTick: 0);
        buffer.OnBegin(begin, nowTick: 0);
        buffer.Release(nowTick: WindowMs);

        Assert.Equal(
            new[]
            {
                new Call("begin", 0x10, 100, 0, 0, begin),
                new Call("path", 0x10, 110, 0, 0, @"E:\renamed.txt"),
                new Call("complete", 0x10, 120, 4096, unchecked((int)0xC0000022), null),
                new Call("foreign", 0x20, 130, 0, 0, null),
            },
            sink.Calls);
        Assert.Same(begin, sink.Calls[0].Payload);
    }

    [Fact]
    public void TryAccept_GoesStraightToTheSink()
    {
        var sink = new RecordingSink { Accept = path => path.StartsWith(@"\Device\", StringComparison.Ordinal) ? @"E:\file.txt" : null };
        using ReorderBuffer buffer = Create(sink);
        IFileIoSink front = buffer;

        string path = @"\Device\HarddiskVolume5\file.txt";
        Assert.True(front.TryAccept(ref path, processId: 42, out char drive));
        Assert.Equal(@"E:\file.txt", path); // rewritten by the sink
        Assert.Equal('E', drive);

        string other = @"C:\other.txt";
        Assert.False(front.TryAccept(ref other, processId: 42, out _));
        Assert.Equal(0, buffer.Count);
        Assert.Empty(sink.Calls);
    }

    [Fact]
    public void Dispose_PassesOnEverythingHeldInTimestampOrder_ThenPassesEventsStraightThrough()
    {
        // What happens at Stop and when a faulted session is restarted: the source's consumer has delivered its
        // last event, and the engine is about to release everything that waits for a completion.
        var sink = new RecordingSink();
        ReorderBuffer buffer = Create(sink);
        buffer.OnComplete(0x10, timestampQpc: 300, extraInfo: 3, ntStatus: 0, nowTick: 0);
        buffer.OnBegin(Begin(0x10, qpc: 200), nowTick: 0);
        buffer.OnForeignBegin(0x10, timestampQpc: 100, nowTick: 1);

        buffer.Dispose();

        Assert.Equal(new long[] { 100, 200, 300 }, sink.Qpcs);
        Assert.Equal(0, buffer.Count);

        // A consumer that did not stop in time: nothing would release its events any more, so they go at once.
        buffer.OnComplete(0x20, timestampQpc: 50, extraInfo: 5, ntStatus: 0, nowTick: 2);
        Assert.Equal(new long[] { 100, 200, 300, 50 }, sink.Qpcs);

        buffer.Release(nowTick: 10_000);
        buffer.Dispose();
        Assert.Equal(4, sink.Calls.Count); // nothing passed on twice
    }

    [Fact]
    public void Full_PassesOnTheEarliestLoggedBeforeItsTime_AndCountsIt()
    {
        var sink = new RecordingSink();
        using var buffer = new ReorderBuffer(sink, WindowMs, Fail, capacity: 3, releaseOnTimer: false);
        buffer.OnForeignBegin(1, timestampQpc: 300, nowTick: 0);
        buffer.OnForeignBegin(1, timestampQpc: 400, nowTick: 0);
        buffer.OnForeignBegin(1, timestampQpc: 200, nowTick: 0);
        Assert.Empty(sink.Calls);

        buffer.OnForeignBegin(1, timestampQpc: 500, nowTick: 1);

        Assert.Equal(new long[] { 200 }, sink.Qpcs);
        Assert.Equal(1, buffer.Overflows);
        Assert.Equal(3, buffer.Count);

        // Logged before the one that had to go early, so it is late now and goes at once; that is not another overflow.
        buffer.OnForeignBegin(1, timestampQpc: 150, nowTick: 2);
        Assert.Equal(new long[] { 200, 150 }, sink.Qpcs);
        Assert.Equal(1, buffer.Overflows);

        buffer.Release(nowTick: 1 + WindowMs);
        Assert.Equal(new long[] { 200, 150, 300, 400, 500 }, sink.Qpcs);
        Assert.Equal(1, buffer.Overflows);
    }

    [Fact]
    public void SinkThatThrows_CostsOnlyThatEvent()
    {
        var errors = new List<Exception>();
        var sink = new RecordingSink { Throw = call => call.Qpc == 200 };
        using var buffer = new ReorderBuffer(sink, WindowMs, errors.Add, releaseOnTimer: false);
        buffer.OnForeignBegin(1, timestampQpc: 300, nowTick: 0);
        buffer.OnForeignBegin(1, timestampQpc: 200, nowTick: 0);
        buffer.OnForeignBegin(1, timestampQpc: 100, nowTick: 0);

        buffer.Release(nowTick: WindowMs);

        Assert.Equal(new long[] { 100, 300 }, sink.Qpcs);
        Assert.IsType<InvalidOperationException>(Assert.Single(errors));
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void Timer_PassesOnWhatFallsDue_WithoutFurtherArrivals()
    {
        // A quiet system: after the last events of a burst nothing else arrives to release them.
        var errors = new List<Exception>(); // the timer thread must not throw
        var sink = new RecordingSink();
        using var buffer = new ReorderBuffer(sink, windowMs: 100, error => { lock (errors) errors.Add(error); });
        IFileIoSink front = buffer;

        front.OnComplete(0x10, timestampQpc: 200, extraInfo: 1, ntStatus: 0);
        front.OnForeignBegin(0x10, timestampQpc: 100);

        Assert.True(SpinWait.SpinUntil(() => sink.Calls.Count == 2, TimeSpan.FromSeconds(10)), "the timer released nothing");
        Assert.Equal(new long[] { 100, 200 }, sink.Qpcs);
        Assert.Equal(0, buffer.Count);
        lock (errors)
            Assert.Empty(errors);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    [InlineData(1, 0)]
    public void Constructor_RejectsAnEmptyWindowOrCapacity(int windowMs, int capacity)
    {
        // A window of 0 means no reordering: EtwFileIoSource then creates no buffer at all.
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReorderBuffer(new RecordingSink(), windowMs, Fail, capacity));
    }

    [Fact]
    public void RandomDelaysWithinTheWindow_AreUndoneExactly()
    {
        // Each processor's events stay in order but its buffer reaches the consumer up to 500 ms late, as the
        // flush interval allows; the window is 750 ms. Ties keep their arrival order.
        var random = new Random(31);
        var arrivals = new List<(long Tick, long Qpc, ulong Id)>();
        var lastArrival = new long[4];
        for (int i = 0; i < 20_000; i++)
        {
            long qpc = i / 3 * 10; // three events per timestamp
            int cpu = random.Next(lastArrival.Length);
            long tick = Math.Max(lastArrival[cpu], (qpc / 100) + random.Next(500));
            lastArrival[cpu] = tick;
            arrivals.Add((tick, qpc, (ulong)i));
        }

        List<(long Tick, long Qpc, ulong Id)> delivered = [.. arrivals.OrderBy(a => a.Tick)]; // stable
        var sink = new RecordingSink();
        using ReorderBuffer buffer = Create(sink);
        foreach ((long tick, long qpc, ulong id) in delivered)
            buffer.OnComplete(irp: 0x10, qpc, extraInfo: id, ntStatus: 0, tick);
        buffer.Release(delivered[^1].Tick + WindowMs);

        IEnumerable<ulong> expected = delivered
            .Select((a, order) => (a.Qpc, Order: order, a.Id))
            .OrderBy(a => a.Qpc)
            .ThenBy(a => a.Order)
            .Select(a => a.Id);
        Assert.Equal(expected, sink.Calls.Select(c => c.Info));
    }

    [Fact]
    public void ManyEvents_ArePassedOnInOrder_InLogLinearTime()
    {
        // 100 events per millisecond, each up to 10 ms out of order: about 50,000 are held at any time. A sorted
        // list would move tens of thousands of entries per event; the heap takes a few steps.
        const int count = 200_000;
        var sink = new OrderCheckingSink();
        using var buffer = new ReorderBuffer(sink, windowMs: 500, Fail, releaseOnTimer: false);
        var watch = Stopwatch.StartNew();

        Feed(buffer, first: 0, count);
        buffer.Release(nowTick: (count / 100) + 500);

        watch.Stop();
        Assert.Equal(count, sink.Count);
        Assert.True(sink.InOrder, "events were passed on out of order");
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), $"took {watch.Elapsed.TotalMilliseconds:N0} ms");
    }

    [Fact]
    public void SteadyStream_AllocatesNothingPerEvent()
    {
        var sink = new OrderCheckingSink();
        using var buffer = new ReorderBuffer(sink, windowMs: 100, Fail, releaseOnTimer: false);
        Feed(buffer, first: 0, count: 100_000); // grows the heap to its steady size

        long before = GC.GetAllocatedBytesForCurrentThread();
        Feed(buffer, first: 100_000, count: 100_000);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(sink.InOrder);
        Assert.True(allocated < 100_000, $"{allocated:N0} bytes allocated for 100,000 events");
    }

    /// <summary>Completions at 100 per tick, each logged up to 1,000 events before its place in the arrival order.</summary>
    private static void Feed(ReorderBuffer buffer, int first, int count)
    {
        for (int i = first; i < first + count; i++)
        {
            long qpc = (i * 10L) - ((i * 7919L) % 10_000);
            buffer.OnComplete(0x10, qpc, extraInfo: (ulong)i, ntStatus: 0, nowTick: i / 100);
        }
    }

    private static ReorderBuffer Create(IFileIoSink sink) => new(sink, WindowMs, Fail, releaseOnTimer: false);

    private static void Fail(Exception error) => Assert.Fail($"The sink threw: {error}");

    private static PendingIo Begin(ulong irp, long qpc) => new()
    {
        Op = RawFileOp.Read,
        Irp = irp,
        TimestampQpc = qpc,
        Path = @"E:\data.bin",
        Drive = 'E',
    };

    private sealed record Call(string Kind, ulong Irp, long Qpc, ulong Info, int Status, object? Payload);

    /// <summary>Records every call in order; safe to read while the buffer's timer calls it.</summary>
    private sealed class RecordingSink : IFileIoSink
    {
        private readonly List<Call> _calls = [];

        /// <summary>What TryAccept rewrites a path to, or null to reject it.</summary>
        public Func<string, string?>? Accept { get; init; }

        /// <summary>Which calls throw instead of being recorded.</summary>
        public Func<Call, bool>? Throw { get; init; }

        public List<Call> Calls
        {
            get
            {
                lock (_calls)
                    return [.. _calls];
            }
        }

        public List<long> Qpcs => Calls.ConvertAll(c => c.Qpc);

        public List<ulong> Irps => Calls.ConvertAll(c => c.Irp);

        public bool TryAccept(ref string path, int processId, out char drive)
        {
            drive = '\0';
            if (Accept?.Invoke(path) is not { } accepted)
                return false;
            path = accepted;
            drive = accepted[0];
            return true;
        }

        public void OnBegin(PendingIo io) => Record(new Call("begin", io.Irp, io.TimestampQpc, 0, 0, io));

        public void OnForeignBegin(ulong irp, long timestampQpc) => Record(new Call("foreign", irp, timestampQpc, 0, 0, null));

        public void OnComplete(ulong irp, long timestampQpc, ulong extraInfo, int ntStatus) =>
            Record(new Call("complete", irp, timestampQpc, extraInfo, ntStatus, null));

        public void OnPathOperation(ulong irp, long timestampQpc, string path) => Record(new Call("path", irp, timestampQpc, 0, 0, path));

        private void Record(Call call)
        {
            if (Throw?.Invoke(call) == true)
                throw new InvalidOperationException("The sink failed.");
            lock (_calls)
                _calls.Add(call);
        }
    }

    /// <summary>Checks that completions arrive by timestamp, ties in the order given by their ExtraInfo; allocates nothing.</summary>
    private sealed class OrderCheckingSink : IFileIoSink
    {
        private long _lastQpc = long.MinValue;
        private ulong _lastInfo;

        public int Count { get; private set; }

        public bool InOrder { get; private set; } = true;

        public bool TryAccept(ref string path, int processId, out char drive)
        {
            drive = '\0';
            return false;
        }

        public void OnBegin(PendingIo io) => InOrder = false;

        public void OnForeignBegin(ulong irp, long timestampQpc) => InOrder = false;

        public void OnPathOperation(ulong irp, long timestampQpc, string path) => InOrder = false;

        public void OnComplete(ulong irp, long timestampQpc, ulong extraInfo, int ntStatus)
        {
            if (timestampQpc < _lastQpc || (timestampQpc == _lastQpc && extraInfo < _lastInfo))
                InOrder = false;
            _lastQpc = timestampQpc;
            _lastInfo = extraInfo;
            Count++;
        }
    }
}
