using System.Diagnostics;
using System.IO;
using DriveMonitor.Core.IO;

namespace DriveMonitor.App.Services;

/// <summary>File Explorer helpers. They touch the file system, so call them off the UI thread.</summary>
public static class ShellActions
{
    private static readonly string Explorer =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>
    /// Opens the folder that contains <paramref name="path"/> in File Explorer, with the file selected
    /// when it still exists.
    /// </summary>
    /// <returns>Null on success, otherwise a message for the user.</returns>
    public static string? ShowInFolder(string path)
    {
        string? folder = Path.GetDirectoryName(path);
        if (folder is null)
        {
            // A drive root has no parent to select it in; open the root itself.
            if (!Directory.Exists(path))
                return $"{path} is not available any more (the drive may have been removed).";
            StartExplorer(Quote(path));
            return null;
        }

        if (!Directory.Exists(folder))
            return $"The folder {folder} does not exist any more (it was deleted or renamed, or the drive was removed).";

        StartExplorer(File.Exists(path) || Directory.Exists(path) ? "/select," + Quote(path) : Quote(folder));
        return null;
    }

    /// <summary>
    /// Opens <paramref name="folder"/> in File Explorer, creating it first if needed. The elevated app creates it as
    /// the activity log does, without following a link below <paramref name="trustedRoot"/>.
    /// </summary>
    /// <exception cref="IOException">A folder below the root is a link, or the folder cannot be created.</exception>
    public static void OpenFolder(string folder, string trustedRoot)
    {
        LinkSafeFolder.OpenBelow(trustedRoot, folder, create: true).Dispose();
        StartExplorer(Quote(folder));
    }

    private static void StartExplorer(string arguments) =>
        Process.Start(new ProcessStartInfo(Explorer, arguments) { UseShellExecute = false })?.Dispose();

    // "E:\" must not be quoted: the backslash would escape the closing quote. Roots contain no spaces.
    private static string Quote(string path) => path.EndsWith('\\') ? path : $"\"{path}\"";
}
