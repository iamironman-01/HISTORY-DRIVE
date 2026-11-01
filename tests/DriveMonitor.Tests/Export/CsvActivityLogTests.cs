using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using DriveMonitor.Core.Export;
using DriveMonitor.Core.Models;
using DriveMonitor.Tests.TestSupport;

namespace DriveMonitor.Tests.Export;

/// <summary>The background CSV log of published events.</summary>
public sealed class CsvActivityLogTests
{
    private static readonly string HeaderLine = string.Join(",", Csv.EventHeader) + "\r\n";
    private static readonly byte[] Bom = [0xEF, 0xBB, 0xBF];
    private static readonly int HeaderBytes = Bom.Length + Encoding.UTF8.GetByteCount(HeaderLine);

    /// <summary>Bytes of one encoded row; every <see cref="Event"/> row has the same length.</summary>
    private static readonly int RowBytes = Encoding.UTF8.GetByteCount(RowText(Event(0)));

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

    [Fact]
    public void Dispose_WhenTheDriveStopsAcceptingData_ReturnsAndCountsTheRestAsDropped()
    {
        using var destination = new PipeDestination(); // never read: every write beyond the pipe buffer stalls
        var log = new CsvActivityLog(destination.FilePath) { StallTimeout = TimeSpan.FromMilliseconds(300) };
        for (int i = 0; i < 5_000; i++)
            log.Write(Event(i));

        var clock = Stopwatch.StartNew();
        Exception? error = Record.Exception(log.Dispose);
        clock.Stop();

        Assert.Null(error);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took {clock.Elapsed}");
        Assert.True(
            SpinWait.SpinUntil(() => log.Written + log.Dropped == 5_000, TimeSpan.FromSeconds(10)),
            $"written {log.Written} + dropped {log.Dropped} != 5000");
        Assert.True(log.Dropped > 0);
        Assert.IsType<TimeoutException>(log.Error);
    }

    [Fact]
    public void Dispose_WaitsAsLongAsTheDriveKeepsAcceptingData()
    {
        using var destination = new PipeDestination();
        var log = new CsvActivityLog(destination.FilePath) { StallTimeout = TimeSpan.FromSeconds(1) };
        const int count = 4_000;
        for (int i = 0; i < count; i++)
            log.Write(Event(i));

        // About 250 KB/s: draining takes longer than StallTimeout, but every chunk lands well within it.
        destination.StartReading(pause: TimeSpan.FromMilliseconds(50));
        log.Dispose();

        Assert.Equal(count, log.Written);
        Assert.Equal(0L, log.Dropped);
        Assert.Null(log.Error);
        List<string[]> records = Rfc4180.Parse(Encoding.UTF8.GetString(destination.WaitForEnd()).TrimStart('\uFEFF'));
        Assert.Equal(count + 1, records.Count);
        Assert.Equal(Event(count - 1).Path, records[^1][2]);
    }

    [Fact]
    public void Dispose_WhenTheWriterIgnoresCancellation_StillStopsTheLogWithAnError()
    {
        using var temp = new TempDirectory();
        using var asking = new ManualResetEventSlim();
        using var answer = new ManualResetEventSlim();
        using var log = new CsvActivityLog(temp.PathOf("log.csv"), minFreeBytes: 1)
        {
            StallTimeout = TimeSpan.FromMilliseconds(100),

            // A hung drive: asking for its free space blocks, and cancelling the log does not end that.
            FreeSpaceProbe = _ =>
            {
                asking.Set();
                answer.Wait();
                return null;
            },
        };
        try
        {
            log.Write(Event(0));
            Assert.True(asking.Wait(TimeSpan.FromSeconds(5)), "the log did not reach its drive");
            log.Write(Event(1));

            var clock = Stopwatch.StartNew();
            log.Dispose();
            clock.Stop();

            // Dispose gave up on the writer; whoever stopped the log must not take it for one that wrote everything.
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"Dispose took {clock.Elapsed}");
            Assert.IsType<TimeoutException>(log.Error);
            Assert.Equal(0L, log.Written);
            Assert.Equal(1L, log.Dropped); // the queued event; the one the writer holds is counted when the drive answers
        }
        finally
        {
            answer.Set();
        }

