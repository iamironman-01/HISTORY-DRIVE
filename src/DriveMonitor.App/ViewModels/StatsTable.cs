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
        foreach (TSnapshot snapshot in changed)
        {
            TKey key = _keyOf(snapshot);
            if (_rows.TryGetValue(key, out TRow? row))
            {
                _update(row, snapshot);
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
    }

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
            if (View.Filter is not null)
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
    }
}
