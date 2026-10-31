namespace DriveMonitor.Core.Filtering;

/// <summary>
/// The set of monitored drive letters, stored as a 26-bit mask so the ETW consumer thread
/// can test every event without locking while the UI changes the selection.
/// </summary>
public sealed class DriveFilter
{
    private int _mask;

    public event Action? Changed;

    public bool IsMonitored(char drive)
    {
        int bit = Bit(drive);
        return bit != 0 && (Volatile.Read(ref _mask) & bit) != 0;
    }

    public void SetMonitored(char drive, bool monitored)
    {
        int bit = Bit(drive);
        if (bit == 0)
            throw new ArgumentOutOfRangeException(nameof(drive), drive, "Expected a drive letter A-Z.");

        int old, updated;
        do
        {
            old = Volatile.Read(ref _mask);
            updated = monitored ? old | bit : old & ~bit;
        }
        while (old != updated && Interlocked.CompareExchange(ref _mask, updated, old) != old);

        if (old != updated)
            Changed?.Invoke();
    }

    /// <summary>Replaces the whole selection.</summary>
    public void SetMonitored(IEnumerable<char> drives)
    {
        int mask = 0;
        foreach (char drive in drives)
            mask |= Bit(drive);
        if (Interlocked.Exchange(ref _mask, mask) != mask)
            Changed?.Invoke();
    }

    public IReadOnlyList<char> MonitoredDrives
    {
        get
        {
            int mask = Volatile.Read(ref _mask);
            var drives = new List<char>();
            for (int i = 0; i < 26; i++)
            {
                if ((mask & (1 << i)) != 0)
                    drives.Add((char)('A' + i));
            }

            return drives;
        }
    }

    /// <summary>True when <paramref name="path"/> is a DOS path on a monitored drive.</summary>
    public bool TryMatch(string? path, out char drive)
    {
        drive = '\0';
        if (path is null || path.Length < 2 || path[1] != ':')
            return false;
        int bit = Bit(path[0]);
        if (bit == 0 || (Volatile.Read(ref _mask) & bit) == 0)
            return false;
        drive = char.ToUpperInvariant(path[0]);
        return true;
    }

    private static int Bit(char drive)
    {
        char upper = char.ToUpperInvariant(drive);
        return upper is >= 'A' and <= 'Z' ? 1 << (upper - 'A') : 0;
    }
}
