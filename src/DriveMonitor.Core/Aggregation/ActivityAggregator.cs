using DriveMonitor.Core.Models;

namespace DriveMonitor.Core.Aggregation;

/// <summary>
/// Per-file, per-drive and per-process totals. Written by the trace pipeline, read by the UI
/// through snapshots: <see cref="TakeChangedFiles"/> returns only what changed since the last
/// call, so refreshing a large table stays cheap.
/// </summary>
/// <remarks>
/// Paging I/O (lazy writer, read-ahead, memory-mapped files) repeats bytes an application
/// already moved through the cache, so it goes into separate "paging" totals and is not
/// attributed to processes. Failed operations are counted but change no totals.
/// </remarks>
public sealed class ActivityAggregator
{
    private const int MaxNamesPerFile = 16;
    private const int MaxExtraNamesPerFile = 4096;

    private readonly object _sync = new();
    private readonly Dictionary<string, FileEntry> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<char, DriveEntry> _drives = new();
    private readonly Dictionary<(int Pid, string Name), ProcessEntry> _processes = new();
    private HashSet<FileEntry> _changedFiles = new(ReferenceEqualityComparer.Instance);
    private HashSet<ProcessEntry> _changedProcesses = new(ReferenceEqualityComparer.Instance);
    private long _failedEvents;
    private long _untrackedEvents;

    public ActivityAggregator(int maxTrackedFiles = 200_000)
    {
        MaxTrackedFiles = maxTrackedFiles;
    }

    public int MaxTrackedFiles { get; }

    /// <summary>Operations that completed with an error (not part of any total).</summary>
    public long FailedEvents => Interlocked.Read(ref _failedEvents);

    /// <summary>Events for new files that arrived after <see cref="MaxTrackedFiles"/> was reached.</summary>
    public long UntrackedEvents => Interlocked.Read(ref _untrackedEvents);

    public int TrackedFiles
    {
        get
        {
            lock (_sync)
                return _files.Count;
        }
    }

    public void Record(FileActivityEvent ev)
    {
        if (ev.Status == IoStatus.Failed)
        {
            Interlocked.Increment(ref _failedEvents);
            return;
        }

        lock (_sync)
        {
            DriveEntry drive = GetDrive(ev.Drive);
            drive.Events++;
            drive.LastActivity = ev.Timestamp;

            FileEntry? file = GetOrAddFile(ev);
            ProcessEntry? process = ev.IsPaging ? null : GetProcess(ev);

            switch (ev.Operation)
            {
                case FileOperation.Read when ev.IsPaging:
                    drive.PagingBytesRead += ev.Bytes;
                    if (file is not null)
                        file.PagingBytesRead += ev.Bytes;
                    break;

                case FileOperation.Write when ev.IsPaging:
                    drive.PagingBytesWritten += ev.Bytes;
                    if (file is not null)
                        file.PagingBytesWritten += ev.Bytes;
                    break;

                case FileOperation.Read:
                    drive.Reads++;
                    drive.BytesRead += ev.Bytes;
                    if (file is not null)
                    {
                        file.Reads++;
                        file.BytesRead += ev.Bytes;
                    }

                    if (process is not null)
                    {
                        process.Reads++;
                        process.BytesRead += ev.Bytes;
                    }

                    break;

                case FileOperation.Write:
                    drive.Writes++;
                    drive.BytesWritten += ev.Bytes;
                    if (file is not null)
                    {
                        file.Writes++;
                        file.BytesWritten += ev.Bytes;
                    }

                    if (process is not null)
                    {
                        process.Writes++;
                        process.BytesWritten += ev.Bytes;
                    }

                    break;

                case FileOperation.Create:
                    drive.Creates++;
                    if (process is not null)
                        process.Creates++;
                    if (file is not null)
                    {
                        file.Lifecycle = (file.Lifecycle | FileLifecycle.Created) & ~FileLifecycle.Deleted;
                        if ((ev.Flags & FileEventFlags.DeleteOnClose) != 0)
                            file.Lifecycle |= FileLifecycle.Deleted;
                    }

                    break;

                case FileOperation.Open:
                    if (file is not null)
                    {
                        file.Opens++;
                        if ((ev.Flags & FileEventFlags.DeleteOnClose) != 0)
                            file.Lifecycle |= FileLifecycle.Deleted;
                    }

                    break;

                case FileOperation.Delete:
                    drive.Deletes++;
                    if (process is not null)
                        process.Deletes++;
                    if (file is not null)
                        file.Lifecycle |= FileLifecycle.Deleted;
                    break;

                case FileOperation.Rename:
                    drive.Renames++;
                    if (process is not null)
                        process.Renames++;
                    if (file is not null)
                    {
                        file.Lifecycle |= FileLifecycle.Renamed;
                        if (ev.TargetPath is not null)
                            file.RenamedTo = ev.TargetPath;
                    }

                    break;

                case FileOperation.SetInfo when file is not null && (ev.Flags & FileEventFlags.SizeChange) != 0:
                    file.Lifecycle |= FileLifecycle.Resized;
                    break;
            }

            if (file is not null)
            {
                file.LastSeen = ev.Timestamp;
                file.LastOperation = ev.Operation;
                if (!ev.IsPaging)
                {
                    file.LastProcess = ev.ProcessName;
                    file.AddProcess(ev.ProcessName);
                }

                _changedFiles.Add(file);
            }

            if (process is not null)
            {
                process.LastSeen = ev.Timestamp;
                if (file is not null)
                    process.Files.Add(file);
                _changedProcesses.Add(process);
            }
        }
    }

