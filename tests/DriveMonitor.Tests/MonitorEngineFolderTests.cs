using DriveMonitor.Core;
using DriveMonitor.Core.Aggregation;
using DriveMonitor.Core.Export;
using DriveMonitor.Core.Models;
using DriveMonitor.Core.Tracing;
using DriveMonitor.Tests.TestSupport;

namespace DriveMonitor.Tests;

/// <summary>
/// The Directory flag <see cref="MonitorEngine"/> gives a folder's delete or rename, which Windows reports without
/// it. Driven through the engine's <see cref="IFileIoSink"/> side, as in <see cref="MonitorEngineSinkTests"/>: each
/// operation's completion (and a rename's new path) arrives right after it starts, so it is published at once.
/// </summary>
public sealed class MonitorEngineFolderTests
{
    private const int OtherProcessId = 4242;
    private const string Folder = @"E:\photos";
    private const string NewName = @"E:\pictures";
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int StatusDirectoryNotEmpty = unchecked((int)0xC0000101);
    private const int StatusNotADirectory = unchecked((int)0xC0000103);

    private ulong _irp = 0x1000;
    private long _qpc = 100;
    private DateTime _time = new(2026, 10, 30, 12, 0, 0);

    [Theory]
    [InlineData(FileOperation.Open, FileOperation.Delete)] // RemoveDirectoryW: an open as a folder, then a disposition
    [InlineData(FileOperation.Open, FileOperation.Rename)]
    [InlineData(FileOperation.List, FileOperation.Delete)]
    [InlineData(FileOperation.List, FileOperation.Rename)] // Explorer moving a folder it showed
    public void FolderSeenAsAFolder_IsPublishedAsAFolderWhenDeletedOrRenamed(FileOperation seenAs, FileOperation change)
    {
        using MonitorEngine engine = CreateEngine();
        FileActivityEvent seen = seenAs == FileOperation.Open ? OpenFolder(engine, Folder) : List(engine, Folder);
        Assert.Equal(seenAs, seen.Operation);

        FileActivityEvent ev;
        if (change == FileOperation.Delete)
        {
            ev = Delete(engine, Folder);
        }
        else
        {
            Assert.False(OpenEither(engine, Folder).IsDirectory); // how MoveFileEx opens the source
            ev = Rename(engine, Folder, NewName);
        }

        Assert.Equal(change, ev.Operation);
        Assert.Equal(IoStatus.Success, ev.Status);
        Assert.Equal(FileEventFlags.Directory, ev.Flags);
        Assert.True(FileStats(engine, Folder).IsDirectory); // the statistics agree
    }

    [Fact]
    public void FolderDelete_IsLoggedWithTheFolderFlag_AndAFileLaterAtItsPathWithout()
    {
        using var temp = new TempDirectory();
        string logPath = temp.PathOf("activity.csv");
        using MonitorEngine engine = CreateEngine();
        engine.StartLog(logPath);

        OpenFolder(engine, Folder);
        Delete(engine, Folder);
        CreateFile(engine, Folder);
        Delete(engine, Folder);
        engine.StopLog();

        List<string[]> records = Rfc4180.Parse(File.ReadAllText(logPath));
        Assert.Equal(Csv.EventHeader, records[0]);
        int operation = Array.IndexOf(Csv.EventHeader, "Operation");
        int flags = Array.IndexOf(Csv.EventHeader, "Flags");
        Assert.Equal(
            new[] { ("Open", "Directory"), ("Delete", "Directory"), ("Create", ""), ("Delete", "") },
            records.Skip(1).Select(record => (record[operation], record[flags])));
    }

    [Theory]
    [InlineData(FileOperation.Delete)]
    [InlineData(FileOperation.Rename)]
    public void FileThatTakesTheFoldersPathLater_IsNotPublishedAsAFolder(FileOperation folderLeftBy)
    {
        using MonitorEngine engine = CreateEngine();
        OpenFolder(engine, Folder);
        FileActivityEvent left = folderLeftBy == FileOperation.Delete ? Delete(engine, Folder) : Rename(engine, Folder, NewName);
        Assert.True(left.IsDirectory);

        Assert.False(CreateFile(engine, Folder).IsDirectory);
        Assert.False(Rename(engine, Folder, @"E:\photos.txt").IsDirectory);
        Assert.False(Delete(engine, @"E:\photos.txt").IsDirectory);

        Assert.False(FileStats(engine, Folder).IsDirectory); // the statistics agree: the path holds a file now
    }

