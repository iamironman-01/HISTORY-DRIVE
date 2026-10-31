using DriveMonitor.Core.Aggregation;
using DriveMonitor.Core.Models;

namespace DriveMonitor.Tests.Aggregation;

/// <summary>Per-file, per-drive and per-process totals, fed with hand-made events.</summary>
public sealed class ActivityAggregatorTests
{
    private const string FileA = @"E:\docs\a.txt";
    private const string FileB = @"E:\docs\b.txt";
    private const string Folder = @"E:\docs";

    private static readonly DateTime T0 = new(2026, 10, 30, 12, 0, 0);

    // ---- reads and writes ----

    [Fact]
    public void ReadsAndWrites_AreTotalledPerFileDriveAndProcess()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Read, FileA, bytes: 100));
        aggregator.Record(Ev(FileOperation.Read, FileA, bytes: 50, second: 1));
        aggregator.Record(Ev(FileOperation.Write, FileA, bytes: 30, second: 2));
        aggregator.Record(Ev(FileOperation.Write, FileB, bytes: 70, second: 3));

        FileStatsSnapshot a = FileStats(aggregator, FileA);
        Assert.Equal('E', a.Drive);
        Assert.False(a.IsDirectory);
        Assert.Equal(2L, a.Reads);
        Assert.Equal(150L, a.BytesRead);
        Assert.Equal(1L, a.Writes);
        Assert.Equal(30L, a.BytesWritten);
        Assert.Equal(0L, a.PagingBytesRead);
        Assert.Equal(0L, a.PagingBytesWritten);
        Assert.Equal(0, a.Opens);
        Assert.Equal(FileLifecycle.None, a.Lifecycle);
        Assert.Equal(T0, a.FirstSeen);
        Assert.Equal(T0.AddSeconds(2), a.LastSeen);
        Assert.Equal(FileOperation.Write, a.LastOperation);
        Assert.Equal("app.exe", a.LastProcess);
        Assert.Equal("app.exe", a.Processes);
        Assert.Equal(1, a.ProcessCount);

        DriveTotals drive = Assert.Single(aggregator.GetDriveTotals());
        Assert.Equal('E', drive.Drive);
        Assert.Equal(4L, drive.Events);
        Assert.Equal(2L, drive.Reads);
        Assert.Equal(150L, drive.BytesRead);
        Assert.Equal(2L, drive.Writes);
        Assert.Equal(100L, drive.BytesWritten);
        Assert.Equal(T0.AddSeconds(3), drive.LastActivity);

        ProcessStatsSnapshot process = Assert.Single(aggregator.GetAllProcesses());
        Assert.Equal(100, process.ProcessId);
        Assert.Equal("app.exe", process.ProcessName);
        Assert.Equal(2L, process.Reads);
        Assert.Equal(150L, process.BytesRead);
        Assert.Equal(2L, process.Writes);
        Assert.Equal(100L, process.BytesWritten);
        Assert.Equal(2, process.Files);
        Assert.Equal(T0, process.FirstSeen);
        Assert.Equal(T0.AddSeconds(3), process.LastSeen);
    }

    [Fact]
    public void UnknownStatus_IsCountedLikeSuccess()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Write, FileA, bytes: 10, status: IoStatus.Unknown));

        Assert.Equal(10L, FileStats(aggregator, FileA).BytesWritten);
        Assert.Equal(1L, Assert.Single(aggregator.GetDriveTotals()).Writes);
        Assert.Equal(0L, aggregator.FailedEvents);
    }

    [Fact]
    public void PagingIo_GoesOnlyIntoThePagingTotals()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Read, FileA, bytes: 4096, flags: FileEventFlags.Paging, processId: 4, processName: "System"));
        aggregator.Record(Ev(
            FileOperation.Write, FileA, bytes: 8192, flags: FileEventFlags.Paging | FileEventFlags.NonCached, processId: 4, processName: "System", second: 1));

        FileStatsSnapshot file = FileStats(aggregator, FileA);
        Assert.Equal(0L, file.Reads);
        Assert.Equal(0L, file.BytesRead);
        Assert.Equal(0L, file.Writes);
        Assert.Equal(0L, file.BytesWritten);
        Assert.Equal(4096L, file.PagingBytesRead);
        Assert.Equal(8192L, file.PagingBytesWritten);
        Assert.Equal(FileOperation.Write, file.LastOperation);
        Assert.Equal(T0.AddSeconds(1), file.LastSeen);
        Assert.Equal("", file.LastProcess);
        Assert.Equal("", file.Processes);
        Assert.Equal(0, file.ProcessCount);

        DriveTotals drive = Assert.Single(aggregator.GetDriveTotals());
        Assert.Equal(2L, drive.Events);
        Assert.Equal(0L, drive.Reads);
        Assert.Equal(0L, drive.BytesRead);
        Assert.Equal(0L, drive.Writes);
        Assert.Equal(0L, drive.BytesWritten);
        Assert.Equal(4096L, drive.PagingBytesRead);
        Assert.Equal(8192L, drive.PagingBytesWritten);

        Assert.Empty(aggregator.GetAllProcesses());
        Assert.Empty(aggregator.TakeChangedProcesses());
    }

    [Fact]
    public void PagingIo_DoesNotReplaceTheLastProcess()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Write, FileA, bytes: 10, processName: "notepad.exe"));
        aggregator.Record(Ev(FileOperation.Write, FileA, bytes: 4096, flags: FileEventFlags.Paging, processId: 4, processName: "System", second: 1));

        FileStatsSnapshot file = FileStats(aggregator, FileA);
        Assert.Equal("notepad.exe", file.LastProcess);
        Assert.Equal("notepad.exe", file.Processes);
        Assert.Equal(1, file.ProcessCount);
        Assert.Equal(1L, file.Writes);
        Assert.Equal(10L, file.BytesWritten);
        Assert.Equal(4096L, file.PagingBytesWritten);
        Assert.Equal("notepad.exe", Assert.Single(aggregator.GetAllProcesses()).ProcessName);
    }

    [Fact]
    public void FailedEvents_OnlyIncrementTheFailedCounter()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Write, FileA, bytes: 100, status: IoStatus.Failed));
        aggregator.Record(Ev(FileOperation.Create, FileB, status: IoStatus.Failed));

        Assert.Equal(2L, aggregator.FailedEvents);
        Assert.Equal(0, aggregator.TrackedFiles);
        Assert.Empty(aggregator.GetAllFiles());
        Assert.Empty(aggregator.GetDriveTotals());
        Assert.Empty(aggregator.GetAllProcesses());
        Assert.Empty(aggregator.TakeChangedFiles());
        Assert.Empty(aggregator.TakeChangedProcesses());
    }

    // ---- lifecycle ----

    [Fact]
    public void Create_MarksTheFileCreated()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Create, FileA));

        Assert.Equal(FileLifecycle.Created, FileStats(aggregator, FileA).Lifecycle);
    }

    [Fact]
    public void Delete_MarksTheFileDeleted()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Delete, FileA));

        Assert.Equal(FileLifecycle.Deleted, FileStats(aggregator, FileA).Lifecycle);
    }

    [Fact]
    public void CreateAfterDelete_ClearsDeleted()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Delete, FileA));
        aggregator.Record(Ev(FileOperation.Create, FileA, second: 1));

        Assert.Equal(FileLifecycle.Created, FileStats(aggregator, FileA).Lifecycle);
    }

    [Fact]
    public void DeleteAfterCreate_KeepsBothFlags()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Create, FileA));
        aggregator.Record(Ev(FileOperation.Delete, FileA, second: 1));

        Assert.Equal(FileLifecycle.Created | FileLifecycle.Deleted, FileStats(aggregator, FileA).Lifecycle);
    }

    [Fact]
    public void CreateWithDeleteOnClose_IsCreatedAndDeleted()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Create, FileA, flags: FileEventFlags.DeleteOnClose));

        Assert.Equal(FileLifecycle.Created | FileLifecycle.Deleted, FileStats(aggregator, FileA).Lifecycle);
    }

    [Fact]
    public void OpenWithDeleteOnClose_MarksDeleted()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Open, FileA, flags: FileEventFlags.DeleteOnClose));

        FileStatsSnapshot file = FileStats(aggregator, FileA);
        Assert.Equal(FileLifecycle.Deleted, file.Lifecycle);
        Assert.Equal(1, file.Opens);
    }

    [Fact]
    public void CancelledDelete_ClearsDeleted()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Delete, FileA));
        aggregator.Record(Ev(FileOperation.SetInfo, FileA, flags: FileEventFlags.DeleteCancelled, second: 1));

        FileStatsSnapshot file = FileStats(aggregator, FileA);
        Assert.Equal(FileLifecycle.None, file.Lifecycle);
        Assert.Equal(FileOperation.SetInfo, file.LastOperation);
    }

    [Theory]
    [InlineData(FileOperation.Create, FileLifecycle.Created)] // a temporary file kept after all
    [InlineData(FileOperation.Open, FileLifecycle.None)]
    public void CancelledDeleteOnClose_ClearsDeleted(FileOperation operation, FileLifecycle expected)
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(operation, FileA, flags: FileEventFlags.DeleteOnClose));
        aggregator.Record(Ev(FileOperation.SetInfo, FileA, flags: FileEventFlags.DeleteCancelled, second: 1));

        Assert.Equal(expected, FileStats(aggregator, FileA).Lifecycle);
    }

    [Fact]
    public void DeleteAfterACancelledDelete_MarksDeletedAgain()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Delete, FileA));
        aggregator.Record(Ev(FileOperation.SetInfo, FileA, flags: FileEventFlags.DeleteCancelled, second: 1));
        aggregator.Record(Ev(FileOperation.Delete, FileA, second: 2));

        Assert.Equal(FileLifecycle.Deleted, FileStats(aggregator, FileA).Lifecycle);
    }

    [Fact]
    public void Open_CountsOpensWithoutChangingTheLifecycle()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Open, FileA));
        aggregator.Record(Ev(FileOperation.Open, FileA, second: 1));

        FileStatsSnapshot file = FileStats(aggregator, FileA);
        Assert.Equal(2, file.Opens);
        Assert.Equal(FileLifecycle.None, file.Lifecycle);
        Assert.Equal(FileOperation.Open, file.LastOperation);
    }

    [Fact]
    public void Rename_MarksRenamed()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Rename, FileA));

        Assert.Equal(FileLifecycle.Renamed, FileStats(aggregator, FileA).Lifecycle);
    }

    [Fact]
    public void SizeChange_MarksResized_OtherMetadataChangesDoNot()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.SetInfo, FileA, bytes: 1000, flags: FileEventFlags.SizeChange));
        aggregator.Record(Ev(FileOperation.SetInfo, FileB));

        Assert.Equal(FileLifecycle.Resized, FileStats(aggregator, FileA).Lifecycle);
        Assert.Equal(FileLifecycle.None, FileStats(aggregator, FileB).Lifecycle);
    }

    [Fact]
    public void Lifecycle_FlagsAccumulate()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Create, FileA));
        aggregator.Record(Ev(FileOperation.Write, FileA, bytes: 10, second: 1));
        aggregator.Record(Ev(FileOperation.SetInfo, FileA, bytes: 5, flags: FileEventFlags.SizeChange, second: 2));
        aggregator.Record(Ev(FileOperation.Rename, FileA, second: 3));

        FileStatsSnapshot file = FileStats(aggregator, FileA);
        Assert.Equal(FileLifecycle.Created | FileLifecycle.Resized | FileLifecycle.Renamed, file.Lifecycle);
        Assert.Equal(FileOperation.Rename, file.LastOperation);
    }

    [Fact]
    public void CreatesDeletesAndRenames_AreCountedPerDriveAndProcess()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Create, FileA));
        aggregator.Record(Ev(FileOperation.Create, FileB));
        aggregator.Record(Ev(FileOperation.Delete, FileA));
        aggregator.Record(Ev(FileOperation.Rename, FileB));
        aggregator.Record(Ev(FileOperation.Open, FileA));
        aggregator.Record(Ev(FileOperation.Flush, FileA));
        aggregator.Record(Ev(FileOperation.SetInfo, FileA, flags: FileEventFlags.SizeChange));

        DriveTotals drive = Assert.Single(aggregator.GetDriveTotals());
        Assert.Equal(7L, drive.Events);
        Assert.Equal(2L, drive.Creates);
        Assert.Equal(1L, drive.Deletes);
        Assert.Equal(1L, drive.Renames);
        Assert.Equal(0L, drive.Reads);
        Assert.Equal(0L, drive.Writes);

        ProcessStatsSnapshot process = Assert.Single(aggregator.GetAllProcesses());
        Assert.Equal(2L, process.Creates);
        Assert.Equal(1L, process.Deletes);
        Assert.Equal(1L, process.Renames);
        Assert.Equal(2, process.Files);
    }

    // ---- folders ----

    [Theory]
    [InlineData(FileOperation.Open, FileEventFlags.Directory)]
    [InlineData(FileOperation.List, FileEventFlags.Directory)]
    [InlineData(FileOperation.List, FileEventFlags.None)]
    [InlineData(FileOperation.SetInfo, FileEventFlags.Directory)]
    [InlineData(FileOperation.Flush, FileEventFlags.Directory)]
    public void FolderOpensAndListings_DoNotCreateRows(FileOperation operation, FileEventFlags flags)
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(operation, Folder, flags: flags));

        Assert.Equal(0, aggregator.TrackedFiles);
        Assert.Empty(aggregator.TakeChangedFiles());
        Assert.Equal(0L, aggregator.UntrackedEvents);
        Assert.Equal(1L, Assert.Single(aggregator.GetDriveTotals()).Events);
        Assert.Equal(0, Assert.Single(aggregator.GetAllProcesses()).Files);
    }

    [Theory]
    [InlineData(FileOperation.Create, FileLifecycle.Created)]
    [InlineData(FileOperation.Delete, FileLifecycle.Deleted)]
    [InlineData(FileOperation.Rename, FileLifecycle.Renamed)]
    public void FolderCreateDeleteAndRename_CreateRows(FileOperation operation, FileLifecycle expected)
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(operation, Folder, flags: FileEventFlags.Directory));

        FileStatsSnapshot folder = FileStats(aggregator, Folder);
        Assert.True(folder.IsDirectory);
        Assert.Equal(expected, folder.Lifecycle);
    }

    [Fact]
    public void ExistingFolderRow_IsUpdatedByLaterOpensAndListings()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Create, Folder, flags: FileEventFlags.Directory));
        aggregator.Record(Ev(FileOperation.Open, Folder, flags: FileEventFlags.Directory, second: 1));
        aggregator.Record(Ev(FileOperation.List, Folder, flags: FileEventFlags.Directory, second: 2));

        FileStatsSnapshot folder = FileStats(aggregator, Folder);
        Assert.Equal(1, folder.Opens);
        Assert.Equal(FileLifecycle.Created, folder.Lifecycle);
        Assert.Equal(FileOperation.List, folder.LastOperation);
        Assert.Equal(T0.AddSeconds(2), folder.LastSeen);
    }

    [Theory]
    [InlineData(FileOperation.Open)] // RemoveDirectory opens the folder as a folder, then deletes it
    [InlineData(FileOperation.List)]
    public void FolderSeenAsAFolder_GetsAFolderRowWhenDeletedOrRenamed(FileOperation seenAs)
    {
        const string photos = @"E:\photos";
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(seenAs, Folder, flags: FileEventFlags.Directory));
        aggregator.Record(Ev(seenAs, photos, flags: FileEventFlags.Directory));
        Assert.Equal(0, aggregator.TrackedFiles);

        // Windows reports both without the folder flag.
        aggregator.Record(Ev(FileOperation.Delete, Folder, second: 1));
        aggregator.Record(Ev(FileOperation.Rename, photos, second: 1));

        FileStatsSnapshot deleted = FileStats(aggregator, Folder);
        Assert.True(deleted.IsDirectory);
        Assert.Equal(FileLifecycle.Deleted, deleted.Lifecycle);
        FileStatsSnapshot renamed = FileStats(aggregator, photos);
        Assert.True(renamed.IsDirectory);
        Assert.Equal(FileLifecycle.Renamed, renamed.Lifecycle);
    }

    [Fact]
    public void DeleteAndRenameOfAPathNotSeenAsAFolder_GiveFileRows()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Delete, FileA));
        aggregator.Record(Ev(FileOperation.Rename, FileB));

        Assert.False(FileStats(aggregator, FileA).IsDirectory);
        Assert.False(FileStats(aggregator, FileB).IsDirectory);
    }

    [Fact]
    public void FolderEvidenceArrivingAfterTheRow_MarksTheRowAsAFolder()
    {
        var aggregator = new ActivityAggregator();

        // The delete was delivered before the folder open that preceded it.
        aggregator.Record(Ev(FileOperation.Delete, Folder, second: 1));
        aggregator.Record(Ev(FileOperation.Open, Folder, flags: FileEventFlags.Directory));

        FileStatsSnapshot folder = FileStats(aggregator, Folder);
        Assert.True(folder.IsDirectory);
        Assert.Equal(FileLifecycle.Deleted, folder.Lifecycle);
    }

    [Fact]
    public void OpenThatMayHaveTakenAFolder_GetsNoRowUntilThePathTurnsOutToBeAFile()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Open, Folder, flags: FileEventFlags.MaybeDirectory)); // e.g. a folder watcher
        aggregator.Record(Ev(FileOperation.Open, FileA, flags: FileEventFlags.MaybeDirectory));

        Assert.Equal(0, aggregator.TrackedFiles);
        Assert.Equal(0L, aggregator.UntrackedEvents);
        Assert.Equal(2L, Assert.Single(aggregator.GetDriveTotals()).Events);

        aggregator.Record(Ev(FileOperation.Read, FileA, bytes: 10, second: 1));
        aggregator.Record(Ev(FileOperation.Open, FileA, flags: FileEventFlags.MaybeDirectory, second: 2));

        FileStatsSnapshot file = Assert.Single(aggregator.GetAllFiles());
        Assert.Equal(FileA, file.Path);
        Assert.False(file.IsDirectory);
        Assert.Equal(1L, file.Reads);
        Assert.Equal(1, file.Opens); // counted once the path has a row
    }

    [Fact]
    public void OpenThatMayHaveTakenAFolder_GetsARowWhenItDeletes()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Open, FileA, flags: FileEventFlags.MaybeDirectory | FileEventFlags.DeleteOnClose));

        FileStatsSnapshot file = FileStats(aggregator, FileA);
        Assert.False(file.IsDirectory);
        Assert.Equal(FileLifecycle.Deleted, file.Lifecycle);
    }

    [Fact]
    public void FolderOpenedWithDeleteOnClose_GetsAFolderRow()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Open, Folder, flags: FileEventFlags.Directory | FileEventFlags.DeleteOnClose));

        FileStatsSnapshot folder = FileStats(aggregator, Folder);
        Assert.True(folder.IsDirectory);
        Assert.Equal(FileLifecycle.Deleted, folder.Lifecycle);
    }

    [Fact]
    public void KnownFolder_PagingIoMetadataChangesAndOpensThatTakeEitherTypeGiveNoRow()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.List, Folder, flags: FileEventFlags.Directory));
        aggregator.Record(Ev(FileOperation.Read, Folder, bytes: 4096, flags: FileEventFlags.Paging, processId: 4, processName: "System", second: 1));
        aggregator.Record(Ev(FileOperation.SetInfo, Folder, second: 2)); // timestamps set through a backup-semantics handle
        aggregator.Record(Ev(FileOperation.Open, Folder, flags: FileEventFlags.MaybeDirectory, second: 3));
        aggregator.Record(Ev(FileOperation.Flush, Folder, second: 4));

        Assert.Equal(0, aggregator.TrackedFiles);
        Assert.Equal(0L, aggregator.UntrackedEvents);
        DriveTotals drive = Assert.Single(aggregator.GetDriveTotals());
        Assert.Equal(5L, drive.Events);
        Assert.Equal(4096L, drive.PagingBytesRead);
    }

    [Fact]
    public void FolderReplacedByAFile_RowTurnsIntoAFile()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.List, Folder, flags: FileEventFlags.Directory));
        aggregator.Record(Ev(FileOperation.Delete, Folder, second: 1));
        Assert.True(FileStats(aggregator, Folder).IsDirectory);

        aggregator.Record(Ev(FileOperation.Create, Folder, second: 2));

        FileStatsSnapshot row = FileStats(aggregator, Folder);
        Assert.False(row.IsDirectory);
        Assert.Equal(FileLifecycle.Created, row.Lifecycle);
    }

    [Fact]
    public void FileReplacedByAFolder_RowTurnsIntoAFolder()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Write, FileA, bytes: 10));
        aggregator.Record(Ev(FileOperation.Delete, FileA, second: 1));
        aggregator.Record(Ev(FileOperation.Create, FileA, flags: FileEventFlags.Directory, second: 2));

        FileStatsSnapshot row = FileStats(aggregator, FileA);
        Assert.True(row.IsDirectory);
        Assert.Equal(FileLifecycle.Created, row.Lifecycle);
    }

    [Theory]
    [InlineData(FileOperation.Read)]
    [InlineData(FileOperation.Write)]
    [InlineData(FileOperation.Open)]
    public void RememberedFolderPath_UsedAsAFile_GetsAFileRow(FileOperation operation)
    {
        var aggregator = new ActivityAggregator();

        // The folder went away unseen and a file took its path.
        aggregator.Record(Ev(FileOperation.List, Folder, flags: FileEventFlags.Directory));
        aggregator.Record(Ev(operation, Folder, bytes: 5, second: 1));
        aggregator.Record(Ev(FileOperation.Delete, Folder, second: 2));

        FileStatsSnapshot row = FileStats(aggregator, Folder);
        Assert.False(row.IsDirectory);
        Assert.Equal(FileLifecycle.Deleted, row.Lifecycle);
    }

    [Fact]
    public void RememberedFolders_AreLimitedLikeTrackedFiles_KeepingTheLatest()
    {
        var aggregator = new ActivityAggregator(maxTrackedFiles: 2);

        foreach (string folder in new[] { @"E:\f1", @"E:\f2", @"E:\f2", @"E:\f3" })
            aggregator.Record(Ev(FileOperation.List, folder, flags: FileEventFlags.Directory));
        aggregator.Record(Ev(FileOperation.Rename, @"E:\f3", second: 1));
        aggregator.Record(Ev(FileOperation.Rename, @"E:\f1", second: 1)); // forgotten when f3 did not fit

        Assert.True(FileStats(aggregator, @"E:\f3").IsDirectory);
        Assert.False(FileStats(aggregator, @"E:\f1").IsDirectory);
    }

    [Fact]
    public void Clear_ForgetsRememberedFolders()
    {
        var aggregator = new ActivityAggregator();
        aggregator.Record(Ev(FileOperation.List, Folder, flags: FileEventFlags.Directory));

        aggregator.Clear();
        aggregator.Record(Ev(FileOperation.Rename, Folder, second: 1));

        Assert.False(FileStats(aggregator, Folder).IsDirectory);
    }

    // ---- limits and snapshots ----

    [Fact]
    public void MaxTrackedFiles_LaterNewFilesAreCountedButNotTracked()
    {
        const string fileC = @"E:\docs\c.txt";
        var aggregator = new ActivityAggregator(maxTrackedFiles: 2);

        aggregator.Record(Ev(FileOperation.Read, FileA, bytes: 10));
        aggregator.Record(Ev(FileOperation.Read, FileB, bytes: 20));
        aggregator.Record(Ev(FileOperation.Read, fileC, bytes: 30));
        aggregator.Record(Ev(FileOperation.Read, fileC, bytes: 40));
        aggregator.Record(Ev(FileOperation.Read, FileA, bytes: 1));
        aggregator.Record(Ev(FileOperation.List, Folder, flags: FileEventFlags.Directory));

        Assert.Equal(2, aggregator.MaxTrackedFiles);
        Assert.Equal(2, aggregator.TrackedFiles);
        Assert.Equal(2L, aggregator.UntrackedEvents); // both events on c.txt; the listing never wanted a row
        Assert.Equal(new[] { FileA, FileB }, aggregator.GetAllFiles().Select(f => f.Path).Order(StringComparer.Ordinal));

        FileStatsSnapshot a = FileStats(aggregator, FileA);
        Assert.Equal(2L, a.Reads);
        Assert.Equal(11L, a.BytesRead);

        DriveTotals drive = Assert.Single(aggregator.GetDriveTotals());
        Assert.Equal(6L, drive.Events);
        Assert.Equal(5L, drive.Reads);
        Assert.Equal(101L, drive.BytesRead);

        ProcessStatsSnapshot process = Assert.Single(aggregator.GetAllProcesses());
        Assert.Equal(5L, process.Reads);
        Assert.Equal(101L, process.BytesRead);
        Assert.Equal(2, process.Files);
    }

    [Fact]
    public void TakeChangedFiles_ReturnsEachChangedFileOnceUntilItChangesAgain()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Read, FileA, bytes: 1));
        aggregator.Record(Ev(FileOperation.Read, FileA, bytes: 2));
        aggregator.Record(Ev(FileOperation.Write, FileB, bytes: 3));

        List<FileStatsSnapshot> changed = aggregator.TakeChangedFiles();
        Assert.Equal(new[] { FileA, FileB }, changed.Select(f => f.Path).Order(StringComparer.Ordinal));
        Assert.Equal(3L, changed.Single(f => f.Path == FileA).BytesRead);

        Assert.Empty(aggregator.TakeChangedFiles());

        aggregator.Record(Ev(FileOperation.Write, FileB, bytes: 4));

        FileStatsSnapshot b = Assert.Single(aggregator.TakeChangedFiles());
        Assert.Equal(FileB, b.Path);
        Assert.Equal(7L, b.BytesWritten);
        Assert.Empty(aggregator.TakeChangedFiles());
    }

    [Fact]
    public void TakeChangedProcesses_ReturnsEachChangedProcessOnceUntilItChangesAgain()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Read, FileA, bytes: 1, processId: 1, processName: "one.exe"));
        aggregator.Record(Ev(FileOperation.Read, FileB, bytes: 2, processId: 1, processName: "one.exe"));
        aggregator.Record(Ev(FileOperation.Read, FileA, bytes: 3, processId: 2, processName: "two.exe"));

        Assert.Equal(new[] { "one.exe", "two.exe" }, aggregator.TakeChangedProcesses().Select(p => p.ProcessName).Order(StringComparer.Ordinal));
        Assert.Empty(aggregator.TakeChangedProcesses());

        aggregator.Record(Ev(FileOperation.Write, FileB, bytes: 4, processId: 2, processName: "two.exe"));

        ProcessStatsSnapshot two = Assert.Single(aggregator.TakeChangedProcesses());
        Assert.Equal("two.exe", two.ProcessName);
        Assert.Equal(1L, two.Reads);
        Assert.Equal(1L, two.Writes);
        Assert.Equal(2, two.Files);
    }

    // ---- processes ----

    [Theory]
    [InlineData(1, "a")]
    [InlineData(3, "a, b, c")]
    [InlineData(4, "a, b, c +1 more")]
    [InlineData(5, "a, b, c +2 more")]
    [InlineData(16, "a, b, c +13 more")]
    [InlineData(17, "a, b, c +14 more")]
    public void Processes_NamesTheFirstThreeAndCountsTheRest(int distinctProcesses, string expected)
    {
        var aggregator = new ActivityAggregator();

        for (int i = 0; i < distinctProcesses; i++)
            aggregator.Record(Ev(FileOperation.Read, FileA, processId: 1000 + i, processName: ((char)('a' + i)).ToString()));

        FileStatsSnapshot file = FileStats(aggregator, FileA);
        Assert.Equal(expected, file.Processes);
        Assert.Equal(distinctProcesses, file.ProcessCount);
    }

    [Fact]
    public void Processes_TheSameNameAgainInAnyCase_IsListedOnce()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Read, FileA, processId: 1, processName: "app.exe"));
        aggregator.Record(Ev(FileOperation.Read, FileA, processId: 2, processName: "Other.exe"));
        aggregator.Record(Ev(FileOperation.Read, FileA, processId: 3, processName: "APP.EXE"));
        aggregator.Record(Ev(FileOperation.Read, FileA, processId: 2, processName: "Other.exe"));

        FileStatsSnapshot file = FileStats(aggregator, FileA);
        Assert.Equal("app.exe, Other.exe", file.Processes);
        Assert.Equal(2, file.ProcessCount);
        Assert.Equal("Other.exe", file.LastProcess);
    }

    [Fact]
    public void Processes_RepeatedEventsFromTheSeventeenthProcess_CountItOnce()
    {
        var aggregator = new ActivityAggregator();

        for (int i = 0; i < 17; i++)
            aggregator.Record(Ev(FileOperation.Read, FileA, processId: 1000 + i, processName: $"p{i:00}.exe"));
        aggregator.Record(Ev(FileOperation.Read, FileA, processId: 1016, processName: "p16.exe"));
        aggregator.Record(Ev(FileOperation.Read, FileA, processId: 1016, processName: "p16.exe"));

        FileStatsSnapshot file = FileStats(aggregator, FileA);
        Assert.Equal(17, file.ProcessCount);
        Assert.Equal("p00.exe, p01.exe, p02.exe +14 more", file.Processes);
    }

    [Fact]
    public void Processes_TextFollowsEachNewName_AndIgnoresRepeats()
    {
        var aggregator = new ActivityAggregator();
        string[] expected = ["p0", "p0, p1", "p0, p1, p2", "p0, p1, p2 +1 more", "p0, p1, p2 +2 more"];

        for (int i = 0; i < expected.Length; i++)
        {
            aggregator.Record(Ev(FileOperation.Read, FileA, processId: i, processName: $"p{i}"));
            for (int seen = 0; seen <= i; seen++)
                aggregator.Record(Ev(FileOperation.Write, FileA, processId: seen, processName: $"P{seen}")); // repeats in another case

            FileStatsSnapshot changed = Assert.Single(aggregator.TakeChangedFiles());
            Assert.Equal(expected[i], changed.Processes);
            Assert.Equal(i + 1, changed.ProcessCount);
            Assert.Equal(expected[i], FileStats(aggregator, FileA).Processes);
        }
    }

    [Fact]
    public void Processes_TextIsReusedUntilANewNameArrives()
    {
        var aggregator = new ActivityAggregator();
        aggregator.Record(Ev(FileOperation.Read, FileA, processId: 1, processName: "one.exe"));
        aggregator.Record(Ev(FileOperation.Read, FileA, processId: 2, processName: "two.exe"));
        string before = Assert.Single(aggregator.TakeChangedFiles()).Processes;

        for (int i = 0; i < 100; i++)
            aggregator.Record(Ev(FileOperation.Write, FileA, bytes: 1, processId: 1, processName: "ONE.EXE"));

        // Snapshots are taken under the lock Record needs; rebuilding the text for each would allocate per row per refresh.
        Assert.Same(before, Assert.Single(aggregator.TakeChangedFiles()).Processes);
        Assert.Same(before, FileStats(aggregator, FileA).Processes);

        aggregator.Record(Ev(FileOperation.Read, FileA, processId: 3, processName: "three.exe"));

        Assert.Equal("one.exe, two.exe, three.exe", Assert.Single(aggregator.TakeChangedFiles()).Processes);
    }

    [Fact]
    public void Processes_NamesPastTheCountedLimit_LeaveTextAndCountAlone()
    {
        const int counted = 16 + 4096; // names listed, plus names only counted
        var aggregator = new ActivityAggregator();
        for (int i = 0; i < counted; i++)
            aggregator.Record(Ev(FileOperation.Read, FileA, processId: i, processName: $"p{i:0000}.exe"));

        FileStatsSnapshot full = Assert.Single(aggregator.TakeChangedFiles());
        Assert.Equal("p0000.exe, p0001.exe, p0002.exe +4109 more", full.Processes);
        Assert.Equal(counted, full.ProcessCount);

        aggregator.Record(Ev(FileOperation.Read, FileA, processId: counted, processName: "late.exe"));

        FileStatsSnapshot after = Assert.Single(aggregator.TakeChangedFiles());
        Assert.Equal(full.Processes, after.Processes);
        Assert.Equal(counted, after.ProcessCount);
    }

    [Fact]
    public void Processes_StaysCorrectThroughManyEventsWhileNewNamesArrive()
    {
        var aggregator = new ActivityAggregator();
        var random = new Random(20261031);
        var distinct = new List<string>(); // first spelling of each name, in order of arrival

        for (int i = 0; i < 20_000; i++)
        {
            int id = random.Next(1 + (i / 500)); // the pool grows to 40 names, well past the 16 that are listed
            string name = random.Next(2) == 0 ? $"proc{id}.exe" : $"PROC{id}.EXE";
            if (!distinct.Contains(name, StringComparer.OrdinalIgnoreCase))
                distinct.Add(name);
            aggregator.Record(Ev(FileOperation.Read, FileA, bytes: 1, processId: id, processName: name));

            if (random.Next(20) == 0)
            {
                FileStatsSnapshot changed = Assert.Single(aggregator.TakeChangedFiles());
                Assert.Equal(ExpectedProcesses(distinct), changed.Processes);
                Assert.Equal(distinct.Count, changed.ProcessCount);
            }
        }

        FileStatsSnapshot file = FileStats(aggregator, FileA);
        Assert.True(distinct.Count > 16, "the run should go past the names that are listed");
        Assert.Equal(ExpectedProcesses(distinct), file.Processes);
        Assert.Equal(distinct.Count, file.ProcessCount);
        Assert.Equal(20_000L, file.Reads);
    }

    [Fact]
    public void ProcessTotals_AreKeyedByProcessIdAndName()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Read, FileA, processId: 100, processName: "a.exe"));
        aggregator.Record(Ev(FileOperation.Read, FileA, processId: 100, processName: "b.exe")); // id reused by another program
        aggregator.Record(Ev(FileOperation.Read, FileB, processId: 200, processName: "a.exe")); // second instance
        aggregator.Record(Ev(FileOperation.Read, FileB, processId: 200, processName: "a.exe"));

        List<ProcessStatsSnapshot> processes = aggregator.GetAllProcesses()
            .OrderBy(p => p.ProcessId)
            .ThenBy(p => p.ProcessName, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(
            new[] { (100, "a.exe", 1L), (100, "b.exe", 1L), (200, "a.exe", 2L) },
            processes.Select(p => (p.ProcessId, p.ProcessName, p.Reads)));
    }

    // ---- files and drives ----

    [Fact]
    public void FilePaths_AreCaseInsensitive()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Read, @"E:\Docs\A.txt", bytes: 1));
        aggregator.Record(Ev(FileOperation.Read, @"e:\docs\a.TXT", bytes: 2));

        FileStatsSnapshot file = Assert.Single(aggregator.GetAllFiles());
        Assert.Equal(@"E:\Docs\A.txt", file.Path); // the first spelling is kept
        Assert.Equal(2L, file.Reads);
        Assert.Equal(3L, file.BytesRead);
    }

    [Fact]
    public void GetDriveTotals_IsOrderedByDriveLetter()
    {
        var aggregator = new ActivityAggregator();

        aggregator.Record(Ev(FileOperation.Read, @"F:\x.bin"));
        aggregator.Record(Ev(FileOperation.Read, @"D:\x.bin"));
        aggregator.Record(Ev(FileOperation.Read, @"E:\x.bin"));

        Assert.Equal(new[] { 'D', 'E', 'F' }, aggregator.GetDriveTotals().Select(d => d.Drive));
    }

    [Fact]
    public void Clear_ResetsEverything()
    {
        var aggregator = new ActivityAggregator(maxTrackedFiles: 1);
        aggregator.Record(Ev(FileOperation.Read, FileA, bytes: 10));
        aggregator.Record(Ev(FileOperation.Read, FileB, bytes: 20)); // over the cap
        aggregator.Record(Ev(FileOperation.Write, FileA, status: IoStatus.Failed));
        Assert.Equal(1L, aggregator.UntrackedEvents);
        Assert.Equal(1L, aggregator.FailedEvents);

        aggregator.Clear();

        Assert.Equal(0, aggregator.TrackedFiles);
        Assert.Equal(0L, aggregator.FailedEvents);
        Assert.Equal(0L, aggregator.UntrackedEvents);
        Assert.Empty(aggregator.GetAllFiles());
        Assert.Empty(aggregator.GetAllProcesses());
        Assert.Empty(aggregator.GetDriveTotals());
        Assert.Empty(aggregator.TakeChangedFiles());
        Assert.Empty(aggregator.TakeChangedProcesses());

        // Counting starts afresh, including the file cap.
        aggregator.Record(Ev(FileOperation.Read, FileB, bytes: 5));

        FileStatsSnapshot b = Assert.Single(aggregator.GetAllFiles());
        Assert.Equal(FileB, b.Path);
        Assert.Equal(1L, b.Reads);
        Assert.Equal(5L, Assert.Single(aggregator.GetDriveTotals()).BytesRead);
    }

    private static FileActivityEvent Ev(
        FileOperation operation,
        string path,
        long bytes = 0,
        FileEventFlags flags = FileEventFlags.None,
        IoStatus status = IoStatus.Success,
        int processId = 100,
        string processName = "app.exe",
        int second = 0) => new()
    {
        Operation = operation,
        Path = path,
        Drive = char.ToUpperInvariant(path[0]),
        Bytes = bytes,
        Flags = flags,
        Status = status,
        ProcessId = processId,
        ProcessName = processName,
        Timestamp = T0.AddSeconds(second),
    };

    private static FileStatsSnapshot FileStats(ActivityAggregator aggregator, string path) =>
        Assert.Single(aggregator.GetAllFiles(), f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>The display rule, written out independently of how the aggregator caches the text.</summary>
    private static string ExpectedProcesses(List<string> distinct) =>
        distinct.Count <= 3
            ? string.Join(", ", distinct)
            : $"{string.Join(", ", distinct.Take(3))} +{distinct.Count - 3} more";
}
