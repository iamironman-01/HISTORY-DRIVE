using System.Globalization;

namespace DriveMonitor.Tests.TestSupport;

/// <summary>Sets <see cref="CultureInfo.CurrentCulture"/> for the current thread until disposed.</summary>
internal sealed class CultureScope : IDisposable
{
    private readonly CultureInfo _previous;

    public CultureScope(CultureInfo culture)
    {
        _previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = culture;
    }

    /// <summary>Formats with the invariant culture, so expected strings do not depend on the machine's settings.</summary>
    public static CultureScope Invariant() => new(CultureInfo.InvariantCulture);

    /// <summary>
    /// A culture that formats visibly differently from the invariant one: "," decimal separator,
    /// "." group separator, "~" negative sign and "." time separator.
    /// </summary>
    public static CultureInfo Unusual()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.NumberDecimalSeparator = ",";
        culture.NumberFormat.NumberGroupSeparator = ".";
        culture.NumberFormat.NegativeSign = "~";
        culture.DateTimeFormat.TimeSeparator = ".";
        culture.DateTimeFormat.DateSeparator = "/";
        return culture;
    }

    public void Dispose() => CultureInfo.CurrentCulture = _previous;
}
