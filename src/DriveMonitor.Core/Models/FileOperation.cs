namespace DriveMonitor.Core.Models;

/// <summary>What happened to a file, as reported by the kernel file I/O trace.</summary>
public enum FileOperation : byte
{
    Read,
    Write,

    /// <summary>A new file or folder was created, or an existing file was overwritten/superseded.</summary>
    Create,

    /// <summary>An existing file or folder was opened.</summary>
    Open,

    /// <summary>The file was marked for deletion, or opened with delete-on-close.</summary>
    Delete,

    /// <summary>The file was renamed or moved. The kernel trace only reports the old name.</summary>
    Rename,

    /// <summary>Size, attributes, timestamps or other metadata were changed.</summary>
    SetInfo,

    /// <summary>Buffered data was flushed to the device.</summary>
    Flush,

    /// <summary>A folder's contents were listed.</summary>
    List,
}

/// <summary>Outcome of an operation, taken from its completion event when one was seen.</summary>
public enum IoStatus : byte
{
    /// <summary>No completion event was matched, so the outcome is not known.</summary>
    Unknown,
    Success,
    Failed,
}

[Flags]
public enum FileEventFlags : byte
{
    None = 0,

    /// <summary>
    /// Paging I/O issued by the cache or memory manager (lazy writer, read-ahead,
    /// memory-mapped files). It duplicates bytes the application already read or wrote
    /// through the cache, so totals keep it separate.
    /// </summary>
    Paging = 1,

    /// <summary>The request bypassed the file system cache.</summary>
    NonCached = 2,

    /// <summary>The target is a directory.</summary>
    Directory = 4,

    /// <summary>Opened with FILE_DELETE_ON_CLOSE: the file is deleted when its last handle closes.</summary>
    DeleteOnClose = 8,

    /// <summary>The file's end-of-file was set (truncated or extended); <see cref="FileActivityEvent.Bytes"/> is the new size.</summary>
    SizeChange = 16,
}
