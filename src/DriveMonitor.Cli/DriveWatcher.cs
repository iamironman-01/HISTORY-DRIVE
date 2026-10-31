using DriveMonitor.Core.Filtering;
using DriveMonitor.Core.Models;
using DriveMonitor.Core.Volumes;

namespace DriveMonitor.Cli;

/// <summary>
/// Keeps drivemon's default selection, every drive except the Windows drive and network shares, up to date
/// while it runs, so that a pendrive plugged in after start is monitored like one that was already there.
/// </summary>
/// <remarks>
/// A check costs one GetLogicalDrives call. The drives are enumerated, which queries every device, only when a
/// drive letter came or went or when a full check is asked for. Drives are only ever added: a pendrive that is
/// pulled out and plugged in again keeps its letter monitored.
/// </remarks>
internal sealed class DriveWatcher
{
    private readonly DriveFilter _monitored;
    private readonly bool _includeSystemDrive;
    private readonly Func<int> _presentLetters;
    private readonly Func<IReadOnlyList<DriveDescriptor>> _enumerate;

    // The letters at the last enumeration, bit n for 'A' + n. No real set is -1, so the first check enumerates.
    private int _knownLetters = -1;

    /// <param name="monitored">The engine's drive filter, which new drives are added to.</param>
    /// <param name="includeSystemDrive">--system: the Windows drive belongs to the selection too.</param>
    /// <param name="presentLetters">The drive letters that exist now, as <see cref="PresentLetters"/> returns them.</param>
    /// <param name="enumerate">Describes the drives that exist now, as <see cref="DriveCatalog.Enumerate"/> does.</param>
    public DriveWatcher(
        DriveFilter monitored,
        bool includeSystemDrive,
        Func<int> presentLetters,
        Func<IReadOnlyList<DriveDescriptor>> enumerate)
    {
        _monitored = monitored;
        _includeSystemDrive = includeSystemDrive;
        _presentLetters = presentLetters;
        _enumerate = enumerate;
    }

    /// <summary>
    /// Monitors every present drive of the default selection that is not monitored yet; the first check selects
    /// the drives present at start.
    /// </summary>
    /// <param name="full">
    /// Enumerate even if no drive letter came or went, to notice a letter that now belongs to another kind of
    /// drive, or a drive whose kind Windows could not tell yet when its letter appeared.
    /// </param>
    /// <returns>The drives that were added, in drive letter order.</returns>
    /// <exception cref="IOException">The drives could not be read; the next check tries again.</exception>
    public IReadOnlyList<DriveDescriptor> Check(bool full = false)
    {
        int letters = _presentLetters();
        if (letters == _knownLetters && !full)
            return [];

        // Remembered only once the enumeration has worked, so that a failed one is repeated.
        IReadOnlyList<DriveDescriptor> drives = _enumerate();
        _knownLetters = letters;

        var added = new List<DriveDescriptor>();
        foreach (DriveDescriptor drive in drives.OrderBy(d => d.Letter))
        {
            if (IsSelectedByDefault(drive, _includeSystemDrive) && !_monitored.IsMonitored(drive.Letter))
            {
                _monitored.SetMonitored(drive.Letter, true);
                added.Add(drive);
            }
        }

        return added;
    }

    /// <summary>The selection without --drives: <see cref="DriveCatalog.IsMonitoredByDefault"/>, plus the Windows drive with --system.</summary>
    public static bool IsSelectedByDefault(DriveDescriptor drive, bool includeSystemDrive) =>
        DriveCatalog.IsMonitoredByDefault(drive) || (includeSystemDrive && drive.IsSystemDrive);

    /// <summary>The drive letters that exist now, bit n set for letter 'A' + n. Reads no device.</summary>
    public static int PresentLetters()
    {
        int letters = 0;
        foreach (string root in Environment.GetLogicalDrives())
        {
            char letter = char.ToUpperInvariant(root[0]);
            if (letter is >= 'A' and <= 'Z')
                letters |= 1 << (letter - 'A');
        }

        return letters;
    }
}
