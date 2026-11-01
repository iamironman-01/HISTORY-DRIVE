using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using DriveMonitor.Core.Export;
using DriveMonitor.Core.Interop;
using DriveMonitor.Core.Models;
using DriveMonitor.Tests.TestSupport;
using Microsoft.Win32.SafeHandles;

namespace DriveMonitor.Tests.Export;

/// <summary>The background CSV log of published events.</summary>
public sealed class CsvActivityLogTests
{
    /// <summary>The links a standard user can plant where the elevated log creates or deletes its parts.</summary>
    public enum PlantedLink
    {
        Junction,
        HardLink,
        SymbolicLink,
    }

    private static readonly string HeaderLine = string.Join(",", Csv.EventHeader) + "\r\n";
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private static readonly int HeaderBytes = Bom.Length + Encoding.UTF8.GetByteCount(HeaderLine);

    /// <summary>Bytes of one encoded row; every <see cref="Event"/> row has the same length.</summary>
    private static readonly int RowBytes = Encoding.UTF8.GetByteCount(RowText(Event(0)));

    [Fact]
    public void Constructor_CreatesTheFolderAndWritesTheHeaderAtOnce()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.FullPath, "nested", "deeper", "log.csv");

        using var log = new CsvActivityLog(path);

        Assert.Equal(path, log.FilePath);
        byte[] bytes = ReadShared(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal(HeaderLine, Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public void Constructor_ReplacesAnExistingLog()
    {
        using var temp = new TempDirectory();
        string path = temp.PathOf("log.csv");
        File.WriteAllText(path, "old content that must disappear\r\n");

        using (new CsvActivityLog(path))
        {
        }

        Assert.Equal(HeaderLine, File.ReadAllText(path));
    }

    [Fact]
    public void Dispose_FlushesEveryEventInOrder()
    {
        using var temp = new TempDirectory();
        string path = temp.PathOf("log.csv");
        var log = new CsvActivityLog(path);

        for (int i = 0; i < 100; i++)
        {
            log.Write(new FileActivityEvent
            {
                Sequence = i,
                Timestamp = new DateTime(2026, 10, 30, 12, 0, 0),
                Operation = FileOperation.Read,
                Path = $@"E:\file{i}.txt",
                Drive = 'E',
                Bytes = i,
            });
        }

        log.Dispose();

        Assert.Equal(100L, log.Written);
        Assert.Equal(0L, log.Dropped);
        Assert.Null(log.Error);
        List<string[]> records = Rfc4180.Parse(File.ReadAllText(path));
        Assert.Equal(101, records.Count);
        Assert.Equal(Csv.EventHeader, records[0]);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal($@"E:\file{i}.txt", records[i + 1][2]);
            Assert.Equal(i.ToString(CultureInfo.InvariantCulture), records[i + 1][4]);
        }
    }

    [Fact]
    public void Write_AfterDispose_IsCountedAsDropped()
    {
        using var temp = new TempDirectory();
        var log = new CsvActivityLog(temp.PathOf("log.csv"));
        log.Dispose();

        log.Write(new FileActivityEvent { Path = @"E:\late.txt", Drive = 'E' });

        Assert.Equal(1L, log.Dropped);
        Assert.Equal(0L, log.Written);
    }

    [Fact]
    public void Dispose_Twice_IsHarmless()
    {
        using var temp = new TempDirectory();
        var log = new CsvActivityLog(temp.PathOf("log.csv"));

        log.Dispose();

        Assert.Null(Record.Exception(log.Dispose));
    }

    [Fact]
    public void Dispose_WhenTheDriveStopsAcceptingData_ReturnsAndCountsTheRestAsDropped()
    {
        using var destination = new PipeDestination(); // never read: every write beyond the pipe buffer stalls
        var log = new CsvActivityLog(destination.FilePath) { StallTimeout = TimeSpan.FromMilliseconds(300) };
        for (int i = 0; i < 5_000; i++)
            log.Write(Event(i));

        var clock = Stopwatch.StartNew();
        Exception? error = Record.Exception(log.Dispose);
        clock.Stop();

        Assert.Null(error);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took {clock.Elapsed}");
        Assert.True(
            SpinWait.SpinUntil(() => log.Written + log.Dropped == 5_000, TimeSpan.FromSeconds(10)),
            $"written {log.Written} + dropped {log.Dropped} != 5000");
        Assert.True(log.Dropped > 0);
        Assert.IsType<TimeoutException>(log.Error);
    }

    [Fact]
    public void Dispose_WaitsAsLongAsTheDriveKeepsAcceptingData()
    {
        using var destination = new PipeDestination();
        var log = new CsvActivityLog(destination.FilePath) { StallTimeout = TimeSpan.FromSeconds(1) };
        const int count = 4_000;
        for (int i = 0; i < count; i++)
            log.Write(Event(i));

        // About 250 KB/s: draining takes longer than StallTimeout, but every chunk lands well within it.
        destination.StartReading(pause: TimeSpan.FromMilliseconds(50));
        log.Dispose();

        Assert.Equal(count, log.Written);
        Assert.Equal(0L, log.Dropped);
        Assert.Null(log.Error);
        List<string[]> records = Rfc4180.Parse(Encoding.UTF8.GetString(destination.WaitForEnd()).TrimStart('\uFEFF'));
        Assert.Equal(count + 1, records.Count);
        Assert.Equal(Event(count - 1).Path, records[^1][2]);
    }

    [Fact]
    public void AfterAWriteError_EveryUnwrittenAndLaterEventIsCountedAsDropped()
    {
        using var destination = new PipeDestination();
        destination.StartReading();
        var log = new CsvActivityLog(destination.FilePath);
        log.Write(Event(0));
        Assert.True(SpinWait.SpinUntil(() => log.Written == 1, TimeSpan.FromSeconds(5)), "the first event was not written");

        destination.Disconnect(); // the drive is removed: the next write fails
        log.Write(Event(1));
        Assert.True(SpinWait.SpinUntil(() => log.Error is not null, TimeSpan.FromSeconds(5)), "the write error was not reported");
        for (int i = 2; i < 1_002; i++)
            log.Write(Event(i));

        // Counted at once, while the log is still open: nothing waits in memory for a pump that has stopped.
        Assert.IsAssignableFrom<IOException>(log.Error);
        Assert.Equal(1L, log.Written);
        Assert.Equal(1_001L, log.Dropped);

        log.Dispose();

        Assert.Equal(1L, log.Written);
        Assert.Equal(1_001L, log.Dropped);
    }

    [Fact]
    public void Write_PastTheSizeLimit_ContinuesInANewPartWithItsOwnHeader()
    {
        using var temp = new TempDirectory();
        long limit = HeaderBytes + (3 * RowBytes);
        var log = new CsvActivityLog(temp.PathOf("log.csv"), maxFileBytes: limit);

        for (int i = 0; i < 10; i++)
            log.Write(Event(i));
        log.Dispose();

        Assert.Equal(10L, log.Written);
        Assert.Equal(temp.PathOf("log.004.csv"), log.CurrentFilePath);
        string[] parts = [temp.PathOf("log.csv"), temp.PathOf("log.002.csv"), temp.PathOf("log.003.csv"), temp.PathOf("log.004.csv")];
        Assert.Equal(parts.Order(), Directory.GetFiles(temp.FullPath).Order());
        Assert.All(parts, part => Assert.InRange(new FileInfo(part).Length, HeaderBytes + RowBytes, limit));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => Event(i).Path), parts.SelectMany(PathsIn));
    }

    [Fact]
    public void Rotation_KeepsOnlyTheNewestParts()
    {
        using var temp = new TempDirectory();
        var log = new CsvActivityLog(temp.PathOf("log.csv"), maxFileBytes: HeaderBytes + (3 * RowBytes), maxFiles: 2);

        for (int i = 0; i < 10; i++)
            log.Write(Event(i));
        log.Dispose();

        Assert.Equal(10L, log.Written); // written, even though the first parts were deleted later
        string[] kept = [temp.PathOf("log.003.csv"), temp.PathOf("log.004.csv")];
        Assert.Equal(kept.Order(), Directory.GetFiles(temp.FullPath).Order());
        Assert.Equal(Enumerable.Range(6, 4).Select(i => Event(i).Path), kept.SelectMany(PathsIn));
    }

    [Fact]
    public void Rotation_ARowLargerThanTheLimit_GetsAPartOfItsOwn()
    {
        using var temp = new TempDirectory();
        var log = new CsvActivityLog(temp.PathOf("log.csv"), maxFileBytes: HeaderBytes + 10);

        for (int i = 0; i < 3; i++)
            log.Write(Event(i));
        log.Dispose();

        Assert.Equal(3L, log.Written);
        Assert.Equal(new[] { Event(0).Path }, PathsIn(temp.PathOf("log.csv")));
        Assert.Equal(new[] { Event(1).Path }, PathsIn(temp.PathOf("log.002.csv")));
        Assert.Equal(new[] { Event(2).Path }, PathsIn(temp.PathOf("log.003.csv")));
    }

    [Theory]
    [InlineData("log.csv", true)]
    [InlineData("LOG.CSV", true)]
    [InlineData("log.002.csv", true)]
    [InlineData("log.1234.csv", true)]
    [InlineData("log.csv.002", false)]
    [InlineData("log..csv", false)]
    [InlineData("log.x02.csv", false)]
    [InlineData("log2.csv", false)]
    [InlineData("log.002.txt", false)]
    [InlineData("other.csv", false)]
    public void IsLogFile_MatchesTheFileAndItsLaterParts(string name, bool expected)
    {
        using var temp = new TempDirectory();
        using var log = new CsvActivityLog(temp.PathOf("log.csv"));

        Assert.Equal(expected, log.IsLogFile(temp.PathOf(name)));
    }

    [Fact]
    public void Constructor_WhenTheDriveIsAlreadyShortOfSpace_ThrowsAndCreatesNoFile()
    {
        using var temp = new TempDirectory();
        string path = temp.PathOf("log.csv");

        IOException error = Assert.Throws<IOException>(() => new CsvActivityLog(path, minFreeBytes: long.MaxValue));

        Assert.StartsWith("Only ", error.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Write_WhenTheDriveRunsShortOfSpace_StopsTheLogWithAnError()
    {
        using var temp = new TempDirectory();
        string path = temp.PathOf("log.csv");
        var log = new CsvActivityLog(path, minFreeBytes: 1024);
        log.FreeSpaceProbe = _ => 1023; // the drive filled up after the log started

        log.Write(Event(0));
        Assert.True(SpinWait.SpinUntil(() => log.Error is not null, TimeSpan.FromSeconds(5)), "the log did not stop");
        log.Write(Event(1));
        log.Dispose();

        IOException error = Assert.IsType<IOException>(log.Error);
        Assert.Contains("1023 B is free", error.Message);
        Assert.Equal(0L, log.Written);
        Assert.Equal(2L, log.Dropped);
        Assert.Equal(HeaderLine, File.ReadAllText(path));
    }

    [Fact]
    public void Constructor_RefusesALogFolderThatIsAJunction()
    {
        using var temp = new TempDirectory();
        string victim = Directory.CreateDirectory(temp.PathOf("victim")).FullName;
        string logs = temp.PathOf("Logs");
        using IDisposable planted = FileLinks.CreateJunction(logs, victim);

        Assert.Throws<IOException>(() => new CsvActivityLog(Path.Combine(logs, "log.csv")));
        Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
    }

    [Theory]
    [InlineData(PlantedLink.Junction)]
    [InlineData(PlantedLink.HardLink)]
    [InlineData(PlantedLink.SymbolicLink)]
    public void Constructor_RefusesALinkWithTheFilesName(PlantedLink link)
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(Directory.CreateDirectory(temp.PathOf("Logs")).FullName, "log.csv");
        using Victim? victim = Victim.TryPlant(link, path, temp.FullPath);
        if (victim is null)
            return; // Windows refuses symbolic links without administrator rights or Developer Mode; CI runs elevated.

        IOException error = Assert.Throws<IOException>(() => new CsvActivityLog(path));

        Assert.Contains(path, error.Message);
        victim.AssertUntouched();
    }

    [Theory]
    [InlineData(PlantedLink.Junction)]
    [InlineData(PlantedLink.HardLink)]
    [InlineData(PlantedLink.SymbolicLink)]
    public void Rotation_WhenALinkHasTheNextPartsName_StopsTheLogWithAnError(PlantedLink link)
    {
        using var temp = new TempDirectory();
        string logs = Directory.CreateDirectory(temp.PathOf("Logs")).FullName;
        string next = Path.Combine(logs, "log.002.csv");
        using Victim? victim = Victim.TryPlant(link, next, temp.FullPath);
        if (victim is null)
            return; // Windows refuses symbolic links without administrator rights or Developer Mode; CI runs elevated.
        var log = new CsvActivityLog(Path.Combine(logs, "log.csv"), maxFileBytes: HeaderBytes + (3 * RowBytes));

        for (int i = 0; i < 10; i++)
            log.Write(Event(i));
        log.Dispose();

        IOException error = Assert.IsType<IOException>(log.Error);
        Assert.Contains(next, error.Message);
        Assert.Equal(3L, log.Written); // the first part; the row that starts the next one, and every later row, is dropped
        Assert.Equal(7L, log.Dropped);
        Assert.Equal(Path.Combine(logs, "log.csv"), log.CurrentFilePath);
        Assert.Equal(Enumerable.Range(0, 3).Select(i => Event(i).Path), PathsIn(Path.Combine(logs, "log.csv")));
        victim.AssertUntouched();
    }

    [Theory]
    [InlineData(PlantedLink.Junction)]
    [InlineData(PlantedLink.HardLink)]
    [InlineData(PlantedLink.SymbolicLink)]
    public void Rotation_LeavesALinkThatTookAnOldPartsName_AndGoesOn(PlantedLink link)
    {
        using var temp = new TempDirectory();
        string logs = Directory.CreateDirectory(temp.PathOf("Logs")).FullName;
        string first = Path.Combine(logs, "log.csv");
        var log = new CsvActivityLog(first, maxFileBytes: HeaderBytes + (3 * RowBytes), maxFiles: 2);
        for (int i = 0; i < 4; i++)
            log.Write(Event(i));
        Assert.True(SpinWait.SpinUntil(() => log.Written == 4, TimeSpan.FromSeconds(5)), "the second part was not started");

        // The log has closed its first part; someone allowed to delete it puts a link in its place, which
        // the log would delete before its third part.
        File.Delete(first);
        using Victim? victim = Victim.TryPlant(link, first, temp.FullPath);
        for (int i = 4; i < 10; i++)
            log.Write(Event(i));
        log.Dispose();
        if (victim is null)
            return; // Windows refuses symbolic links without administrator rights or Developer Mode; CI runs elevated.

        Assert.Null(log.Error);
        Assert.Equal(10L, log.Written);
        Assert.True(Path.Exists(first), "the link was deleted");
        victim.AssertUntouched();
        string[] kept = [first, Path.Combine(logs, "log.003.csv"), Path.Combine(logs, "log.004.csv")];
        Assert.Equal(kept.Order(), Directory.EnumerateFileSystemEntries(logs).Order());
    }

    [Fact]
    public void Rotation_EmptiesAPlainFileLeftAtAPartsName()
    {
        using var temp = new TempDirectory();
        File.WriteAllText(temp.PathOf("log.002.csv"), "a part of an earlier log, which must disappear\r\n");
        var log = new CsvActivityLog(temp.PathOf("log.csv"), maxFileBytes: HeaderBytes + (3 * RowBytes));

        for (int i = 0; i < 6; i++)
            log.Write(Event(i));
        log.Dispose();

        Assert.Null(log.Error);
        Assert.Equal(Enumerable.Range(3, 3).Select(i => Event(i).Path), PathsIn(temp.PathOf("log.002.csv")));
    }

    [Fact]
    public void TheLogFolder_CannotBeRenamedOrDeletedWhileTheLogRuns()
    {
        using var temp = new TempDirectory();
        string logs = Directory.CreateDirectory(temp.PathOf("Logs")).FullName;
        var log = new CsvActivityLog(Path.Combine(logs, "log.csv"));

        // Moved away between two parts, the folder could be replaced by a junction that the next part follows.
        Assert.False(CanOpenForDelete(logs), "the folder can be renamed or deleted while the log runs");

        log.Dispose();

        Assert.True(CanOpenForDelete(logs), "the folder is still held after the log closed");
    }

    [Fact]
    public void Constructor_TakesAFolderPathLongerThanMaxPath()
    {
        using var temp = new TempDirectory();
        string logs = temp.FullPath;
        while (logs.Length <= 300)
            logs = Path.Combine(logs, "a folder with a long name");
        var log = new CsvActivityLog(Path.Combine(logs, "log.csv"), maxFileBytes: HeaderBytes + RowBytes, maxFiles: 2);

        for (int i = 0; i < 3; i++)
            log.Write(Event(i));
        log.Dispose();

        Assert.Null(log.Error);
        Assert.Equal(3L, log.Written);
        string[] kept = [Path.Combine(logs, "log.002.csv"), Path.Combine(logs, "log.003.csv")];
        Assert.Equal(kept.Order(), Directory.GetFiles(logs).Order());
    }

    [Fact]
    public void Constructor_PathOfAFolder_Throws()
    {
        using var temp = new TempDirectory();

        IOException error = Assert.Throws<IOException>(() => new CsvActivityLog(temp.FullPath + Path.DirectorySeparatorChar));

        Assert.Contains("is a folder", error.Message);
    }

    [Fact]
    public void Constructor_InvalidCapacity_ThrowsBeforeTouchingTheFileOrTheFolder()
    {
        using var temp = new TempDirectory();
        string logs = Directory.CreateDirectory(temp.PathOf("Logs")).FullName;
        string path = Path.Combine(logs, "log.csv");
        File.WriteAllText(path, "an earlier log, which must survive\r\n");

        Assert.Throws<ArgumentOutOfRangeException>(() => new CsvActivityLog(path, capacity: 0));

        // Nothing may be left open by a constructor that failed: neither the folder nor the file.
        Assert.True(CanOpenForDelete(logs), "the folder is still held");
        Assert.Equal("an earlier log, which must survive\r\n", Encoding.UTF8.GetString(ReadShared(path)));
    }

    // ---- a trusted root, as the app passes %LOCALAPPDATA% for %LOCALAPPDATA%\DriveMonitor\Logs ----

    [Fact]
    public void TrustedRoot_TheFoldersBelowItAreCreated_AndTheLogRotatesAndDeletesItsPartsThere()
    {
        using var temp = new TempDirectory();
        string root = Directory.CreateDirectory(temp.PathOf("Local")).FullName;
        string logs = Path.Combine(root, "DriveMonitor", "Logs");
        var log = new CsvActivityLog(Path.Combine(logs, "log.csv"), maxFileBytes: HeaderBytes + (3 * RowBytes), maxFiles: 2, trustedRoot: root);

        Assert.False(CanOpenForDelete(logs), "the folder can be renamed or deleted while the log runs");
        for (int i = 0; i < 10; i++)
            log.Write(Event(i));
        log.Dispose();

        Assert.Null(log.Error);
        Assert.Equal(10L, log.Written);
        string[] kept = [Path.Combine(logs, "log.003.csv"), Path.Combine(logs, "log.004.csv")];
        Assert.Equal(kept.Order(), Directory.GetFiles(logs).Order());
        Assert.Equal(Enumerable.Range(6, 4).Select(i => Event(i).Path), kept.SelectMany(PathsIn));
        Assert.True(CanOpenForDelete(logs), "the folder is still held after the log closed");
    }

    [Fact]
    public void TrustedRoot_AJunctionInPlaceOfAFolderAboveTheLogFolder_IsRefused_AndNothingIsWrittenThroughIt()
    {
        // The review's case: while no log runs, a program without elevation replaces %LOCALAPPDATA%\DriveMonitor
        // with a junction to a folder of its choice that has a Logs folder, here with a file of the log's name.
        using var temp = new TempDirectory();
        string root = Directory.CreateDirectory(temp.PathOf("Local")).FullName;
        string elsewhere = Directory.CreateDirectory(temp.PathOf("elsewhere")).FullName;
        string elsewhereLog = Path.Combine(Directory.CreateDirectory(Path.Combine(elsewhere, "Logs")).FullName, "activity.csv");
        File.WriteAllText(elsewhereLog, "keep me");
        string app = Path.Combine(root, "DriveMonitor");
        using IDisposable planted = FileLinks.CreateJunction(app, elsewhere);

        IOException error = Assert.Throws<IOException>(() => new CsvActivityLog(Path.Combine(app, "Logs", "activity.csv"), trustedRoot: root));

        Assert.Equal($"'{app}' is a junction, symbolic link or other reparse point; it is not followed.", error.Message);
        Assert.Equal(new[] { elsewhereLog }, Directory.EnumerateFileSystemEntries(Path.GetDirectoryName(elsewhereLog)!));
        Assert.Equal("keep me", File.ReadAllText(elsewhereLog));
    }

    [Fact]
    public void TrustedRoot_ALogFolderThatIsAJunction_IsRefused()
    {
        using var temp = new TempDirectory();
        string root = Directory.CreateDirectory(temp.PathOf("Local")).FullName;
        string victim = Directory.CreateDirectory(temp.PathOf("victim")).FullName;
        string logs = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "DriveMonitor")).FullName, "Logs");
        using IDisposable planted = FileLinks.CreateJunction(logs, victim);

        IOException error = Assert.Throws<IOException>(() => new CsvActivityLog(Path.Combine(logs, "log.csv"), trustedRoot: root));

        Assert.Contains($"'{logs}' is a junction", error.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(victim));
    }

    [Fact]
    public void TrustedRoot_TheRootItselfIsTrusted_EvenWhenItIsAJunction()
    {
        // An administrator may have redirected %LOCALAPPDATA%; the log may also be written directly in the root.
        using var temp = new TempDirectory();
        string redirected = Directory.CreateDirectory(temp.PathOf("redirected")).FullName;
        string root = temp.PathOf("Local");
        using IDisposable junction = FileLinks.CreateJunction(root, redirected);

        using (var log = new CsvActivityLog(Path.Combine(root, "log.csv"), trustedRoot: root))
            log.Write(Event(0));

        Assert.Equal(new[] { Event(0).Path }, PathsIn(Path.Combine(redirected, "log.csv")));
    }

    [Theory]
    [InlineData(@"..\log.csv")]
    [InlineData(@"..\elsewhere\log.csv")]
    [InlineData(@"..\Local2\log.csv")]
    public void TrustedRoot_AFileThatIsNotBelowIt_IsRejectedBeforeAnythingIsCreated(string relativeToRoot)
    {
        using var temp = new TempDirectory();
        string root = Directory.CreateDirectory(temp.PathOf("Local")).FullName;
        string path = Path.GetFullPath(Path.Combine(root, relativeToRoot));

        Assert.Throws<ArgumentException>(() => new CsvActivityLog(path, trustedRoot: root));

        Assert.Equal(new[] { root }, Directory.EnumerateFileSystemEntries(temp.FullPath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
    }

    [Fact]
    public void WithoutATrustedRoot_AJunctionAboveTheLogFolder_IsStillFollowed()
    {
        // drivemon --csv: the user chose the path, links on the way included; only the log's folder itself and its
        // files are checked. It is what the app did with its own folder before it passed a trusted root.
        using var temp = new TempDirectory();
        string elsewhere = Directory.CreateDirectory(temp.PathOf("elsewhere")).FullName;
        string elsewhereLogs = Directory.CreateDirectory(Path.Combine(elsewhere, "Logs")).FullName;
        string chosen = temp.PathOf("chosen");
        using IDisposable junction = FileLinks.CreateJunction(chosen, elsewhere);

        using (new CsvActivityLog(Path.Combine(chosen, "Logs", "activity.csv")))
        {
        }

        Assert.Equal(HeaderLine, File.ReadAllText(Path.Combine(elsewhereLogs, "activity.csv")));
    }

    /// <summary>Whether another program could rename or delete the folder now, which takes an open with delete access.</summary>
    private static bool CanOpenForDelete(string folder)
    {
        const uint Delete = 0x0001_0000;
        const uint ShareAll = 0x7;
        const uint FileFlagBackupSemantics = 0x0200_0000;
        using SafeFileHandle handle = NativeMethods.CreateFileW(
            folder, Delete, ShareAll, IntPtr.Zero, NativeMethods.OPEN_EXISTING, FileFlagBackupSemantics, IntPtr.Zero);
        return !handle.IsInvalid;
    }

    /// <summary>Reads the file while the log may still hold it open for writing.</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>The paths logged in one part, after checking that the part starts with its own BOM and header.</summary>
    private static List<string> PathsIn(string part)
    {
        byte[] bytes = File.ReadAllBytes(part);
        Assert.Equal(Bom, bytes[..3]);
        List<string[]> records = Rfc4180.Parse(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        Assert.Equal(Csv.EventHeader, records[0]);
        return records.Skip(1).Select(r => r[2]).ToList();
    }

    private static string RowText(FileActivityEvent ev)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        Csv.WriteEvent(text, ev);
        return text.ToString();
    }

    private static FileActivityEvent Event(int i) => new()
    {
        Sequence = i,
        Timestamp = new DateTime(2026, 10, 30, 12, 0, 0),
        Operation = FileOperation.Write,
        Path = $@"E:\photos\2026\holiday\img_{i:D5}.jpg",
        Drive = 'E',
        Bytes = 4096,
        Offset = 0,
        ProcessName = "copy.exe",
        ProcessId = 4242,
        ThreadId = 7,
    };

    /// <summary>
    /// What a link planted at one of the log's names leads to: a folder for a junction, a file for the other
    /// links, standing for a file only administrators may change. It must stay untouched.
    /// </summary>
    private sealed class Victim : IDisposable
    {
        private const string Content = "keep me";
        private readonly string _path;
        private readonly IDisposable? _junction;

        private Victim(string path, IDisposable? junction)
        {
            _path = path;
            _junction = junction;
        }

        /// <summary>Creates the victim in <paramref name="folder"/> and plants <paramref name="link"/> to it at <paramref name="linkPath"/>.</summary>
        /// <returns>Null when Windows refuses the link: a symbolic link needs administrator rights or Developer Mode.</returns>
        public static Victim? TryPlant(PlantedLink link, string linkPath, string folder)
        {
            if (link == PlantedLink.Junction)
            {
                string target = Directory.CreateDirectory(Path.Combine(folder, "victim")).FullName;
                return new Victim(target, FileLinks.CreateJunction(linkPath, target));
            }

            string file = Path.Combine(folder, "victim.txt");
            File.WriteAllText(file, Content);
            if (link == PlantedLink.HardLink)
                FileLinks.CreateHardLink(linkPath, file);
            else if (!FileLinks.TryCreateFileSymbolicLink(linkPath, file))
                return null;
            return new Victim(file, null);
        }

        public void AssertUntouched()
        {
            if (_junction is null)
                Assert.Equal(Content, File.ReadAllText(_path));
            else
                Assert.Empty(Directory.EnumerateFileSystemEntries(_path));
        }

        /// <summary>Removes a junction, which the temporary folder's recursive delete could not.</summary>
        public void Dispose() => _junction?.Dispose();
    }

    /// <summary>
    /// A named pipe the log writes into, standing in for a drive whose speed the test controls. Until
    /// <see cref="StartReading"/> is called nobody reads, so writes stall as on a hung network share.
    /// </summary>
    private sealed class PipeDestination : IDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly Task _connected;
        private readonly CancellationTokenSource _stop = new();
        private readonly MemoryStream _received = new();
        private Task _reader = Task.CompletedTask;

        public PipeDestination()
        {
            string name = "DriveMonitor.Tests." + Guid.NewGuid().ToString("N");
            FilePath = $@"\\.\pipe\{name}";
            _server = new NamedPipeServerStream(
                name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 4096, outBufferSize: 4096);
            _connected = _server.WaitForConnectionAsync();
        }

        public string FilePath { get; }

        /// <summary>Reads everything the log writes, pausing after each read to set the drive's speed.</summary>
        public void StartReading(TimeSpan pause = default)
        {
            _reader = Task.Run(async () =>
            {
                var buffer = new byte[16 * 1024];
                try
                {
                    await _connected;
                    int read;
                    while ((read = await _server.ReadAsync(buffer, _stop.Token)) > 0)
                    {
                        lock (_received)
                            _received.Write(buffer, 0, read);
                        if (pause > TimeSpan.Zero)
                            await Task.Delay(pause, _stop.Token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // The test stopped reading.
                }
            });
        }

        /// <summary>The drive goes away: the log's next write fails.</summary>
        public void Disconnect()
        {
            _stop.Cancel();
            _reader.Wait(TimeSpan.FromSeconds(5));
            _server.Dispose();
        }

        /// <summary>Waits until the log has closed its end, then returns everything it wrote.</summary>
        public byte[] WaitForEnd()
        {
            Assert.True(_reader.Wait(TimeSpan.FromSeconds(10)), "the log did not close the pipe");
            lock (_received)
                return _received.ToArray();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _reader.Wait(TimeSpan.FromSeconds(5));
            _server.Dispose();
            _stop.Dispose();
        }
    }
}
