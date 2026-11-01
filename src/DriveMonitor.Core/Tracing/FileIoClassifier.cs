using DriveMonitor.Core.Models;

namespace DriveMonitor.Core.Tracing;

/// <summary>Turns a raw kernel FileIo operation (plus its completion, if seen) into a <see cref="FileActivityEvent"/>.</summary>
internal static class FileIoClassifier
{
    // NT CreateOptions bits (the low 24 bits of the FileIo_Create CreateOptions field).
    internal const int FileDirectoryFile = 0x00000001;
    internal const int FileNonDirectoryFile = 0x00000040;
    internal const int FileDeleteOnClose = 0x00001000;

    // NT create dispositions.
    internal const int FileSupersede = 0;
    internal const int FileOpen = 1;
    internal const int FileCreate = 2;
    internal const int FileOpenIf = 3;
    internal const int FileOverwrite = 4;
    internal const int FileOverwriteIf = 5;

    // IO_STATUS_BLOCK.Information values returned by IRP_MJ_CREATE.
    internal const ulong FileSuperseded = 0;
    internal const ulong FileOpened = 1;
    internal const ulong FileCreated = 2;
    internal const ulong FileOverwritten = 3;

    // IRP flags carried in FileIo_ReadWrite.IoFlags.
    internal const int IrpNoCache = 0x00000001;
    internal const int IrpPagingIo = 0x00000002;

    // FILE_INFORMATION_CLASS values.
    internal const int FileBasicInformation = 4;
    internal const int FileRenameInformation = 10;
    internal const int FileLinkInformation = 11;
    internal const int FileDispositionInformation = 13;
    internal const int FilePositionInformation = 14;
    internal const int FileAllocationInformation = 19;
    internal const int FileEndOfFileInformation = 20;
    internal const int FileValidDataLengthInformation = 39;
    internal const int FileShortNameInformation = 40;
    internal const int FileDispositionInformationEx = 64;
    internal const int FileRenameInformationEx = 65;
    internal const int FileLinkInformationEx = 72;

    // FileDispositionInformationEx flag: delete (without it the request cancels a pending delete).
    internal const ulong FileDispositionDelete = 0x1;

    // STATUS_END_OF_FILE: a read that starts at or past the end of the file.
    internal const int StatusEndOfFile = unchecked((int)0xC0000011);

