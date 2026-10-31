using System.Windows.Data;
using DriveMonitor.Core.Models;

namespace DriveMonitor.App.ViewModels;

/// <summary>
/// The Live activity list: turns drained events into rows (grouping repeated operations), keeps at
/// most <see cref="MaxRows"/> of them, and holds new events back while the view is paused.
/// </summary>
/// <remarks>
/// UI thread only. Work per batch is O(events + evicted rows): grouping is a dictionary lookup, and
/// the list changes once per batch (per-item notifications for small batches, one Reset for large).
/// </remarks>
public sealed class ActivityFeed
{
    private const long GroupWindowTicks = TimeSpan.TicksPerSecond;
    private const long GroupCandidateMs = 2000;

    private readonly BatchObservableCollection<ActivityRowViewModel> _rows = [];
    private readonly Dictionary<GroupKey, ActivityRowViewModel> _groupable = new(GroupKeyComparer.Instance);
    private readonly Queue<FileActivityEvent> _pauseBuffer = new();
    private readonly List<ActivityRowViewModel> _added = [];
    private readonly List<ActivityRowViewModel> _dirty = [];
    private bool _groupRepeated = true;

    public ActivityFeed(Predicate<object> filter, int maxRows)
    {
        MaxRows = Math.Max(1, maxRows);
        View = new ListCollectionView(_rows) { Filter = filter };
    }

    /// <summary>Raised before rows are added or removed, so the view can remember its scroll position.</summary>
    public event EventHandler? RowsChanging;

    /// <summary>Raised after rows were added or removed (at most once per batch).</summary>
    public event EventHandler? RowsChanged;

    /// <summary>The filtered (and possibly sorted) rows the grid shows.</summary>
    public ListCollectionView View { get; }

    public int MaxRows { get; }

    /// <summary>Rows in the list, visible or not.</summary>
    public int TotalRows => _rows.Count;

    public bool IsPaused { get; private set; }

    /// <summary>Events waiting to be shown when the view resumes.</summary>
    public int PausedEvents => _pauseBuffer.Count;

    /// <summary>Events that arrived while paused but did not fit in the pause buffer.</summary>
    public long PausedOverflow { get; private set; }

    public bool GroupRepeated
    {
        get => _groupRepeated;
        set
        {
            _groupRepeated = value;
            if (!value)
                _groupable.Clear();
        }
    }

    /// <summary>Adds drained events to the list, or to the pause buffer while paused. Call once per refresh.</summary>
    public void Add(IReadOnlyList<FileActivityEvent> events)
    {
        PruneGroupable(Environment.TickCount64);
        if (events.Count == 0)
            return;

        if (!IsPaused)
        {
            Append(events);
            return;
        }

        // Keep the newest MaxRows events: on resume the list keeps only the newest rows anyway.
        foreach (FileActivityEvent ev in events)
        {
            if (_pauseBuffer.Count >= MaxRows)
            {
                _pauseBuffer.Dequeue();
                PausedOverflow++;
            }

            _pauseBuffer.Enqueue(ev);
        }
    }

    public void Pause() => IsPaused = true;

    /// <summary>Shows the events that arrived while paused.</summary>
    /// <returns>How many events arrived while paused but could not be kept.</returns>
    public long Resume()
    {
        IsPaused = false;
        long overflow = PausedOverflow;
        PausedOverflow = 0;
        if (_pauseBuffer.Count > 0)
        {
            FileActivityEvent[] buffered = _pauseBuffer.ToArray();
            _pauseBuffer.Clear();
            Append(buffered);
        }

        return overflow;
    }

