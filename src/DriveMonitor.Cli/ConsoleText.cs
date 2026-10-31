namespace DriveMonitor.Cli;

/// <summary>
/// Makes text that other people control safe to write to the console. A process name comes from the command
/// line, which whoever starts the process chooses; a listing pattern from whoever lists a folder; a file name
/// or a volume label from whoever made the drive. Unfiltered, CR, LF and escape sequences in them can
/// overwrite, erase or forge drivemon's lines, and bidirectional overrides can make a name read as something
/// it is not.
/// </summary>
internal static class ConsoleText
{
    /// <summary>
    /// Replaces each control or bidirectional formatting character with '?', which no Windows file name can
    /// contain, so a replacement stays visible. Ordinary text is returned as it is.
    /// </summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";

        int index = 0;
        while (index < text.Length && !IsUnsafe(text[index]))
            index++;
        if (index == text.Length)
            return text;

        char[] chars = text.ToCharArray();
        for (; index < chars.Length; index++)
        {
            if (IsUnsafe(chars[index]))
                chars[index] = '?';
        }

        return new string(chars);
    }

    /// <summary>
    /// C0 and C1 controls and DEL (CR, LF, backspace, ESC, CSI...), the Unicode line and paragraph separators,
    /// and the bidirectional marks, embeddings, overrides and isolates.
    /// </summary>
    internal static bool IsUnsafe(char c) =>
        char.IsControl(c)
        || c is '\u2028' or '\u2029'
        || c is '\u061C' or '\u200E' or '\u200F'
        || c is >= '\u202A' and <= '\u202E'
        || c is >= '\u2066' and <= '\u2069';
}
