using DriveMonitor.Core;
using DriveMonitor.Core.Models;
using DriveMonitor.Core.Tracing;
using DriveMonitor.Tests.TestSupport;

namespace DriveMonitor.Tests;

/// <summary>
/// Drives <see cref="MonitorEngine"/> through its internal <see cref="IFileIoSink"/> side, the way
/// the ETW source calls it, without starting a kernel session (so no elevation is needed).
/// </summary>
public sealed class MonitorEngineSinkTests
{
    private const int OtherProcessId = 4242;
    private const string FileOnE = @"E:\photos\img_0042.jpg";

    [Fact]
    public void NewEngine_IsIdle()
    {
        using var engine = new MonitorEngine();

        Assert.False(engine.IsRunning);
        Assert.Null(engine.StartedAt);
        Assert.Null(engine.Log);
        Assert.Equal(0L, engine.PublishedEvents);
        Assert.Equal(0L, engine.KernelEvents);
        Assert.Equal(0, engine.LostEvents);
        Assert.Equal(0, engine.PendingCompletions);
        Assert.Empty(engine.Drives.MonitoredDrives);
    }

    [Fact]
    public void StopAndDispose_WithoutStart_AreHarmless()
    {
        var engine = new MonitorEngine();
        int stateChanges = 0;
        engine.StateChanged += (_, _) => stateChanges++;

        engine.Stop();
        engine.Dispose();
        engine.Dispose();

        Assert.False(engine.IsRunning);
        Assert.Equal(0, stateChanges);
    }

