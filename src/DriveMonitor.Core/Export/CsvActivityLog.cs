using System.Threading.Channels;
using DriveMonitor.Core.Models;

namespace DriveMonitor.Core.Export;

/// <summary>
/// Appends every published event to a CSV file on a background thread, so a slow disk never
/// stalls the trace consumer. If the writer falls more than <c>capacity</c> events behind,
/// new events are dropped and counted.
/// </summary>
public sealed class CsvActivityLog : IDisposable
{
    private readonly Channel<FileActivityEvent> _channel;
    private readonly StreamWriter _writer;
    private readonly Task _pump;
    private long _written;
    private long _dropped;
    private int _disposed;

    public CsvActivityLog(string filePath, int capacity = 100_000)
    {
        FilePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        var stream = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read);
        _writer = new StreamWriter(stream, Csv.FileEncoding);
        Csv.WriteHeader(_writer, Csv.EventHeader);
        _writer.Flush();

        _channel = Channel.CreateBounded<FileActivityEvent>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _pump = Task.Run(PumpAsync);
    }

    public string FilePath { get; }

    public long Written => Interlocked.Read(ref _written);

    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>The write error that stopped the log (e.g. its drive was removed), if any.</summary>
    public Exception? Error => _pump.Exception?.GetBaseException();

    public void Write(FileActivityEvent ev)
    {
        if (!_channel.Writer.TryWrite(ev))
            Interlocked.Increment(ref _dropped);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _channel.Writer.TryComplete();
        try
        {
            _pump.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // The pump failed earlier; the exception stays available through Error.
        }

        try
        {
            _writer.Dispose();
        }
        catch (IOException)
        {
            // Final flush failed (device gone); nothing more can be saved.
        }
    }

    private async Task PumpAsync()
    {
        ChannelReader<FileActivityEvent> reader = _channel.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (reader.TryRead(out FileActivityEvent? ev))
            {
                Csv.WriteEvent(_writer, ev);
                Interlocked.Increment(ref _written);
            }

            await _writer.FlushAsync().ConfigureAwait(false);
        }
    }
}
