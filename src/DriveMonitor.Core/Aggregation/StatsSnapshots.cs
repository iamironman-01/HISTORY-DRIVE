using DriveMonitor.Core.Models;

namespace DriveMonitor.Core.Aggregation;

[Flags]
public enum FileLifecycle : byte
{
    None = 0,
    Created = 1,
    Deleted = 2,
    Renamed = 4,
    Resized = 8,
}

/// <summary>Totals for one file (or folder) since monitoring started or was last cleared.</summary>
/// <param name="Reads">Application read requests (paging I/O excluded).</param>
/// <param name="PagingBytesRead">Bytes the cache/memory manager read from the device for this file.</param>
/// <param name="Processes">Up to three process names, plus "+N more".</param>
/// <param name="RenamedTo">The new path of the last rename, when Windows reported it.</param>
/// <param name="ProcessNames">
/// Every process name stored for the file, for searching; null when <paramref name="Processes"/> already names them all.
/// </param>
public readonly record struct FileStatsSnapshot(
    string Path,
    char Drive,
    bool IsDirectory,
    long Reads,
    long Writes,
    long BytesRead,
    long BytesWritten,
    long PagingBytesRead,
    long PagingBytesWritten,
    int Opens,
    FileLifecycle Lifecycle,
    DateTime FirstSeen,
    DateTime LastSeen,
    FileOperation LastOperation,
    string LastProcess,
    string Processes,
    int ProcessCount,
    string? RenamedTo = null,
    IReadOnlyList<string>? ProcessNames = null);

public readonly record struct DriveTotals(
    char Drive,
    long Events,
    long Reads,
    long Writes,
    long BytesRead,
    long BytesWritten,
    long PagingBytesRead,
    long PagingBytesWritten,
    long Creates,
    long Deletes,
    long Renames,
    DateTime LastActivity);

public readonly record struct ProcessStatsSnapshot(
    int ProcessId,
    string ProcessName,
    long Reads,
    long Writes,
    long BytesRead,
    long BytesWritten,
    long Creates,
    long Deletes,
    long Renames,
    int Files,
    DateTime FirstSeen,
    DateTime LastSeen);
