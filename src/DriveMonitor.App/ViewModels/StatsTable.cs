using System.Windows.Data;

namespace DriveMonitor.App.ViewModels;

/// <summary>
/// A table of statistics rows (one per file or process) updated in place from the aggregator's
/// change snapshots, so a refresh costs O(changed rows) however large the table is.
/// </summary>
/// <typeparam name="TKey">Identity of a row, e.g. the file path.</typeparam>
/// <typeparam name="TSnapshot">The aggregator's snapshot type.</typeparam>
/// <typeparam name="TRow">The row view model.</typeparam>
public sealed class StatsTable<TKey, TSnapshot, TRow>
    where TKey : notnull
    where TRow : class
{
    private readonly Dictionary<TKey, TRow> _rows;
    private readonly Func<TSnapshot, TKey> _keyOf;
    private readonly Func<TSnapshot, TRow> _create;
    private readonly Action<TRow, TSnapshot> _update;
    private readonly Predicate<object> _filter;
    private readonly List<TRow> _added = [];
    private readonly ViewResort _resort;

    public StatsTable(
        Func<TSnapshot, TKey> keyOf,
        Func<TSnapshot, TRow> create,
        Action<TRow, TSnapshot> update,
        Predicate<object> filter,
        IEqualityComparer<TKey>? comparer = null)
    {
        _rows = new Dictionary<TKey, TRow>(comparer);
        _keyOf = keyOf;
        _create = create;
        _update = update;
        _filter = filter;
        View = new ListCollectionView(Items);
        _resort = new ViewResort(View);
    }

    public BatchObservableCollection<TRow> Items { get; } = [];

    /// <summary>The filtered (and possibly sorted) rows the grid shows.</summary>
    public ListCollectionView View { get; }

    /// <summary>Rows selected in the grid; see <see cref="BatchObservableCollection{T}.SelectedCount"/>.</summary>
    public int SelectedCount
    {
        get => Items.SelectedCount;
        set => Items.SelectedCount = value;
    }

    public void Apply(IReadOnlyList<TSnapshot> changed)
    {
        // The view runs its filter only when a row is added or the view is refreshed, but a search can match
        // values that change in place: a file read by one more process, or renamed.
        Predicate<object>? filter = View.Filter;
        bool matchChanged = false;
        foreach (TSnapshot snapshot in changed)
        {
            TKey key = _keyOf(snapshot);
            if (_rows.TryGetValue(key, out TRow? row))
            {
                bool matched = filter?.Invoke(row) ?? true;
                _update(row, snapshot);
                _resort.RowsChanged();
                if (filter is not null && filter(row) != matched)
                    matchChanged = true;
            }
            else
            {
                row = _create(snapshot);
                _rows.Add(key, row);
                _added.Add(row);
            }
        }

        if (_added.Count > 0)
        {
            Items.AddRange(_added);
            _added.Clear();
        }

        // Rare, since a search matches paths and process names rather than totals, so refreshing the whole view
        // then costs little overall.
        if (matchChanged)
        {
            View.Refresh();
            _resort.Refreshed();
        }
    }

    /// <summary>Re-sorts a sorted view whose rows changed in place (see <see cref="ViewResort"/>); call about once a second.</summary>
    public void ResortChangedRows() => _resort.Run();

    public void Clear()
    {
        _rows.Clear();
        _added.Clear();
        Items.Clear();
    }

    /// <summary>
    /// Re-applies the filter after the search text changed. With no search the view runs without a
    /// filter, which keeps additions to a table of 200,000 files cheap.
    /// </summary>
    public void ApplySearch(bool hasSearch)
    {
        if (!hasSearch)
        {
            if (View.Filter is null)
                return;
            View.Filter = null;
        }
        else if (View.Filter is null)
        {
            View.Filter = _filter; // setting the filter refreshes the view
        }
        else
        {
            View.Refresh();
        }

        // Every way here refreshed the view, which also sorted it again.
        _resort.Refreshed();
    }
}
