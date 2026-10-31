using DriveMonitor.Core.Filtering;

namespace DriveMonitor.App.ViewModels;

/// <summary>
/// Monitors the letters of drives that just arrived, before the drive list is read again. Reading it waits for
/// the 750 ms debounce and then takes a moment more for a pendrive that was just mounted; until a letter is
/// monitored the engine drops everything on it, which is often the very activity the user wants to see (an
/// antivirus scan, AutoPlay, a copy started at once). The enumeration still makes the final choice.
/// </summary>
/// <remarks>Used only while "Monitor new USB drives automatically" is on. UI thread only.</remarks>
internal sealed class DriveArrivals(DriveFilter filter)
{
    // Letters monitored ahead of an enumeration, each with the number of the arrival that did it.
    private readonly Dictionary<char, long> _armed = new();

    /// <summary>Arrivals so far. An enumeration notes it when it starts and passes it to <see cref="Enumerated"/>.</summary>
    public long Count { get; private set; }

    /// <summary>
    /// Monitors each new letter whose drive the enumeration will most likely monitor: one with no drive listed
    /// (a pendrive that was never plugged in before) or whose drive was external when it was last connected.
    /// A listed drive that is still connected keeps its check box, and a returning internal drive its choice.
    /// </summary>
    public void Arrived(IEnumerable<char> letters, IReadOnlyDictionary<char, DriveItemViewModel> known)
    {
        Count++;
        foreach (char letter in letters)
        {
            if (known.TryGetValue(letter, out DriveItemViewModel? drive) && (drive.IsConnected || !drive.Descriptor.IsExternal))
                continue;

            filter.SetMonitored(letter, true);
            _armed[letter] = Count;
        }
    }

    /// <summary>
    /// Called once an enumeration that started at arrival <paramref name="arrivalsAtStart"/> has been merged. The
    /// drives it found have made their own choice. A letter armed before it started that it did not find lost its
    /// volume again, so the letter goes back to its drive's choice, or to unmonitored when no drive is listed.
    /// </summary>
    public void Enumerated(long arrivalsAtStart, IReadOnlySet<char> present, IReadOnlyDictionary<char, DriveItemViewModel> known)
    {
        // Removing during enumeration is supported by Dictionary since .NET Core 3.0.
        foreach ((char letter, long arrival) in _armed)
        {
            if (arrival > arrivalsAtStart)
                continue; // it arrived while this enumeration ran; the next one looks

            _armed.Remove(letter);
            if (!present.Contains(letter))
                filter.SetMonitored(letter, known.TryGetValue(letter, out DriveItemViewModel? drive) && drive.IsMonitored);
        }
    }
}
