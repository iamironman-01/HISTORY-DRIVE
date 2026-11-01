using DriveMonitor.Core.Models;

namespace DriveMonitor.Core.Aggregation;

/// <summary>
/// Remembers which paths are folders, so that <see cref="MonitorEngine"/> can publish a folder's delete or
/// rename with the Directory flag. Windows reports both without it: RemoveDirectoryW opens the folder with
/// FILE_DIRECTORY_FILE and then sends a disposition that names no type, and MoveFileEx and IFileOperation open
/// the source without that option.
/// </summary>
/// <remarks>
/// <para>
/// A path's type follows the evidence <see cref="ActivityAggregator"/> uses
/// (<see cref="ActivityAggregator.FolderEvidence"/>), so the published flag and the Files table agree: a path is
/// a folder once something opens or creates it with FILE_DIRECTORY_FILE or lists it, and stops being one once
/// something creates it as a file, opens it with FILE_NON_DIRECTORY_FILE, or reads, writes or resizes it.
/// Failed operations teach nothing, as the aggregator does not record them. A delete or rename that succeeded
/// forgets its path, while one whose outcome is unknown (its completion was not seen) leaves it known: FAT rejects
/// the newer form of both requests and Windows retries with the older one, which must still find the folder. A
/// rename whose new path Windows reported, and that did not fail, gives the new path the type of the old one.
/// </para>
/// <para>
/// Paths below a deleted or renamed folder are left as they are. Windows deletes only an empty folder, so
/// nothing known lies below a deleted one unless its own delete was missed. Below a renamed folder the old paths
/// are gone, and whatever takes one of them later is nearly always created, opened as a file, read, written or
/// renamed there first, which shows it to be a file; the new paths are learned when they are next opened as
/// folders or listed. Moving them would cost a pass over the whole set for every folder rename.
/// </para>
/// <para>
/// Memory is bounded like the aggregator's: at most <see cref="MaxFolders"/> paths, starting over when full,
/// which keeps the folders seen last. Thread-safe. The engine calls <see cref="Apply"/> for every event it
/// publishes, under its correlator lock (or on the trace consumer thread when completion events are off), so
/// events that cannot change anything (paging I/O, metadata changes, flushes, opens that take either type)
/// return without taking the lock, and the others take it for a hash lookup or two.
/// </para>
/// </remarks>
internal sealed class FolderTracker
{
    private readonly object _sync = new();
    private readonly HashSet<string> _folders = new(StringComparer.OrdinalIgnoreCase);

    public FolderTracker(int maxFolders) => MaxFolders = maxFolders;

    public int MaxFolders { get; }

    public int Count
    {
        get
        {
            lock (_sync)
                return _folders.Count;
        }
    }

    public bool IsFolder(string path)
    {
        lock (_sync)
            return _folders.Contains(path);
    }

    /// <summary>
    /// Learns from <paramref name="ev"/> and returns it, as a copy with the Directory flag when it deletes or
    /// renames a path known to be a folder (failed or not: the target is a folder either way).
    /// </summary>
    public FileActivityEvent Apply(FileActivityEvent ev)
    {
        if (ev.Operation is FileOperation.Delete or FileOperation.Rename)
            return ApplyPathChange(ev);

        if (ev.Status == IoStatus.Failed || ActivityAggregator.FolderEvidence(ev) is not bool folder)
            return ev;

        lock (_sync)
        {
            if (folder)
                Remember(ev.Path);
            else if (_folders.Count > 0)
                _folders.Remove(ev.Path);
        }

        return ev;
    }

    public void Clear()
    {
        lock (_sync)
            _folders.Clear();
    }

    private FileActivityEvent ApplyPathChange(FileActivityEvent ev)
    {
        bool folder;
        lock (_sync)
        {
            folder = ev.IsDirectory || (_folders.Count > 0 && _folders.Contains(ev.Path));

            // A failed delete or rename leaves the path as it was, and so may one whose outcome is unknown: on FAT
            // the first form of either request fails and Windows sends the older form next, which must still find
            // the folder. Only a confirmed success moves the path away.
            if (ev.Status != IoStatus.Failed)
            {
                if (ev.Status == IoStatus.Success)
                    _folders.Remove(ev.Path);
                if (ev.Operation == FileOperation.Rename && ev.TargetPath is { } target)
                {
                    if (folder)
                        Remember(target);
                    else
                        _folders.Remove(target);
                }
            }
        }

        return folder && !ev.IsDirectory ? ev with { Flags = ev.Flags | FileEventFlags.Directory } : ev;
    }

    private void Remember(string path)
    {
        // Starting over keeps the folders seen last, which is what a delete or rename right after the open or
        // listing needs.
        if (_folders.Count >= MaxFolders)
        {
            if (_folders.Contains(path))
                return;
            _folders.Clear();
        }

        _folders.Add(path);
    }
}
