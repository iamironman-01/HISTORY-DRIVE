using DriveMonitor.Core.Models;
using DriveMonitor.Core.Tracing;
using DriveMonitor.Tests.TestSupport;

namespace DriveMonitor.Tests.Tracing;

/// <summary>Turning raw kernel operations (built by hand) into <see cref="FileActivityEvent"/>s.</summary>
public sealed class FileIoClassifierTests
{
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int StatusObjectNameCollision = unchecked((int)0xC0000035);
    private const int StatusDiskFull = unchecked((int)0xC000007F);
    private const int StatusDeviceIoError = unchecked((int)0xC0000185);
    private const int StatusBufferOverflow = unchecked((int)0x80000005);

    private static readonly DateTime Time = new(2026, 10, 30, 9, 15, 30, 250);

    // ---- common fields ----

    [Fact]
    public void Classify_CopiesTheIdentityOfTheOperation()
    {
        PendingIo io = Completed(Io(RawFileOp.Write), status: 0, info: 10);
        io.IoSize = 10;

        FileActivityEvent ev = Classify(io, sequence: 42);

        Assert.Equal(42L, ev.Sequence);
        Assert.Equal(Time, ev.Timestamp);
        Assert.Equal(@"E:\docs\report.docx", ev.Path);
        Assert.Equal('E', ev.Drive);
        Assert.Equal(4321, ev.ProcessId);
        Assert.Equal("WINWORD.EXE", ev.ProcessName);
        Assert.Equal(8765, ev.ThreadId);
    }

    [Fact]
    public void Classify_WithoutCompletion_IsUnknownWithNtStatusZero()
    {
        PendingIo io = Io(RawFileOp.Write);
        io.NtStatus = StatusAccessDenied; // left over, but no completion was matched
        io.CompletionInfo = 5;
        io.IoSize = 100;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(IoStatus.Unknown, ev.Status);
        Assert.Equal(0, ev.NtStatus);
        Assert.Equal(100L, ev.Bytes);
        Assert.Null(ev.Detail);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(0x00000103)] // STATUS_PENDING
    [InlineData(0x40000000)] // informational
    public void Classify_NonNegativeStatus_IsSuccess(int status)
    {
        PendingIo io = Completed(Io(RawFileOp.Flush), status, info: 0);

        FileActivityEvent ev = Classify(io);

        Assert.Equal(IoStatus.Success, ev.Status);
        Assert.Equal(status, ev.NtStatus);
        Assert.Null(ev.Detail);
    }

    [Fact]
    public void Classify_WarningStatus_IsAFailure()
    {
        PendingIo io = Completed(Io(RawFileOp.Write), StatusBufferOverflow, info: 0);
        io.IoSize = 512;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(IoStatus.Failed, ev.Status);
        Assert.Equal(StatusBufferOverflow, ev.NtStatus);
        Assert.Equal("buffer overflow", ev.Detail);
    }

    [Fact]
    public void Classify_UnknownRawOperation_ReturnsNull()
    {
        Assert.Null(FileIoClassifier.Classify(Io((RawFileOp)200), 1));
    }

    // ---- read / write ----

    [Theory]
    [InlineData(4096UL, 4096, 4096L)] // full transfer
    [InlineData(100UL, 4096, 100L)] // short read at the end of the file
    [InlineData(0UL, 4096, 0L)] // read at the end of the file
    [InlineData(8192UL, 4096, 4096L)] // never more than requested
    [InlineData(512UL, 0, 512L)] // no request size known: trust the completion
    [InlineData(5_000_000_000UL, 0, 2_147_483_647L)] // capped at int.MaxValue
    public void ReadWrite_Success_ReportsTheTransferredBytes(ulong info, int ioSize, long expected)
    {
        foreach (RawFileOp op in new[] { RawFileOp.Read, RawFileOp.Write })
        {
            PendingIo io = Completed(Io(op), status: 0, info);
            io.IoSize = ioSize;

            FileActivityEvent ev = Classify(io);

            Assert.Equal(op == RawFileOp.Read ? FileOperation.Read : FileOperation.Write, ev.Operation);
            Assert.Equal(IoStatus.Success, ev.Status);
            Assert.Equal(expected, ev.Bytes);
            Assert.Null(ev.Detail);
        }
    }

