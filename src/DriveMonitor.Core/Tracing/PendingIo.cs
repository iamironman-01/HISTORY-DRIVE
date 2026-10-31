namespace DriveMonitor.Core.Tracing;

/// <summary>Kernel FileIo initiation event kinds that DriveMonitor follows.</summary>
internal enum RawFileOp : byte
{
    Create,
    Read,
    Write,
    SetInfo,
    Delete,
    Rename,
    Flush,
    DirEnum,
}

/// <summary>
/// Fields copied out of a kernel FileIo initiation event (TraceEvent recycles its event
/// objects, so nothing may keep a reference to them), plus the completion once it is matched.
/// </summary>
internal sealed class PendingIo
{
    public RawFileOp Op;

    /// <summary>
    /// Address of the I/O request (Filter Manager callback data); links the initiation to its
    /// FileIo_OpEnd completion. Recycled quickly by Windows. 0 when unknown.
    /// </summary>
    public ulong Irp;

    /// <summary>Per-stream file-system context (FsContext); shared by every open of the same file.</summary>
    public ulong FileKey;

    /// <summary>The FILE_OBJECT of this particular open.</summary>
    public ulong FileObject;

    /// <summary>Raw QueryPerformanceCounter timestamp; exact ordering for completion matching.</summary>
    public long TimestampQpc;

    public DateTime Timestamp;
    public string Path = "";
    public char Drive;
    public int ProcessId;
    public int ThreadId;
    public string ProcessName = "";

    // Read / Write
    public int IoSize;
    public long Offset = -1;
    public int IrpFlags;

    // Create
    public int CreateOptions;
    public int CreateDisposition;

    // SetInfo / Delete / Rename
    public int InfoClass;
    public ulong ExtraInfo;

    // DirEnum
    public string? Pattern;

    // Completion (FileIo_OpEnd)
    public bool Completed;
    public int NtStatus;
    public ulong CompletionInfo;

    // Rename / hard-link target from a FileIo_PathOperation event.
    public string? TargetPath;

    /// <summary>Hold this operation until its path event arrives (set only once Windows is known to send them).</summary>
    public bool AwaitingPath;

    // Correlator bookkeeping. Waiting operations on one request address are chained by start, latest
    // first: Next is the operation that started before this one, Prev the one that started after it.
    public long ReceivedTick;
    public PendingIo? NextSameIrp;
    public PendingIo? PrevSameIrp;
    public PendingIo? NextAwaitingPath;
    public PendingIo? PrevAwaitingPath;
}
