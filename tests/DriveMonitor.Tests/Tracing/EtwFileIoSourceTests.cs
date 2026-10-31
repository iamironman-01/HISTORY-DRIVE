using DriveMonitor.Core;
using DriveMonitor.Core.Models;
using DriveMonitor.Core.Tracing;

namespace DriveMonitor.Tests.Tracing;

/// <summary>
/// How <see cref="EtwFileIoSource"/> puts a <see cref="ReorderBuffer"/> in front of its sink and empties it when it
/// stops. No kernel session is started, so no elevation is needed.
/// </summary>
public sealed class EtwFileIoSourceTests
{
    [Fact]
    public void ReorderWindowMs_IsTheFlushIntervalPlusAMargin_UnlessSet()
    {
        var options = new MonitorOptions();
        Assert.Equal(750, options.ReorderWindowMs);

        options.FlushIntervalMs = 1000;
        Assert.Equal(1250, options.ReorderWindowMs);

        options.FlushIntervalMs = 0; // TraceEvent's own flush, once a second
        Assert.Equal(1250, options.ReorderWindowMs);

        options.ReorderWindowMs = 0;
        options.FlushIntervalMs = 500;
        Assert.Equal(0, options.ReorderWindowMs);
    }

    [Fact]
    public void Source_MatchingCompletions_ReportsThroughAReorderBuffer()
    {
        using MonitorEngine engine = CreateEngine(new MonitorOptions());
        using EtwFileIoSource source = CreateSource(engine);

        Assert.IsType<ReorderBuffer>(source.Sink);
        Assert.Equal(0L, source.ReorderOverflows);
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(750, false)]
    public void Source_WithoutReordering_ReportsStraightToItsSink(int reorderWindowMs, bool useCompletionEvents)
    {
        // A window of 0 keeps the behaviour from before reordering: events are matched in the order they arrive.
        // Without completion events nothing is matched, so nothing is held back either.
        using MonitorEngine engine = CreateEngine(new MonitorOptions { ReorderWindowMs = reorderWindowMs, UseCompletionEvents = useCompletionEvents });
        using EtwFileIoSource source = CreateSource(engine);

        Assert.Same(engine, source.Sink);
        source.Sink.OnBegin(Read(irp: 0x10, qpc: 100, size: 4096));
        source.Sink.OnComplete(0x10, timestampQpc: 150, extraInfo: 4096, ntStatus: 0);
        Assert.Equal(1, engine.DrainEvents(new List<FileActivityEvent>())); // published at once
    }

    [Fact]
    public void Stop_PassesOnWhatTheReorderBufferHolds_AndARestartedSourceHasItsOwn()
    {
        // MonitorEngine.Stop, and the restart after a fault, stop the source before they release what still waits for
        // a completion. A read and its completion, delivered in the wrong order and still held, must be matched by then.
        using MonitorEngine engine = CreateEngine(new MonitorOptions());
        EtwFileIoSource source = CreateSource(engine);
        source.Sink.OnComplete(0x10, timestampQpc: 150, extraInfo: 4096, ntStatus: 0);
        source.Sink.OnBegin(Read(irp: 0x10, qpc: 100, size: 4096));

        source.Stop();

        var events = new List<FileActivityEvent>();
        Assert.Equal(1, engine.DrainEvents(events));
        Assert.Equal(IoStatus.Success, events[0].Status);
        Assert.Equal(4096L, events[0].Bytes);
        Assert.Equal(0, engine.PendingCompletions);

        // A consumer thread that outlived Stop: its events are not held where nothing would release them.
        source.Sink.OnBegin(Read(irp: 0x11, qpc: 200, size: 10));
        source.Sink.OnComplete(0x11, timestampQpc: 250, extraInfo: 10, ntStatus: 0);
        Assert.Equal(1, engine.DrainEvents(events));

        // The restarted session's source holds its own events, and nothing is published twice.
        using EtwFileIoSource restarted = CreateSource(engine);
        Assert.NotSame(source.Sink, restarted.Sink);
        restarted.Sink.OnComplete(0x10, timestampQpc: 350, extraInfo: 512, ntStatus: 0);
        restarted.Sink.OnBegin(Read(irp: 0x10, qpc: 300, size: 4096));
        restarted.Stop();
        Assert.Equal(1, engine.DrainEvents(events));
        Assert.Equal(new[] { 4096L, 10L, 512L }, events.Select(e => e.Bytes));
        Assert.All(events, e => Assert.Equal(IoStatus.Success, e.Status));
    }

    private static MonitorEngine CreateEngine(MonitorOptions options)
    {
        var engine = new MonitorEngine(options);
        engine.Drives.SetMonitored('E', true);
        return engine;
    }

    private static EtwFileIoSource CreateSource(MonitorEngine engine) =>
        new(engine, engine.ProcessNames, engine.Volumes.ToDosPath, engine.Options);

    private static PendingIo Read(ulong irp, long qpc, int size) => new()
    {
        Op = RawFileOp.Read,
        Irp = irp,
        TimestampQpc = qpc,
        Timestamp = new DateTime(2026, 10, 31, 12, 0, 0),
        Path = @"E:\photos\img_0042.jpg",
        Drive = 'E',
        ProcessId = 4242,
        ThreadId = 7,
        ProcessName = "viewer.exe",
        IoSize = size,
        Offset = 0,
    };
}