    /// <summary>Snapshots of the files that changed since the previous call.</summary>
    public List<FileStatsSnapshot> TakeChangedFiles()
    {
        lock (_sync)
        {
            if (_changedFiles.Count == 0)
                return [];
            HashSet<FileEntry> changed = _changedFiles;
            _changedFiles = new HashSet<FileEntry>(ReferenceEqualityComparer.Instance);

            // Snapshots are taken under the lock Record needs on the trace consumer thread, so the list is
            // sized once rather than regrown, as in the methods below.
            var snapshots = new List<FileStatsSnapshot>(changed.Count);
            foreach (FileEntry file in changed)
                snapshots.Add(file.Snapshot());
            return snapshots;
        }
    }

    /// <summary>Snapshots of the processes that changed since the previous call.</summary>
    public List<ProcessStatsSnapshot> TakeChangedProcesses()
    {
        lock (_sync)
        {
            if (_changedProcesses.Count == 0)
                return [];
            HashSet<ProcessEntry> changed = _changedProcesses;
            _changedProcesses = new HashSet<ProcessEntry>(ReferenceEqualityComparer.Instance);
            var snapshots = new List<ProcessStatsSnapshot>(changed.Count);
            foreach (ProcessEntry process in changed)
                snapshots.Add(process.Snapshot());
            return snapshots;
        }
    }

    public List<FileStatsSnapshot> GetAllFiles()
    {
        lock (_sync)
        {
            var snapshots = new List<FileStatsSnapshot>(_files.Count);
            foreach (FileEntry file in _files.Values)
                snapshots.Add(file.Snapshot());
            return snapshots;
        }
    }

    public List<ProcessStatsSnapshot> GetAllProcesses()
    {
        lock (_sync)
        {
            var snapshots = new List<ProcessStatsSnapshot>(_processes.Count);
            foreach (ProcessEntry process in _processes.Values)
                snapshots.Add(process.Snapshot());
            return snapshots;
        }
    }

    public List<DriveTotals> GetDriveTotals()
    {
        lock (_sync)
            return _drives.Values.Select(d => d.Snapshot()).OrderBy(d => d.Drive).ToList();
    }

    public void Clear()
    {
        lock (_sync)
        {
            _files.Clear();
            _drives.Clear();
            _processes.Clear();
            _changedFiles.Clear();
            _changedProcesses.Clear();
        }

        Interlocked.Exchange(ref _failedEvents, 0);
        Interlocked.Exchange(ref _untrackedEvents, 0);
    }

    private DriveEntry GetDrive(char letter)
    {
        if (!_drives.TryGetValue(letter, out DriveEntry? drive))
        {
            drive = new DriveEntry(letter);
            _drives.Add(letter, drive);
        }

        return drive;
    }

    private FileEntry? GetOrAddFile(FileActivityEvent ev)
    {
        if (_files.TryGetValue(ev.Path, out FileEntry? file))
            return file;

        // Folder opens and listings would flood the table while someone browses; folders get a
        // row only when created, deleted or renamed.
        if (ev.Operation == FileOperation.List
            || (ev.IsDirectory && ev.Operation is not (FileOperation.Create or FileOperation.Delete or FileOperation.Rename)))
        {
            return null;
        }

        if (_files.Count >= MaxTrackedFiles)
        {
            Interlocked.Increment(ref _untrackedEvents);
            return null;
        }

        file = new FileEntry(ev.Path, ev.Drive, ev.IsDirectory, ev.Timestamp);
        _files.Add(ev.Path, file);
        return file;
    }

