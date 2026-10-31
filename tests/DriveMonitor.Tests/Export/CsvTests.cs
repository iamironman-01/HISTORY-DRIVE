using System.Globalization;
using System.Text;
using DriveMonitor.Core.Aggregation;
using DriveMonitor.Core.Export;
using DriveMonitor.Core.Models;
using DriveMonitor.Tests.TestSupport;

namespace DriveMonitor.Tests.Export;

/// <summary>CSV escaping, the event and file-statistics rows, and the export files.</summary>
public sealed class CsvTests
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    // ---- Escape ----

    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("plain", "plain")]
    [InlineData(@"E:\photos\img 001.jpg", @"E:\photos\img 001.jpg")]
    [InlineData("a=b", "a=b")]
    [InlineData("'quoted", "'quoted")]
    [InlineData("\u65E5\u672C\u8A9E.txt", "\u65E5\u672C\u8A9E.txt")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("\"", "\"\"\"\"")]
    [InlineData("line1\r\nline2", "\"line1\r\nline2\"")]
    [InlineData("line1\nline2", "\"line1\nline2\"")]
    [InlineData("a\rb", "\"a\rb\"")]
    [InlineData(" leading", "\" leading\"")]
    [InlineData("trailing ", "\"trailing \"")]
    [InlineData(" ", "\" \"")]
    [InlineData(" =1+2", "\" =1+2\"")]
    public void Escape_QuotesOnlyWhenNeeded(string? value, string expected)
    {
        Assert.Equal(expected, Csv.Escape(value));
    }

    [Theory]
    [InlineData("=1+2", "'=1+2")]
    [InlineData("+cmd", "'+cmd")]
    [InlineData("-5", "'-5")]
    [InlineData("-", "'-")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tdata", "'\tdata")]
    [InlineData("\rdata", "\"'\rdata\"")]
    [InlineData("=a,b", "\"'=a,b\"")]
    [InlineData("=HYPERLINK(\"x\")", "\"'=HYPERLINK(\"\"x\"\")\"")]
    public void Escape_FormulaPrefix_GetsAnApostrophe(string value, string expected)
    {
        Assert.Equal(expected, Csv.Escape(value));
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "a,b")]
    [InlineData("say \"hi\"", "say \"hi\"")]
    [InlineData("line1\r\nline2", "line1\r\nline2")]
    [InlineData(" both ", " both ")]
    [InlineData("=1+2", "'=1+2")]
    [InlineData("\r=x,\"y\"", "'\r=x,\"y\"")]
    public void Escape_RoundTripsThroughAnRfc4180Reader(string value, string expected)
    {
        string[] record = Assert.Single(Rfc4180.Parse(Csv.Escape(value) + "\r\n"));

        Assert.Equal(expected, Assert.Single(record));
    }

    // ---- rows ----

    [Fact]
    public void WriteRow_SeparatesWithCommasAndEndsWithCrLf()
    {
        Assert.Equal("a,,c\r\n", Write(writer => Csv.WriteRow(writer, new[] { "a", null, "c" })));
        Assert.Equal("\r\n", Write(writer => Csv.WriteRow(writer, Array.Empty<string?>())));
    }

    [Fact]
    public void WriteHeader_WritesTheEventColumns()
    {
        Assert.Equal(
            "Time,Operation,Path,Drive,Bytes,Offset,Process,PID,TID,Flags,Status,NtStatus,Detail,Target\r\n",
            Write(writer => Csv.WriteHeader(writer, Csv.EventHeader)));
    }

    [Fact]
    public void WriteHeader_WritesTheFileStatisticsColumns()
    {
        Assert.Equal(
            "Path,Drive,Folder,Reads,Bytes read,Writes,Bytes written,Paging bytes read,Paging bytes written,"
            + "Opens,Lifecycle,First seen,Last seen,Last operation,Last process,Processes,Renamed to\r\n",
            Write(writer => Csv.WriteHeader(writer, Csv.FileStatsHeader)));
    }

    [Fact]
    public void WriteEvent_WritesOneFieldPerHeaderColumn()
    {
        var ev = new FileActivityEvent
        {
            Sequence = 7,
            Timestamp = new DateTime(2026, 10, 30, 13, 45, 7, 123),
            Operation = FileOperation.Write,
            Path = @"E:\data\report, final.txt",
            Drive = 'E',
            Bytes = 1_048_576,
            Offset = 4096,
            ProcessId = 1234,
            ProcessName = "WINWORD.EXE",
            ThreadId = 5678,
            Flags = FileEventFlags.Paging | FileEventFlags.NonCached,
            Status = IoStatus.Success,
            NtStatus = 0,
            Detail = null,
        };

        string[] row = WriteEventRow(ev);

        Assert.Equal(Csv.EventHeader.Length, row.Length);
        string[] expected =
        [
            "2026-10-30 13:45:07.123", "Write", @"E:\data\report, final.txt", "E", "1048576", "4096", "WINWORD.EXE",
            "1234", "5678", "Paging, NonCached", "Success", "", "", "",
        ];
        Assert.Equal(expected, row);
    }

    [Fact]
    public void WriteEvent_WritesTheRenameTargetInTheLastColumn()
    {
        var ev = new FileActivityEvent
        {
            Operation = FileOperation.Rename,
            Path = @"E:\old.txt",
            TargetPath = @"E:\new, final.txt",
            Drive = 'E',
            Detail = @"→ E:\new, final.txt",
        };

        string[] row = WriteEventRow(ev);

        Assert.Equal(Csv.EventHeader.Length, row.Length);
        Assert.Equal("Target", Csv.EventHeader[^1]);
        Assert.Equal(@"E:\new, final.txt", row[^1]);
    }

    [Fact]
    public void WriteEvent_FormatsNumbersAndTimesWithTheInvariantCulture()
    {
        using var culture = new CultureScope(CultureScope.Unusual());
        var ev = new FileActivityEvent
        {
            Timestamp = new DateTime(2026, 1, 2, 3, 4, 5, 6),
            Operation = FileOperation.SetInfo,
            Path = @"E:\x.bin",
            Drive = 'E',
            Bytes = -5,
            Offset = 1_234_567,
            ProcessId = 99,
            ThreadId = 100,
            Status = IoStatus.Failed,
            NtStatus = unchecked((int)0xC000007F),
            Detail = "disk full",
        };

        string[] row = WriteEventRow(ev);

        Assert.Equal("2026-01-02 03:04:05.006", row[0]);
        Assert.Equal("-5", row[4]);
        Assert.Equal("1234567", row[5]);
        Assert.Equal("0xC000007F", row[11]);
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(unchecked((int)0xC0000022), "0xC0000022")]
    [InlineData(unchecked((int)0x80000006), "0x80000006")]
    [InlineData(0x103, "0x00000103")]
    public void WriteEvent_NtStatusIsHexOrEmpty(int ntStatus, string expected)
    {
        string[] row = WriteEventRow(new FileActivityEvent { Path = @"E:\x", Drive = 'E', NtStatus = ntStatus });

        Assert.Equal(expected, row[11]);
    }

    [Theory]
    [InlineData(-1L, "")]
    [InlineData(-7L, "")]
    [InlineData(0L, "0")]
    [InlineData(5_000_000_000L, "5000000000")]
    public void WriteEvent_OffsetIsEmptyWhenNotApplicable(long offset, string expected)
    {
        string[] row = WriteEventRow(new FileActivityEvent { Path = @"E:\x", Drive = 'E', Offset = offset });

        Assert.Equal(expected, row[5]);
    }

    [Fact]
    public void WriteEvent_DefaultEvent_HasEmptyOptionalFields()
    {
        string[] row = WriteEventRow(new FileActivityEvent { Path = @"E:\x", Drive = 'E', Operation = FileOperation.List });

        Assert.Equal(14, row.Length);
        Assert.Equal("", row[13]); // no rename target
        Assert.Equal("List", row[1]);
        Assert.Equal("0", row[4]); // bytes
        Assert.Equal("", row[5]); // offset -1
        Assert.Equal("", row[6]); // no process name
        Assert.Equal("", row[9]); // no flags
        Assert.Equal("Unknown", row[10]);
        Assert.Equal("", row[11]); // no NtStatus
        Assert.Equal("", row[12]); // no detail
    }

    [Fact]
    public void WriteEvent_EscapesTextFields()
    {
        var ev = new FileActivityEvent
        {
            Path = "E:\\odd \"name\",\r\n.txt",
            Drive = 'E',
            ProcessName = "-evil.exe",
            Flags = FileEventFlags.Directory,
            Detail = "=cmd|' /C calc'!A0",
        };

        string[] row = WriteEventRow(ev);

        Assert.Equal(14, row.Length);
        Assert.Equal("E:\\odd \"name\",\r\n.txt", row[2]);
        Assert.Equal("'-evil.exe", row[6]);
        Assert.Equal("Directory", row[9]);
        Assert.Equal("'=cmd|' /C calc'!A0", row[12]);
    }

    [Fact]
    public void WriteFileStats_WritesOneFieldPerHeaderColumn()
    {
        var stats = new FileStatsSnapshot(
            Path: @"E:\music\a, b.mp3",
            Drive: 'E',
            IsDirectory: false,
            Reads: 3,
            Writes: 2,
            BytesRead: 3000,
            BytesWritten: 2000,
            PagingBytesRead: 4096,
            PagingBytesWritten: 8192,
            Opens: 5,
            Lifecycle: FileLifecycle.Created | FileLifecycle.Resized,
            FirstSeen: new DateTime(2026, 1, 2, 3, 4, 5, 6),
            LastSeen: new DateTime(2026, 1, 2, 3, 4, 6, 7),
            LastOperation: FileOperation.Write,
            LastProcess: "vlc.exe",
            Processes: "vlc.exe, explorer.exe, a.exe +2 more",
            ProcessCount: 5);

        string[] row = WriteFileStatsRow(stats);

        Assert.Equal(Csv.FileStatsHeader.Length, row.Length);
        string[] expected =
        [
            @"E:\music\a, b.mp3", "E", "", "3", "3000", "2", "2000", "4096", "8192", "5", "Created, Resized",
            "2026-01-02 03:04:05.006", "2026-01-02 03:04:06.007", "Write", "vlc.exe", "vlc.exe, explorer.exe, a.exe +2 more", "",
        ];
        Assert.Equal(expected, row);
    }

    [Fact]
    public void WriteFileStats_WritesTheRenameTarget()
    {
        var stats = new FileStatsSnapshot(
            @"E:\old.txt", 'E', false, 0, 0, 0, 0, 0, 0, 1, FileLifecycle.Renamed, default, default, FileOperation.Rename,
            "explorer.exe", "explorer.exe", 1, RenamedTo: @"E:\new.txt");

        string[] row = WriteFileStatsRow(stats);

        Assert.Equal("Renamed to", Csv.FileStatsHeader[^1]);
        Assert.Equal(@"E:\new.txt", row[^1]);
    }

    [Fact]
    public void WriteFileStats_FolderWithoutLifecycle()
    {
        var stats = new FileStatsSnapshot(
            @"E:\photos", 'E', true, 0, 0, 0, 0, 0, 0, 2, FileLifecycle.None, default, default, FileOperation.Open, "", "", 0);

        string[] row = WriteFileStatsRow(stats);

        Assert.Equal(17, row.Length);
        Assert.Equal("yes", row[2]);
        Assert.Equal("", row[10]);
        Assert.Equal("0001-01-01 00:00:00.000", row[11]);
        Assert.Equal("Open", row[13]);
    }

    [Fact]
    public void WriteFileStats_FormatsNumbersWithTheInvariantCulture()
    {
        using var culture = new CultureScope(CultureScope.Unusual());
        var stats = new FileStatsSnapshot(
            @"E:\big.iso", 'E', false, 1, 0, 4_700_000_000, 0, 0, 0, 1, FileLifecycle.None,
            new DateTime(2026, 1, 2, 3, 4, 5, 6), new DateTime(2026, 1, 2, 3, 4, 5, 6), FileOperation.Read, "a.exe", "a.exe", 1);

        string[] row = WriteFileStatsRow(stats);

        Assert.Equal("4700000000", row[4]);
        Assert.Equal("2026-01-02 03:04:05.006", row[11]);
    }

    // ---- export files ----

    [Fact]
    public void FileEncoding_IsUtf8WithByteOrderMark()
    {
        Assert.Equal(Utf8Bom, Csv.FileEncoding.GetPreamble());
    }

    [Fact]
    public void ExportEvents_WritesBomHeaderAndOneRowPerEvent()
    {
        const string nonAsciiPath = "E:\\fotos\\\u00F1and\u00FA \u65E5\u672C.jpg";
        using var temp = new TempDirectory();
        string path = temp.PathOf("events.csv");
        FileActivityEvent[] events =
        [
            new() { Path = nonAsciiPath, Drive = 'E', Operation = FileOperation.Read, Bytes = 10 },
            new() { Path = @"E:\b, c.txt", Drive = 'E', Operation = FileOperation.Delete },
        ];

        Csv.ExportEvents(path, events);

        List<string[]> records = ReadCsvFile(path);
        Assert.Equal(3, records.Count);
        Assert.Equal(Csv.EventHeader, records[0]);
        Assert.Equal(nonAsciiPath, records[1][2]);
        Assert.Equal("10", records[1][4]);
        Assert.Equal(@"E:\b, c.txt", records[2][2]);
        Assert.Equal("Delete", records[2][1]);
    }

    [Fact]
    public void ExportEvents_NoEvents_WritesOnlyTheHeader()
    {
        using var temp = new TempDirectory();
        string path = temp.PathOf("empty.csv");

        Csv.ExportEvents(path, Array.Empty<FileActivityEvent>());

        Assert.Equal(Csv.EventHeader, Assert.Single(ReadCsvFile(path)));
    }

    [Fact]
    public void ExportEvents_ReplacesAnExistingFile()
    {
        using var temp = new TempDirectory();
        string path = temp.PathOf("events.csv");
        File.WriteAllText(path, new string('x', 10_000));

        Csv.ExportEvents(path, Array.Empty<FileActivityEvent>());

        Assert.Equal(Csv.EventHeader, Assert.Single(ReadCsvFile(path)));
    }

    [Fact]
    public void ExportFileStats_WritesBomHeaderAndOneRowPerFile()
    {
        using var temp = new TempDirectory();
        string path = temp.PathOf("files.csv");
        FileStatsSnapshot[] files =
        [
            new(@"E:\a.txt", 'E', false, 1, 2, 3, 4, 5, 6, 7, FileLifecycle.Deleted, default, default, FileOperation.Delete, "x.exe", "x.exe", 1),
            new(@"E:\dir", 'E', true, 0, 0, 0, 0, 0, 0, 0, FileLifecycle.Created, default, default, FileOperation.Create, "y.exe", "y.exe", 1),
        ];

        Csv.ExportFileStats(path, files);

        List<string[]> records = ReadCsvFile(path);
        Assert.Equal(3, records.Count);
        Assert.Equal(Csv.FileStatsHeader, records[0]);
        Assert.Equal(new[] { @"E:\a.txt", "E", "", "1", "3", "2", "4", "5", "6", "7", "Deleted" }, records[1][..11]);
        Assert.Equal(@"E:\dir", records[2][0]);
        Assert.Equal("yes", records[2][2]);
        Assert.Equal("Created", records[2][10]);
    }

    private static string Write(Action<TextWriter> write)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        write(writer);
        return writer.ToString();
    }

    private static string[] WriteEventRow(FileActivityEvent ev) =>
        Assert.Single(Rfc4180.Parse(Write(writer => Csv.WriteEvent(writer, ev))));

    private static string[] WriteFileStatsRow(FileStatsSnapshot stats) =>
        Assert.Single(Rfc4180.Parse(Write(writer => Csv.WriteFileStats(writer, stats))));

    /// <summary>Checks the UTF-8 byte-order mark, then parses the rest strictly.</summary>
    private static List<string[]> ReadCsvFile(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        Assert.True(bytes.Length >= Utf8Bom.Length, "The file is shorter than a byte-order mark.");
        Assert.Equal(Utf8Bom, bytes[..Utf8Bom.Length]);
        return Rfc4180.Parse(Encoding.UTF8.GetString(bytes, Utf8Bom.Length, bytes.Length - Utf8Bom.Length));
    }
}
