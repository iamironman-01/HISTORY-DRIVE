using DriveMonitor.Core;
using DriveMonitor.Core.Models;
using DriveMonitor.Core.Tracing;
using DriveMonitor.Tests.TestSupport;

namespace DriveMonitor.Tests;

/// <summary>
/// Drives <see cref="MonitorEngine"/> through its internal <see cref="IFileIoSink"/> side, the way
/// the ETW source calls it, without starting a kernel session (so no elevation is needed). The source
/// reports through a <see cref="ReorderBuffer"/>, which passes events on in timestamp order; most tests
/// call the engine directly, as the buffer does, and the "Reordered" tests go through a buffer.
/// </summary>
public sealed class MonitorEngineSinkTests
{
    private const int OtherProcessId = 4242;
    private const string FileOnE = @"E:\photos\img_0042.jpg";
    private const int StatusObjectNameNotFound = unchecked((int)0xC0000034);
    private const int ReorderWindowMs = 750;

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
    public void Start_AfterDispose_ThrowsObjectDisposedException()
    {
        var engine = new MonitorEngine();
        int stateChanges = 0;
        engine.StateChanged += (_, _) => stateChanges++;

        engine.Dispose();

        // Checked before the elevation check, so even as administrator no kernel session is created.
        Assert.Throws<ObjectDisposedException>(engine.Start);
        Assert.False(engine.IsRunning);
        Assert.Null(engine.StartedAt);
        Assert.Equal(0, stateChanges);
    }

