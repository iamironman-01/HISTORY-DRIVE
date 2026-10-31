namespace DriveMonitor.Core.Models;

/// <summary>One file operation on a monitored drive.</summary>
public sealed record FileActivityEvent
{
    /// <summary>Monotonic sequence number assigned when the event is published.</summary>
    public long Sequence { get; init; }

    /// <summary>Local time at which the operation started.</summary>
    public DateTime Timestamp { get; init; }

    public FileOperation Operation { get; init; }

    /// <summary>Full DOS path, e.g. <c>E:\photos\img001.jpg</c>. For a rename, the old path.</summary>
    public string Path { get; init; } = "";

    /// <summary>For a rename or hard link: the new path, when Windows reported it.</summary>
    public string? TargetPath { get; init; }

    /// <summary>Upper-case drive letter of <see cref="Path"/>.</summary>
    public char Drive { get; init; }

    /// <summary>
    /// For reads and writes: bytes transferred when the completion was seen, otherwise
    /// the requested size. For resize operations: the new size. Zero otherwise.
    /// </summary>
    public long Bytes { get; init; }

    /// <summary>File offset of a read or write, or -1 when not applicable.</summary>
    public long Offset { get; init; } = -1;

    public int ProcessId { get; init; }

    public string ProcessName { get; init; } = "";

    public int ThreadId { get; init; }

    public FileEventFlags Flags { get; init; }

    public IoStatus Status { get; init; }

    /// <summary>Raw NTSTATUS from the completion event (0 when unknown).</summary>
    public int NtStatus { get; init; }

    /// <summary>Short human-readable detail, e.g. "created", "size → 4,096 bytes", "file not found".</summary>
    public string? Detail { get; init; }

    public bool IsPaging => (Flags & FileEventFlags.Paging) != 0;

    public bool IsDirectory => (Flags & FileEventFlags.Directory) != 0;
}