    [Fact]
    public void FolderDeleted_IsForgotten()
    {
        using MonitorEngine engine = CreateEngine();
        List(engine, Folder);
        Assert.True(engine.Folders.IsFolder(Folder));

        Assert.True(Delete(engine, Folder).IsDirectory);

        Assert.False(engine.Folders.IsFolder(Folder));
        Assert.Equal(0, engine.Folders.Count);
    }

    [Fact]
    public void RenamedFolder_IsKnownAtItsNewPath()
    {
        const string recycled = @"E:\$RECYCLE.BIN\S-1-5-21-1-2-3-1001\$R4QX2ZB";
        using MonitorEngine engine = CreateEngine();
        List(engine, Folder);

        FileActivityEvent renamed = Rename(engine, Folder, NewName);

        Assert.True(renamed.IsDirectory);
        Assert.Equal(NewName, renamed.TargetPath);
        Assert.False(engine.Folders.IsFolder(Folder));
        Assert.True(engine.Folders.IsFolder(NewName));

        // Explorer then moves it to the Recycle Bin without opening it as a folder first.
        OpenEither(engine, NewName);
        FileActivityEvent recycledEvent = Rename(engine, NewName, recycled);

        Assert.True(recycledEvent.IsDirectory);
        Assert.Equal("moved to the Recycle Bin", recycledEvent.Detail);
        Assert.True(FileStats(engine, NewName).IsDirectory);
        Assert.True(engine.Folders.IsFolder(recycled));
    }

    [Fact]
    public void RenameWithoutANewPath_ForgetsTheFolder()
    {
        // Windows 10 sends no path events, so a rename never learns where the folder went.
        using MonitorEngine engine = CreateEngine();
        OpenFolder(engine, Folder);

        FileActivityEvent renamed = Rename(engine, Folder, target: null);

        Assert.True(renamed.IsDirectory);
        Assert.Null(renamed.TargetPath);
        Assert.Equal(0, engine.Folders.Count);
    }

    [Theory]
    [InlineData(FileOperation.Delete, StatusDirectoryNotEmpty)]
    [InlineData(FileOperation.Rename, StatusAccessDenied)] // a file inside is open
    public void FolderDeleteOrRenameThatFails_IsAFolderAndLeavesItWhereItWas(FileOperation change, int status)
    {
        using MonitorEngine engine = CreateEngine();
        OpenFolder(engine, Folder);

        FileActivityEvent refused = change == FileOperation.Delete
            ? Delete(engine, Folder, status)
            : Rename(engine, Folder, NewName, status);

        Assert.Equal(IoStatus.Failed, refused.Status);
        Assert.True(refused.IsDirectory);
        Assert.True(engine.Folders.IsFolder(Folder));
        Assert.False(engine.Folders.IsFolder(NewName));

        Assert.True(Delete(engine, Folder).IsDirectory); // tried again once emptied
    }

    [Fact]
    public void FolderOpenThatFails_TeachesNothing()
    {
        using MonitorEngine engine = CreateEngine();

        // FILE_DIRECTORY_FILE on a file fails with STATUS_NOT_A_DIRECTORY.
        FileActivityEvent open = OpenFolder(engine, Folder, StatusNotADirectory);

        Assert.Equal(IoStatus.Failed, open.Status);
        Assert.Equal(0, engine.Folders.Count);
        Assert.False(Delete(engine, Folder).IsDirectory);
    }