    [Fact]
    public void Dispose_CalledTwice_IsHarmless()
    {
        using var temp = new TempDirectory();
        var engine = new MonitorEngine();
        int stateChanges = 0;
        engine.StateChanged += (_, _) => stateChanges++;
        engine.StartLog(temp.PathOf("activity.csv"));

        engine.Dispose();
        engine.Dispose();
        engine.Stop();

        Assert.Null(engine.Log);
        Assert.False(engine.IsRunning);
        Assert.Equal(0, stateChanges);
        Assert.Throws<ObjectDisposedException>(engine.Start); // the repeated calls did not undo the first
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
    public void TryAccept_UnnamedEvents_AreCountedEvenWithNoDriveMonitored()
    {
        using var engine = new MonitorEngine();
        IFileIoSink sink = engine;

        Assert.False(Accept(sink, "", OtherProcessId, out _));
        Assert.False(Accept(sink, @"F:\named.txt", OtherProcessId, out _));

        // Without a name the drive is unknown, so the count cannot be limited to the monitored drives.
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
        Assert.False(Accept(sink, temp.PathOf("activity.002.csv"), OtherProcessId, out _)); // a later part
        Assert.True(Accept(sink, temp.PathOf("other.csv"), OtherProcessId, out _));

        engine.StopLog();

        Assert.Null(engine.Log);
        Assert.True(Accept(sink, logPath, OtherProcessId, out _));
    }

    [Fact]
    public void StartLog_AppliesTheLogLimitsOfTheOptions()
    {
        using var temp = new TempDirectory();
        string logPath = temp.PathOf("activity.csv");
        using var engine = new MonitorEngine(new MonitorOptions { MinLogFreeSpaceMB = int.MaxValue });

        Assert.Throws<IOException>(() => engine.StartLog(logPath));
        Assert.Null(engine.Log);
        Assert.False(File.Exists(logPath));
    }

    [Fact]
    public void StartLog_WithATrustedRoot_FollowsNoLinkBelowIt_AndTakesNoFileOutsideIt()
    {
        using var temp = new TempDirectory();
        string root = Directory.CreateDirectory(temp.PathOf("Local")).FullName;
        string victim = Directory.CreateDirectory(temp.PathOf("victim")).FullName;
        using IDisposable planted = FileLinks.CreateJunction(Path.Combine(root, "DriveMonitor"), victim);
        using var engine = new MonitorEngine();

        Assert.Throws<IOException>(() => engine.StartLog(Path.Combine(root, "DriveMonitor", "Logs", "activity.csv"), root));
        Assert.Throws<ArgumentException>(() => engine.StartLog(temp.PathOf("activity.csv"), root));

        Assert.Null(engine.Log);
        Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
        Assert.False(File.Exists(temp.PathOf("activity.csv")));
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
    public void PendingCompletions_IsReadWithoutWaitingForTheCorrelator()
    {
        // The UI polls it every second; the ETW thread may be holding the correlator for a long release.
        using MonitorEngine engine = CreateEngine();
        IFileIoSink sink = engine;
        int? readWhileHeld = null;
        engine.RawOperationObserver = _ =>
        {
            // Runs on this thread while the engine holds the correlator lock to publish the first read.
            Task<int> read = Task.Run(() => engine.PendingCompletions);
            if (read.Wait(TimeSpan.FromSeconds(10)))
                readWhileHeld = read.Result;
        };

        sink.OnBegin(Read(irp: 0x10, qpc: 100, size: 4096));
        sink.OnBegin(Read(irp: 0x11, qpc: 110, size: 4096));
        sink.OnComplete(0x10, timestampQpc: 150, extraInfo: 4096, ntStatus: 0);

        Assert.Equal(1, readWhileHeld); // the second read still waits
        Assert.Equal(1, engine.PendingCompletions);
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
    public void OnComplete_OfAFilteredOutOperationThatReusedTheAddress_IsNotCreditedToTheMonitoredRead()
    {
        using MonitorEngine engine = CreateEngine();
        IFileIoSink sink = engine;
        string missing = @"C:\Windows\System32\missing.dll";
        Assert.False(sink.TryAccept(ref missing, OtherProcessId, out _)); // so the source reports its start as foreign

        // qpc 110: 4 KB read on E:. qpc 120: its completion, in another CPU's buffer, delivered last.
        // qpc 130: an open of the missing C: file reuses the address. qpc 140: it fails, delivered first.
        sink.OnBegin(Read(irp: 0x10, qpc: 110, size: 4096));
        sink.OnForeignBegin(0x10, timestampQpc: 130);
        sink.OnComplete(0x10, timestampQpc: 140, extraInfo: 0, ntStatus: StatusObjectNameNotFound);
        sink.OnComplete(0x10, timestampQpc: 120, extraInfo: 4096, ntStatus: 0);

        var events = new List<FileActivityEvent>();
        Assert.Equal(1, engine.DrainEvents(events));
        FileActivityEvent ev = Assert.Single(events);
        Assert.Equal(IoStatus.Success, ev.Status);
        Assert.Equal(4096L, ev.Bytes);
        Assert.Equal(0L, engine.Stats.FailedEvents);
    }

    [Fact]
    public void OnComplete_OfAForeignReuseAfterALostCompletion_LeavesTheWriteWaiting()
    {
        using MonitorEngine engine = CreateEngine();
        IFileIoSink sink = engine;

        // The write's own completion was lost; a C: open reuses the address 10,000 ticks later and fails.
        sink.OnBegin(Write(irp: 0x10, qpc: 110, size: 65536));
        sink.OnForeignBegin(0x10, timestampQpc: 10_100);
        sink.OnComplete(0x10, timestampQpc: 10_110, extraInfo: 0, ntStatus: StatusObjectNameNotFound);

        // Still waiting, to be published as Unknown with the requested size once the hold time is over.
        Assert.Equal(0, engine.DrainEvents(new List<FileActivityEvent>()));
        Assert.Equal(1, engine.PendingCompletions);
        Assert.Equal(0L, engine.Stats.FailedEvents);
    }

    [Fact]
    public void OnBegin_OfALateOpen_DoesNotTakeTheResultOfAForeignCreate()
    {
        using MonitorEngine engine = CreateEngine();
        IFileIoSink sink = engine;

        // A create on C: reused the address after the open on E: and its completion (a new file) arrived
        // first; the open and its own completion sit in a buffer flushed later.
        sink.OnForeignBegin(0x30, timestampQpc: 250);
        sink.OnComplete(0x30, timestampQpc: 300, extraInfo: FileIoClassifier.FileCreated, ntStatus: 0);
        sink.OnBegin(Open(irp: 0x30, qpc: 100));
        sink.OnComplete(0x30, timestampQpc: 150, extraInfo: FileIoClassifier.FileOpened, ntStatus: 0);

        var events = new List<FileActivityEvent>();
        Assert.Equal(1, engine.DrainEvents(events));
        FileActivityEvent ev = Assert.Single(events);
        Assert.Equal(FileOperation.Open, ev.Operation);
        Assert.Equal(IoStatus.Success, ev.Status);
        Assert.Null(ev.Detail);
    }

    [Fact]
    public void OnPathOperation_OfAForeignRenameThatReusedTheAddress_IsNotTheRenameTarget()
    {
        using MonitorEngine engine = CreateEngine();
        IFileIoSink sink = engine;
        sink.OnPathOperation(0x99, timestampQpc: 1, @"E:\elsewhere.txt"); // Windows sends path events

        sink.OnBegin(Rename(irp: 0x20, qpc: 100));
        sink.OnForeignBegin(0x20, timestampQpc: 130); // a rename on C: reuses the address
        sink.OnPathOperation(0x20, timestampQpc: 140, @"C:\Users\someone\renamed.txt");
        sink.OnPathOperation(0x20, timestampQpc: 120, @"E:\photos\renamed.jpg");
        sink.OnComplete(0x20, timestampQpc: 125, extraInfo: 0, ntStatus: 0);

        var events = new List<FileActivityEvent>();
        Assert.Equal(1, engine.DrainEvents(events));
        Assert.Equal(@"E:\photos\renamed.jpg", Assert.Single(events).TargetPath);
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

    // ---- events delivered out of timestamp order, put back in order by the reorder buffer ----

    [Fact]
    public void Reordered_CompletionOfANewerUseArrivingFirst_GoesToTheLaterRead()
    {
        // Two reads on E: use address 0x10 in turn: the first starts at qpc 100 and completes at 200 with 1 KB, the
        // second starts at 300 and completes at 400 with 2 KB. The second completion sits in the buffer Windows hands
        // over first. In arrival order the first read took it and the second one ended Unknown.
        using MonitorEngine engine = CreateEngine();
        using ReorderBuffer buffer = Reordering(engine);
        buffer.OnComplete(0x10, timestampQpc: 400, extraInfo: 2048, ntStatus: 0, nowTick: 0);
        buffer.OnBegin(Read(irp: 0x10, qpc: 100, size: 4096), nowTick: 400);
        buffer.OnBegin(Read(irp: 0x10, qpc: 300, size: 4096), nowTick: 400);
        buffer.OnComplete(0x10, timestampQpc: 200, extraInfo: 1024, ntStatus: 0, nowTick: 400);

        var events = new List<FileActivityEvent>();
        Assert.Equal(0, engine.DrainEvents(events)); // still held
        buffer.Release(nowTick: 400 + ReorderWindowMs);

        Assert.Equal(2, engine.DrainEvents(events));
        Assert.Equal(new[] { 1024L, 2048L }, events.Select(e => e.Bytes));
        Assert.All(events, e => Assert.Equal(IoStatus.Success, e.Status));
        Assert.Equal(0, engine.PendingCompletions);
    }

    [Fact]
    public void Reordered_LateOpen_DoesNotTakeTheResultOfAForeignCreateWhoseStartArrivesAfterIt()
    {
        // An open on E: (qpc 100, address 0x30) completes at 150: the file existed. A create on C: then reuses the
        // address at 250 and makes a new file at 300. That result arrives first; the open, the create's start and the
        // open's own completion come in a buffer flushed later. In arrival order the open took the create's result
        // and was reported as "created".
        using MonitorEngine engine = CreateEngine();
        using ReorderBuffer buffer = Reordering(engine);
        buffer.OnComplete(0x30, timestampQpc: 300, extraInfo: FileIoClassifier.FileCreated, ntStatus: 0, nowTick: 0);
        buffer.OnBegin(Open(irp: 0x30, qpc: 100), nowTick: 400);
        buffer.OnForeignBegin(0x30, timestampQpc: 250, nowTick: 400);
        buffer.OnComplete(0x30, timestampQpc: 150, extraInfo: FileIoClassifier.FileOpened, ntStatus: 0, nowTick: 400);

        buffer.Release(nowTick: 400 + ReorderWindowMs);

        var events = new List<FileActivityEvent>();
        Assert.Equal(1, engine.DrainEvents(events));
        FileActivityEvent ev = Assert.Single(events);
        Assert.Equal(FileOperation.Open, ev.Operation);
        Assert.Equal(IoStatus.Success, ev.Status);
        Assert.Null(ev.Detail);
    }

    [Fact]
    public void Reordered_LateRead_DoesNotTakeTheFailureOfAForeignReuseWhoseStartArrivesAfterIt()
    {
        // A 4 KB read on E: (qpc 110, address 0x10) completes at 120. An open of a missing C: file reuses the address
        // at 130 and fails at 140. The failure arrives first; the read, the open's start and the read's completion come
        // in a buffer flushed later. In arrival order the read was published as failed with 0 bytes.
        using MonitorEngine engine = CreateEngine();
        using ReorderBuffer buffer = Reordering(engine);
        buffer.OnComplete(0x10, timestampQpc: 140, extraInfo: 0, ntStatus: StatusObjectNameNotFound, nowTick: 0);
        buffer.OnBegin(Read(irp: 0x10, qpc: 110, size: 4096), nowTick: 400);
        buffer.OnForeignBegin(0x10, timestampQpc: 130, nowTick: 400);
        buffer.OnComplete(0x10, timestampQpc: 120, extraInfo: 4096, ntStatus: 0, nowTick: 400);

        buffer.Release(nowTick: 400 + ReorderWindowMs);

        var events = new List<FileActivityEvent>();
        Assert.Equal(1, engine.DrainEvents(events));
        FileActivityEvent ev = Assert.Single(events);
        Assert.Equal(IoStatus.Success, ev.Status);
        Assert.Equal(4096L, ev.Bytes);
        Assert.Equal(0L, engine.Stats.FailedEvents);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Reordered_ReadAndItsCompletionLoggedInTheSameTick_AreMatchedWhicheverArrivesFirst(bool completionFirst)
    {
        // Equal timestamps keep their arrival order, and the correlator counts a completion logged in the same tick as
        // its start as logged at or after it, whether it meets the start directly or in the early store.
        using MonitorEngine engine = CreateEngine();
        using ReorderBuffer buffer = Reordering(engine);
        if (completionFirst)
            buffer.OnComplete(0x10, timestampQpc: 100, extraInfo: 4096, ntStatus: 0, nowTick: 0);
        buffer.OnBegin(Read(irp: 0x10, qpc: 100, size: 4096), nowTick: 0);
        if (!completionFirst)
            buffer.OnComplete(0x10, timestampQpc: 100, extraInfo: 4096, ntStatus: 0, nowTick: 0);

        buffer.Release(nowTick: ReorderWindowMs);

        var events = new List<FileActivityEvent>();
        Assert.Equal(1, engine.DrainEvents(events));
        Assert.Equal(IoStatus.Success, events[0].Status);
        Assert.Equal(4096L, events[0].Bytes);
    }

    [Fact]
    public void Reordered_ReadDeliveredLaterThanTheWindow_StillTakesItsCompletion()
    {
        // A flush delayed on a busy system: the completion was passed on long before its read arrived. The read is passed
        // on at once, and the correlator's early store, kept as the safety net, pairs them.
        using MonitorEngine engine = CreateEngine();
        using ReorderBuffer buffer = Reordering(engine);
        buffer.OnComplete(0x10, timestampQpc: 150, extraInfo: 4096, ntStatus: 0, nowTick: 0);
        buffer.Release(nowTick: ReorderWindowMs);
        Assert.Equal(0, buffer.Count);

        buffer.OnBegin(Read(irp: 0x10, qpc: 100, size: 4096), nowTick: ReorderWindowMs + 400);

        var events = new List<FileActivityEvent>();
        Assert.Equal(1, engine.DrainEvents(events));
        Assert.Equal(IoStatus.Success, events[0].Status);
        Assert.Equal(4096L, events[0].Bytes);
    }

    [Fact]
    public void Reordered_ShuffledDeliveryOnRecycledAddresses_MatchesEveryReadWithItsOwnCompletion()
    {
        // Three request addresses serve 6,000 operations in turn, each one used about every 30 µs; half the operations
        // are reads on E:, half are on other drives and fail. Each processor's events stay in order, but its buffer
        // arrives up to 499 ms late, within the 750 ms window. 100 qpc = 1 ms.
        var random = new Random(20261031);
        var logged = new List<(long Qpc, ulong Irp, int Op, bool IsStart, bool Monitored)>();
        var inUse = new (int Op, bool Monitored)?[3];
        long qpc = 0;
        int operations = 0;
        while (operations < 6000 || inUse.Any(use => use is not null))
        {
            qpc += random.Next(1, 20);
            int slot = random.Next(inUse.Length);
            ulong irp = 0xFFFF_9A00_0000_1000UL + ((ulong)slot * 0x100);
            if (inUse[slot] is { } use)
            {
                logged.Add((qpc, irp, use.Op, false, use.Monitored));
                inUse[slot] = null;
            }
            else if (operations < 6000)
            {
                bool monitored = random.Next(2) == 0;
                logged.Add((qpc, irp, operations, true, monitored));
                inUse[slot] = (operations++, monitored);
            }
        }

        var lastArrival = new long[4];
        var arrivals = new List<(long Tick, int Index)>();
        for (int i = 0; i < logged.Count; i++)
        {
            int cpu = random.Next(lastArrival.Length);
            lastArrival[cpu] = Math.Max(lastArrival[cpu], (logged[i].Qpc / 100) + random.Next(500));
            arrivals.Add((lastArrival[cpu], i));
        }

        using MonitorEngine engine = CreateEngine();
        using ReorderBuffer buffer = Reordering(engine);
        foreach ((long tick, int index) in arrivals.OrderBy(a => a.Tick))
        {
            (long at, ulong irp, int op, bool isStart, bool monitored) = logged[index];
            if (isStart && monitored)
                buffer.OnBegin(Copy(irp, at, op), tick);
            else if (isStart)
                buffer.OnForeignBegin(irp, at, tick);
            else if (monitored)
                buffer.OnComplete(irp, at, extraInfo: (ulong)op + 1, ntStatus: 0, tick);
            else
                buffer.OnComplete(irp, at, extraInfo: 0, ntStatus: StatusObjectNameNotFound, tick);
        }

        buffer.Release(lastArrival.Max() + ReorderWindowMs);

        var events = new List<FileActivityEvent>();
        engine.DrainEvents(events);
        Assert.Equal(logged.Count(e => e.IsStart && e.Monitored), events.Count);
        Assert.All(events, ev =>
        {
            Assert.Equal(IoStatus.Success, ev.Status);
            Assert.Equal($@"E:\copy\{ev.Bytes - 1}.bin", ev.Path); // its own completion carried its number
        });
        Assert.Equal(0, engine.PendingCompletions);
    }

    /// <summary>A read of 1 MiB on E: whose path carries its number.</summary>
    private static PendingIo Copy(ulong irp, long qpc, int op)
    {
        PendingIo io = Read(irp, qpc, size: 1 << 20);
        io.Path = $@"E:\copy\{op}.bin";
        return io;
    }

    /// <summary>
    /// The engine behind a reorder buffer, the way <see cref="EtwFileIoSource"/> reports to it; the tests pass the time
    /// themselves.
    /// </summary>
    private static ReorderBuffer Reordering(MonitorEngine engine) =>
        new(engine, ReorderWindowMs, error => Assert.Fail($"The engine threw: {error}"), releaseOnTimer: false);

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

    private static PendingIo Write(ulong irp, long qpc, int size)
    {
        PendingIo io = Read(irp, qpc, size);
        io.Op = RawFileOp.Write;
        io.ProcessName = "robocopy.exe";
        return io;
    }

    private static PendingIo Open(ulong irp, long qpc) => new()
    {
        Op = RawFileOp.Create,
        Irp = irp,
        TimestampQpc = qpc,
        Timestamp = new DateTime(2026, 10, 30, 12, 0, 0),
        Path = FileOnE,
        Drive = 'E',
        ProcessId = OtherProcessId,
        ThreadId = 7,
        ProcessName = "viewer.exe",
        CreateDisposition = FileIoClassifier.FileOpenIf,
    };
}
