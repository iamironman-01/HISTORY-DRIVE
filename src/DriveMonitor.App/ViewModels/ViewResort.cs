using System.Diagnostics;
using System.Windows.Data;

namespace DriveMonitor.App.ViewModels;

/// <summary>
/// Re-sorts a sorted collection view after its rows changed in place. A ListCollectionView places a row
/// by its sort key only when the row is added or the view is refreshed, so without this a file sorted by
/// size that keeps growing stays where it was when it was small, and later sorted inserts land in a list
/// that is no longer sorted.
/// </summary>
/// <remarks>
/// A refresh re-sorts every row, about 0.1 s for 200,000 rows, so it runs at most as often as
/// <see cref="Run"/> is called and never sooner than ten times its own last duration: re-sorting cannot
/// take more than a tenth of the UI thread. Live sorting would instead attach a binding to every row.
/// </remarks>
internal sealed class ViewResort(ListCollectionView view)
{
    private const int PauseFactor = 10;

    private bool _stale;
    private long _notBefore;

    /// <summary>Rows changed in place, so the order may no longer match the sort.</summary>
    public void RowsChanged() => _stale = true;

    /// <summary>The view was just refreshed for another reason, which sorted it again.</summary>
    public void Refreshed() => _stale = false;

    /// <summary>Re-sorts the view when it is sorted and rows changed since it was last sorted.</summary>
    public void Run()
    {
        if (!_stale)
            return;
        if (view.CustomSort is null && view.SortDescriptions.Count == 0)
        {
            _stale = false; // sorting later refreshes the view anyway
            return;
        }

        long start = Stopwatch.GetTimestamp();
        if (start < _notBefore)
            return;

        _stale = false;
        view.Refresh();
        long end = Stopwatch.GetTimestamp();
        _notBefore = end + ((end - start) * (PauseFactor - 1));
    }
}
