using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Threading.Channels;
using DriveMonitor.Core.IO;
using DriveMonitor.Core.Models;

namespace DriveMonitor.Core.Export;

/// <summary>
/// Appends every published event to a CSV file on a background task, so a slow disk never
/// stalls the trace consumer. If the writer falls more than <c>capacity</c> events behind,
/// new events are dropped and counted.
/// </summary>
/// <remarks>
/// Once the constructor returns, the background task is the only code that writes or closes the
/// file, so <see cref="Dispose"/> can never race it on a slow drive. A long recording is bounded:
/// at <c>maxFileBytes</c> the log continues in a new file (a "part"), only the newest
/// <c>maxFiles</c> parts are kept, and the log stops when its drive has less than
/// <c>minFreeBytes</c> free.
/// <para>
/// The log is written as administrator, often into a folder other users can change (a shared
/// folder, the root of a pendrive) and under names they can predict. A link planted there must not
/// turn its truncations and deletes against another file, so the folder is opened once with
/// <see cref="LinkSafeFolder"/>, which refuses a junction or link, and held open so that it cannot be
/// replaced by one while the log runs. Every part is created and deleted relative to it, and a link,
/// folder or hard-linked file found at a part's name is refused instead of followed.
/// </para>
/// </remarks>
public sealed class CsvActivityLog : IDisposable
{
    /// <summary>Encoded rows collected before one write; also how often <see cref="Written"/> moves.</summary>
    private const int ChunkBytes = 64 * 1024;

    private const int FreeSpaceCheckMs = 1000;
    private const int DiskFullHResult = unchecked((int)0x80070070);
    private const string StalledMessage = "The drive stopped accepting the activity log; events not yet written were dropped.";

    /// <summary>The byte-order mark and the column header that start every part.</summary>
    private static readonly byte[] Header = EncodeHeader();

    /// <summary>The longest <see cref="Dispose"/> waits for the rest of the log, even while the drive keeps accepting it.</summary>
    private static readonly TimeSpan DisposeTimeLimit = TimeSpan.FromSeconds(30);

    private readonly Channel<FileActivityEvent> _channel;
    private readonly CancellationTokenSource _abandon = new();
    private readonly StringWriter _row = new(CultureInfo.InvariantCulture);
    private readonly Encoder _encoder = Csv.FileEncoding.GetEncoder();
    private readonly long _maxFileBytes;
    private readonly int _maxFiles;
    private readonly long _minFreeBytes;
    private readonly string _stem;
    private readonly string _extension;

    /// <summary>The parts' folder, held open from the constructor until the background task ends.</summary>
    private readonly LinkSafeFolder _folder;

    /// <summary>This log's parts that are still on disk, oldest first.</summary>
    private readonly List<string> _parts = [];
    private readonly Task _pump;
    private FileStream _file;
    private byte[] _buffer = new byte[2 * ChunkBytes];
    private int _buffered;
    private int _bufferedRows;
    private long _fileBytes;
    private int _partNumber = 1;
    private long _nextFreeSpaceCheck;
    private volatile string _currentFilePath;
    private long _written;
    private long _dropped;
    private int _disposed;
    private volatile Exception? _error;

    /// <param name="filePath">The first part; later parts insert .002, .003 and so on before the extension.</param>
    /// <param name="maxFileBytes">Size at which the log continues in a new part; 0 for no limit.</param>
    /// <param name="maxFiles">How many parts are kept; starting a new one deletes the oldest. 0 keeps all.</param>
    /// <param name="minFreeBytes">The log stops with an error when less than this is free on its drive; 0 for no check.</param>
    /// <param name="capacity">Events that may wait for the writer before new ones are dropped.</param>
    /// <exception cref="IOException">
    /// The file cannot be created; it, or its folder, is a junction, symbolic link or other reparse point, or the
    /// file has other names (hard links); or its drive already has less than <paramref name="minFreeBytes"/> free.
    /// </exception>
    public CsvActivityLog(string filePath, long maxFileBytes = 0, int maxFiles = 0, long minFreeBytes = 0, int capacity = 100_000)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maxFileBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(maxFiles);
        ArgumentOutOfRangeException.ThrowIfNegative(minFreeBytes);

        // Here, not when the channel is made: by then the file is emptied and the folder and file are open.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        FilePath = Path.GetFullPath(filePath);
        string? folderPath = Path.GetDirectoryName(FilePath);
        if (folderPath is null || Path.GetFileName(FilePath).Length == 0)
            throw new IOException($"'{FilePath}' is a folder, not a file.");
        _extension = Path.GetExtension(FilePath);
        _stem = FilePath[..^_extension.Length];
        _currentFilePath = FilePath;
        _maxFileBytes = maxFileBytes;
        _maxFiles = maxFiles;
        _minFreeBytes = minFreeBytes;

        // Refuse at once rather than start a log that stops at its first write.
        CheckFreeSpace();
        Directory.CreateDirectory(folderPath);
        _folder = LinkSafeFolder.Open(folderPath);
        try
        {
            _file = CreateFile(FilePath);
            try
            {
                _file.Write(Header);
            }
            catch
            {
                _file.Dispose();
                throw;
            }
        }
        catch
        {
            _folder.Dispose();
            throw;
        }

