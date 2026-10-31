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
/// <para>
/// Events arrive roughly, not strictly, in time order: the kernel delivers each processor's buffer
/// on its own, up to a flush interval apart. First and last values, and whether a file is deleted,
/// therefore follow the event timestamps rather than the order of arrival.
/// </para>
/// <para>
/// Memory stays bounded however long monitoring runs: at most <see cref="MaxTrackedFiles"/> files and
/// <see cref="MaxTrackedProcesses"/> processes get rows. Events beyond either limit still count in the
/// drive totals and in the other table, and are counted in <see cref="UntrackedEvents"/> or
/// <see cref="UntrackedProcessEvents"/>.
/// </para>
/// </remarks>
public sealed class ActivityAggregator
{
    private const int MaxNamesPerFile = 16;
    private const int MaxExtraNamesPerFile = 4096;

    private readonly object _sync = new();
    private readonly Dictionary<string, FileEntry> _files = new(StringComparer.OrdinalIgnoreCase);

    // Folders without a row, remembered when they are opened as folders or listed: Windows reports their
    // delete or rename without the folder flag (MonitorEngine adds it for the folders its FolderTracker knows,
    // by the same rules, but events can also come straight here), and lets a backup-semantics open take a
    // folder or a file.
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<char, DriveEntry> _drives = new();
    private readonly Dictionary<(int Pid, string Name), ProcessEntry> _processes = new();
    private HashSet<FileEntry> _changedFiles = new(ReferenceEqualityComparer.Instance);
    private HashSet<ProcessEntry> _changedProcesses = new(ReferenceEqualityComparer.Instance);
    private long _failedEvents;
    private long _untrackedEvents;
    private long _untrackedProcessEvents;

    public ActivityAggregator(int maxTrackedFiles = 200_000, int maxTrackedProcesses = 50_000)
    {
        MaxTrackedFiles = maxTrackedFiles;
        MaxTrackedProcesses = maxTrackedProcesses;
    }

    /// <summary>Distinct files and folders with a row; also bounds the folders remembered without one.</summary>
    public int MaxTrackedFiles { get; }

    /// <summary>
    /// Distinct processes (process ID and name) with a row. Every short-lived program that touches a
    /// monitored drive adds one, so without a limit a long session would keep growing.
    /// </summary>
    public int MaxTrackedProcesses { get; }

    /// <summary>Operations that completed with an error (not part of any total).</summary>
    public long FailedEvents => Interlocked.Read(ref _failedEvents);

    /// <summary>Events for new files that arrived after <see cref="MaxTrackedFiles"/> was reached.</summary>
    public long UntrackedEvents => Interlocked.Read(ref _untrackedEvents);

    /// <summary>
    /// Events of new processes that arrived after <see cref="MaxTrackedProcesses"/> was reached. They still
    /// count in the drive and file totals. Paging I/O is never attributed to a process, so it is not counted here.
    /// </summary>
    public long UntrackedProcessEvents => Interlocked.Read(ref _untrackedProcessEvents);

    public int TrackedFiles
    {
        get
        {
            lock (_sync)
                return _files.Count;
        }
    }

    public int TrackedProcesses
    {
        get
        {
            lock (_sync)
                return _processes.Count;
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
            if (ev.Timestamp > drive.LastActivity)
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
                        // A new file replaces a deleted one, unless it is itself deleted when closed.
                        file.Lifecycle |= FileLifecycle.Created;
                        file.SetDeleted((ev.Flags & FileEventFlags.DeleteOnClose) != 0, ev.Timestamp);
                    }

                    break;

                case FileOperation.Open:
                    if (file is not null)
                    {
                        file.Opens++;
                        if ((ev.Flags & FileEventFlags.DeleteOnClose) != 0)
                            file.SetDeleted(true, ev.Timestamp);
                    }

                    break;

                case FileOperation.Delete:
                    drive.Deletes++;
                    if (process is not null)
                        process.Deletes++;
                    if (file is not null)
                        file.SetDeleted(true, ev.Timestamp);
                    break;

                case FileOperation.Rename:
                    drive.Renames++;
                    if (process is not null)
                        process.Renames++;
                    if (file is not null)
                        file.Renamed(ev.TargetPath, ev.Timestamp);
                    break;

                case FileOperation.SetInfo when file is not null && (ev.Flags & FileEventFlags.SizeChange) != 0:
                    file.Lifecycle |= FileLifecycle.Resized;
                    break;

                // The file is kept after all. This follows the usual single handle: another handle opened with
                // delete-on-close would still delete it.
                case FileOperation.SetInfo when file is not null && (ev.Flags & FileEventFlags.DeleteCancelled) != 0:
                    file.SetDeleted(false, ev.Timestamp);
                    break;
            }

