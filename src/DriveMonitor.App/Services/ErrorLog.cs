using System.Globalization;
using System.IO;
using System.Text;
using DriveMonitor.Core.IO;

namespace DriveMonitor.App.Services;

/// <summary>
/// Appends unexpected exceptions to <c>%LOCALAPPDATA%\DriveMonitor\errors.log</c> so problems can be
/// diagnosed after the fact. Best effort: never throws.
/// </summary>
public static class ErrorLog
{
    private const string FileName = "errors.log";
    private const long MaxFileBytes = 1024 * 1024;
    private static readonly object Sync = new();

    public static string FilePath { get; } = Path.Combine(SettingsStore.AppDataDirectory, FileName);

    public static void Write(Exception exception, string context)
    {
        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(SettingsStore.AppDataDirectory);
                string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
                byte[] entry = Encoding.UTF8.GetBytes($"{time} {context}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");

                // The app runs elevated, and any program of the user can plant a junction or link in this
                // folder; LinkSafeFolder refuses links instead of following them.
                using LinkSafeFolder folder = LinkSafeFolder.Open(SettingsStore.AppDataDirectory);
                using FileStream log = folder.OpenFile(FileName, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);

                // Start again once the log is large. Truncating the open file deletes nothing by path.
                if (log.Length > MaxFileBytes)
                    log.SetLength(0);
                log.Seek(0, SeekOrigin.End);
                log.Write(entry);
            }
        }
        catch (Exception)
        {
            // Nowhere left to report it.
        }
    }
}
