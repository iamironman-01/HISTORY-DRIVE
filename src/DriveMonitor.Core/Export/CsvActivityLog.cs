using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using DriveMonitor.Core.Models;

namespace DriveMonitor.Core.Export;

/// <summary>
/// Appends every published event to a CSV file on a background task, so a slow disk never
/// stalls the trace consumer. If the writer falls more than <c>capacity</c> events behind,
/// new events are dropped and counted.
/// </summary>
/// <remarks>
/// Once the constructor returns, the background task is the only code that writes or closes the
/// file, so <see cref="Dispose"/> can never race it on a slow drive.
/// </remarks>
public sealed class CsvActivityLog : IDisposable
{
    /// <summary>Encoded rows collected before one write; also how often <see cref="Written"/> moves.</summary>
    private const int ChunkBytes = 64 * 1024;

    /// <summary>The byte-order mark and the column header that start the file.</summary>
    private static readonly byte[] Header = EncodeHeader();

    /// <summary>The longest <see cref="Dispose"/> waits for the rest of the log, even while the drive keeps accepting it.</summary>
    private static readonly TimeSpan DisposeTimeLimit = TimeSpan.FromSeconds(30);

    private readonly Channel<FileActivityEvent> _channel;
    private readonly CancellationTokenSource _abandon = new();
    private readonly StringWriter _row = new(CultureInfo.InvariantCulture);
    private readonly Encoder _encoder = Csv.FileEncoding.GetEncoder();
    private readonly FileStream _file;
    private readonly Task _pump;
    private byte[] _buffer = new byte[2 * ChunkBytes];
    private int _buffered;
    private int _bufferedRows;
    private long _written;
    private long _dropped;
    private int _disposed;
    private volatile Exception? _error;

    public CsvActivityLog(string filePath, int capacity = 100_000)
    {
        FilePath = Path.GetFullPath(filePath);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);

        // Unbuffered, because the pump writes whole chunks itself; asynchronous, so a write stuck on a
        // stalled drive can be cancelled instead of keeping the file and the pump busy indefinitely.
        _file = new FileStream(FilePath, FileMode.Create, FileAccess.Write, FileShare.Read, bufferSize: 0, FileOptions.Asynchronous);
        try
        {
            _file.Write(Header);
        }
        catch
        {
            _file.Dispose();
            throw;
        }

        _channel = Channel.CreateBounded<FileActivityEvent>(new BoundedChannelOptions(capacity)
        {
            // Dispose also reads: it counts what a stalled pump will never write.
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _pump = Task.Run(PumpAsync);
    }

    public string FilePath { get; }

    /// <summary>Events that reached the file. Final once the log is disposed and its last write has returned.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Events that will never reach the file: the log was full or closed, or gave up on a stalled drive.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>The write error that stopped the log (e.g. its drive was removed), if any.</summary>
    public Exception? Error => _error ?? _pump.Exception?.GetBaseException();

    /// <summary>How long <see cref="Dispose"/> waits without the log making progress before it gives up on the rest.</summary>
    internal TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public void Write(FileActivityEvent ev)
    {
        if (!_channel.Writer.TryWrite(ev))
            Interlocked.Increment(ref _dropped);
    }

    /// <summary>
    /// Writes what is still queued and closes the file. Waits as long as the drive keeps accepting data
    /// (at most 30 s); once it accepts nothing for <see cref="StallTimeout"/>, the rest is counted as
    /// dropped and this returns without waiting for the stuck write. Never throws.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _channel.Writer.TryComplete();
        var elapsed = Stopwatch.StartNew();
        long written = Written;
        while (!WaitForPump(StallTimeout))
        {
            long now = Written;
            if (now == written || elapsed.Elapsed >= DisposeTimeLimit)
            {
                Abandon();
                break;
            }

            written = now;
        }

        // A pump still stuck in a write keeps using the token.
        if (_pump.IsCompleted)
            _abandon.Dispose();
    }

    private static byte[] EncodeHeader()
    {
        var header = new StringWriter(CultureInfo.InvariantCulture);
        Csv.WriteHeader(header, Csv.EventHeader);
        return [.. Csv.FileEncoding.GetPreamble(), .. Csv.FileEncoding.GetBytes(header.ToString())];
    }

    private async Task PumpAsync()
    {
        ChannelReader<FileActivityEvent> reader = _channel.Reader;
        CancellationToken abandon = _abandon.Token;
        try
        {
            while (await reader.WaitToReadAsync(abandon).ConfigureAwait(false))
            {
                while (reader.TryRead(out FileActivityEvent? ev))
                {
                    Append(ev);
                    if (_buffered >= ChunkBytes)
                        await WriteBufferedAsync(abandon).ConfigureAwait(false);
                }

                await WriteBufferedAsync(abandon).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (abandon.IsCancellationRequested)
        {
            // Dispose gave up on a stalled drive: whatever was not written yet never will be.
            _error = new TimeoutException("The drive stopped accepting the activity log; events not yet written were dropped.");
            Interlocked.Add(ref _dropped, _bufferedRows);
            DropQueued();
        }
        finally
        {
            CloseFile();
        }
    }

    /// <summary>Formats one row and appends it, UTF-8 encoded, to the write buffer.</summary>
    private void Append(FileActivityEvent ev)
    {
        StringBuilder row = _row.GetStringBuilder();
        row.Clear();
        Csv.WriteEvent(_row, ev);

        int needed = _buffered + Csv.FileEncoding.GetMaxByteCount(row.Length);
        if (needed > _buffer.Length)
            Array.Resize(ref _buffer, Math.Max(needed, 2 * _buffer.Length));

        // A long row spans several builder chunks; the encoder carries a surrogate pair split between two.
        foreach (ReadOnlyMemory<char> chunk in row.GetChunks())
            _buffered += _encoder.GetBytes(chunk.Span, _buffer.AsSpan(_buffered), flush: false);
        _buffered += _encoder.GetBytes(ReadOnlySpan<char>.Empty, _buffer.AsSpan(_buffered), flush: true);
        _bufferedRows++;
    }

    private async ValueTask WriteBufferedAsync(CancellationToken abandon)
    {
        if (_bufferedRows == 0)
            return;

        await _file.WriteAsync(_buffer.AsMemory(0, _buffered), abandon).ConfigureAwait(false);
        Interlocked.Add(ref _written, _bufferedRows);
        _buffered = 0;
        _bufferedRows = 0;
    }

    /// <summary>Ends a pump that is stuck on a stalled drive, and counts what it will never write.</summary>
    private void Abandon()
    {
        _abandon.Cancel();

        // Cancelling normally ends the stuck write at once. A driver that ignores it keeps the pump
        // waiting; the pump then counts its own unwritten rows when the write finally returns.
        if (!WaitForPump(TimeSpan.FromSeconds(1)))
            DropQueued();
    }

    private bool WaitForPump(TimeSpan timeout)
    {
        try
        {
            return _pump.Wait(timeout);
        }
        catch (AggregateException)
        {
            return true; // the pump failed earlier; Error has the reason
        }
    }

    private void DropQueued()
    {
        while (_channel.Reader.TryRead(out _))
            Interlocked.Increment(ref _dropped);
    }

    private void CloseFile()
    {
        try
        {
            _file.Dispose();
        }
        catch (IOException)
        {
            // Nothing is buffered, so nothing more could be saved.
        }
    }
}
