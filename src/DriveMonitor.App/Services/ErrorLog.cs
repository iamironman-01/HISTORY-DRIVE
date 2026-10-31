using System.Globalization;
using System.IO;

namespace DriveMonitor.App.Services;

/// <summary>
/// Appends unexpected exceptions to <c>%LOCALAPPDATA%\DriveMonitor\errors.log</c> so problems can be
/// diagnosed after the fact. Best effort: never throws.
/// </summary>
public static class ErrorLog
{
    private const long MaxFileBytes = 1024 * 1024;
    private static readonly object Sync = new();

    public static string FilePath { get; } = Path.Combine(SettingsStore.AppDataDirectory, "errors.log");

    public static void Write(Exception exception, string context)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(SettingsStore.AppDataDirectory);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxFileBytes)
                    file.Delete();

                string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                File.AppendAllText(FilePath, $"{time} {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Nowhere left to report it.
        }
    }
}