        Assert.True(SpinWait.SpinUntil(() => log.Dropped == 2, TimeSpan.FromSeconds(5)), $"dropped {log.Dropped}");
        Assert.Equal(0L, log.Written);
        Assert.IsType<TimeoutException>(log.Error);
    }

    [Fact]
    public void AfterAWriteError_EveryUnwrittenAndLaterEventIsCountedAsDropped()
    {
        using var destination = new PipeDestination();
        destination.StartReading();
        var log = new CsvActivityLog(destination.FilePath);
        log.Write(Event(0));
        Assert.True(SpinWait.SpinUntil(() => log.Written == 1, TimeSpan.FromSeconds(5)), "the first event was not written");

        destination.Disconnect(); // the drive is removed: the next write fails
        log.Write(Event(1));
        Assert.True(SpinWait.SpinUntil(() => log.Error is not null, TimeSpan.FromSeconds(5)), "the write error was not reported");
        for (int i = 2; i < 1_002; i++)
            log.Write(Event(i));

        // Counted at once, while the log is still open: nothing waits in memory for a pump that has stopped.
        Assert.IsAssignableFrom<IOException>(log.Error);
        Assert.Equal(1L, log.Written);
        Assert.Equal(1_001L, log.Dropped);

        log.Dispose();

        Assert.Equal(1L, log.Written);
        Assert.Equal(1_001L, log.Dropped);
    }

    [Fact]
    public void Write_PastTheSizeLimit_ContinuesInANewPartWithItsOwnHeader()
    {
        using var temp = new TempDirectory();
        long limit = HeaderBytes + (3 * RowBytes);
        var log = new CsvActivityLog(temp.PathOf("log.csv"), maxFileBytes: limit);

        for (int i = 0; i < 10; i++)
            log.Write(Event(i));
        log.Dispose();

        Assert.Equal(10L, log.Written);
        Assert.Equal(temp.PathOf("log.004.csv"), log.CurrentFilePath);
        string[] parts = [temp.PathOf("log.csv"), temp.PathOf("log.002.csv"), temp.PathOf("log.003.csv"), temp.PathOf("log.004.csv")];
        Assert.Equal(parts.Order(), Directory.GetFiles(temp.FullPath).Order());
        Assert.All(parts, part => Assert.InRange(new FileInfo(part).Length, HeaderBytes + RowBytes, limit));
        Assert.Equal(Enumerable.Range(0, 10).Select(i => Event(i).Path), parts.SelectMany(PathsIn));
    }

    [Fact]
    public void Rotation_KeepsOnlyTheNewestParts()
    {
        using var temp = new TempDirectory();
        var log = new CsvActivityLog(temp.PathOf("log.csv"), maxFileBytes: HeaderBytes + (3 * RowBytes), maxFiles: 2);

        for (int i = 0; i < 10; i++)
            log.Write(Event(i));
        log.Dispose();

        Assert.Equal(10L, log.Written); // written, even though the first parts were deleted later
        string[] kept = [temp.PathOf("log.003.csv"), temp.PathOf("log.004.csv")];
        Assert.Equal(kept.Order(), Directory.GetFiles(temp.FullPath).Order());
        Assert.Equal(Enumerable.Range(6, 4).Select(i => Event(i).Path), kept.SelectMany(PathsIn));
    }

    [Fact]
    public void Rotation_ARowLargerThanTheLimit_GetsAPartOfItsOwn()
    {
        using var temp = new TempDirectory();
        var log = new CsvActivityLog(temp.PathOf("log.csv"), maxFileBytes: HeaderBytes + 10);

        for (int i = 0; i < 3; i++)
            log.Write(Event(i));
        log.Dispose();

        Assert.Equal(3L, log.Written);
        Assert.Equal(new[] { Event(0).Path }, PathsIn(temp.PathOf("log.csv")));
        Assert.Equal(new[] { Event(1).Path }, PathsIn(temp.PathOf("log.002.csv")));
        Assert.Equal(new[] { Event(2).Path }, PathsIn(temp.PathOf("log.003.csv")));
    }

    [Theory]
    [InlineData("log.csv", true)]
    [InlineData("LOG.CSV", true)]
    [InlineData("log.002.csv", true)]
    [InlineData("log.1234.csv", true)]
    [InlineData("log.csv.002", false)]
    [InlineData("log..csv", false)]
    [InlineData("log.x02.csv", false)]
    [InlineData("log2.csv", false)]
    [InlineData("log.002.txt", false)]
    [InlineData("other.csv", false)]
    public void IsLogFile_MatchesTheFileAndItsLaterParts(string name, bool expected)
    {
        using var temp = new TempDirectory();
        using var log = new CsvActivityLog(temp.PathOf("log.csv"));

        Assert.Equal(expected, log.IsLogFile(temp.PathOf(name)));
    }

    [Fact]
    public void Constructor_WhenTheDriveIsAlreadyShortOfSpace_ThrowsAndCreatesNoFile()
    {
        using var temp = new TempDirectory();
        string path = temp.PathOf("log.csv");

        IOException error = Assert.Throws<IOException>(() => new CsvActivityLog(path, minFreeBytes: long.MaxValue));

        Assert.StartsWith("Only ", error.Message);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void Write_WhenTheDriveRunsShortOfSpace_StopsTheLogWithAnError()
    {
        using var temp = new TempDirectory();
        string path = temp.PathOf("log.csv");
        var log = new CsvActivityLog(path, minFreeBytes: 1024);
        log.FreeSpaceProbe = _ => 1023; // the drive filled up after the log started

        log.Write(Event(0));
        Assert.True(SpinWait.SpinUntil(() => log.Error is not null, TimeSpan.FromSeconds(5)), "the log did not stop");
        log.Write(Event(1));
        log.Dispose();

        IOException error = Assert.IsType<IOException>(log.Error);
        Assert.Contains("1023 B is free", error.Message);
        Assert.Equal(0L, log.Written);
        Assert.Equal(2L, log.Dropped);
        Assert.Equal(HeaderLine, File.ReadAllText(path));
    }

    /// <summary>Reads the file while the log may still hold it open for writing.</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    /// <summary>The paths logged in one part, after checking that the part starts with its own BOM and header.</summary>
    private static List<string> PathsIn(string part)
    {
        byte[] bytes = File.ReadAllBytes(part);
        Assert.Equal(Bom, bytes[..3]);
        List<string[]> records = Rfc4180.Parse(Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3));
        Assert.Equal(Csv.EventHeader, records[0]);
        return records.Skip(1).Select(r => r[2]).ToList();
    }

    private static string RowText(FileActivityEvent ev)
    {
        using var text = new StringWriter(CultureInfo.InvariantCulture);
        Csv.WriteEvent(text, ev);
        return text.ToString();
    }

    private static FileActivityEvent Event(int i) => new()
    {
        Sequence = i,
        Timestamp = new DateTime(2026, 10, 30, 12, 0, 0),
        Operation = FileOperation.Write,
        Path = $@"E:\photos\2026\holiday\img_{i:D5}.jpg",
        Drive = 'E',
        Bytes = 4096,
        Offset = 0,
        ProcessName = "copy.exe",
        ProcessId = 4242,
        ThreadId = 7,
    };

    /// <summary>
    /// A named pipe the log writes into, standing in for a drive whose speed the test controls. Until
    /// <see cref="StartReading"/> is called nobody reads, so writes stall as on a hung network share.
    /// </summary>
    private sealed class PipeDestination : IDisposable
    {
        private readonly NamedPipeServerStream _server;
        private readonly Task _connected;
        private readonly CancellationTokenSource _stop = new();
        private readonly MemoryStream _received = new();
        private Task _reader = Task.CompletedTask;

        public PipeDestination()
        {
            string name = "DriveMonitor.Tests." + Guid.NewGuid().ToString("N");
            FilePath = $@"\\.\pipe\{name}";
            _server = new NamedPipeServerStream(
                name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, inBufferSize: 4096, outBufferSize: 4096);
            _connected = _server.WaitForConnectionAsync();
        }

        public string FilePath { get; }

        /// <summary>Reads everything the log writes, pausing after each read to set the drive's speed.</summary>
        public void StartReading(TimeSpan pause = default)
        {
            _reader = Task.Run(async () =>
            {
                var buffer = new byte[16 * 1024];
                try
                {
                    await _connected;
                    int read;
                    while ((read = await _server.ReadAsync(buffer, _stop.Token)) > 0)
                    {
                        lock (_received)
                            _received.Write(buffer, 0, read);
                        if (pause > TimeSpan.Zero)
                            await Task.Delay(pause, _stop.Token);
                    }
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException)
                {
                    // The test stopped reading.
                }
            });
        }

        /// <summary>The drive goes away: the log's next write fails.</summary>
        public void Disconnect()
        {
            _stop.Cancel();
            _reader.Wait(TimeSpan.FromSeconds(5));
            _server.Dispose();
        }

        /// <summary>Waits until the log has closed its end, then returns everything it wrote.</summary>
        public byte[] WaitForEnd()
        {
            Assert.True(_reader.Wait(TimeSpan.FromSeconds(10)), "the log did not close the pipe");
            lock (_received)
                return _received.ToArray();
        }

        public void Dispose()
        {
            _stop.Cancel();
            _reader.Wait(TimeSpan.FromSeconds(5));
            _server.Dispose();
            _stop.Dispose();
        }
    }
}