    private ProcessEntry GetProcess(FileActivityEvent ev)
    {
        var key = (ev.ProcessId, ev.ProcessName);
        if (!_processes.TryGetValue(key, out ProcessEntry? process))
        {
            process = new ProcessEntry(ev.ProcessId, ev.ProcessName, ev.Timestamp);
            _processes.Add(key, process);
        }

        return process;
    }

    private sealed class FileEntry(string path, char drive, bool isDirectory, DateTime firstSeen)
    {
        private readonly DateTime _firstSeen = firstSeen;
        private List<string>? _processNames;

        // Names beyond the first MaxNamesPerFile: counted and searched but never listed, so a set keeps repeats
        // from inflating the count.
        private HashSet<string>? _extraNames;

        // Every snapshot needs this text, under the lock Record waits for, yet it changes only when a new
        // name arrives; joining the names for each snapshot would cost an allocation per row per refresh.
        private string _processesText = "";

        // Every stored name, for searching once there are more than the text lists. A snapshot is read after the lock
        // is released, so it gets a copy of the lists above, made again only when a name arrived since the last one.
        private string[]? _searchNames;
        private bool _searchNamesStale;

        public string Path { get; } = path;
        public long Reads;
        public long Writes;
        public long BytesRead;
        public long BytesWritten;
        public long PagingBytesRead;
        public long PagingBytesWritten;
        public int Opens;
        public FileLifecycle Lifecycle;
        public DateTime LastSeen = firstSeen;
        public FileOperation LastOperation;
        public string LastProcess = "";
        public string? RenamedTo;

        private int ProcessCount => (_processNames?.Count ?? 0) + (_extraNames?.Count ?? 0);

        public void AddProcess(string name)
        {
            List<string> names = _processNames ??= new List<string>(2);
            foreach (string existing in names)
            {
                if (string.Equals(existing, name, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            if (names.Count < MaxNamesPerFile)
            {
                names.Add(name);
            }
            else
            {
                _extraNames ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (_extraNames.Count >= MaxExtraNamesPerFile || !_extraNames.Add(name))
                    return; // a repeat, or past the names counted: neither changes the text
            }

            int count = ProcessCount;
            _processesText = count <= 3
                ? string.Join(", ", names)
                : $"{names[0]}, {names[1]}, {names[2]} +{count - 3} more";
            _searchNamesStale = count > 3;
        }

        public FileStatsSnapshot Snapshot()
        {
            if (_searchNamesStale)
            {
                var all = new string[ProcessCount];
                _processNames!.CopyTo(all);
                _extraNames?.CopyTo(all, _processNames.Count);
                _searchNames = all;
                _searchNamesStale = false;
            }

            return new(
                Path, drive, isDirectory, Reads, Writes, BytesRead, BytesWritten, PagingBytesRead, PagingBytesWritten,
                Opens, Lifecycle, _firstSeen, LastSeen, LastOperation, LastProcess, _processesText, ProcessCount, RenamedTo,
                _searchNames);
        }
    }

    private sealed class DriveEntry(char letter)
    {
        public long Events;
        public long Reads;
        public long Writes;
        public long BytesRead;
        public long BytesWritten;
        public long PagingBytesRead;
        public long PagingBytesWritten;
        public long Creates;
        public long Deletes;
        public long Renames;
        public DateTime LastActivity;

        public DriveTotals Snapshot() => new(
            letter, Events, Reads, Writes, BytesRead, BytesWritten, PagingBytesRead, PagingBytesWritten, Creates, Deletes, Renames, LastActivity);
    }

    private sealed class ProcessEntry(int processId, string name, DateTime firstSeen)
    {
        private readonly DateTime _firstSeen = firstSeen;
        public readonly HashSet<FileEntry> Files = new(ReferenceEqualityComparer.Instance);
        public long Reads;
        public long Writes;
        public long BytesRead;
        public long BytesWritten;
        public long Creates;
        public long Deletes;
        public long Renames;
        public DateTime LastSeen = firstSeen;

        public ProcessStatsSnapshot Snapshot() => new(
            processId, name, Reads, Writes, BytesRead, BytesWritten, Creates, Deletes, Renames, Files.Count, _firstSeen, LastSeen);
    }
}
