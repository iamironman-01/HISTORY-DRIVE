using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using DriveMonitor.Core;
using DriveMonitor.Core.Models;
using DriveMonitor.Core.Tracing;
using DriveMonitor.Core.Volumes;

namespace DriveMonitor.Cli;

/// <summary>
/// End-to-end check: performs known file operations in a temporary folder on a drive and
/// verifies that the kernel trace reported each of them correctly.
/// </summary>
internal static class SelfTest
{
    private const int MiB = 1024 * 1024;
    private const FileOptions NoBuffering = (FileOptions)0x20000000; // FILE_FLAG_NO_BUFFERING

    private enum Verdict
    {
        Pass,
        Warn,
        Fail,
    }

    public static int Run(CliOptions options)
    {
        var report = new StringBuilder();
        void Say(string line = "")
        {
            Console.WriteLine(line);
            report.AppendLine(line);
        }

        Say($"DriveMonitor self-test  {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {RuntimeInformation.OSDescription}  .NET {Environment.Version}");
        if (!MonitorEngine.IsElevated)
        {
            Say("FAIL  not running as administrator - kernel file tracing needs an elevated prompt.");
            Save(options, report);
            return 1;
        }

        char drive = options.SelfTestDrive ?? PickDrive();
        TestFolder? created = TryCreateFolder(drive, Say);
        if (created is null)
        {
            Save(options, report);
            return 1;
        }

        using TestFolder testFolder = created;
        string folder = testFolder.FullPath;
        var paths = new TestPaths(
            folder,
            testFolder.PathOf("alpha.bin"),
            testFolder.PathOf("beta.bin"),
            testFolder.PathOf("scratch-delete-on-close.tmp"),
            testFolder.PathOf("does-not-exist.txt"),
            testFolder.PathOf("opened-before-start.bin"));
        Say($"Drive {drive}:  test folder {folder}");

        var raw = new List<string>();
        var events = new List<FileActivityEvent>();
        int ownPid = Environment.ProcessId;
        using var engine = new MonitorEngine(new MonitorOptions
        {
            SessionName = "DriveMonitor-SelfTest",
            ExcludeOwnProcess = false,
            CaptureDirectoryListings = true,
        });
        engine.RawOperationObserver = io =>
        {
            if (io.ProcessId == ownPid && io.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase))
                raw.Add(DescribeRaw(io));
        };
        engine.Drives.SetMonitored(drive, true);

        // From here on, every way out stops the kernel session, which outlives the process, and removes the test
        // files: the normal end, a failure, Ctrl+C, closing the console window and a crash.
        var interruptions = new Interruptions(engine, testFolder);
        bool ran;
        string? leftover;
        try
        {
            ran = Exercise(engine, testFolder, paths, interruptions, events, Say);
        }
        finally
        {
            engine.Stop();
            engine.DrainEvents(events);
            leftover = testFolder.Remove();
            Note(leftover, Say);
            interruptions.Dispose();
        }

        if (interruptions.StopRequested)
        {
            Say(leftover is null
                ? "Cancelled: the kernel session is stopped and the test files are removed."
                : "Cancelled: the kernel session is stopped.");
            Save(options, report);
            return 1;
        }

        if (!ran)
        {
            Save(options, report);
            return 1;
        }

        // ---- report ----
        List<Check> checks = Evaluate(events, ownPid, paths);
        checks.Add(new Check("Kernel did not drop events", engine.LostEvents == 0 ? Verdict.Pass : Verdict.Warn, $"lost {engine.LostEvents:N0}"));
        checks.Add(new Check("No errors inside trace callbacks", engine.CallbackErrors == 0 ? Verdict.Pass : Verdict.Fail,
            engine.CallbackErrors == 0 ? "none" : $"{engine.CallbackErrors:N0}, last: {engine.LastCallbackError}"));

        Say();
        Say("Checks:");
        foreach (Check c in checks)
            Say($"  {c.Verdict.ToString().ToUpperInvariant(),-5} {c.Name} - {c.Observed}");

        List<FileActivityEvent> mine = events.Where(e => IsMine(e, ownPid, folder)).ToList();
        Say();
        Say($"Events in the test folder from this process ({mine.Count}):");
        foreach (FileActivityEvent e in mine)
        {
            Say($"  {e.Timestamp:HH:mm:ss.fff} {e.Operation,-7} {e.Bytes,9:N0} @{e.Offset,-9} {e.Flags,-22} {e.Status,-7} {(e.NtStatus == 0 ? "" : $"0x{unchecked((uint)e.NtStatus):X8}"),-10} {Path.GetFileName(e.Path)}  {e.Detail}");
        }