    public void Clear()
    {
        foreach (ActivityRowViewModel row in _rows)
            row.IsEvicted = true;
        _groupable.Clear();
        _pauseBuffer.Clear();
        PausedOverflow = 0;
        RowsChanging?.Invoke(this, EventArgs.Empty);
        _rows.Clear();
        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Re-applies the filter after a filter setting changed.</summary>
    public void Refresh() => View.Refresh();

    private void Append(IReadOnlyList<FileActivityEvent> events)
    {
        long now = Environment.TickCount64;
        foreach (FileActivityEvent ev in events)
        {
            if (!_groupRepeated || !IsGroupable(ev.Operation))
            {
                AddRow(new ActivityRowViewModel(ev));
                continue;
            }

            var key = new GroupKey(ev.Operation, ev.Path, ev.ProcessId, ev.IsPaging, ev.Status);
            if (_groupable.TryGetValue(key, out ActivityRowViewModel? row)
                && !row.IsEvicted
                && Math.Abs(ev.Timestamp.Ticks - row.LastTime.Ticks) <= GroupWindowTicks)
            {
                row.Merge(ev);
                row.LastTouched = now;
                if (!row.IsPendingAdd && !row.IsDirty)
                {
                    row.IsDirty = true;
                    _dirty.Add(row);
                }

                continue;
            }

            row = new ActivityRowViewModel(ev) { LastTouched = now };
            _groupable[key] = row;
            AddRow(row);
        }

        if (_added.Count > 0)
            Publish();

        foreach (ActivityRowViewModel row in _dirty)
        {
            row.IsDirty = false;
            if (!row.IsEvicted)
                row.NotifyChanged();
        }

        _dirty.Clear();
    }

    private void AddRow(ActivityRowViewModel row)
    {
        row.IsPendingAdd = true;
        _added.Add(row);
    }

    /// <summary>Appends the new rows and evicts the oldest beyond <see cref="MaxRows"/>, as one list change.</summary>
    private void Publish()
    {
        int overflow = _rows.Count + _added.Count - MaxRows;
        int evictFromList = Math.Clamp(overflow, 0, _rows.Count);
        int skipAdded = Math.Max(0, overflow - _rows.Count); // a burst larger than MaxRows on its own

        for (int i = 0; i < evictFromList; i++)
            _rows[i].IsEvicted = true;
        for (int i = 0; i < _added.Count; i++)
        {
            _added[i].IsPendingAdd = false;
            if (i < skipAdded)
                _added[i].IsEvicted = true;
        }

        RowsChanging?.Invoke(this, EventArgs.Empty);
        _rows.RemoveFirstAndAddRange(evictFromList, _added, skipAdded);
        _added.Clear();
        RowsChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Forgets rows nobody grouped into for a while, and rows that left the list.</summary>
    private void PruneGroupable(long now)
    {
        if (_groupable.Count == 0)
            return;

        // Removing during enumeration is supported by Dictionary since .NET Core 3.0.
        foreach ((GroupKey key, ActivityRowViewModel row) in _groupable)
        {
            if (row.IsEvicted || now - row.LastTouched > GroupCandidateMs)
                _groupable.Remove(key);
        }
    }

    /// <summary>Creates, deletes and renames always get their own row; they are what people look for.</summary>
    private static bool IsGroupable(FileOperation operation) =>
        operation is FileOperation.Read or FileOperation.Write or FileOperation.Open
            or FileOperation.List or FileOperation.SetInfo or FileOperation.Flush;

    private readonly record struct GroupKey(FileOperation Operation, string Path, int ProcessId, bool IsPaging, IoStatus Status);

    /// <summary>Paths compare case-insensitively, as Windows does.</summary>
    private sealed class GroupKeyComparer : IEqualityComparer<GroupKey>
    {
        public static readonly GroupKeyComparer Instance = new();

        public bool Equals(GroupKey x, GroupKey y) =>
            x.Operation == y.Operation
            && x.ProcessId == y.ProcessId
            && x.IsPaging == y.IsPaging
            && x.Status == y.Status
            && string.Equals(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(GroupKey key) =>
            HashCode.Combine(key.Operation, key.ProcessId, key.IsPaging, key.Status, StringComparer.OrdinalIgnoreCase.GetHashCode(key.Path));
    }
}
