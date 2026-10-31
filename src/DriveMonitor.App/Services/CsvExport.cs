using System.Globalization;
using System.IO;
using DriveMonitor.Core.Aggregation;
using DriveMonitor.Core.Export;
using DriveMonitor.Core.Models;

namespace DriveMonitor.App.Services;

/// <summary>An immutable copy of a Live activity row, safe to write to a file on another thread.</summary>
public readonly record struct ActivityRecord(
    DateTime FirstTime,
    DateTime LastTime,
    FileOperation Operation,
    int Count,
    long Bytes,
    string Path,
    string? TargetPath,
    char Drive,
    string ProcessName,
    int ProcessId,
    FileEventFlags Flags,
    IoStatus Status,
    int NtStatus,
    string? Detail,
    long MinOffset,
    long MaxEnd);

/// <summary>CSV layouts for the tables that Core has no export for (grouped activity rows, processes).</summary>
public static class CsvExport
{
    private const string TimeFormat = "yyyy-MM-dd HH:mm:ss.fff";

    private static readonly string[] ActivityHeader =
    [
        "First time", "Last time", "Operation", "Count", "Bytes", "Path", "Target", "Drive", "Process", "PID",
        "Flags", "Status", "NtStatus", "Detail", "First offset", "End offset",
    ];

    private static readonly string[] ProcessHeader =
    [
        "Process", "PID", "Reads", "Bytes read", "Writes", "Bytes written", "Created", "Deleted", "Renamed", "Files",
        "First seen", "Last seen",
    ];

    public static void WriteActivity(string path, IReadOnlyList<ActivityRecord> rows)
    {
        var inv = CultureInfo.InvariantCulture;
        using var writer = new StreamWriter(path, append: false, Csv.FileEncoding);
        Csv.WriteHeader(writer, ActivityHeader);
        foreach (ActivityRecord r in rows)
        {
            Csv.WriteRow(writer,
            [
                r.FirstTime.ToString(TimeFormat, inv),
                r.LastTime.ToString(TimeFormat, inv),
                r.Operation.ToString(),
                r.Count.ToString(inv),
                r.Bytes.ToString(inv),
                Csv.Escape(r.Path),
                Csv.Escape(r.TargetPath),
                r.Drive.ToString(),
                Csv.Escape(r.ProcessName),
                r.ProcessId.ToString(inv),
                r.Flags == FileEventFlags.None ? "" : Csv.Escape(r.Flags.ToString()),
                r.Status.ToString(),
                r.NtStatus == 0 ? "" : "0x" + unchecked((uint)r.NtStatus).ToString("X8", inv),
                Csv.Escape(r.Detail),
                r.MinOffset >= 0 ? r.MinOffset.ToString(inv) : "",
                r.MaxEnd >= 0 ? r.MaxEnd.ToString(inv) : "",
            ]);
        }
    }

    public static void WriteProcesses(string path, IReadOnlyList<ProcessStatsSnapshot> processes)
    {
        var inv = CultureInfo.InvariantCulture;
        using var writer = new StreamWriter(path, append: false, Csv.FileEncoding);
        Csv.WriteHeader(writer, ProcessHeader);
        foreach (ProcessStatsSnapshot p in processes)
        {
            Csv.WriteRow(writer,
            [
                Csv.Escape(p.ProcessName),
                p.ProcessId.ToString(inv),
                p.Reads.ToString(inv),
                p.BytesRead.ToString(inv),
                p.Writes.ToString(inv),
                p.BytesWritten.ToString(inv),
                p.Creates.ToString(inv),
                p.Deletes.ToString(inv),
                p.Renames.ToString(inv),
                p.Files.ToString(inv),
                p.FirstSeen.ToString(TimeFormat, inv),
                p.LastSeen.ToString(TimeFormat, inv),
            ]);
        }
    }
}
