using System.Diagnostics;
using System.Globalization;
using System.Text;
using DriveMonitor.Core;
using DriveMonitor.Core.Aggregation;
using DriveMonitor.Core.Models;
using DriveMonitor.Core.Volumes;

namespace DriveMonitor.Cli;

/// <summary>Prints file operations on the monitored drives until Ctrl+C or the duration elapses.</summary>
internal static class LiveMonitor
{
    /// <summary>
    /// How often the drives are enumerated even when no drive letter came or went, as the app does: catches a
    /// letter that changed hands between two checks.
    /// </summary>
    private static readonly TimeSpan FullDriveCheckInterval = TimeSpan.FromSeconds(10);

    public static int Run(CliOptions options)
    {
        if (!MonitorEngine.IsElevated)
        {
            Console.Error.WriteLine("drivemon needs an administrator command prompt: kernel file tracing requires elevation.");
            return 1;
        }

        using var engine = new MonitorEngine(new MonitorOptions
        {
            SessionName = "DriveMonitor-CLI",
            CaptureDirectoryListings = options.ShowListings,
        });

        // A crash on a worker thread, such as the engine's release timer, runs neither finally blocks nor
        // ProcessExit handlers, and the kernel session would outlive the process.
        AppDomain.CurrentDomain.UnhandledException += (_, _) => StopQuietly(engine);

        // Without --drives the default selection is kept up to date, so that a pendrive plugged in while
        // drivemon runs is monitored too, as the app does.
        DriveWatcher? watcher = null;
        List<char> selected;
        if (options.Drives is not null)
        {
            selected = options.Drives;
            if (selected.Count == 0)
            {
                Console.Error.WriteLine("No drives to monitor. Use --drives or --system.");
                return 1;
            }
        }
        else
        {
            watcher = new DriveWatcher(engine.Drives, options.IncludeSystemDrive, DriveWatcher.PresentLetters, () =>
            {
                // First, so that file names on a drive that just arrived map to its letter.
                engine.Volumes.Refresh();
                return DriveCatalog.Enumerate(engine.Volumes);
            });
            selected = watcher.Check().Select(d => d.Letter).ToList();
            if (options.IncludeSystemDrive && !selected.Contains(DriveCatalog.SystemDriveLetter))
                selected.Add(DriveCatalog.SystemDriveLetter);
        }

        engine.Drives.SetMonitored(selected);
        if (options.CsvPath is not null && !TryStartLog(engine, options.CsvPath, Console.Error))
            return 1;

        // drivemon never replaces its log, so this is the one the engine writes to until the end.
        CsvLogStatus? logStatus = engine.Log is { } csvLog ? new CsvLogStatus(csvLog) : null;

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };
        engine.Faulted += (_, error) =>
        {
            Console.Error.WriteLine($"Monitoring stopped: {error.Message}");
            stop.Cancel();
        };
        engine.Restarted += (_, error) =>
            Console.Error.WriteLine($"The kernel session stopped ({error.Message}) and was restarted; some events may be missing.");