    [Fact]
    public void ReadWrite_Failed_ReportsNoBytesAndDescribesTheStatus()
    {
        PendingIo io = Completed(Io(RawFileOp.Write), StatusDiskFull, info: 4096);
        io.IoSize = 65536;
        io.Offset = 0;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.Write, ev.Operation);
        Assert.Equal(IoStatus.Failed, ev.Status);
        Assert.Equal(StatusDiskFull, ev.NtStatus);
        Assert.Equal(0L, ev.Bytes);
        Assert.Equal("disk full", ev.Detail);
    }

    [Theory]
    [InlineData(65536, 65536L)]
    [InlineData(0, 0L)]
    [InlineData(-1, 0L)]
    public void ReadWrite_NoCompletion_ReportsTheRequestedSize(int ioSize, long expected)
    {
        PendingIo io = Io(RawFileOp.Read);
        io.IoSize = ioSize;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(IoStatus.Unknown, ev.Status);
        Assert.Equal(expected, ev.Bytes);
    }

    [Theory]
    [InlineData(0x0, FileEventFlags.None)]
    [InlineData(0x1, FileEventFlags.NonCached)]
    [InlineData(0x2, FileEventFlags.Paging)]
    [InlineData(0x3, FileEventFlags.Paging | FileEventFlags.NonCached)]
    [InlineData(0x43, FileEventFlags.Paging | FileEventFlags.NonCached)] // IRP_SYNCHRONOUS_PAGING_IO adds nothing
    [InlineData(0x400, FileEventFlags.None)]
    public void ReadWrite_IrpFlags_GivePagingAndNonCached(int irpFlags, FileEventFlags expected)
    {
        PendingIo io = Completed(Io(RawFileOp.Read), status: 0, info: 4096);
        io.IoSize = 4096;
        io.IrpFlags = irpFlags;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(expected, ev.Flags);
        Assert.Equal((expected & FileEventFlags.Paging) != 0, ev.IsPaging);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(1_048_576L)]
    [InlineData(-1L)]
    public void ReadWrite_OffsetIsCopied(long offset)
    {
        PendingIo io = Io(RawFileOp.Write);
        io.Offset = offset;

        Assert.Equal(offset, Classify(io).Offset);
    }

    [Fact]
    public void NoMoreFiles_OnARead_IsAFailure()
    {
        PendingIo io = Completed(Io(RawFileOp.Read), NtStatusText.StatusNoMoreFiles, info: 0);
        io.IoSize = 4096;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(IoStatus.Failed, ev.Status);
        Assert.Equal("no more files", ev.Detail);
        Assert.Equal(0L, ev.Bytes);
    }

    [Fact]
    public void EndOfFile_OnARead_IsASuccessfulZeroByteRead()
    {
        // The read that ends a read-until-empty loop: it starts at the end of the file and moves nothing.
        PendingIo io = Completed(Io(RawFileOp.Read), FileIoClassifier.StatusEndOfFile, info: 0);
        io.IoSize = 4096;
        io.Offset = 10_000;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.Read, ev.Operation);
        Assert.Equal(IoStatus.Success, ev.Status);
        Assert.Equal(FileIoClassifier.StatusEndOfFile, ev.NtStatus);
        Assert.Equal(0L, ev.Bytes);
        Assert.Equal(10_000L, ev.Offset);
        Assert.Null(ev.Detail);
    }

    [Fact]
    public void EndOfFile_OnAWrite_IsAFailure()
    {
        PendingIo io = Completed(Io(RawFileOp.Write), FileIoClassifier.StatusEndOfFile, info: 0);
        io.IoSize = 4096;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(IoStatus.Failed, ev.Status);
        Assert.Equal("end of file", ev.Detail);
        Assert.Equal(0L, ev.Bytes);
    }

    // ---- create ----

    [Theory]
    [InlineData(FileIoClassifier.FileCreated, FileOperation.Create, "created")]
    [InlineData(FileIoClassifier.FileOverwritten, FileOperation.Create, "overwritten")]
    [InlineData(FileIoClassifier.FileSuperseded, FileOperation.Create, "replaced")]
    [InlineData(FileIoClassifier.FileOpened, FileOperation.Open, null)]
    [InlineData(4UL, FileOperation.Open, null)] // FILE_EXISTS
    [InlineData(5UL, FileOperation.Open, null)] // FILE_DOES_NOT_EXIST
    public void Create_Success_FollowsTheCompletionInformation(ulong info, FileOperation expected, string? detail)
    {
        PendingIo io = Completed(Io(RawFileOp.Create), status: 0, info);
        io.CreateDisposition = FileIoClassifier.FileOpenIf; // the outcome, not the request, decides
        io.IoSize = 4096;
        io.Offset = 123;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(expected, ev.Operation);
        Assert.Equal(detail, ev.Detail);
        Assert.Equal(IoStatus.Success, ev.Status);
        Assert.Equal(FileEventFlags.None, ev.Flags);
        Assert.Equal(0L, ev.Bytes);
        Assert.Equal(-1L, ev.Offset);
    }

    [Theory]
    [InlineData(FileIoClassifier.FileCreate, FileOperation.Create)]
    [InlineData(FileIoClassifier.FileSupersede, FileOperation.Open)]
    [InlineData(FileIoClassifier.FileOpen, FileOperation.Open)]
    [InlineData(FileIoClassifier.FileOpenIf, FileOperation.Open)]
    [InlineData(FileIoClassifier.FileOverwrite, FileOperation.Open)]
    [InlineData(FileIoClassifier.FileOverwriteIf, FileOperation.Open)]
    public void Create_Failed_IsACreateOnlyForTheFileCreateDisposition(int disposition, FileOperation expected)
    {
        PendingIo io = Completed(Io(RawFileOp.Create), StatusAccessDenied, info: 0);
        io.CreateDisposition = disposition;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(expected, ev.Operation);
        Assert.Equal(IoStatus.Failed, ev.Status);
        Assert.Equal(StatusAccessDenied, ev.NtStatus);
        Assert.Equal("access denied", ev.Detail);
    }

    [Fact]
    public void Create_FailedBecauseTheFileExists_SaysAlreadyExists()
    {
        PendingIo io = Completed(Io(RawFileOp.Create), StatusObjectNameCollision, info: 0);
        io.CreateDisposition = FileIoClassifier.FileCreate;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.Create, ev.Operation);
        Assert.Equal("already exists", ev.Detail);
    }

    [Theory]
    [InlineData(FileIoClassifier.FileCreate, FileOperation.Create, "new file requested")]
    [InlineData(FileIoClassifier.FileSupersede, FileOperation.Create, "create or overwrite requested")]
    [InlineData(FileIoClassifier.FileOverwrite, FileOperation.Create, "create or overwrite requested")]
    [InlineData(FileIoClassifier.FileOverwriteIf, FileOperation.Create, "create or overwrite requested")]
    [InlineData(FileIoClassifier.FileOpenIf, FileOperation.Open, "open or create requested")]
    [InlineData(FileIoClassifier.FileOpen, FileOperation.Open, null)]
    [InlineData(9, FileOperation.Open, null)]
    public void Create_NoCompletion_IsInferredFromTheDisposition(int disposition, FileOperation expected, string? detail)
    {
        PendingIo io = Io(RawFileOp.Create);
        io.CreateDisposition = disposition;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(expected, ev.Operation);
        Assert.Equal(detail, ev.Detail);
        Assert.Equal(IoStatus.Unknown, ev.Status);
    }

    [Fact]
    public void Create_DirectoryOption_SetsTheDirectoryFlag()
    {
        PendingIo io = Completed(Io(RawFileOp.Create), status: 0, FileIoClassifier.FileOpened);
        io.CreateOptions = FileIoClassifier.FileDirectoryFile;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.Open, ev.Operation);
        Assert.Equal(FileEventFlags.Directory, ev.Flags);
        Assert.True(ev.IsDirectory);
    }

    [Fact]
    public void Create_DirectoryFlag_IsKeptWhenTheCreateFails()
    {
        PendingIo io = Completed(Io(RawFileOp.Create), StatusAccessDenied, info: 0);
        io.CreateOptions = FileIoClassifier.FileDirectoryFile;

        Assert.Equal(FileEventFlags.Directory, Classify(io).Flags);
    }

    [Theory]
    [InlineData(FileIoClassifier.FileCreated, "created, delete on close")]
    [InlineData(FileIoClassifier.FileOpened, "delete on close")]
    public void Create_DeleteOnClose_SetsTheFlagAndAddsToTheDetail(ulong info, string detail)
    {
        PendingIo io = Completed(Io(RawFileOp.Create), status: 0, info);
        io.CreateOptions = FileIoClassifier.FileDeleteOnClose;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileEventFlags.DeleteOnClose, ev.Flags);
        Assert.Equal(detail, ev.Detail);
    }

    [Fact]
    public void Create_DeleteOnCloseWithoutCompletion_AddsToTheInferredDetail()
    {
        PendingIo io = Io(RawFileOp.Create);
        io.CreateDisposition = FileIoClassifier.FileCreate;
        io.CreateOptions = FileIoClassifier.FileDeleteOnClose;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileEventFlags.DeleteOnClose, ev.Flags);
        Assert.Equal("new file requested, delete on close", ev.Detail);
    }

    [Fact]
    public void Create_DeleteOnCloseThatFailed_HasNoFlag()
    {
        PendingIo io = Completed(Io(RawFileOp.Create), StatusAccessDenied, info: 0);
        io.CreateDisposition = FileIoClassifier.FileCreate;
        io.CreateOptions = FileIoClassifier.FileDeleteOnClose;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileEventFlags.None, ev.Flags);
        Assert.Equal("access denied", ev.Detail);
    }

    [Fact]
    public void Create_DirectoryWithDeleteOnClose_SetsBothFlags()
    {
        PendingIo io = Completed(Io(RawFileOp.Create), status: 0, FileIoClassifier.FileCreated);
        io.CreateOptions = FileIoClassifier.FileDirectoryFile | FileIoClassifier.FileDeleteOnClose;

        Assert.Equal(FileEventFlags.Directory | FileEventFlags.DeleteOnClose, Classify(io).Flags);
    }

    // ---- delete / rename events ----

    [Fact]
    public void DeleteEvent_IsADelete()
    {
        PendingIo io = Completed(Io(RawFileOp.Delete), status: 0, info: 0);

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.Delete, ev.Operation);
        Assert.Null(ev.Detail);
        Assert.Equal(0L, ev.Bytes);
        Assert.Equal(-1L, ev.Offset);
        Assert.Equal(FileEventFlags.None, ev.Flags);
    }

    [Fact]
    public void RenameEvent_IsARename()
    {
        PendingIo io = Completed(Io(RawFileOp.Rename), status: 0, info: 0);

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.Rename, ev.Operation);
        Assert.Equal("renamed or moved", ev.Detail);
    }

    [Fact]
    public void RenameEvent_Failed_DescribesTheStatus()
    {
        PendingIo io = Completed(Io(RawFileOp.Rename), StatusAccessDenied, info: 0);

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.Rename, ev.Operation);
        Assert.Equal(IoStatus.Failed, ev.Status);
        Assert.Equal("access denied", ev.Detail);
    }

    // ---- set information ----

    [Fact]
    public void SetInfo_FilePosition_IsDropped()
    {
        PendingIo io = Io(RawFileOp.SetInfo);
        io.InfoClass = FileIoClassifier.FilePositionInformation;

        Assert.Null(FileIoClassifier.Classify(io, 1));
        Assert.Null(FileIoClassifier.Classify(Completed(io, status: 0, info: 0), 2));
    }

    [Theory]
    [InlineData(FileIoClassifier.FileDispositionInformation)]
    [InlineData(FileIoClassifier.FileDispositionInformationEx)]
    public void SetInfo_DispositionClass_IsADelete(int infoClass)
    {
        PendingIo io = Completed(Io(RawFileOp.SetInfo), status: 0, info: 0);
        io.InfoClass = infoClass;
        io.ExtraInfo = 1;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.Delete, ev.Operation);
        Assert.Null(ev.Detail);
        Assert.Equal(0L, ev.Bytes);
    }

    [Theory]
    [InlineData(FileIoClassifier.FileRenameInformation)]
    [InlineData(FileIoClassifier.FileRenameInformationEx)]
    public void SetInfo_RenameClass_IsARename(int infoClass)
    {
        PendingIo io = Completed(Io(RawFileOp.SetInfo), status: 0, info: 0);
        io.InfoClass = infoClass;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.Rename, ev.Operation);
        Assert.Equal("renamed or moved", ev.Detail);
    }

    [Fact]
    public void SetInfo_EndOfFile_IsASizeChangeWithTheNewSize()
    {
        using CultureScope culture = CultureScope.Invariant();
        PendingIo io = Completed(Io(RawFileOp.SetInfo), status: 0, info: 0);
        io.InfoClass = FileIoClassifier.FileEndOfFileInformation;
        io.ExtraInfo = 4096;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.SetInfo, ev.Operation);
        Assert.Equal(FileEventFlags.SizeChange, ev.Flags);
        Assert.Equal(4096L, ev.Bytes);
        Assert.Equal("size \u2192 4,096 bytes", ev.Detail);
        Assert.Equal(-1L, ev.Offset);
    }

    [Theory]
    [InlineData(FileIoClassifier.FileAllocationInformation, 1_048_576UL, "allocation \u2192 1,048,576 bytes", 1_048_576L)]
    [InlineData(FileIoClassifier.FileValidDataLengthInformation, 0UL, "valid data length \u2192 0 bytes", 0L)]
    public void SetInfo_OtherSizeClasses_ReportTheSizeWithoutTheSizeChangeFlag(int infoClass, ulong extraInfo, string detail, long bytes)
    {
        using CultureScope culture = CultureScope.Invariant();
        PendingIo io = Completed(Io(RawFileOp.SetInfo), status: 0, info: 0);
        io.InfoClass = infoClass;
        io.ExtraInfo = extraInfo;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.SetInfo, ev.Operation);
        Assert.Equal(FileEventFlags.None, ev.Flags);
        Assert.Equal(bytes, ev.Bytes);
        Assert.Equal(detail, ev.Detail);
    }

    [Fact]
    public void SetInfo_SizeLargerThanLongMaxValue_IsClamped()
    {
        using CultureScope culture = CultureScope.Invariant();
        PendingIo io = Completed(Io(RawFileOp.SetInfo), status: 0, info: 0);
        io.InfoClass = FileIoClassifier.FileEndOfFileInformation;
        io.ExtraInfo = ulong.MaxValue;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(long.MaxValue, ev.Bytes);
        Assert.Equal("size \u2192 9,223,372,036,854,775,807 bytes", ev.Detail);
    }

    [Fact]
    public void SetInfo_SizeDetail_UsesTheCurrentCulture()
    {
        using var culture = new CultureScope(CultureScope.Unusual());
        PendingIo io = Completed(Io(RawFileOp.SetInfo), status: 0, info: 0);
        io.InfoClass = FileIoClassifier.FileEndOfFileInformation;
        io.ExtraInfo = 1_234_567;

        Assert.Equal("size \u2192 1.234.567 bytes", Classify(io).Detail);
    }

    [Theory]
    [InlineData(FileIoClassifier.FileBasicInformation, "attributes or timestamps")]
    [InlineData(FileIoClassifier.FileLinkInformation, "hard link created")]
    [InlineData(FileIoClassifier.FileLinkInformationEx, "hard link created")]
    [InlineData(FileIoClassifier.FileShortNameInformation, "short name changed")]
    [InlineData(99, "information class 99")]
    [InlineData(0, "information class 0")]
    public void SetInfo_OtherClasses_AreDescribed(int infoClass, string detail)
    {
        PendingIo io = Completed(Io(RawFileOp.SetInfo), status: 0, info: 0);
        io.InfoClass = infoClass;
        io.ExtraInfo = 777;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.SetInfo, ev.Operation);
        Assert.Equal(detail, ev.Detail);
        Assert.Equal(0L, ev.Bytes);
        Assert.Equal(FileEventFlags.None, ev.Flags);
    }

    [Fact]
    public void SetInfo_EndOfFileThatFailed_DescribesTheStatusButKeepsTheRequestedSize()
    {
        PendingIo io = Completed(Io(RawFileOp.SetInfo), StatusDiskFull, info: 0);
        io.InfoClass = FileIoClassifier.FileEndOfFileInformation;
        io.ExtraInfo = 4096;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(IoStatus.Failed, ev.Status);
        Assert.Equal("disk full", ev.Detail);
        Assert.Equal(FileEventFlags.SizeChange, ev.Flags);
        Assert.Equal(4096L, ev.Bytes);
    }

    // ---- flush / directory listing ----

    [Fact]
    public void Flush_IsAFlush()
    {
        PendingIo io = Completed(Io(RawFileOp.Flush), status: 0, info: 0);
        io.IoSize = 4096;
        io.Offset = 10;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.Flush, ev.Operation);
        Assert.Equal(0L, ev.Bytes);
        Assert.Equal(-1L, ev.Offset);
        Assert.Equal(FileEventFlags.None, ev.Flags);
        Assert.Null(ev.Detail);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("*", null)]
    [InlineData("*.jpg", "pattern *.jpg")]
    [InlineData("report?.docx", "pattern report?.docx")]
    public void DirEnum_IsAListingOfADirectory(string? pattern, string? detail)
    {
        PendingIo io = Completed(Io(RawFileOp.DirEnum), status: 0, info: 0);
        io.Pattern = pattern;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.List, ev.Operation);
        Assert.Equal(FileEventFlags.Directory, ev.Flags);
        Assert.Equal(detail, ev.Detail);
        Assert.Equal(IoStatus.Success, ev.Status);
    }

    [Theory]
    [InlineData(NtStatusText.StatusNoMoreFiles)]
    [InlineData(NtStatusText.StatusNoSuchFile)]
    public void DirEnum_EndOfListingStatus_CountsAsSuccess(int status)
    {
        PendingIo io = Completed(Io(RawFileOp.DirEnum), status, info: 0);
        io.Pattern = "*.txt";

        FileActivityEvent ev = Classify(io);

        Assert.Equal(IoStatus.Success, ev.Status);
        Assert.Equal(status, ev.NtStatus);
        Assert.Equal("pattern *.txt", ev.Detail);
    }

    [Fact]
    public void DirEnum_OtherFailure_IsAFailure()
    {
        PendingIo io = Completed(Io(RawFileOp.DirEnum), StatusDeviceIoError, info: 0);
        io.Pattern = "*.txt";

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.List, ev.Operation);
        Assert.Equal(IoStatus.Failed, ev.Status);
        Assert.Equal("device I/O error", ev.Detail);
        Assert.Equal(FileEventFlags.Directory, ev.Flags);
    }

    // ---- disposition requests and rename targets ----

    [Theory]
    [InlineData(FileIoClassifier.FileDispositionInformation, 1UL, FileOperation.Delete)]
    [InlineData(FileIoClassifier.FileDispositionInformation, 0UL, FileOperation.SetInfo)]
    [InlineData(FileIoClassifier.FileDispositionInformationEx, 0x1UL, FileOperation.Delete)] // DELETE
    [InlineData(FileIoClassifier.FileDispositionInformationEx, 0x3UL, FileOperation.Delete)] // DELETE | POSIX_SEMANTICS
    [InlineData(FileIoClassifier.FileDispositionInformationEx, 0x9UL, FileOperation.Delete)] // DELETE | ON_CLOSE
    [InlineData(FileIoClassifier.FileDispositionInformationEx, 0x8UL, FileOperation.SetInfo)] // ON_CLOSE alone clears delete-on-close
    [InlineData(FileIoClassifier.FileDispositionInformationEx, 0x0UL, FileOperation.SetInfo)]
    public void DeleteEvent_DispositionFlagsDecideBetweenDeleteAndCancel(int infoClass, ulong extraInfo, FileOperation expected)
    {
        PendingIo io = Completed(Io(RawFileOp.Delete), status: 0, info: 0);
        io.InfoClass = infoClass;
        io.ExtraInfo = extraInfo;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(expected, ev.Operation);
        Assert.Equal(expected == FileOperation.SetInfo ? "delete cancelled" : null, ev.Detail);
    }

    [Fact]
    public void SetInfo_DispositionWithoutTheDeleteFlag_IsACancel()
    {
        PendingIo io = Completed(Io(RawFileOp.SetInfo), status: 0, info: 0);
        io.InfoClass = FileIoClassifier.FileDispositionInformation;
        io.ExtraInfo = 0;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.SetInfo, ev.Operation);
        Assert.Equal("delete cancelled", ev.Detail);
    }

    [Fact]
    public void DeleteEvent_Failed_DescribesTheStatusInsteadOfTheRequest()
    {
        // FAT rejects the Ex disposition class; Windows then retries with the classic one.
        PendingIo io = Completed(Io(RawFileOp.Delete), unchecked((int)0xC000000D), info: 0);
        io.InfoClass = FileIoClassifier.FileDispositionInformationEx;
        io.ExtraInfo = 0x3;

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.Delete, ev.Operation);
        Assert.Equal(IoStatus.Failed, ev.Status);
        Assert.Equal("invalid parameter", ev.Detail);
    }

    [Fact]
    public void RenameEvent_WithATarget_ReportsIt()
    {
        PendingIo io = Completed(Io(RawFileOp.Rename), status: 0, info: 0);
        io.TargetPath = @"E:\docs\final.docx";

        FileActivityEvent ev = Classify(io);

        Assert.Equal(@"E:\docs\final.docx", ev.TargetPath);
        Assert.Equal("\u2192 E:\\docs\\final.docx", ev.Detail);
    }

    [Theory]
    [InlineData(@"E:\$RECYCLE.BIN\S-1-5-21-1-2-3-1001\$RAB12CD.docx")]
    [InlineData(@"E:\$Recycle.Bin\S-1-5-18\$R1.docx")]
    public void RenameEvent_IntoTheRecycleBin_SaysSo(string target)
    {
        PendingIo io = Completed(Io(RawFileOp.Rename), status: 0, info: 0);
        io.TargetPath = target;

        FileActivityEvent ev = Classify(io);

        Assert.Equal("moved to the Recycle Bin", ev.Detail);
        Assert.Equal(target, ev.TargetPath);
    }

    [Theory]
    [InlineData(@"E:\docs\report.docx")] // the old name: the event did not carry the target
    [InlineData(@"e:\DOCS\REPORT.DOCX")]
    [InlineData(@"\Device\HarddiskVolume9\x.docx")] // no drive letter
    [InlineData(@"\\server\share\x.docx")]
    [InlineData("E:")]
    [InlineData("")]
    public void RenameEvent_WithAnUnusableTarget_IgnoresIt(string target)
    {
        PendingIo io = Completed(Io(RawFileOp.Rename), status: 0, info: 0);
        io.TargetPath = target;

        FileActivityEvent ev = Classify(io);

        Assert.Null(ev.TargetPath);
        Assert.Equal("renamed or moved", ev.Detail);
    }

    [Fact]
    public void SetInfo_HardLinkWithATarget_ReportsIt()
    {
        PendingIo io = Completed(Io(RawFileOp.SetInfo), status: 0, info: 0);
        io.InfoClass = FileIoClassifier.FileLinkInformationEx;
        io.TargetPath = @"E:\links\report.docx";

        FileActivityEvent ev = Classify(io);

        Assert.Equal(FileOperation.SetInfo, ev.Operation);
        Assert.Equal(@"E:\links\report.docx", ev.TargetPath);
        Assert.Equal("hard link \u2192 E:\\links\\report.docx", ev.Detail);
    }

    private static PendingIo Io(RawFileOp op) => new()
    {
        Op = op,
        Irp = 0xFFFF_9A00_1234_5600,
        TimestampQpc = 123_456,
        Timestamp = Time,
        Path = @"E:\docs\report.docx",
        Drive = 'E',
        ProcessId = 4321,
        ThreadId = 8765,
        ProcessName = "WINWORD.EXE",
    };

    private static PendingIo Completed(PendingIo io, int status, ulong info)
    {
        io.Completed = true;
        io.NtStatus = status;
        io.CompletionInfo = info;
        return io;
    }

    private static FileActivityEvent Classify(PendingIo io, long sequence = 1)
    {
        FileActivityEvent? ev = FileIoClassifier.Classify(io, sequence);
        Assert.NotNull(ev);
        return ev;
    }
}