        Say();
        Say($"Raw kernel fields ({raw.Count}):");
        foreach (string line in raw)
            Say("  " + line);

        Say();
        Say($"Diagnostics: kernel file events {engine.KernelEvents:N0}, published {engine.PublishedEvents:N0}, " +
            $"lost {engine.LostEvents:N0}, dropped {engine.DroppedEvents:N0}, without file name {engine.UnnamedEvents:N0}, " +
            $"other events on {drive}: {events.Count - mine.Count:N0}");
        Say($"             callback errors {engine.CallbackErrors:N0}, session restarts {engine.Restarts}, " +
            $"name rundown requested {engine.FileNameRundownRequested}, rename paths seen {engine.PathOperationsSeen}");

        int failures = checks.Count(c => c.Verdict == Verdict.Fail);
        int warnings = checks.Count(c => c.Verdict == Verdict.Warn);
        Say(failures == 0
            ? $"RESULT: PASS ({warnings} warning(s))"
            : $"RESULT: FAIL ({failures} failure(s), {warnings} warning(s))");
        Save(options, report);
        return failures == 0 ? 0 : 1;
    }

    private sealed record Check(string Name, Verdict Verdict, string Observed);

    private sealed record TestPaths(string Folder, string FileA, string FileB, string FileTemp, string Missing, string Preopened);

    /// <summary>
    /// Starts the kernel session, performs the operations under test and collects their events. The caller
    /// stops the session and removes the files, whatever happens here.
    /// </summary>
    /// <returns>False when the test could not run (the reason has been said) or was cancelled.</returns>
    private static bool Exercise(
        MonitorEngine engine,
        TestFolder testFolder,
        TestPaths paths,
        Interruptions interruptions,
        List<FileActivityEvent> events,
        Action<string> say)
    {
        var (folder, fileA, fileB, fileTemp, missing, preopened) = paths;

        // A file whose handle exists before the session starts: its name can only come from the
        // kernel's file-name rundown.
        FileStream? earlyHandle = null;
        try
        {
            try
            {
                earlyHandle = testFolder.CreateNew(preopened, FileAccess.ReadWrite, FileShare.Read);
                earlyHandle.Write(new byte[64 * 1024]);
                earlyHandle.Flush(flushToDisk: true);
            }
            catch (Exception ex)
            {
                say($"FAIL  could not prepare the test folder: {ex.GetType().Name}: {ex.Message}");
                return false;
            }

            if (interruptions.StopRequested)
                return false;

            var startClock = Stopwatch.StartNew();
            try
            {
                engine.Start();
            }
            catch (MonitorStartException ex)
            {
                say($"FAIL  could not start the kernel session: {ex.Message}");
                return false;
            }

            say($"Session started in {startClock.ElapsedMilliseconds} ms; waiting for it to settle...");
            if (interruptions.Wait(TimeSpan.FromSeconds(2)))
                return false;

            // ---- the operations under test ----
            var data = new byte[MiB];
            Random.Shared.NextBytes(data);
            long cachedRead = 0, uncachedRead = 0;
            try
            {
                using (earlyHandle)
                {
                    earlyHandle.Position = 0;
                    _ = earlyHandle.Read(new byte[64 * 1024]);
                    earlyHandle.Position = 0;
                    earlyHandle.Write(data, 0, 4096);
                    earlyHandle.Flush(flushToDisk: true);
                }

                using (FileStream fs = testFolder.CreateNew(fileA, FileAccess.Write, FileShare.None, FileOptions.WriteThrough))
                {
                    for (int i = 0; i < 4; i++)
                        fs.Write(data, i * (MiB / 4), MiB / 4);
                }

                using (var fs = new FileStream(fileA, FileMode.Open, FileAccess.Read, FileShare.Read, 0))
                {
                    var buffer = new byte[64 * 1024];
                    int n;
                    while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
                        cachedRead += n;
                }

                uncachedRead = ReadUnbuffered(fileA);
                testFolder.Move(fileA, fileB);
                using (var fs = new FileStream(fileB, FileMode.Open, FileAccess.Write))
                    fs.SetLength(1000);
                testFolder.Delete(fileB);
                using (FileStream fs = testFolder.CreateNew(fileTemp, FileAccess.Write, FileShare.None, FileOptions.DeleteOnClose))
                    fs.Write(data, 0, 4096);
                testFolder.Forget(fileTemp); // deleted as its handle closed
                _ = Directory.GetFiles(folder);
                try
                {
                    using FileStream _ = File.OpenRead(missing);
                }
                catch (FileNotFoundException)
                {
                    // Expected: this is the failed-open case.
                }
            }
            catch (Exception ex)
            {
                say($"FAIL  the test operations themselves failed: {ex.GetType().Name}: {ex.Message}");
            }

            say($"Performed: read+write of a file opened before start, 1 MiB write-through write, {cachedRead:N0} B cached read, " +
                $"{uncachedRead:N0} B unbuffered read, rename, truncate, delete, delete-on-close, listing, failed open.");

            // ---- collect ----
            int ownPid = Environment.ProcessId;
            var waited = Stopwatch.StartNew();
            while (waited.Elapsed < TimeSpan.FromSeconds(10))
            {
                engine.DrainEvents(events);
                if (Evaluate(events, ownPid, paths).All(c => c.Verdict == Verdict.Pass))
                    break;
                if (interruptions.Wait(TimeSpan.FromMilliseconds(250)))
                    return false;
            }

            return true;
        }
        finally
        {
            // An open file cannot be deleted by the cleanup that follows.
            earlyHandle?.Dispose();
        }
    }

    /// <summary>
    /// Ctrl+C (or Ctrl+Break), closing the console window and a crash would each end the process at once,
    /// leaving the kernel session running (it outlives the process, keeps its buffers and one of the few
    /// system-wide logger slots, and goes on tracing every file operation) and the test files on the drive.
    /// These handlers turn them into a request that the main thread answers by cleaning up, and clean up
    /// themselves when the main thread cannot.
    /// </summary>
    private sealed class Interruptions : IDisposable
    {
        /// <summary>
        /// How long closing the console window waits for the main thread to remove the files. Windows ends the
        /// process about 5 seconds after the window is closed.
        /// </summary>
        private static readonly TimeSpan CleanupWait = TimeSpan.FromSeconds(3);

        private readonly MonitorEngine _engine;
        private readonly TestFolder _folder;

        // Never disposed: a handler that has already started may still use them while the process ends.
        private readonly CancellationTokenSource _stop = new();
        private readonly ManualResetEventSlim _cleanedUp = new();

        public Interruptions(MonitorEngine engine, TestFolder folder)
        {
            _engine = engine;
            _folder = folder;
            Console.CancelKeyPress += OnCancelKeyPress;
            AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
        }

        public bool StopRequested => _stop.IsCancellationRequested;

        /// <summary>Waits up to <paramref name="timeout"/>; true as soon as a stop is requested.</summary>
        public bool Wait(TimeSpan timeout) => _stop.Token.WaitHandle.WaitOne(timeout);

        /// <summary>Called once the main thread has stopped the session and removed the files.</summary>
        public void Dispose()
        {
            _cleanedUp.Set();
            Console.CancelKeyPress -= OnCancelKeyPress;
            AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
            AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
        }

        private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
        {
            // Keep the process alive: the main thread notices within a quarter of a second, or once the file
            // operation under way is done, and cleans up.
            e.Cancel = true;
            _stop.Cancel();
        }

        private void OnProcessExit(object? sender, EventArgs e)
        {
            // Raised when the console window is closed. The session goes first, whatever the main thread is
            // doing; the files are the main thread's to remove, or this handler's if it does not get there.
            StopEngine();
            _stop.Cancel();
            if (!_cleanedUp.Wait(CleanupWait))
                _folder.Remove();
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            // A crash on a worker thread, such as the engine's release timer, runs neither finally blocks nor
            // ProcessExit handlers.
            StopEngine();
            _folder.Remove();
        }

        private void StopEngine()
        {
            try
            {
                _engine.Stop();
            }
            catch (Exception)
            {
                // The process is ending; there is nothing left to try.
            }
        }
    }

    private static List<Check> Evaluate(List<FileActivityEvent> events, int ownPid, TestPaths paths)
    {
        var (folder, fileA, fileB, fileTemp, missing, preopened) = paths;
        List<FileActivityEvent> mine = events.Where(e => IsMine(e, ownPid, folder)).ToList();
        IEnumerable<FileActivityEvent> On(string path) => mine.Where(e => string.Equals(e.Path, path, StringComparison.OrdinalIgnoreCase));
        var checks = new List<Check>();

        FileActivityEvent? create = On(fileA).FirstOrDefault(e => e.Operation == FileOperation.Create);
        checks.Add(create switch
        {
            { Status: IoStatus.Success, Detail: "created" } => new Check("New file reported as created", Verdict.Pass, "created"),
            not null => new Check("New file reported as created", Verdict.Warn, $"{create.Status} {create.Detail}"),
            null => new Check("New file reported as created", Verdict.Fail, "no create event"),
        });

        long written = On(fileA).Where(e => e.Operation == FileOperation.Write && !e.IsPaging).Sum(e => e.Bytes);
        checks.Add(new Check("1 MiB of writes seen", written >= MiB ? Verdict.Pass : Verdict.Fail, $"{written:N0} bytes"));

        long cached = On(fileA).Where(e => e.Operation == FileOperation.Read && !e.IsPaging && (e.Flags & FileEventFlags.NonCached) == 0).Sum(e => e.Bytes);
        checks.Add(new Check("1 MiB of cached reads seen", cached >= MiB ? Verdict.Pass : Verdict.Fail, $"{cached:N0} bytes"));

        long uncached = On(fileA).Where(e => e.Operation == FileOperation.Read && !e.IsPaging && (e.Flags & FileEventFlags.NonCached) != 0).Sum(e => e.Bytes);
        checks.Add(new Check("1 MiB of unbuffered reads seen", uncached >= MiB ? Verdict.Pass : Verdict.Fail, $"{uncached:N0} bytes"));

        long readAtEof = On(fileA).Where(e => e.Operation == FileOperation.Read && !e.IsPaging).Sum(e => e.Bytes);
        checks.Add(new Check("Read bytes not over-counted", readAtEof <= 2L * MiB ? Verdict.Pass : Verdict.Warn, $"{readAtEof:N0} bytes for 2 MiB read"));

        FileActivityEvent? rename = On(fileA).FirstOrDefault(e => e.Operation == FileOperation.Rename && e.Status != IoStatus.Failed);
        checks.Add(rename is not null
            ? new Check("Rename seen", Verdict.Pass, "old name reported")
            : new Check("Rename seen", Verdict.Fail, "no rename event"));
        checks.Add(rename?.TargetPath switch
        {
            string target when string.Equals(target, fileB, StringComparison.OrdinalIgnoreCase)
                => new Check("Rename target reported", Verdict.Pass, Path.GetFileName(target)),
            string target => new Check("Rename target reported", Verdict.Warn, $"reported as {target}"),
            null => new Check("Rename target reported", Verdict.Warn, "this Windows build did not report the new name"),
        });

        FileActivityEvent? truncate = On(fileB).FirstOrDefault(e => (e.Flags & FileEventFlags.SizeChange) != 0);
        checks.Add(truncate switch
        {
            { Bytes: 1000 } => new Check("Truncate to 1,000 bytes seen", Verdict.Pass, truncate.Detail ?? ""),
            not null => new Check("Truncate to 1,000 bytes seen", Verdict.Warn, $"size reported as {truncate.Bytes:N0}"),
            null => new Check("Truncate to 1,000 bytes seen", Verdict.Fail, "no end-of-file change"),
        });

        // FAT rejects the newer disposition class, so Windows retries: one failed and one successful
        // delete event is normal there. A successful (or unconfirmed) one must exist.
        checks.Add(On(fileB).Any(e => e.Operation == FileOperation.Delete && e.Status != IoStatus.Failed)
            ? new Check("Delete seen", Verdict.Pass, "delete reported")
            : new Check("Delete seen", Verdict.Fail, "no successful delete event"));

        long earlyIo = On(preopened).Count(e => e.Operation is FileOperation.Read or FileOperation.Write);
        checks.Add(earlyIo > 0
            ? new Check("File opened before start is named", Verdict.Pass, $"{earlyIo} read/write event(s)")
            : new Check("File opened before start is named", Verdict.Warn,
                "no events: I/O on files opened before monitoring stays unnamed until they are reopened"));

        checks.Add(On(fileTemp).Any(e => (e.Flags & FileEventFlags.DeleteOnClose) != 0)
            ? new Check("Delete-on-close seen", Verdict.Pass, "flag reported")
            : new Check("Delete-on-close seen", Verdict.Fail, "no delete-on-close flag"));

        checks.Add(On(folder).Any(e => e.Operation == FileOperation.List)
            ? new Check("Folder listing seen", Verdict.Pass, "listing reported")
            : new Check("Folder listing seen", Verdict.Warn, "no listing event"));

        FileActivityEvent? failedOpen = On(missing).FirstOrDefault();
        checks.Add(failedOpen switch
        {
            { Status: IoStatus.Failed } => new Check("Failed open reported as failed", Verdict.Pass, failedOpen.Detail ?? ""),
            not null => new Check("Failed open reported as failed", Verdict.Warn, $"{failedOpen.Operation} {failedOpen.Status}"),
            null => new Check("Failed open reported as failed", Verdict.Fail, "no event for the missing file"),
        });

        int matched = mine.Count(e => e.Status != IoStatus.Unknown);
        double ratio = mine.Count == 0 ? 0 : (double)matched / mine.Count;
        checks.Add(new Check("Completions matched", ratio >= 0.9 ? Verdict.Pass : Verdict.Warn, $"{matched}/{mine.Count} ({ratio:P0})"));

        string expectedName = Path.GetFileName(Environment.ProcessPath) ?? "drivemon.exe";
        var names = mine.Select(e => e.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        checks.Add(names.Count > 0 && names.All(n => string.Equals(n, expectedName, StringComparison.OrdinalIgnoreCase))
            ? new Check("Process attributed", Verdict.Pass, expectedName)
            : new Check("Process attributed", Verdict.Warn, names.Count == 0 ? "no events" : string.Join(", ", names)));

        return checks;
    }

    private static bool IsMine(FileActivityEvent e, int ownPid, string folder) =>
        e.ProcessId == ownPid && e.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);

    private static string DescribeRaw(PendingIo io)
    {
        var inv = CultureInfo.InvariantCulture;
        return string.Create(inv,
            $"{io.Op,-8} {Path.GetFileName(io.Path),-28} irp={(io.Irp != 0 ? "yes" : "no"),-3} done={io.Completed,-5} " +
            $"status=0x{unchecked((uint)io.NtStatus):X8} info={io.CompletionInfo,-8} size={io.IoSize,-8} off={io.Offset,-8} " +
            $"irpFlags=0x{io.IrpFlags:X} createOpts=0x{io.CreateOptions:X} disp={io.CreateDisposition} class={io.InfoClass} extra={io.ExtraInfo}" +
            $"{(io.TargetPath is null ? "" : " target=" + io.TargetPath)}");
    }

    /// <summary>
    /// The drive root is usually writable by every user, and this process is elevated: see
    /// <see cref="TestFolder"/> for how the folder is kept free of anything another user controls.
    /// </summary>
    private static TestFolder? TryCreateFolder(char drive, Action<string> say)
    {
        try
        {
            return TestFolder.Create($"{drive}:\\");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            say($"FAIL  could not create the test folder on {drive}: {ex.Message}");
            return null;
        }
    }

    private static void Note(string? problem, Action<string> say)
    {
        if (problem is not null)
            say($"note: {problem}");
    }

    private static long ReadUnbuffered(string path)
    {
        const int chunk = 64 * 1024; // multiple of any sector size
        using Microsoft.Win32.SafeHandles.SafeFileHandle handle =
            File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, NoBuffering);
        long total = 0;
        unsafe
        {
            void* memory = NativeMemory.AlignedAlloc(chunk, 4096);
            try
            {
                var span = new Span<byte>(memory, chunk);
                int n;
                while ((n = RandomAccess.Read(handle, span, total)) > 0)
                {
                    total += n;
                    if (n < chunk)
                        break;
                }
            }
            finally
            {
                NativeMemory.AlignedFree(memory);
            }
        }

        return total;
    }

    private static char PickDrive()
    {
        DriveDescriptor? candidate = DriveCatalog.Enumerate()
            .Where(d => d.IsReady && DriveCatalog.IsMonitoredByDefault(d) && d.Kind is DriveKind.Fixed or DriveKind.Removable)
            .OrderByDescending(d => d.IsExternal)
            .FirstOrDefault();
        return candidate?.Letter ?? DriveCatalog.SystemDriveLetter;
    }

    private static void Save(CliOptions options, StringBuilder report)
    {
        if (options.ReportPath is null)
            return;
        try
        {
            File.WriteAllText(options.ReportPath, report.ToString(), Encoding.UTF8);
            Console.WriteLine($"Report saved to {options.ReportPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not save the report: {ex.Message}");
        }
    }
}