    [Theory]
    [InlineData(FileOperation.Create)]
    [InlineData(FileOperation.Open)]
    [InlineData(FileOperation.Write)]
    public void KnownFolderPathUsedAsAFile_IsNoLongerAFolder(FileOperation usedAs)
    {
        using MonitorEngine engine = CreateEngine();

        // The folder went away unseen and a file took its path; the statistics conclude the same.
        List(engine, Folder);
        Assert.True(engine.Folders.IsFolder(Folder));
        FileActivityEvent used = usedAs switch
        {
            FileOperation.Create => CreateFile(engine, Folder),
            FileOperation.Open => OpenFile(engine, Folder),
            _ => Write(engine, Folder),
        };
        Assert.Equal(usedAs, used.Operation);
        Assert.False(engine.Folders.IsFolder(Folder));

        Assert.False(Delete(engine, Folder).IsDirectory);
        Assert.False(FileStats(engine, Folder).IsDirectory);
    }

    [Fact]
    public void KnownFolder_StaysKnownThroughPagingIoMetadataChangesAndOpensThatTakeEitherType()
    {
        using MonitorEngine engine = CreateEngine();
        List(engine, Folder);

        PagingRead(engine, Folder);
        SetTimestamps(engine, Folder);
        OpenEither(engine, Folder);

        Assert.True(Rename(engine, Folder, NewName).IsDirectory);
        Assert.True(FileStats(engine, Folder).IsDirectory);
    }

    [Fact]
    public void KnownFolders_AreLimitedToMaxTrackedFiles_KeepingTheLatest()
    {
        using MonitorEngine engine = CreateEngine(new MonitorOptions { MaxTrackedFiles = 2 });

        foreach (string folder in new[] { @"E:\f1", @"E:\f2", @"E:\f2", @"E:\f3" })
            OpenFolder(engine, folder);

        Assert.Equal(2, engine.Folders.MaxFolders);
        Assert.Equal(1, engine.Folders.Count); // started over when f3 did not fit
        Assert.True(Delete(engine, @"E:\f3").IsDirectory);
        Assert.False(Delete(engine, @"E:\f1").IsDirectory);
    }

    [Fact]
    public void ClearStatistics_ForgetsKnownFolders()
    {
        using MonitorEngine engine = CreateEngine();
        OpenFolder(engine, Folder);
        Assert.Equal(1, engine.Folders.Count);

        engine.ClearStatistics();

        Assert.Equal(0, engine.Folders.Count);
        Assert.False(Delete(engine, Folder).IsDirectory);
    }

    [Fact]
    public void WithoutCompletionEvents_AnUnknownOutcomeCountsAsDone()
    {
        using MonitorEngine engine = CreateEngine(new MonitorOptions { UseCompletionEvents = false });
        OpenFolder(engine, Folder);

        FileActivityEvent deleted = Delete(engine, Folder);

        Assert.Equal(IoStatus.Unknown, deleted.Status);
        Assert.True(deleted.IsDirectory);
        Assert.Equal(0, engine.Folders.Count);
    }

    [Fact]
    public void FolderOpen_TeachesTheEngineEvenWhenNobodyGetsItsEvent()
    {
        using MonitorEngine engine = CreateEngine(new MonitorOptions { UseCompletionEvents = false, MaxQueuedEvents = 1 });
        IFileIoSink sink = engine;

        sink.OnBegin(FolderOpenIo(Folder));
        sink.OnBegin(DeleteIo(Folder)); // nobody drained, so the open's event is dropped

        var events = new List<FileActivityEvent>();
        Assert.Equal(1, engine.DrainEvents(events));
        Assert.Equal(1L, engine.DroppedEvents);
        FileActivityEvent deleted = Assert.Single(events);
        Assert.Equal(FileOperation.Delete, deleted.Operation);
        Assert.True(deleted.IsDirectory);
    }

    private static MonitorEngine CreateEngine(MonitorOptions? options = null)
    {
        var engine = new MonitorEngine(options);
        engine.Drives.SetMonitored('E', true);
        return engine;
    }