        try
        {
            engine.Start();
        }
        catch (MonitorStartException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        // Start went ahead despite something the user should know, such as a session guard another program holds.
        if (engine.StartWarning is { } warning)
            Console.Error.WriteLine(ConsoleText.Clean(warning));

        string letters = string.Join(", ", selected.Select(d => d + ":"));
        if (watcher is null)
            Console.WriteLine($"Monitoring {letters} - press Ctrl+C to stop.");
        else if (selected.Count > 0)
            Console.WriteLine($"Monitoring {letters} and drives plugged in later - press Ctrl+C to stop.");
        else
            Console.WriteLine("Waiting for a drive to be plugged in - press Ctrl+C to stop.");
        if (engine.Log is { } log)
            Console.WriteLine($"Writing every event to {log.FilePath}");
        Console.WriteLine();

        var printer = new EventPrinter(options);
        var buffer = new List<FileActivityEvent>(4096);

        // Elapsed time on a monotonic clock: a wall-clock deadline moves when the clock is adjusted.
        TimeSpan duration = options.Duration ?? TimeSpan.MaxValue;
        var running = Stopwatch.StartNew();
        TimeSpan nextFullDriveCheck = FullDriveCheckInterval;
        while (!stop.IsCancellationRequested && running.Elapsed < duration)
        {
            stop.Token.WaitHandle.WaitOne(200);
            Flush(engine, printer, buffer);
            if (watcher is not null)
            {
                bool full = running.Elapsed >= nextFullDriveCheck;
                if (full)
                    nextFullDriveCheck = running.Elapsed + FullDriveCheckInterval;
                AddNewDrives(watcher, full);
            }

            // A log that failed refuses the later events; monitoring goes on without it.
            logStatus?.ReportFailure(Console.Error);
        }

        engine.Stop();
        Flush(engine, printer, buffer);

        // Before the summary, which needs the log's final counts.
        logStatus?.Finish(engine.StopLog, Console.Out, Console.Error);
        if (!options.NoSummary)
            PrintSummary(engine, logStatus);
        return ExitCode(logStatus);
    }

    /// <summary>1 when the CSV log stopped on an error, so that rows the user asked for are missing from it; otherwise 0.</summary>
    internal static int ExitCode(CsvLogStatus? log) => log is { Failed: true } ? 1 : 0;

    /// <summary>
    /// Starts the CSV log, or says in one line why the file cannot be written (open in another program, a
    /// folder, an invalid path, no permission) instead of crashing with a stack trace.
    /// </summary>
    internal static bool TryStartLog(MonitorEngine engine, string path, TextWriter error)
    {
        try
        {
            engine.StartLog(path);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            error.WriteLine($"Cannot write the CSV file: {ex.Message}");
            return false;
        }
    }

    /// <summary>Stops the session and closes the CSV log while the process dies.</summary>
    private static void StopQuietly(MonitorEngine engine)
    {
        try
        {
            engine.Dispose();
        }
        catch (Exception)
        {
            // The process is ending; there is nothing left to try.
        }
    }

    /// <summary>Monitors the drives plugged in since the last check, and says so even with --quiet.</summary>
    private static void AddNewDrives(DriveWatcher watcher, bool full)
    {
        IReadOnlyList<DriveDescriptor> added;
        try
        {
            added = watcher.Check(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // a drive in the middle of arriving or leaving; the next check tries again
        }

        foreach (DriveDescriptor drive in added)
            Console.WriteLine($"Now monitoring {DescribeDrive(drive)}");
    }

    /// <summary>"F: STICK (SanDisk Ultra)". The label and the model come from the drive, so they are cleaned.</summary>
    internal static string DescribeDrive(DriveDescriptor drive)
    {
        var text = new StringBuilder().Append(drive.Letter).Append(':');
        if (drive.Label is not null)
            text.Append(' ').Append(ConsoleText.Clean(drive.Label));
        if (drive.Model is not null)
            text.Append(" (").Append(ConsoleText.Clean(drive.Model)).Append(')');
        return text.ToString();
    }

    private static void Flush(MonitorEngine engine, EventPrinter printer, List<FileActivityEvent> buffer)
    {
        buffer.Clear();
        engine.DrainEvents(buffer);
        foreach (FileActivityEvent ev in buffer)
            printer.Print(ev);
    }

    private static void PrintSummary(MonitorEngine engine, CsvLogStatus? log)
    {
        Console.WriteLine();
        Console.WriteLine("=== Drives ===");
        foreach (DriveTotals d in engine.Stats.GetDriveTotals())
        {
            Console.WriteLine(
                $"{d.Drive}:  read {Formatting.Bytes(d.BytesRead)} in {d.Reads:N0} ops, wrote {Formatting.Bytes(d.BytesWritten)} in {d.Writes:N0} ops, " +
                $"{d.Creates:N0} created, {d.Deletes:N0} deleted, {d.Renames:N0} renamed");
        }

        List<FileStatsSnapshot> files = engine.Stats.GetAllFiles();
        if (files.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"=== Busiest files (of {files.Count:N0}) ===");
            foreach (FileStatsSnapshot f in files.OrderByDescending(f => f.BytesRead + f.BytesWritten).ThenByDescending(f => f.LastSeen).Take(20))
            {
                string lifecycle = f.Lifecycle == FileLifecycle.None ? "" : $" [{f.Lifecycle}]";
                Console.WriteLine(
                    $"  R {Formatting.Bytes(f.BytesRead),9}  W {Formatting.Bytes(f.BytesWritten),9}  " +
                    $"{ConsoleText.Clean(f.Path)}{lifecycle}  ({ConsoleText.Clean(f.Processes)})");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Kernel events: {engine.KernelEvents:N0}  published: {engine.PublishedEvents:N0}  lost by kernel: {engine.LostEvents:N0}  " +
            $"dropped: {engine.DroppedEvents:N0}  without file name: {engine.UnnamedEvents:N0}");
        if (engine.CallbackErrors > 0 || engine.Restarts > 0)
        {
            Console.WriteLine(
                $"Callback errors: {engine.CallbackErrors:N0} (last: {ConsoleText.Clean(engine.LastCallbackError ?? "-")})  " +
                $"session restarts: {engine.Restarts}");
        }

        if (log is not null)
            Console.WriteLine(log.SummaryLine());
    }
}

internal sealed class EventPrinter(CliOptions options)
{
    private readonly bool _quiet = options.Quiet;