    /// <returns>The event, or null for operations that carry no useful information (e.g. file-pointer moves).</returns>
    internal static FileActivityEvent? Classify(PendingIo io, long sequence)
    {
        IoStatus status = !io.Completed
            ? IoStatus.Unknown
            : NtStatusText.IsSuccess(io.NtStatus) || IsBenignFailure(io) ? IoStatus.Success : IoStatus.Failed;

        FileOperation operation;
        FileEventFlags flags = FileEventFlags.None;
        long bytes = 0;
        long offset = -1;
        string path = io.Path;
        string? detail = null;
        string? target = null;

        switch (io.Op)
        {
            case RawFileOp.Read:
            case RawFileOp.Write:
                operation = io.Op == RawFileOp.Read ? FileOperation.Read : FileOperation.Write;
                if ((io.IrpFlags & IrpPagingIo) != 0)
                    flags |= FileEventFlags.Paging;
                if ((io.IrpFlags & IrpNoCache) != 0)
                    flags |= FileEventFlags.NonCached;
                offset = io.Offset;
                bytes = status switch
                {
                    IoStatus.Success => TransferredBytes(io),
                    IoStatus.Failed => 0,
                    _ => io.IoSize,
                };
                break;

            case RawFileOp.Create:
                (operation, detail) = ClassifyCreate(io, status);

                // Without either type option an open takes a folder as well as a file (backup semantics); a create
                // without FILE_DIRECTORY_FILE always makes a file, so only an open leaves the type in doubt.
                if ((io.CreateOptions & FileDirectoryFile) != 0)
                    flags |= FileEventFlags.Directory;
                else if (operation == FileOperation.Open && (io.CreateOptions & FileNonDirectoryFile) == 0)
                    flags |= FileEventFlags.MaybeDirectory;

                if ((io.CreateOptions & FileDeleteOnClose) != 0 && status != IoStatus.Failed)
                {
                    flags |= FileEventFlags.DeleteOnClose;
                    detail = detail is null ? "delete on close" : $"{detail}, delete on close";
                }

                break;

            case RawFileOp.Delete:
                (operation, detail, flags) = ClassifyDisposition(io);
                break;

            case RawFileOp.Rename:
                operation = FileOperation.Rename;
                target = TargetOf(io);
                detail = DescribeRename(target);
                break;

            case RawFileOp.SetInfo:
                switch (io.InfoClass)
                {
                    case FilePositionInformation:
                        return null; // only moves the file pointer
                    case FileDispositionInformation or FileDispositionInformationEx:
                        (operation, detail, flags) = ClassifyDisposition(io);
                        break;
                    case FileRenameInformation or FileRenameInformationEx:
                        operation = FileOperation.Rename;
                        target = TargetOf(io);
                        detail = DescribeRename(target);
                        break;
                    case FileLinkInformation or FileLinkInformationEx:
                        operation = FileOperation.SetInfo;
                        target = TargetOf(io);
                        detail = target is null ? "hard link created" : $"hard link → {target}";
                        break;
                    default:
                        operation = FileOperation.SetInfo;
                        (detail, bytes) = DescribeSetInfo(io);
                        if (io.InfoClass == FileEndOfFileInformation)
                            flags |= FileEventFlags.SizeChange;
                        break;
                }

                break;

            case RawFileOp.Flush:
                operation = FileOperation.Flush;
                break;

            case RawFileOp.DirEnum:
                operation = FileOperation.List;
                flags |= FileEventFlags.Directory;
                if (!string.IsNullOrEmpty(io.Pattern) && io.Pattern != "*")
                    detail = $"pattern {io.Pattern}";
                break;

            default:
                return null;
        }

        if (status == IoStatus.Failed)
            detail = NtStatusText.Describe(io.NtStatus);

        return new FileActivityEvent
        {
            Sequence = sequence,
            Timestamp = io.Timestamp,
            Operation = operation,
            Path = path,
            TargetPath = target,
            Drive = io.Drive,
            Bytes = bytes,
            Offset = offset,
            ProcessId = io.ProcessId,
            ProcessName = io.ProcessName,
            ThreadId = io.ThreadId,
            Flags = flags,
            Status = status,
            NtStatus = io.Completed ? io.NtStatus : 0,
            Detail = detail,
        };
    }

    private static (FileOperation Operation, string? Detail) ClassifyCreate(PendingIo io, IoStatus status)
    {
        if (status == IoStatus.Success)
        {
            return io.CompletionInfo switch
            {
                FileCreated => (FileOperation.Create, "created"),
                FileOverwritten => (FileOperation.Create, "overwritten"),
                FileSuperseded => (FileOperation.Create, "replaced"),
                _ => (FileOperation.Open, null),
            };
        }

        if (status == IoStatus.Failed)
            return (io.CreateDisposition == FileCreate ? FileOperation.Create : FileOperation.Open, null);

        // No completion seen: infer from what was requested.
        return io.CreateDisposition switch
        {
            FileCreate => (FileOperation.Create, "new file requested"),
            FileSupersede or FileOverwrite or FileOverwriteIf => (FileOperation.Create, "create or overwrite requested"),
            FileOpenIf => (FileOperation.Open, "open or create requested"),
            _ => (FileOperation.Open, null),
        };
    }

