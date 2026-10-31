using System.Globalization;
using System.Text;
using DriveMonitor.Core.Export;
using DriveMonitor.Core.Models;
using DriveMonitor.Tests.TestSupport;

namespace DriveMonitor.Tests.Export;

/// <summary>The background CSV log of published events.</summary>
public sealed class CsvActivityLogTests
{
    private static readonly string HeaderLine = string.Join(",", Csv.EventHeader) + "\r\n";

    [Fact]
    public void Constructor_CreatesTheFolderAndWritesTheHeaderAtOnce()
    {
        using var temp = new TempDirectory();
        string path = Path.Combine(temp.FullPath, "nested", "deeper", "log.csv");

        using var log = new CsvActivityLog(path);

        Assert.Equal(path, log.FilePath);
        byte[] bytes = ReadShared(path);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        Assert.Equal(HeaderLine, Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
    }

    [Fact]
    public void Constructor_ReplacesAnExistingLog()
    {
        using var temp = new TempDirectory();
        string path = temp.PathOf("log.csv");
        File.WriteAllText(path, "old content that must disappear\r\n");

        using (new CsvActivityLog(path))
        {
        }

        Assert.Equal(HeaderLine, File.ReadAllText(path));
    }

    [Fact]
    public void Dispose_FlushesEveryEventInOrder()
    {
        using var temp = new TempDirectory();
        string path = temp.PathOf("log.csv");
        var log = new CsvActivityLog(path);

        for (int i = 0; i < 100; i++)
        {
            log.Write(new FileActivityEvent
            {
                Sequence = i,
                Timestamp = new DateTime(2026, 10, 30, 12, 0, 0),
                Operation = FileOperation.Read,
                Path = $@"E:\file{i}.txt",
                Drive = 'E',
                Bytes = i,
            });
        }

        log.Dispose();

        Assert.Equal(100L, log.Written);
        Assert.Equal(0L, log.Dropped);
        Assert.Null(log.Error);
        List<string[]> records = Rfc4180.Parse(File.ReadAllText(path));
        Assert.Equal(101, records.Count);
        Assert.Equal(Csv.EventHeader, records[0]);
        for (int i = 0; i < 100; i++)
        {
            Assert.Equal($@"E:\file{i}.txt", records[i + 1][2]);
            Assert.Equal(i.ToString(CultureInfo.InvariantCulture), records[i + 1][4]);
        }
    }

    [Fact]
    public void Write_AfterDispose_IsCountedAsDropped()
    {
        using var temp = new TempDirectory();
        var log = new CsvActivityLog(temp.PathOf("log.csv"));
        log.Dispose();

        log.Write(new FileActivityEvent { Path = @"E:\late.txt", Drive = 'E' });

        Assert.Equal(1L, log.Dropped);
        Assert.Equal(0L, log.Written);
    }

    [Fact]
    public void Dispose_Twice_IsHarmless()
    {
        using var temp = new TempDirectory();
        var log = new CsvActivityLog(temp.PathOf("log.csv"));

        log.Dispose();

        Assert.Null(Record.Exception(log.Dispose));
    }

    /// <summary>Reads the file while the log may still hold it open for writing.</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
