using DriveMonitor.Core.Interop;

namespace DriveMonitor.Core.Volumes;

/// <summary>
/// Translates kernel paths such as <c>\Device\HarddiskVolume5\docs\a.txt</c> into DOS paths
/// such as <c>E:\docs\a.txt</c>.
/// </summary>
/// <remarks>
/// Lookups are lock-free: they read an immutable snapshot that <see cref="Refresh"/> replaces
/// atomically, so the ETW consumer thread can call <see cref="ToDosPath"/> while the UI thread
/// refreshes after a pendrive is plugged in. A lookup miss for something that looks like a
/// storage volume triggers a rate-limited refresh, which covers volumes mounted after start.
/// </remarks>
public sealed class VolumeMapper
{
    private const string DevicePrefix = @"\Device\";
    private const string SystemRootPrefix = @"\SystemRoot\";
    private const int AutoRefreshIntervalMs = 2000;

    private static readonly string WindowsDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private readonly Func<char, string?> _queryDosDevice;
    private volatile Snapshot _snapshot;

    // Environment.TickCount64 of the last automatic refresh. 0 lets the first miss refresh
    // (the tick count is milliseconds since boot, always far above the interval).
    private long _lastAutoRefreshTick;

    private sealed class Snapshot(Dictionary<string, char> deviceToDrive, string?[] driveToDevice)
    {
        public Dictionary<string, char> DeviceToDrive { get; } = deviceToDrive;
        public string?[] DriveToDevice { get; } = driveToDevice;
    }

    public VolumeMapper()
        : this(NativeMethods.TryQueryDosDevice)
    {
    }

    /// <param name="queryDosDevice">Returns the kernel device for a drive letter (QueryDosDevice), or null.</param>
    public VolumeMapper(Func<char, string?> queryDosDevice)
    {
        _queryDosDevice = queryDosDevice;
        _snapshot = Build();
    }

    /// <summary>Re-reads the drive letter → device mapping. Call after drives are added or removed.</summary>
    public void Refresh() => _snapshot = Build();

    /// <summary>The kernel device behind a drive letter, e.g. <c>\Device\HarddiskVolume5</c>.</summary>
    public string? GetDevicePath(char driveLetter)
    {
        int index = char.ToUpperInvariant(driveLetter) - 'A';
        return index is >= 0 and < 26 ? _snapshot.DriveToDevice[index] : null;
    }

    /// <summary>
    /// Converts a kernel or Win32-namespace path into a DOS path. Paths that cannot be mapped
    /// (named pipes, volumes without a drive letter, shadow copies) and non-paths are returned
    /// unchanged; DOS paths come back as they are, so converting twice is harmless. Never throws
    /// for any input and never returns null.
    /// </summary>
    public string ToDosPath(string? kernelPath)
    {
        if (string.IsNullOrEmpty(kernelPath))
            return "";

        // Already a DOS path.
        if (kernelPath.Length >= 2 && kernelPath[1] == ':')
            return kernelPath;

        // \??\E:\x and \\?\E:\x (Win32 namespace), \??\UNC\server\share\x.
        if (kernelPath.StartsWith(@"\??\", StringComparison.Ordinal) || kernelPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            ReadOnlySpan<char> rest = kernelPath.AsSpan(4);
            if (rest.Length >= 2 && rest[1] == ':')
                return rest.ToString();
            if (rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
                return string.Concat(@"\\", rest[4..]);
            return kernelPath;
        }

        // \SystemRoot\System32\x (kernel image paths) → C:\Windows\System32\x.
        if (kernelPath.StartsWith(SystemRootPrefix, StringComparison.OrdinalIgnoreCase) && WindowsDirectory.Length > 0)
            return string.Concat(WindowsDirectory, kernelPath.AsSpan(SystemRootPrefix.Length - 1));

        if (!kernelPath.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase))
            return kernelPath;

        int separator = kernelPath.IndexOf('\\', DevicePrefix.Length);
        ReadOnlySpan<char> device = separator < 0 ? kernelPath.AsSpan() : kernelPath.AsSpan(0, separator);

        if (TryLookup(device, out char drive) || (LooksLikeVolume(device) && TryAutoRefresh() && TryLookup(device, out drive)))
        {
            ReadOnlySpan<char> root = [drive, ':'];
            return separator < 0 ? string.Concat(root, @"\") : string.Concat(root, kernelPath.AsSpan(separator));
        }

        // \Device\Mup\server\share\x → \\server\share\x (UNC paths). Redirector paths of mapped
        // drives (\Device\Mup\;LanmanRedirector\;Z:...) are left alone.
        if (separator >= 0
            && IsUncProvider(device)
            && kernelPath.Length > separator + 1
            && kernelPath[separator + 1] != ';')
        {
            return string.Concat(@"\", kernelPath.AsSpan(separator));
        }

        return kernelPath;
    }

    private static bool IsUncProvider(ReadOnlySpan<char> device) =>
        device.Equals(@"\Device\Mup", StringComparison.OrdinalIgnoreCase)
        || device.Equals(@"\Device\LanmanRedirector", StringComparison.OrdinalIgnoreCase);

    private bool TryLookup(ReadOnlySpan<char> device, out char drive)
    {
        var lookup = _snapshot.DeviceToDrive.GetAlternateLookup<ReadOnlySpan<char>>();
        return lookup.TryGetValue(device, out drive);
    }

    private static bool LooksLikeVolume(ReadOnlySpan<char> device)
    {
        ReadOnlySpan<char> name = device[DevicePrefix.Length..];
        if (name.StartsWith("HarddiskVolumeShadowCopy", StringComparison.OrdinalIgnoreCase))
            return false;
        return name.StartsWith("HarddiskVolume", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("CdRom", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("Floppy", StringComparison.OrdinalIgnoreCase);
    }

    private bool TryAutoRefresh()
    {
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastAutoRefreshTick);
        if (now - last < AutoRefreshIntervalMs)
            return false;
        if (Interlocked.CompareExchange(ref _lastAutoRefreshTick, now, last) != last)
            return false; // another thread is refreshing
        Refresh();
        return true;
    }

    private Snapshot Build()
    {
        var deviceToDrive = new Dictionary<string, char>(StringComparer.OrdinalIgnoreCase);
        var driveToDevice = new string?[26];
        for (char letter = 'A'; letter <= 'Z'; letter++)
        {
            string? target;
            try
            {
                target = _queryDosDevice(letter);
            }
            catch (Exception)
            {
                continue;
            }

            // SUBST drives map to \??\C:\folder and are aliases of another drive; skip them.
            if (target is null || !target.StartsWith(DevicePrefix, StringComparison.OrdinalIgnoreCase))
                continue;

            driveToDevice[letter - 'A'] = target;
            deviceToDrive.TryAdd(target, letter); // first letter wins if a volume has several
        }

        return new Snapshot(deviceToDrive, driveToDevice);
    }
}