    /// <summary>
    /// A disposition request carries the requested state in ExtraInfo: the DeleteFile boolean for
    /// FileDispositionInformation, flags for FileDispositionInformationEx. Without the delete bit
    /// the request cancels a pending delete, which the DeleteCancelled flag tells the statistics.
    /// </summary>
    private static (FileOperation Operation, string? Detail, FileEventFlags Flags) ClassifyDisposition(PendingIo io)
    {
        bool cancels = io.InfoClass switch
        {
            FileDispositionInformation => io.ExtraInfo == 0,
            FileDispositionInformationEx => (io.ExtraInfo & FileDispositionDelete) == 0,
            _ => false,
        };
        return cancels
            ? (FileOperation.SetInfo, "delete cancelled", FileEventFlags.DeleteCancelled)
            : (FileOperation.Delete, null, FileEventFlags.None);
    }

    /// <summary>
    /// The reported rename/link target, if it is a DOS path on the operation's own drive that differs from the
    /// old path. Neither a rename nor a hard link can leave its volume, so a target on another drive can only be
    /// the path event of an unrelated operation matched to this one (request addresses are recycled within
    /// microseconds); the operation is then reported without a target rather than with a wrong one.
    /// </summary>
    private static string? TargetOf(PendingIo io) =>
        io.TargetPath is { Length: >= 3 } target
        && target[1] == ':'
        && char.ToUpperInvariant(target[0]) == char.ToUpperInvariant(io.Drive)
        && !IsSamePath(target, io.Path)
            ? target
            : null;

    /// <summary>
    /// True when <paramref name="target"/> names <paramref name="path"/> as it already is. The folders above the
    /// last name compare without case, as Windows looks them up. The last name compares with case: Windows keeps
    /// the case a name is given, so renaming "photos" to "Photos" is a real rename.
    /// </summary>
    private static bool IsSamePath(string target, string path)
    {
        int name = target.LastIndexOf('\\');
        return name >= 0
            && name == path.LastIndexOf('\\')
            && target.AsSpan(0, name).Equals(path.AsSpan(0, name), StringComparison.OrdinalIgnoreCase)
            && target.AsSpan(name).SequenceEqual(path.AsSpan(name));
    }

    private static string DescribeRename(string? target) => target switch
    {
        null => "renamed or moved",
        _ when target.Contains(@"\$RECYCLE.BIN\", StringComparison.OrdinalIgnoreCase) => "moved to the Recycle Bin",
        _ => $"→ {target}",
    };

    private static (string Detail, long Bytes) DescribeSetInfo(PendingIo io)
    {
        long size = io.ExtraInfo > long.MaxValue ? long.MaxValue : (long)io.ExtraInfo;
        return io.InfoClass switch
        {
            FileEndOfFileInformation => ($"size → {size:N0} bytes", size),
            FileAllocationInformation => ($"allocation → {size:N0} bytes", size),
            FileValidDataLengthInformation => ($"valid data length → {size:N0} bytes", size),
            FileBasicInformation => ("attributes or timestamps", 0),
            FileShortNameInformation => ("short name changed", 0),
            _ => ($"information class {io.InfoClass}", 0),
        };
    }

    private static long TransferredBytes(PendingIo io)
    {
        // Completion Information = bytes transferred; it can never exceed the request, nor, when no request
        // size was recorded, the most a single request can ask for (a ULONG).
        ulong limit = io.IoSize > 0 ? (ulong)io.IoSize : uint.MaxValue;
        return (long)Math.Min(io.CompletionInfo, limit);
    }

    /// <summary>Statuses that end an operation normally even though NT_SUCCESS is false.</summary>
    private static bool IsBenignFailure(PendingIo io) => io.Op switch
    {
        // The end of a listing, or a pattern that matched nothing.
        RawFileOp.DirEnum => io.NtStatus is NtStatusText.StatusNoMoreFiles or NtStatusText.StatusNoSuchFile,

        // Moves 0 bytes (Information is 0); every read-until-empty loop (FileStream, StreamReader, CopyTo) ends with one.
        RawFileOp.Read => io.NtStatus == StatusEndOfFile,
        _ => false,
    };
}
