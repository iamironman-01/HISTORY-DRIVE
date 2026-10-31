using System.Globalization;

namespace DriveMonitor.Core;

public static class Formatting
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    /// <summary>1536 → "1.5 KB" (binary units, as Explorer shows them).</summary>
    public static string Bytes(long bytes)
    {
        // Work on the unsigned magnitude: negating long.MinValue overflows back to itself.
        ulong magnitude = bytes < 0 ? unchecked((ulong)(-(bytes + 1))) + 1 : (ulong)bytes;
        string text = Magnitude(magnitude);
        return bytes < 0 ? "-" + text : text;
    }

    /// <summary>Bytes per second as "12.3 MB/s".</summary>
    public static string Rate(double bytesPerSecond) =>
        Bytes((long)Math.Round(Math.Max(0, bytesPerSecond))) + "/s";

    private static string Magnitude(ulong bytes)
    {
        if (bytes < 1024)
            return bytes.ToString(CultureInfo.CurrentCulture) + " B";

        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        string format = value >= 100 ? "0" : value >= 10 ? "0.#" : "0.##";
        return value.ToString(format, CultureInfo.CurrentCulture) + " " + Units[unit];
    }
}