    [Fact]
    public void TryAccept_PathOnAMonitoredDrive_ReturnsTheDrive()
    {
        using MonitorEngine engine = CreateEngine();
        IFileIoSink sink = engine;

        Assert.True(Accept(sink, @"e:\photos\a.jpg", OtherProcessId, out char drive));
        Assert.Equal('E', drive);
        Assert.False(Accept(sink, @"F:\other.txt", OtherProcessId, out _));
        Assert.Equal(0L, engine.UnnamedEvents);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryAccept_EventWithoutAFileName_IsCountedAsUnnamed(string? path)
    {
        using MonitorEngine engine = CreateEngine();
        IFileIoSink sink = engine;

        Assert.False(Accept(sink, path, OtherProcessId, out _));
        Assert.Equal(1L, engine.UnnamedEvents);
    }

    [Fact]
    public void TryAccept_OwnProcess_IsExcludedUnlessThatIsSwitchedOff()
    {
        using MonitorEngine engine = CreateEngine();
        IFileIoSink sink = engine;

        Assert.False(Accept(sink, FileOnE, Environment.ProcessId, out _));

        engine.Options.ExcludeOwnProcess = false;

        Assert.True(Accept(sink, FileOnE, Environment.ProcessId, out _));
    }

    [Fact]
    public void TryAccept_TheActivityLogFile_IsNeverReported()
    {
        using var temp = new TempDirectory();
        string logPath = temp.PathOf("activity.csv");
        using MonitorEngine engine = CreateEngine();
        engine.Drives.SetMonitored(logPath[0], true);
        IFileIoSink sink = engine;

        engine.StartLog(logPath);

        Assert.NotNull(engine.Log);
        Assert.False(Accept(sink, logPath, OtherProcessId, out _));
        Assert.False(Accept(sink, logPath.ToUpperInvariant(), OtherProcessId, out _));
        Assert.True(Accept(sink, temp.PathOf("other.csv"), OtherProcessId, out _));

        engine.StopLog();

        Assert.Null(engine.Log);
        Assert.True(Accept(sink, logPath, OtherProcessId, out _));
    }

    [Fact]
    public void OnBegin_WithoutCompletionEvents_PublishesAtOnce()
    {
        using MonitorEngine engine = CreateEngine(new MonitorOptions { UseCompletionEvents = false });
        IFileIoSink sink = engine;

        sink.OnBegin(Read(irp: 0x10, qpc: 100, size: 4096));

        var events = new List<FileActivityEvent>();
        Assert.Equal(1, engine.DrainEvents(events));
        FileActivityEvent ev = Assert.Single(events);
        Assert.Equal(1L, ev.Sequence);
        Assert.Equal(FileOperation.Read, ev.Operation);
        Assert.Equal(IoStatus.Unknown, ev.Status);
        Assert.Equal(4096L, ev.Bytes);
        Assert.Equal("viewer.exe", ev.ProcessName);
        Assert.Equal(1L, engine.PublishedEvents);
        Assert.Equal(0, engine.PendingCompletions);
        Assert.Equal(4096L, Assert.Single(engine.Stats.GetAllFiles()).BytesRead);
    }

    [Fact]
    public void OnBegin_WaitsForTheCompletion()
    {
        using MonitorEngine engine = CreateEngine();
        IFileIoSink sink = engine;
        var events = new List<FileActivityEvent>();

        sink.OnBegin(Read(irp: 0x10, qpc: 100, size: 4096));

        Assert.Equal(0, engine.DrainEvents(events));
        Assert.Equal(1, engine.PendingCompletions);

        sink.OnComplete(0x10, timestampQpc: 150, extraInfo: 1000, ntStatus: 0);

        Assert.Equal(1, engine.DrainEvents(events));
        FileActivityEvent ev = Assert.Single(events);
        Assert.Equal(IoStatus.Success, ev.Status);
        Assert.Equal(1000L, ev.Bytes);
        Assert.Equal(0, engine.PendingCompletions);
    }

    [Fact]
    public void OnComplete_ArrivingBeforeItsBegin_IsStillMatched()
    {
        using MonitorEngine engine = CreateEngine();
        IFileIoSink sink = engine;

        sink.OnComplete(0x10, timestampQpc: 150, extraInfo: 0, ntStatus: unchecked((int)0xC0000185));
        sink.OnBegin(Read(irp: 0x10, qpc: 100, size: 4096));

        var events = new List<FileActivityEvent>();
        Assert.Equal(1, engine.DrainEvents(events));
        FileActivityEvent ev = Assert.Single(events);
        Assert.Equal(IoStatus.Failed, ev.Status);
        Assert.Equal("device I/O error", ev.Detail);
        Assert.Equal(0L, ev.Bytes);
        Assert.Equal(1L, engine.Stats.FailedEvents);
    }

    [Fact]
    public void Publish_BeyondMaxQueuedEvents_DropsTheOldest()
    {
        using MonitorEngine engine = CreateEngine(new MonitorOptions { UseCompletionEvents = false, MaxQueuedEvents = 2 });
        IFileIoSink sink = engine;

        for (int i = 0; i < 3; i++)
            sink.OnBegin(Read(irp: 0, qpc: i, size: 10));

        Assert.Equal(3L, engine.PublishedEvents);
        Assert.Equal(1L, engine.DroppedEvents);
        var events = new List<FileActivityEvent>();
        Assert.Equal(2, engine.DrainEvents(events));
        Assert.Equal(new[] { 2L, 3L }, events.Select(e => e.Sequence));
        Assert.Equal(3L, Assert.Single(engine.Stats.GetAllFiles()).Reads); // statistics still saw every event
    }

    [Fact]
    public void DrainEvents_TakesAtMostMax()
    {
        using MonitorEngine engine = CreateEngine(new MonitorOptions { UseCompletionEvents = false });
        IFileIoSink sink = engine;
        for (int i = 0; i < 3; i++)
            sink.OnBegin(Read(irp: 0, qpc: i, size: 10));
        var events = new List<FileActivityEvent>();

        Assert.Equal(2, engine.DrainEvents(events, max: 2));
        Assert.Equal(1, engine.DrainEvents(events, max: 2));
        Assert.Equal(0, engine.DrainEvents(events, max: 2));
        Assert.Equal(new[] { 1L, 2L, 3L }, events.Select(e => e.Sequence));
    }

    [Fact]
    public void RawOperationObserver_SeesOperationsThatPublishNothing()
    {
        using MonitorEngine engine = CreateEngine(new MonitorOptions { UseCompletionEvents = false });
        var seen = new List<PendingIo>();
        engine.RawOperationObserver = seen.Add;
        IFileIoSink sink = engine;
        var pointerMove = new PendingIo
        {
            Op = RawFileOp.SetInfo,
            InfoClass = FileIoClassifier.FilePositionInformation,
            Path = FileOnE,
            Drive = 'E',
        };

        sink.OnBegin(pointerMove);

        Assert.Same(pointerMove, Assert.Single(seen));
        Assert.Equal(0L, engine.PublishedEvents);
        Assert.Equal(0, engine.DrainEvents(new List<FileActivityEvent>()));
        Assert.Empty(engine.Stats.GetAllFiles());
    }

    [Fact]
    public void ClearStatistics_DiscardsTotalsAndUndrainedEvents()
    {
        using MonitorEngine engine = CreateEngine(new MonitorOptions { UseCompletionEvents = false });
        IFileIoSink sink = engine;
        sink.OnBegin(Read(irp: 0, qpc: 1, size: 10));
        sink.OnBegin(Read(irp: 0, qpc: 2, size: 10));

        engine.ClearStatistics();

        Assert.Equal(0, engine.DrainEvents(new List<FileActivityEvent>()));
        Assert.Empty(engine.Stats.GetAllFiles());
        Assert.Equal(2L, engine.PublishedEvents); // a running count, not a statistic
    }

    [Fact]
    public void TryAccept_KernelPathOfAKnownVolume_IsRewrittenToADosPath()
    {
        using var engine = new MonitorEngine();
        char system = DriveMonitor.Core.Volumes.DriveCatalog.SystemDriveLetter;
        string? device = engine.Volumes.GetDevicePath(system);
        Assert.NotNull(device); // the Windows drive always has a kernel device
        engine.Drives.SetMonitored(system, true);
        IFileIoSink sink = engine;
        string path = device + @"\folder\file.txt";

        Assert.True(sink.TryAccept(ref path, OtherProcessId, out char drive));
        Assert.Equal(system, drive);
        Assert.Equal($@"{system}:\folder\file.txt", path);
    }

    [Fact]
    public void OnPathOperation_AttachesTheRenameTarget()
    {
        using MonitorEngine engine = CreateEngine();
        IFileIoSink sink = engine;

        sink.OnBegin(Rename(irp: 0x20, qpc: 100));
        sink.OnPathOperation(0x20, timestampQpc: 120, @"E:\photos\renamed.jpg");
        sink.OnComplete(0x20, timestampQpc: 150, extraInfo: 0, ntStatus: 0);

        var events = new List<FileActivityEvent>();
        Assert.Equal(1, engine.DrainEvents(events));
        FileActivityEvent ev = Assert.Single(events);
        Assert.Equal(FileOperation.Rename, ev.Operation);
        Assert.Equal(@"E:\photos\renamed.jpg", ev.TargetPath);
        Assert.Equal(@"→ E:\photos\renamed.jpg", ev.Detail);
        Assert.True(engine.PathOperationsSeen);
        Assert.Equal(@"E:\photos\renamed.jpg", Assert.Single(engine.Stats.GetAllFiles()).RenamedTo);
    }

    [Fact]
    public void OnBegin_OnceRenamePathsAreSeen_HoldsARenameUntilItsPathArrives()
    {
        using MonitorEngine engine = CreateEngine();
        IFileIoSink sink = engine;
        var events = new List<FileActivityEvent>();
        sink.OnPathOperation(0x99, timestampQpc: 1, @"E:\elsewhere.txt"); // Windows sends path events

        sink.OnBegin(Rename(irp: 0x30, qpc: 100));
        sink.OnComplete(0x30, timestampQpc: 150, extraInfo: 0, ntStatus: 0);

        Assert.Equal(0, engine.DrainEvents(events)); // complete, but its path is still due

        sink.OnPathOperation(0x30, timestampQpc: 160, @"E:\$RECYCLE.BIN\S-1-5-21\$R1.jpg");

        Assert.Equal(1, engine.DrainEvents(events));
        Assert.Equal("moved to the Recycle Bin", Assert.Single(events).Detail);
    }

    [Fact]
    public void OnPathOperation_ArrivingBeforeItsRename_IsStillMatched()
    {
        using MonitorEngine engine = CreateEngine();
        IFileIoSink sink = engine;

        sink.OnPathOperation(0x40, timestampQpc: 120, @"E:\photos\new.jpg");
        sink.OnBegin(Rename(irp: 0x40, qpc: 100));
        sink.OnComplete(0x40, timestampQpc: 150, extraInfo: 0, ntStatus: 0);

        var events = new List<FileActivityEvent>();
        Assert.Equal(1, engine.DrainEvents(events));
        Assert.Equal(@"E:\photos\new.jpg", Assert.Single(events).TargetPath);
    }

    private static MonitorEngine CreateEngine(MonitorOptions? options = null)
    {
        var engine = new MonitorEngine(options);
        engine.Drives.SetMonitored('E', true);
        return engine;
    }

    /// <summary>The ETW source never passes null (unnamed events arrive as ""); null here stands for that.</summary>
    private static bool Accept(IFileIoSink sink, string? path, int processId, out char drive)
    {
        string value = path ?? "";
        return sink.TryAccept(ref value, processId, out drive);
    }

    private static PendingIo Rename(ulong irp, long qpc) => new()
    {
        Op = RawFileOp.Rename,
        Irp = irp,
        TimestampQpc = qpc,
        Timestamp = new DateTime(2026, 10, 30, 12, 0, 0),
        Path = FileOnE,
        Drive = 'E',
        ProcessId = OtherProcessId,
        ThreadId = 7,
        ProcessName = "explorer.exe",
        InfoClass = FileIoClassifier.FileRenameInformationEx,
    };

    private static PendingIo Read(ulong irp, long qpc, int size) => new()
    {
        Op = RawFileOp.Read,
        Irp = irp,
        TimestampQpc = qpc,
        Timestamp = new DateTime(2026, 10, 30, 12, 0, 0),
        Path = FileOnE,
        Drive = 'E',
        ProcessId = OtherProcessId,
        ThreadId = 7,
        ProcessName = "viewer.exe",
        IoSize = size,
        Offset = 0,
    };
}
