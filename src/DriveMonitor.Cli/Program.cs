using System.Globalization;
using System.Reflection;
using DriveMonitor.Core;
using DriveMonitor.Core.Models;
using DriveMonitor.Core.Volumes;

namespace DriveMonitor.Cli;

internal static class Program
{
    private static int Main(string[] args)
    {
        CliOptions options;
        try
        {
            options = CliOptions.Parse(args);
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine(ex.Message);
            Console.Error.WriteLine();
            CliOptions.PrintUsage(Console.Error);
            return 2;
        }

        if (options.ShowHelp)
        {
            CliOptions.PrintUsage(Console.Out);
            return 0;
        }

        if (options.ShowVersion)
        {
            Console.WriteLine($"drivemon {InformationalVersion()}");
            return 0;
        }

        if (options.ListDrives)
        {
            PrintDrives();
            return 0;
        }

        return options.SelfTest ? SelfTest.Run(options) : LiveMonitor.Run(options);
    }

    /// <summary>The version stamped in at build time, e.g. 0.1.0+&lt;commit SHA&gt; (see Directory.Build.props).</summary>
    private static string InformationalVersion() =>
        typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? "unknown";

    private static void PrintDrives()
    {
        var mapper = new VolumeMapper();
        Console.WriteLine($"{"Drive",-6}{"Type",-11}{"Bus",-9}{"FS",-7}{"Size",10}{"Free",10}  {"Default",-8} Label / model / device");
        foreach (DriveDescriptor d in DriveCatalog.Enumerate(mapper))
        {
            string size = d.IsReady ? Formatting.Bytes(d.TotalBytes) : "-";
            string free = d.IsReady ? Formatting.Bytes(d.FreeBytes) : "-";
            string monitored = DriveCatalog.IsMonitoredByDefault(d) ? "yes" : d.IsSystemDrive ? "(system)" : "no";
            string description = string.Join(" | ", new[] { d.Label, d.Model, d.DevicePath }.Where(s => !string.IsNullOrEmpty(s)));
            Console.WriteLine($"{d.Letter + ":",-6}{d.Kind,-11}{d.Bus,-9}{d.FileSystem ?? "-",-7}{size,10}{free,10}  {monitored,-8} {description}");
        }
    }
}

internal sealed class CliOptions
{
    /// <summary>The longest --duration, 365 days.</summary>
    internal const int MaxDurationSeconds = 365 * 24 * 60 * 60;

    public bool ShowHelp { get; private set; }
    public bool ShowVersion { get; private set; }
    public bool ListDrives { get; private set; }
    public bool SelfTest { get; private set; }
    public char? SelfTestDrive { get; private set; }
    public string? ReportPath { get; private set; }
    public List<char>? Drives { get; private set; }
    public bool IncludeSystemDrive { get; private set; }
    public bool ShowPaging { get; private set; }
    public bool ShowOpens { get; private set; }
    public bool ShowListings { get; private set; }
    public bool ShowOther { get; private set; }
    public bool ShowFailed { get; private set; }
    public string? CsvPath { get; private set; }
    public TimeSpan? Duration { get; private set; }
    public bool Quiet { get; private set; }
    public bool NoSummary { get; private set; }

    public static CliOptions Parse(string[] args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "-h" or "--help" or "/?":
                    o.ShowHelp = true;
                    break;
                case "--version":
                    o.ShowVersion = true;
                    break;
                case "--list":
                    o.ListDrives = true;
                    break;
                case "--self-test":
                    o.SelfTest = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        o.SelfTestDrive = ParseDrive(args[++i]);
                    break;
                case "--report":
                    o.ReportPath = Value(args, ref i, arg);
                    break;
                case "-d" or "--drives":
                    o.Drives = Value(args, ref i, arg)
                        .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries)
                        .Select(ParseDrive)
                        .Distinct()
                        .ToList();
                    break;
                case "--system":
                    o.IncludeSystemDrive = true;
                    break;
                case "--paging":
                    o.ShowPaging = true;
                    break;
                case "--opens":
                    o.ShowOpens = true;
                    break;
                case "--listings":
                    o.ShowListings = true;
                    break;
                case "--other":
                    o.ShowOther = true;
                    break;
                case "--failed":
                    o.ShowFailed = true;
                    break;
                case "--csv":
                    o.CsvPath = Value(args, ref i, arg);
                    break;
                case "-t" or "--duration":
                    o.Duration = ParseDuration(Value(args, ref i, arg));
                    break;
                case "-q" or "--quiet":
                    o.Quiet = true;
                    break;
                case "--no-summary":
                    o.NoSummary = true;
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{arg}'.");
            }
        }

        return o;
    }

    public static void PrintUsage(TextWriter w)
    {
        w.WriteLine("drivemon - shows every file read and write on pendrives and secondary disks");
        w.WriteLine();
        w.WriteLine("Usage:");
        w.WriteLine("  drivemon [options]              Monitor drives and print each file operation");
        w.WriteLine("  drivemon --list                 List drives and whether they are monitored by default");
        w.WriteLine("  drivemon --self-test [E:]       Verify capture on a drive (uses a temporary folder)");
        w.WriteLine();
        w.WriteLine("Options:");
        w.WriteLine("  -d, --drives E,F     Drives to monitor (default: all except the Windows drive)");
        w.WriteLine("      --system         Also monitor the Windows drive");
        w.WriteLine("      --opens          Show file opens");
        w.WriteLine("      --listings       Show folder listings");
        w.WriteLine("      --other          Show metadata changes and flushes");
        w.WriteLine("      --paging         Show paging I/O from the cache and memory manager");
        w.WriteLine("      --failed         Show failed operations");
        w.WriteLine("      --csv FILE       Also write every event to a CSV file");
        w.WriteLine("  -t, --duration SEC   Stop after SEC seconds (default: until Ctrl+C)");
        w.WriteLine("  -q, --quiet          Do not print individual events");
        w.WriteLine("      --no-summary     Do not print per-file totals on exit");
        w.WriteLine("      --report FILE    Self-test: also save the report to FILE");
        w.WriteLine("  -h, --help           Show this help");
        w.WriteLine("      --version        Show the version and exit");
        w.WriteLine();
        w.WriteLine("Needs an administrator command prompt (kernel tracing requires it).");
    }

    private static string Value(string[] args, ref int i, string name)
    {
        if (i + 1 >= args.Length)
            throw new ArgumentException($"Option '{name}' needs a value.");
        return args[++i];
    }

    private static char ParseDrive(string text)
    {
        string t = text.Trim().TrimEnd('\\', ':');
        if (t.Length != 1 || !char.IsAsciiLetter(t[0]))
            throw new ArgumentException($"'{text}' is not a drive letter.");
        return char.ToUpperInvariant(t[0]);
    }

    /// <summary>
    /// Seconds above 0 and at most a year. "Infinity", "1e400" and "NaN" are valid doubles, and values
    /// far beyond a year overflow TimeSpan, so they must fail here as usage errors, not later as crashes.
    /// </summary>
    private static TimeSpan ParseDuration(string text)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds)
            || !double.IsFinite(seconds) || seconds <= 0 || seconds > MaxDurationSeconds)
        {
            throw new ArgumentException(
                $"Invalid duration '{text}': give a number of seconds above 0 and at most {MaxDurationSeconds} (365 days).");
        }

        return TimeSpan.FromSeconds(seconds);
    }
}