            if (file is not null)
            {
                file.Seen(ev);
                if (!ev.IsPaging)
                    file.AddProcess(ev.ProcessName);
                _changedFiles.Add(file);
            }

            if (process is not null)
            {
                process.Seen(ev.Timestamp);
                if (file is not null)
                    process.AddFile(file);
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
            _folders.Clear();
            _drives.Clear();
            _processes.Clear();
            _changedFiles.Clear();
            _changedProcesses.Clear();
        }

        Interlocked.Exchange(ref _failedEvents, 0);
        Interlocked.Exchange(ref _untrackedEvents, 0);
        Interlocked.Exchange(ref _untrackedProcessEvents, 0);
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
        bool? folder = FolderEvidence(ev);
        if (_files.TryGetValue(ev.Path, out FileEntry? file))
        {
            // A path changes type when it is deleted and created again as the other type, and the evidence
            // can arrive after the delete or rename that created the row.
            if (folder is bool isFolder)
                file.IsDirectory = isFolder;
            return file;
        }

        bool directory = folder ?? (_folders.Count > 0 && _folders.Contains(ev.Path));
        if (!WantsRow(ev, directory))
        {
            if (folder == true)
                RememberFolder(ev.Path);
            return null;
        }

        if (_files.Count >= MaxTrackedFiles)
        {
            Interlocked.Increment(ref _untrackedEvents);
            return null;
        }

