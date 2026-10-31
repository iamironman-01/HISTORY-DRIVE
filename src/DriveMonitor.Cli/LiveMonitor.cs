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

        IReadOnlyList<DriveDescriptor> drives = DriveCatalog.Enumerate(engine.Volumes);
        List<char> selected = options.Drives
            ?? drives.Where(d => DriveCatalog.IsMonitoredByDefault(d) || (options.IncludeSystemDrive && d.IsSystemDrive))
                .Select(d => d.Letter)
                .ToList();
        if (options.Drives is null && options.IncludeSystemDrive && !selected.Contains(DriveCatalog.SystemDriveLetter))
            selected.Add(DriveCatalog.SystemDriveLetter);
        if (selected.Count == 0)
        {
            Console.Error.WriteLine("No drives to monitor. Use --drives or --system.");
            return 1;
        }

        engine.Drives.SetMonitored(selected);
        if (options.CsvPath is not null && !TryStartLog(engine, options.CsvPath, Console.Error))
            return 1;

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

        Console.WriteLine($"Monitoring {string.Join(", ", selected.Select(d => d + ":"))} - press Ctrl+C to stop.");
        if (engine.Log is { } log)
            Console.WriteLine($"Writing every event to {log.FilePath}");
        Console.WriteLine();

        var printer = new EventPrinter(options);
        var buffer = new List<FileActivityEvent>(4096);

        // Elapsed time on a monotonic clock: a wall-clock deadline moves when the clock is adjusted.
        TimeSpan duration = options.Duration ?? TimeSpan.MaxValue;
        var running = Stopwatch.StartNew();
        while (!stop.IsCancellationRequested && running.Elapsed < duration)
        {
            stop.Token.WaitHandle.WaitOne(200);
            Flush(engine, printer, buffer);
        }

        engine.Stop();
        Flush(engine, printer, buffer);
        if (!options.NoSummary)
            PrintSummary(engine);
        return 0;
    }

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

    private static void Flush(MonitorEngine engine, EventPrinter printer, List<FileActivityEvent> buffer)
    {
        buffer.Clear();
        engine.DrainEvents(buffer);
        foreach (FileActivityEvent ev in buffer)
            printer.Print(ev);
    }

    private static void PrintSummary(MonitorEngine engine)
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
                    $"  R {Formatting.Bytes(f.BytesRead),9}  W {Formatting.Bytes(f.BytesWritten),9}  {f.Path}{lifecycle}  ({f.Processes})");
            }
        }

        Console.WriteLine();
        Console.WriteLine(
            $"Kernel events: {engine.KernelEvents:N0}  published: {engine.PublishedEvents:N0}  lost by kernel: {engine.LostEvents:N0}  " +
            $"dropped: {engine.DroppedEvents:N0}  without file name: {engine.UnnamedEvents:N0}");
        if (engine.CallbackErrors > 0 || engine.Restarts > 0)
        {
            Console.WriteLine(
                $"Callback errors: {engine.CallbackErrors:N0} (last: {engine.LastCallbackError ?? "-"})  session restarts: {engine.Restarts}");
        }
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

        var line = new StringBuilder(160);
        line.Append(ev.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append("  ");
        Console.Write(line.ToString());

        ConsoleColor previous = Console.ForegroundColor;
        Console.ForegroundColor = ev.Status == IoStatus.Failed ? ConsoleColor.DarkGray : ColorFor(ev.Operation);
        Console.Write(ev.Operation.ToString().ToUpperInvariant().PadRight(8));
        Console.ForegroundColor = previous;

        line.Clear();
        line.Append(ev.Operation is FileOperation.Read or FileOperation.Write ? Formatting.Bytes(ev.Bytes).PadLeft(9) : new string(' ', 9));
        line.Append("  ").Append(ev.Path);
        if (ev.Offset >= 0 && ev.Operation is FileOperation.Read or FileOperation.Write)
            line.Append("  @").Append(ev.Offset.ToString("N0", CultureInfo.CurrentCulture));
        line.Append("  ").Append(ev.ProcessName).Append(" (").Append(ev.ProcessId).Append(')');
        if (ev.IsPaging)
            line.Append("  [paging]");
        if (ev.Detail is not null)
            line.Append("  ").Append(ev.Detail);
        Console.WriteLine(line.ToString());
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
