using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace DriveMonitor.App.ViewModels;

/// <summary>
/// An <see cref="ObservableCollection{T}"/> that appends and trims many items at once.
/// </summary>
/// <remarks>
/// WPF collection views reject multi-item change notifications, so a batch is reported either
/// item by item (keeps the grid's containers, selection and scroll position) or, above
/// <see cref="ResetThreshold"/> items, as a single Reset (one rebuild instead of thousands of
/// notifications during a burst). A large <see cref="SelectedCount"/> moves smaller batches to a
/// Reset as well.
/// </remarks>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    // Below this many selected rows the selection adds little to the cost of a per-item notification.
    private const int CostlySelection = 500;

    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs ResetArgs = new(NotifyCollectionChangedAction.Reset);

    /// <summary>Batches with more changed items than this are reported as one Reset.</summary>
    public int ResetThreshold { get; init; } = 200;

    /// <summary>
    /// Rows selected in the grid that shows this collection. The grid goes through its whole selection for
    /// every per-item notification, so reported item by item a batch costs changed items × selected rows,
    /// against about one pass over the list for a Reset, which keeps the selection. Once that product
    /// exceeds the list's length, the batch is reported as a Reset.
    /// </summary>
    public int SelectedCount { get; set; }

    /// <summary>Appends <paramref name="items"/>.</summary>
    public void AddRange(IReadOnlyList<T> items) => RemoveFirstAndAddRange(0, items);

    /// <summary>
    /// Removes the first <paramref name="removeCount"/> items, then appends
    /// <paramref name="items"/>[<paramref name="start"/>..], as one batch.
    /// </summary>
    public void RemoveFirstAndAddRange(int removeCount, IReadOnlyList<T> items, int start = 0)
    {
        removeCount = Math.Clamp(removeCount, 0, Count);
        int addCount = Math.Max(0, items.Count - start);
        if (removeCount == 0 && addCount == 0)
            return;

        if (!ReportAsReset(removeCount + addCount))
        {
            // Always remove index 0: the oldest items are at the start.
            for (int i = 0; i < removeCount; i++)
                RemoveAt(0);
            for (int i = start; i < items.Count; i++)
                Add(items[i]);
            return;
        }

        CheckReentrancy();
        var list = (List<T>)Items;
        list.RemoveRange(0, removeCount);
        list.EnsureCapacity(list.Count + addCount);
        for (int i = start; i < items.Count; i++)
            list.Add(items[i]);
        RaiseReset();
    }

    /// <summary>Replaces the whole content with one Reset notification.</summary>
    public void ReplaceAll(IEnumerable<T> items)
    {
        CheckReentrancy();
        var list = (List<T>)Items;
        list.Clear();
        list.AddRange(items);
        RaiseReset();
    }

    private bool ReportAsReset(int changes) =>
        changes > ResetThreshold || (SelectedCount >= CostlySelection && (long)changes * SelectedCount > Count);

    private void RaiseReset()
    {
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
        OnCollectionChanged(ResetArgs);
    }
}