    public bool ShouldShow(FileActivityEvent ev)
    {
        if (_quiet)
            return false;
        if (ev.Status == IoStatus.Failed && !options.ShowFailed)
            return false;
        if (ev.IsPaging && !options.ShowPaging)
            return false;
        return ev.Operation switch
        {
            FileOperation.Open => options.ShowOpens,
            FileOperation.List => options.ShowListings,
            FileOperation.SetInfo or FileOperation.Flush => options.ShowOther,
            _ => true,
        };
    }

    public void Print(FileActivityEvent ev)
    {
        if (!ShouldShow(ev))
            return;

        Console.Write(ev.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  ");

        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = ev.Status == IoStatus.Failed ? ConsoleColor.DarkGray : ColorFor(ev.Operation);
        Console.Write(ev.Operation.ToString().ToUpperInvariant().PadRight(8));
        Console.ForegroundColor = previous;

        Console.WriteLine(Describe(ev));
    }

    /// <summary>
    /// The line after the operation column. The path, the process name (from a command line) and the detail
    /// (listing patterns, rename targets) are chosen by other users, so they are cleaned before printing.
    /// </summary>
    internal static string Describe(FileActivityEvent ev)
    {
        var line = new StringBuilder(160);
        line.Append(ev.Operation is FileOperation.Read or FileOperation.Write ? Formatting.Bytes(ev.Bytes).PadLeft(9) : new string(' ', 9));
        line.Append("  ").Append(ConsoleText.Clean(ev.Path));
        if (ev.Offset >= 0 && ev.Operation is FileOperation.Read or FileOperation.Write)
            line.Append("  @").Append(ev.Offset.ToString("N0", CultureInfo.CurrentCulture));
        line.Append("  ").Append(ConsoleText.Clean(ev.ProcessName)).Append(" (").Append(ev.ProcessId).Append(')');
        if (ev.IsPaging)
            line.Append("  [paging]");
        if (ev.Detail is not null)
            line.Append("  ").Append(ConsoleText.Clean(ev.Detail));
        return line.ToString();
    }

    private static ConsoleColor ColorFor(FileOperation operation) => operation switch
    {
        FileOperation.Read => ConsoleColor.Cyan,
        FileOperation.Write => ConsoleColor.Yellow,
        FileOperation.Create => ConsoleColor.Green,
        FileOperation.Delete => ConsoleColor.Red,
        FileOperation.Rename => ConsoleColor.Magenta,
        _ => ConsoleColor.Gray,
    };
}
