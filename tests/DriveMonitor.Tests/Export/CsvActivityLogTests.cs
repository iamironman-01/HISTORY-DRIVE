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

    /// <summary>Reads the file while the log may still hold it open for writing.</summary>
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
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