        _fileBytes = Header.Length;
        _parts.Add(FilePath);
        _channel = Channel.CreateBounded<FileActivityEvent>(new BoundedChannelOptions(capacity)
        {
            // Dispose also reads: it counts what a stalled pump will never write.
            SingleReader = false,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _pump = Task.Run(PumpAsync);
    }

    /// <summary>The first part of the log, as given to the constructor but made absolute.</summary>
    public string FilePath { get; }

    /// <summary>The part being written: <see cref="FilePath"/>, or a later part once the log has rolled over.</summary>
    public string CurrentFilePath => _currentFilePath;

    /// <summary>Events that reached the file. Final once the log is disposed and its last write has returned.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Events that will never reach the file: the log was full or closed, or gave up on a stalled drive.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>
    /// The write error that stopped the log (e.g. its drive was removed), if any; it does not change once set.
    /// By then every earlier event is counted as written or dropped, and later events are counted as dropped,
    /// except when <see cref="Dispose"/> gave up on a drive that ignores cancellation: the rows of the call that
    /// drive holds are counted when the call returns.
    /// </summary>
    public Exception? Error => _error;

    /// <summary>How long <see cref="Dispose"/> waits without the log making progress before it gives up on the rest.</summary>
    internal TimeSpan StallTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Bytes free for the log on the drive of a path, or null when unknown; tests replace it.</summary>
    internal Func<string, long?> FreeSpaceProbe { get; set; } = AvailableFreeSpace;

    public void Write(FileActivityEvent ev)
    {
        if (!_channel.Writer.TryWrite(ev))
            Interlocked.Increment(ref _dropped);
    }

    /// <summary>Whether <paramref name="path"/> is one of this log's files: <see cref="FilePath"/> or a later part.</summary>
    public bool IsLogFile(string path)
    {
        if (string.Equals(path, FilePath, StringComparison.OrdinalIgnoreCase))
            return true;

        // A later part: the stem, a dot, a number, the extension.
        int digits = path.Length - _stem.Length - 1 - _extension.Length;
        return digits > 0
            && path.StartsWith(_stem, StringComparison.OrdinalIgnoreCase)
            && path[_stem.Length] == '.'
            && path.EndsWith(_extension, StringComparison.OrdinalIgnoreCase)
            && !path.AsSpan(_stem.Length + 1, digits).ContainsAnyExceptInRange('0', '9');
    }

    /// <summary>
    /// Writes what is still queued and closes the file. Waits as long as the drive keeps accepting data
    /// (at most 30 s); once it accepts nothing for <see cref="StallTimeout"/>, the rest is counted as
    /// dropped, <see cref="Error"/> says so, and this returns without waiting for the stuck write. Never throws.
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
        using var header = new StringWriter(CultureInfo.InvariantCulture);
        Csv.WriteHeader(header, Csv.EventHeader);
        return [.. Csv.FileEncoding.GetPreamble(), .. Csv.FileEncoding.GetBytes(header.ToString())];
    }

    /// <summary>
    /// Creates a part in the log's folder, or empties the plain file already of that name; a link, a folder or
    /// a file with other names is refused. Unbuffered, because the pump writes whole chunks itself;
    /// asynchronous, so a write stuck on a stalled drive can be cancelled instead of keeping the file and the
    /// pump busy indefinitely.
    /// </summary>
    private FileStream CreateFile(string path)
    {
        // Emptied only once LinkSafeFolder has checked what the name leads to; a pipe has nothing to empty.
        FileStream file = _folder.OpenFile(
            Path.GetFileName(path), FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, options: FileOptions.Asynchronous);
        try
        {
            if (file.CanSeek)
                file.SetLength(0);
            return file;
        }
        catch
        {
            file.Dispose();
            throw;
        }
    }