    private static FileStatsSnapshot FileStats(MonitorEngine engine, string path) =>
        Assert.Single(engine.Stats.GetAllFiles(), f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

    private FileActivityEvent OpenFolder(MonitorEngine engine, string path, int status = 0) =>
        Run(engine, FolderOpenIo(path), FileIoClassifier.FileOpened, status);

    private FileActivityEvent List(MonitorEngine engine, string path)
    {
        PendingIo io = Io(RawFileOp.DirEnum, path);
        io.Pattern = "*";
        return Run(engine, io);
    }

    private FileActivityEvent CreateFile(MonitorEngine engine, string path)
    {
        PendingIo io = Io(RawFileOp.Create, path);
        io.CreateOptions = FileIoClassifier.FileNonDirectoryFile;
        io.CreateDisposition = FileIoClassifier.FileCreate;
        return Run(engine, io, FileIoClassifier.FileCreated);
    }

    private FileActivityEvent OpenFile(MonitorEngine engine, string path)
    {
        PendingIo io = Io(RawFileOp.Create, path);
        io.CreateOptions = FileIoClassifier.FileNonDirectoryFile;
        io.CreateDisposition = FileIoClassifier.FileOpen;
        return Run(engine, io, FileIoClassifier.FileOpened);
    }

    /// <summary>An open that takes a folder or a file (backup semantics), as MoveFileEx and IFileOperation open a rename's source.</summary>
    private FileActivityEvent OpenEither(MonitorEngine engine, string path)
    {
        PendingIo io = Io(RawFileOp.Create, path);
        io.CreateDisposition = FileIoClassifier.FileOpen;
        return Run(engine, io, FileIoClassifier.FileOpened);
    }

    private FileActivityEvent Write(MonitorEngine engine, string path)
    {
        PendingIo io = Io(RawFileOp.Write, path);
        io.IoSize = 10;
        io.Offset = 0;
        return Run(engine, io, info: 10);
    }

    private FileActivityEvent PagingRead(MonitorEngine engine, string path)
    {
        PendingIo io = Io(RawFileOp.Read, path);
        io.IoSize = 4096;
        io.Offset = 0;
        io.IrpFlags = FileIoClassifier.IrpPagingIo;
        return Run(engine, io, info: 4096);
    }

    private FileActivityEvent SetTimestamps(MonitorEngine engine, string path)
    {
        PendingIo io = Io(RawFileOp.SetInfo, path);
        io.InfoClass = FileIoClassifier.FileBasicInformation;
        return Run(engine, io);
    }

    private FileActivityEvent Delete(MonitorEngine engine, string path, int status = 0) => Run(engine, DeleteIo(path), status: status);

    private FileActivityEvent Rename(MonitorEngine engine, string path, string? target, int status = 0)
    {
        PendingIo io = Io(RawFileOp.Rename, path);
        io.InfoClass = FileIoClassifier.FileRenameInformationEx;
        return Run(engine, io, status: status, target: target);
    }

    private PendingIo FolderOpenIo(string path)
    {
        PendingIo io = Io(RawFileOp.Create, path);
        io.CreateOptions = FileIoClassifier.FileDirectoryFile;
        io.CreateDisposition = FileIoClassifier.FileOpen;
        return io;
    }

    /// <summary>The disposition that RemoveDirectoryW and DeleteFileW send.</summary>
    private PendingIo DeleteIo(string path)
    {
        PendingIo io = Io(RawFileOp.Delete, path);
        io.InfoClass = FileIoClassifier.FileDispositionInformationEx;
        io.ExtraInfo = FileIoClassifier.FileDispositionDelete;
        return io;
    }

    private PendingIo Io(RawFileOp op, string path)
    {
        _time = _time.AddMilliseconds(1);
        return new PendingIo
        {
            Op = op,
            Irp = _irp++,
            Timestamp = _time,
            Path = path,
            Drive = 'E',
            ProcessId = OtherProcessId,
            ThreadId = 7,
            ProcessName = "explorer.exe",
        };
    }

    /// <summary>Starts the operation, delivers its new path and completion, and returns the one event it published.</summary>
    private FileActivityEvent Run(MonitorEngine engine, PendingIo io, ulong info = 0, int status = 0, string? target = null)
    {
        IFileIoSink sink = engine;
        io.TimestampQpc = _qpc;
        sink.OnBegin(io);
        if (engine.Options.UseCompletionEvents)
        {
            if (target is not null)
                sink.OnPathOperation(io.Irp, _qpc + 1, target);
            sink.OnComplete(io.Irp, _qpc + 2, info, status);
        }

        _qpc += 10;
        var events = new List<FileActivityEvent>();
        engine.DrainEvents(events);
        return Assert.Single(events);
    }
}
