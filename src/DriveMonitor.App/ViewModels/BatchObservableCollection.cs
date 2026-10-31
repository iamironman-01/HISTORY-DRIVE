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
/// notifications during a burst).
/// </remarks>
public sealed class BatchObservableCollection<T> : ObservableCollection<T>
{
    private static readonly PropertyChangedEventArgs CountChanged = new(nameof(Count));
    private static readonly PropertyChangedEventArgs IndexerChanged = new("Item[]");
    private static readonly NotifyCollectionChangedEventArgs ResetArgs = new(NotifyCollectionChangedAction.Reset);

    /// <summary>Batches with more changed items than this are reported as one Reset.</summary>
    public int ResetThreshold { get; init; } = 200;

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

        if (removeCount + addCount <= ResetThreshold)
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

    private void RaiseReset()
    {
        OnPropertyChanged(CountChanged);
        OnPropertyChanged(IndexerChanged);
        OnCollectionChanged(ResetArgs);
    }
}