    private static long? AvailableFreeSpace(string path)
    {
        try
        {
            // Uses the drive letter of the path. Network (UNC) paths are rejected and so go unchecked;
            // a full share still stops the log with its write error.
            return new DriveInfo(path).AvailableFreeSpace;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private async Task PumpAsync()
    {
        ChannelReader<FileActivityEvent> reader = _channel.Reader;
        CancellationToken abandon = _abandon.Token;
        Exception? error = null;
        try
        {
            while (await reader.WaitToReadAsync(abandon).ConfigureAwait(false))
            {
                while (reader.TryRead(out FileActivityEvent? ev))
                {
                    int rowStart = _buffered;
                    Append(ev);
                    if (StartsNewPart(rowStart))
                    {
                        // The rows before this one fill the current part; this one starts the next.
                        await WriteBufferedAsync(rowStart, _bufferedRows - 1, abandon).ConfigureAwait(false);
                        await StartNextPartAsync(abandon).ConfigureAwait(false);
                    }

                    if (_buffered >= ChunkBytes)
                        await WriteBufferedAsync(_buffered, _bufferedRows, abandon).ConfigureAwait(false);
                }

                await WriteBufferedAsync(_buffered, _bufferedRows, abandon).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (abandon.IsCancellationRequested)
        {
            // Dispose gave up on a stalled drive.
            error = new TimeoutException(StalledMessage);
        }
        catch (Exception ex)
        {
            // Disk full, drive removed, network share gone: the log ends here.
            error = ex;
        }
        finally
        {
            // Nothing more will be written. Refuse new events, so Write counts them as dropped, and count
            // the rows that were buffered or queued.
            _channel.Writer.TryComplete();
            Interlocked.Add(ref _dropped, _bufferedRows);
            DropQueued();
            CloseFile();
            _folder.Dispose();
            _row.Dispose();

            // Last, so a caller that sees the error also sees the final counts; kept if Dispose, giving up
            // on this task, has set it already.
            Interlocked.CompareExchange(ref _error, error, null);
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

    /// <summary>
    /// Whether the row just appended at <paramref name="rowStart"/> would take the current part past
    /// the size limit. A part always gets at least one row, so even a row larger than the limit is written.
    /// </summary>
    private bool StartsNewPart(int rowStart) =>
        _maxFileBytes > 0 && _fileBytes + _buffered > _maxFileBytes && _fileBytes + rowStart > Header.Length;

    /// <summary>Writes the first <paramref name="bytes"/> of the buffer, which hold <paramref name="rows"/> rows, and keeps the rest.</summary>
    private async ValueTask WriteBufferedAsync(int bytes, int rows, CancellationToken abandon)
    {
        if (rows == 0)
            return;

        CheckFreeSpaceWhenDue();
        await _file.WriteAsync(_buffer.AsMemory(0, bytes), abandon).ConfigureAwait(false);
        _fileBytes += bytes;
        Interlocked.Add(ref _written, rows);
        _buffer.AsSpan(bytes, _buffered - bytes).CopyTo(_buffer);
        _buffered -= bytes;
        _bufferedRows -= rows;
    }

    private async ValueTask StartNextPartAsync(CancellationToken abandon)
    {
        CloseFile();
        DeleteOldParts();
        CheckFreeSpace();

        string path = $"{_stem}.{(++_partNumber).ToString("D3", CultureInfo.InvariantCulture)}{_extension}";
        _file = CreateFile(path);
        _parts.Add(path);
        _currentFilePath = path;
        await _file.WriteAsync(Header, abandon).ConfigureAwait(false);
        _fileBytes = Header.Length;
    }

    /// <summary>Deletes all but the newest parts, so that the next one keeps the number of parts within the limit.</summary>
    private void DeleteOldParts()
    {
        if (_maxFiles == 0)
            return;

        // A part that another program holds open without delete sharing (a spreadsheet, say) cannot be
        // deleted now; it stays listed and is tried again before the next part. So does a name that a
        // link, a folder or a hard-linked file has taken over, which is never deleted. Newer parts are kept.
        int excess = _parts.Count - (_maxFiles - 1);
        for (int i = excess - 1; i >= 0; i--)
        {
            try
            {
                _folder.DeleteFile(Path.GetFileName(_parts[i]));
                _parts.RemoveAt(i);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Kept for now.
            }
        }
    }

    /// <summary>Checks the free space at most once a second; a busy log writes many chunks per second.</summary>
    private void CheckFreeSpaceWhenDue()
    {
        long now = Environment.TickCount64;
        if (_minFreeBytes == 0 || now < _nextFreeSpaceCheck)
            return;

        _nextFreeSpaceCheck = now + FreeSpaceCheckMs;
        CheckFreeSpace();
    }

    /// <exception cref="IOException">Less than the minimum is free on the log's drive.</exception>
    private void CheckFreeSpace()
    {
        if (_minFreeBytes == 0 || FreeSpaceProbe(FilePath) is not { } free || free >= _minFreeBytes)
            return;

        throw new IOException(
            $"Only {Formatting.Bytes(free)} is free on {Path.GetPathRoot(FilePath)}; " +
            $"the activity log stops when less than {Formatting.Bytes(_minFreeBytes)} is left.",
            DiskFullHResult);
    }

    /// <summary>Ends a pump that is stuck on a stalled drive, and counts what it will never write.</summary>
    private void Abandon()
    {
        _abandon.Cancel();

        // Cancelling normally ends the stuck write at once. A driver that ignores it keeps the pump
        // waiting; the pump then counts its own unwritten rows when the write finally returns.
        if (!WaitForPump(TimeSpan.FromSeconds(1)))
        {
            DropQueued();

            // Not left to the pump, which may never return: whoever stopped the log reads Error now to
            // learn that it did not write everything.
            Interlocked.CompareExchange(ref _error, new TimeoutException(StalledMessage), null);
        }
    }

    private bool WaitForPump(TimeSpan timeout)
    {
        try
        {
            return _pump.Wait(timeout);
        }
        catch (AggregateException)
        {
            return true; // the pump records its own errors in Error; this only keeps Dispose from throwing
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
