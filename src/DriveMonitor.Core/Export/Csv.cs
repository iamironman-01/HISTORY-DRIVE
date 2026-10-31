using System.Globalization;
using System.Text;
using DriveMonitor.Core.Aggregation;
using DriveMonitor.Core.Models;

namespace DriveMonitor.Core.Export;

/// <summary>RFC 4180 CSV helpers and the column layouts used for exports and logs.</summary>
public static class Csv
{
    /// <summary>UTF-8 with a byte-order mark, so Excel shows non-ASCII file names correctly.</summary>
    public static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static readonly string[] EventHeader =
    [
        "Time", "Operation", "Path", "Drive", "Bytes", "Offset", "Process", "PID", "TID", "Flags", "Status", "NtStatus", "Detail", "Target",
    ];

    public static readonly string[] FileStatsHeader =
    [
        "Path", "Drive", "Folder", "Reads", "Bytes read", "Writes", "Bytes written", "Paging bytes read", "Paging bytes written",
        "Opens", "Lifecycle", "First seen", "Last seen", "Last operation", "Last process", "Processes", "Renamed to",
    ];

    /// <summary>
    /// Quotes a text field when needed. Text starting with = + - @ (or a tab/CR) is prefixed
    /// with an apostrophe so spreadsheet apps do not evaluate it as a formula.
    /// </summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return "";
        if (value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
            value = "'" + value;
        bool quote = value.AsSpan().IndexOfAny(",\"\r\n") >= 0 || value[0] == ' ' || value[^1] == ' ';
        return quote ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }

    public static void WriteRow(TextWriter writer, IEnumerable<string?> fields)
    {
        bool first = true;
        foreach (string? field in fields)
        {
            if (!first)
                writer.Write(',');
            writer.Write(field);
            first = false;
        }

        writer.Write("\r\n");
    }

    public static void WriteHeader(TextWriter writer, IEnumerable<string> header) =>
        WriteRow(writer, header.Select(Escape));

    public static void WriteEvent(TextWriter writer, FileActivityEvent ev)
    {
        var inv = CultureInfo.InvariantCulture;
        WriteRow(writer,
        [
            ev.Timestamp.ToString("yyyy-MM-dd HH:mm:ss.fff", inv),
            ev.Operation.ToString(),
            Escape(ev.Path),
            ev.Drive.ToString(),
            ev.Bytes.ToString(inv),
            ev.Offset >= 0 ? ev.Offset.ToString(inv) : "",
            Escape(ev.ProcessName),
            ev.ProcessId.ToString(inv),
            ev.ThreadId.ToString(inv),
            ev.Flags == FileEventFlags.None ? "" : Escape(ev.Flags.ToString()),
            ev.Status.ToString(),
            ev.NtStatus == 0 ? "" : "0x" + unchecked((uint)ev.NtStatus).ToString("X8", inv),
            Escape(ev.Detail),
            Escape(ev.TargetPath),
        ]);
    }

    public static void WriteFileStats(TextWriter writer, FileStatsSnapshot s)
    {
        var inv = CultureInfo.InvariantCulture;
        WriteRow(writer,
        [
            Escape(s.Path),
            s.Drive.ToString(),
            s.IsDirectory ? "yes" : "",
            s.Reads.ToString(inv),
            s.BytesRead.ToString(inv),
            s.Writes.ToString(inv),
            s.BytesWritten.ToString(inv),
            s.PagingBytesRead.ToString(inv),
            s.PagingBytesWritten.ToString(inv),
            s.Opens.ToString(inv),
            s.Lifecycle == FileLifecycle.None ? "" : Escape(s.Lifecycle.ToString()),
            s.FirstSeen.ToString("yyyy-MM-dd HH:mm:ss.fff", inv),
            s.LastSeen.ToString("yyyy-MM-dd HH:mm:ss.fff", inv),
            s.LastOperation.ToString(),
            Escape(s.LastProcess),
            Escape(s.Processes),
            Escape(s.RenamedTo),
        ]);
    }

    public static void ExportEvents(string path, IEnumerable<FileActivityEvent> events)
    {
        using var writer = new StreamWriter(path, append: false, FileEncoding);
        WriteHeader(writer, EventHeader);
        foreach (FileActivityEvent ev in events)
            WriteEvent(writer, ev);
    }

    public static void ExportFileStats(string path, IEnumerable<FileStatsSnapshot> files)
    {
        using var writer = new StreamWriter(path, append: false, FileEncoding);
        WriteHeader(writer, FileStatsHeader);
        foreach (FileStatsSnapshot file in files)
            WriteFileStats(writer, file);
    }
}
