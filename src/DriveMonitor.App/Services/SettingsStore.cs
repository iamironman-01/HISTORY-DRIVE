using System.IO;
using System.Text.Json;
using DriveMonitor.Core.IO;

namespace DriveMonitor.App.Services;

/// <summary>Window position and size in device-independent units.</summary>
public sealed class WindowBounds
{
    public double Left { get; set; }

    public double Top { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool IsMaximized { get; set; }
}

/// <summary>Preferences kept between sessions.</summary>
public sealed class AppSettings
{
    public const int DefaultMaxRows = 20_000;
    public const int MinimumMaxRows = 1_000;
    public const int MaximumMaxRows = 1_000_000;
    public const double DefaultLeftPanelWidth = 300;

    // Saved window bounds beyond these are not from any real screen. They must stay far below Int32.MaxValue,
    // where WPF rejects a window size or position with an exception that would stop every start.
    private const double MaximumWindowSize = 100_000;
    private const double MaximumWindowOffset = 1_000_000;

    public bool ShowReads { get; set; } = true;

    public bool ShowWrites { get; set; } = true;

    public bool ShowCreated { get; set; } = true;

    public bool ShowDeleted { get; set; } = true;

    public bool ShowRenamed { get; set; } = true;

    public bool ShowOpens { get; set; }

    public bool ShowListings { get; set; }

    /// <summary>Metadata changes (SetInfo) and flushes.</summary>
    public bool ShowOtherChanges { get; set; }

    public bool ShowPaging { get; set; }

    public bool ShowFailed { get; set; }

    public bool AutoScroll { get; set; } = true;

    public bool GroupRepeated { get; set; } = true;

    public bool HideOwnActivity { get; set; } = true;

    public bool AutoStart { get; set; } = true;

    public bool AutoMonitorNewDrives { get; set; } = true;

    /// <summary>Rows kept in the Live activity list; older rows are removed.</summary>
    public int MaxRows { get; set; } = DefaultMaxRows;

    /// <summary>Letters of fixed drives (other than the Windows drive) the user chose not to monitor.</summary>
    public List<string> UnmonitoredFixedDrives { get; set; } = [];

    public bool MonitorSystemDrive { get; set; }

    public WindowBounds? Window { get; set; }

    public double LeftPanelWidth { get; set; } = DefaultLeftPanelWidth;

    public bool IsFixedDriveUnmonitored(char letter) =>
        UnmonitoredFixedDrives.Contains(char.ToUpperInvariant(letter).ToString(), StringComparer.OrdinalIgnoreCase);

    public void SetFixedDriveMonitored(char letter, bool monitored)
    {
        string key = char.ToUpperInvariant(letter).ToString();
        UnmonitoredFixedDrives.RemoveAll(d => string.Equals(d, key, StringComparison.OrdinalIgnoreCase));
        if (!monitored)
            UnmonitoredFixedDrives.Add(key);
    }

    /// <summary>Repairs values a hand-edited or older file may contain.</summary>
    internal void Normalize()
    {
        MaxRows = Math.Clamp(MaxRows, MinimumMaxRows, MaximumMaxRows);

        // System.Text.Json writes null into non-nullable properties when the file says so.
        UnmonitoredFixedDrives = (UnmonitoredFixedDrives ?? [])
            .Select(d => d?.Trim().TrimEnd('\\', ':') ?? "")
            .Where(d => d.Length == 1 && char.IsAsciiLetter(d[0]))
            .Select(d => d.ToUpperInvariant())
            .Distinct()
            .ToList();

        if (!double.IsFinite(LeftPanelWidth) || LeftPanelWidth < 200 || LeftPanelWidth > 800)
            LeftPanelWidth = DefaultLeftPanelWidth;

        // NaN fails every comparison, so it is dropped as well.
        if (Window is { } w
            && !(Math.Abs(w.Left) <= MaximumWindowOffset && Math.Abs(w.Top) <= MaximumWindowOffset
                && w.Width > 0 && w.Width <= MaximumWindowSize
                && w.Height > 0 && w.Height <= MaximumWindowSize))
        {
            Window = null;
        }
    }
}

/// <summary>Loads and saves <see cref="AppSettings"/> as JSON in <c>%LOCALAPPDATA%\DriveMonitor\settings.json</c>.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private readonly string? _trustedRoot;