        // From here on the row holds the type, so the path need not be remembered.
        if (_folders.Count > 0)
            _folders.Remove(ev.Path);
        file = new FileEntry(ev.Path, ev.Drive, directory, ev.Timestamp);
        _files.Add(ev.Path, file);
        return file;
    }

    private void RememberFolder(string path)
    {
        // Bounded like the file table. Starting over keeps the folders seen last, which is what a delete or
        // rename right after the open or listing needs.
        if (_folders.Count >= MaxTrackedFiles)
        {
            if (_folders.Contains(path))
                return;
            _folders.Clear();
        }

        _folders.Add(path);
    }

    /// <summary>
    /// Folder opens and listings would flood the table while someone browses, so a folder gets a row only
    /// when it is created, deleted or renamed. An open that may have taken a folder (folder watchers open
    /// them that way) gets none either unless it deletes; once the path has a row, it counts there.
    /// </summary>
    private static bool WantsRow(FileActivityEvent ev, bool directory)
    {
        if (ev.Operation == FileOperation.List)
            return false;

        bool changesPath = ev.Operation is FileOperation.Create or FileOperation.Delete or FileOperation.Rename
            || (ev.Flags & FileEventFlags.DeleteOnClose) != 0;
        return changesPath || (!directory && (ev.Flags & FileEventFlags.MaybeDirectory) == 0);
    }

    /// <summary>
    /// True when the event shows its path to be a folder, false when it shows a file, null when it cannot tell.
    /// Shared with <see cref="FolderTracker"/>, so that the published folder flag follows the same rules.
    /// </summary>
    internal static bool? FolderEvidence(FileActivityEvent ev)
    {
        if (ev.IsDirectory || ev.Operation == FileOperation.List)
            return true;

        switch (ev.Operation)
        {
            // Only a request with FILE_DIRECTORY_FILE (the Directory flag) creates a folder, and an open without it
            // refuses folders unless it accepted either type (MaybeDirectory).
            case FileOperation.Create:
                return false;
            case FileOperation.Open:
                return (ev.Flags & FileEventFlags.MaybeDirectory) != 0 ? null : false;

            // Applications cannot read, write or resize a folder; paging I/O can reach a folder's index.
            case FileOperation.Read or FileOperation.Write:
                return ev.IsPaging ? null : false;
            case FileOperation.SetInfo when (ev.Flags & FileEventFlags.SizeChange) != 0:
                return false;
            default:
                return null;
        }
    }

    private ProcessEntry? GetProcess(FileActivityEvent ev)
    {
        var key = (ev.ProcessId, ev.ProcessName);
        if (_processes.TryGetValue(key, out ProcessEntry? process))
            return process;

        if (_processes.Count >= MaxTrackedProcesses)
        {
            Interlocked.Increment(ref _untrackedProcessEvents);
            return null;
        }

        process = new ProcessEntry(ev.ProcessId, ev.ProcessName, ev.Timestamp);
        _processes.Add(key, process);
        return process;
    }

    private sealed class FileEntry(string path, char drive, bool isDirectory, DateTime firstSeen)
    {
        private DateTime _firstSeen = firstSeen;

        // Timestamps of the events that last set LastProcess, RenamedTo and the Deleted flag, so that an
        // event delivered late cannot overwrite what a newer one set.
        private DateTime _lastProcessAt;
        private DateTime _renamedAt;
        private DateTime _deletedAt;
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
        public bool IsDirectory = isDirectory;
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

        /// <summary>Updates the first and last values; of two events with the same timestamp, the one recorded last wins.</summary>
        public void Seen(FileActivityEvent ev)
        {
            if (ev.Timestamp < _firstSeen)
                _firstSeen = ev.Timestamp;

            if (ev.Timestamp >= LastSeen)
            {
                LastSeen = ev.Timestamp;
                LastOperation = ev.Operation;
            }

            // Ordered on its own: paging I/O names no process, and a newer paging event must not stop an older
            // application event, delivered after it, from naming its process.
            if (!ev.IsPaging && ev.Timestamp >= _lastProcessAt)
            {
                _lastProcessAt = ev.Timestamp;
                LastProcess = ev.ProcessName;
            }
        }

        /// <summary>Sets or clears Deleted, unless a later delete, cancel or create has already decided it.</summary>
        public void SetDeleted(bool deleted, DateTime at)
        {
            if (at < _deletedAt)
                return;

            _deletedAt = at;
            Lifecycle = deleted ? Lifecycle | FileLifecycle.Deleted : Lifecycle & ~FileLifecycle.Deleted;
        }

        public void Renamed(string? target, DateTime at)
        {
            Lifecycle |= FileLifecycle.Renamed;
            if (target is not null && at >= _renamedAt)
            {
                _renamedAt = at;
                RenamedTo = target;
            }
        }

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
                Path, drive, IsDirectory, Reads, Writes, BytesRead, BytesWritten, PagingBytesRead, PagingBytesWritten,
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
        private DateTime _firstSeen = firstSeen;

        // Created with the first file row: folder watchers, listings and I/O on untracked files touch none.
        private HashSet<FileEntry>? _files;
        public long Reads;
        public long Writes;
        public long BytesRead;
        public long BytesWritten;
        public long Creates;
        public long Deletes;
        public long Renames;
        public DateTime LastSeen = firstSeen;

        public void Seen(DateTime at)
        {
            if (at < _firstSeen)
                _firstSeen = at;
            if (at > LastSeen)
                LastSeen = at;
        }

        public void AddFile(FileEntry file)
        {
            _files ??= new HashSet<FileEntry>(ReferenceEqualityComparer.Instance);
            _files.Add(file);
        }

        public ProcessStatsSnapshot Snapshot() => new(
            processId, name, Reads, Writes, BytesRead, BytesWritten, Creates, Deletes, Renames, _files?.Count ?? 0, _firstSeen, LastSeen);
    }
}
