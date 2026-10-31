using System.Globalization;
using System.IO;
using DriveMonitor.Core.Models;

namespace DriveMonitor.App.ViewModels;

/// <summary>Shared wording and formats for the tables.</summary>
internal static class DisplayText
{
    public static string Operation(FileOperation operation) => operation switch
    {
        FileOperation.Read => "Read",
        FileOperation.Write => "Write",
        FileOperation.Create => "Create",
        FileOperation.Open => "Open",
        FileOperation.Delete => "Delete",
        FileOperation.Rename => "Rename",
        FileOperation.SetInfo => "Change",
        FileOperation.Flush => "Flush",
        FileOperation.List => "List",
        _ => operation.ToString(),
    };

    /// <summary>"14:03:12.345", whatever the regional time separator.</summary>
    public static string PreciseTime(DateTime time) => time.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);

    /// <summary>"14:03:12", or empty for <c>default</c>.</summary>
    public static string Time(DateTime time) =>
        time == default ? "" : time.ToString("HH:mm:ss", CultureInfo.InvariantCulture);

    public static string Number(long value) => value.ToString("N0", CultureInfo.CurrentCulture);

    /// <summary>The last path segment; the whole path for a drive root.</summary>
    public static string FileName(string path)
    {
        string name = Path.GetFileName(path);
        return name.Length > 0 ? name : path;
    }

    /// <summary>The containing folder, or empty for a drive root.</summary>
    public static string Folder(string path) => Path.GetDirectoryName(path) ?? "";
}