    /// <param name="filePath">The settings file; null for <c>%LOCALAPPDATA%\DriveMonitor\settings.json</c>.</param>
    /// <param name="trustedRoot">
    /// A folder above the file below which no link is followed when the file is written or set aside;
    /// <see cref="TrustedRoot"/> for the default file. Without one only the file's own folder is checked.
    /// </param>
    public SettingsStore(string? filePath = null, string? trustedRoot = null)
    {
        FilePath = filePath ?? Path.Combine(AppDataDirectory, "settings.json");
        _trustedRoot = trustedRoot ?? (filePath is null ? TrustedRoot : null);
    }

    /// <summary>
    /// <c>%LOCALAPPDATA%</c>, which holds every file the app writes. The elevated app trusts its path, which an
    /// administrator may have redirected, but follows no link below it (<see cref="LinkSafeFolder.OpenBelow"/>):
    /// the user's own programs can put one there.
    /// </summary>
    public static string TrustedRoot { get; } = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    /// <summary><c>%LOCALAPPDATA%\DriveMonitor</c>.</summary>
    public static string AppDataDirectory { get; } = Path.Combine(TrustedRoot, "DriveMonitor");

    /// <summary>Where "Record log" writes its CSV files.</summary>
    public static string LogsDirectory { get; } = Path.Combine(AppDataDirectory, "Logs");

    public string FilePath { get; }

    /// <summary>The saved settings; defaults when the file is missing, unreadable or corrupt.</summary>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) is { } settings)
            {
                settings.Normalize();
                return settings;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // Start with defaults; the file is rewritten on exit.
        }

        return new AppSettings();
    }

    /// <returns>False when the file could not be written, or when it or a folder on its way is a link.</returns>
    public bool TrySave(AppSettings settings)
    {
        try
        {
            string fullPath = Path.GetFullPath(FilePath);
            string name = Path.GetFileName(fullPath);
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(settings, JsonOptions);

            // The app runs elevated, and any program of the user can plant a junction or link in this
            // folder, or in place of it; LinkSafeFolder refuses links instead of following them.
            using LinkSafeFolder folder = OpenFolder(Path.GetDirectoryName(fullPath)!, create: true);

            // Write a temporary file first so a crash or full disk never leaves a truncated file behind.
            using FileStream temporary = folder.OpenFile(name + ".tmp", FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, allowRename: true);
            temporary.SetLength(0);
            temporary.Write(json);
            folder.Rename(temporary, name);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Renames the file to <c>settings.bad.json</c> after a start failed, so that a saved value that breaks the
    /// window cannot stop every later start as well: the next start uses the defaults.
    /// </summary>
    /// <returns>Where the file went, or null when there was none or it could not be renamed.</returns>
    public string? TrySetAside()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;
            string fullPath = Path.GetFullPath(FilePath);
            string target = Path.ChangeExtension(FilePath, ".bad.json");

            // Renamed through the folder, as TrySave writes: a link on the way, at the file or at the new name is
            // refused, so the elevated app cannot be made to move or replace another file.
            using LinkSafeFolder folder = OpenFolder(Path.GetDirectoryName(fullPath)!, create: false);
            using FileStream file = folder.OpenFile(
                Path.GetFileName(fullPath), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, allowRename: true);
            folder.Rename(file, Path.GetFileName(target));
            return target;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>The file's folder: below the trusted root without following a link, or else refused only when it is a link itself.</summary>
    private LinkSafeFolder OpenFolder(string folderPath, bool create)
    {
        if (_trustedRoot is not null)
            return LinkSafeFolder.OpenBelow(_trustedRoot, folderPath, create);

        if (create)
            Directory.CreateDirectory(folderPath);
        return LinkSafeFolder.Open(folderPath);
    }
}
