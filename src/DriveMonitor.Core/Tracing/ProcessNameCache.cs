using System.Collections.Concurrent;
using DriveMonitor.Core.Interop;

namespace DriveMonitor.Core.Tracing;

/// <summary>
/// Process id → image name ("explorer.exe"). Filled from the kernel trace's process
/// start/rundown events, with a direct query as fallback for processes it has not seen.
/// </summary>
public sealed class ProcessNameCache
{
    private const int MaxEntries = 65_536;

    private readonly ConcurrentDictionary<int, string> _names = new();

    public string? TryGet(int processId) => _names.TryGetValue(processId, out string? name) ? name : null;

    /// <summary>Records a process seen in a start or rundown event (a reused id overwrites the old name).</summary>
    public void OnProcessStarted(int processId, string? imageFileName)
    {
        if (string.IsNullOrEmpty(imageFileName))
            return;
        TrimIfFull();
        _names[processId] = Path.GetFileName(imageFileName);
    }

    public string Resolve(int processId, string? hint = null)
    {
        if (_names.TryGetValue(processId, out string? name))
            return name;

        name = processId switch
        {
            0 => "Idle",
            4 => "System",
            < 0 => "unknown",
            _ => QueryImageName(processId) ?? (string.IsNullOrEmpty(hint) ? null : hint) ?? $"PID {processId}",
        };
        TrimIfFull();
        _names[processId] = name;
        return name;
    }

    /// <summary>
    /// The kernel's image name is cut to 15 characters ("SearchProtocolH"). The command line usually
    /// starts with the full executable path, so its file name wins when it extends the short name.
    /// </summary>
    internal static string? BestImageName(string? imageFileName, string? commandLine)
    {
        string? shortName = string.IsNullOrEmpty(imageFileName) ? null : Path.GetFileName(imageFileName);
        string? fromCommandLine = ExecutableFromCommandLine(commandLine);
        if (fromCommandLine is null || string.IsNullOrEmpty(shortName))
            return string.IsNullOrEmpty(shortName) ? fromCommandLine : shortName;
        return fromCommandLine.StartsWith(shortName, StringComparison.OrdinalIgnoreCase) ? fromCommandLine : shortName;
    }

    private static string? ExecutableFromCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return null;

        ReadOnlySpan<char> text = commandLine.AsSpan().TrimStart();
        ReadOnlySpan<char> exe;
        if (text[0] == '"')
        {
            ReadOnlySpan<char> rest = text[1..];
            int end = rest.IndexOf('"');
            exe = end < 0 ? rest : rest[..end];
        }
        else
        {
            // Unquoted paths may contain spaces ("C:\Program Files\x.exe -arg"), so cut after ".exe".
            int dotExe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            int space = text.IndexOf(' ');
            exe = dotExe >= 0 ? text[..(dotExe + 4)] : space > 0 ? text[..space] : text;
        }

        ReadOnlySpan<char> name = Path.GetFileName(exe);
        return name.IsEmpty ? null : name.ToString();
    }

    private static string? QueryImageName(int processId)
    {
        try
        {
            string? path = NativeMethods.TryGetProcessImagePath(processId);
            return string.IsNullOrEmpty(path) ? null : Path.GetFileName(path);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void TrimIfFull()
    {
        if (_names.Count >= MaxEntries)
            _names.Clear();
    }
}
