using System.Buffers;
using System.Collections.Concurrent;
using DriveMonitor.Core.Interop;

namespace DriveMonitor.Core.Tracing;

/// <summary>
/// Process id → image name ("explorer.exe"). Filled from the kernel trace's process
/// start/rundown events, with a direct query as fallback for processes it has not seen.
/// Every name it stores or returns has passed <see cref="SafeName"/>.
/// </summary>
public sealed class ProcessNameCache
{
    private const int MaxEntries = 65_536;

    /// <summary>Characters no Windows file name can contain: controls and " * / : &lt; &gt; ? \ |.</summary>
    private static readonly SearchValues<char> InvalidFileNameChars = SearchValues.Create(Path.GetInvalidFileNameChars());

    private readonly ConcurrentDictionary<int, string> _names = new();
    private readonly Func<int, string?> _queryImagePath;

    public ProcessNameCache()
        : this(NativeMethods.TryGetProcessImagePath)
    {
    }

    /// <param name="queryImagePath">Returns the image path of a running process, or null; tests replace it.</param>
    internal ProcessNameCache(Func<int, string?> queryImagePath)
    {
        _queryImagePath = queryImagePath;
    }

    public string? TryGet(int processId) => _names.TryGetValue(processId, out string? name) ? name : null;

    /// <summary>Records a process seen in a start or rundown event (a reused id overwrites the old name).</summary>
    public void OnProcessStarted(int processId, string? imageFileName)
    {
        if (string.IsNullOrEmpty(imageFileName))
            return;
        TrimIfFull();
        _names[processId] = SafeName(Path.GetFileName(imageFileName));
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
            _ => SafeName(QueryImageName(processId) ?? (string.IsNullOrEmpty(hint) ? null : hint) ?? $"PID {processId}"),
        };
        TrimIfFull();
        _names[processId] = name;
        return name;
    }

    /// <summary>
    /// The kernel's image name is cut to 15 characters ("SearchProtocolH"). The command line usually
    /// starts with the full executable path, so its file name wins when it extends the short name.
    /// Whoever starts a process writes its command line, so a name there that no file can have, or one
    /// with control or bidirectional formatting characters, is never used.
    /// </summary>
    internal static string? BestImageName(string? imageFileName, string? commandLine)
    {
        string? shortName = string.IsNullOrEmpty(imageFileName) ? null : Path.GetFileName(imageFileName);
        string? fromCommandLine = ExecutableFromCommandLine(commandLine);
        if (fromCommandLine is null || string.IsNullOrEmpty(shortName))
            return string.IsNullOrEmpty(shortName) ? fromCommandLine : shortName;
        return fromCommandLine.StartsWith(shortName, StringComparison.OrdinalIgnoreCase) ? fromCommandLine : shortName;
    }

    /// <summary>
    /// Replaces each control, line separator or bidirectional formatting character (<see cref="IsUnsafe"/>) with
    /// '?', which no Windows file name can contain, so a replacement stays visible. A name without them is
    /// returned as it is, not copied.
    /// </summary>
    internal static string SafeName(string name)
    {
        int index = IndexOfUnsafe(name);
        if (index < 0)
            return name;

        char[] chars = name.ToCharArray();
        for (; index < chars.Length; index++)
        {
            if (IsUnsafe(chars[index]))
                chars[index] = '?';
        }

        return new string(chars);
    }

    /// <summary>
    /// C0 and C1 controls and DEL (CR, LF, backspace, ESC, CSI...), the Unicode line and paragraph separators,
    /// and the bidirectional marks, embeddings, overrides and isolates. Shown, they can break, erase or forge a
    /// line of output, or make a name read as another ("invoice" + U+202E + "txt.exe" reads as "invoiceexe.txt").
    /// </summary>
    private static bool IsUnsafe(char c) =>
        char.IsControl(c)
        || c is '\u2028' or '\u2029'
        || c is '\u061C' or '\u200E' or '\u200F'
        || c is >= '\u202A' and <= '\u202E'
        || c is >= '\u2066' and <= '\u2069';

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
        bool usable = !name.IsEmpty && !name.ContainsAny(InvalidFileNameChars) && IndexOfUnsafe(name) < 0;
        return usable ? name.ToString() : null;
    }

    private static int IndexOfUnsafe(ReadOnlySpan<char> text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (IsUnsafe(text[i]))
                return i;
        }

        return -1;
    }

    private string? QueryImageName(int processId)
    {
        try
        {
            string? path = _queryImagePath(processId);
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
