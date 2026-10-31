using System.Collections.Specialized;
using DriveMonitor.App.ViewModels;

namespace DriveMonitor.Tests.App;

/// <summary>How a batch of added and removed rows is reported to the grids: item by item or as one Reset.</summary>
public sealed class BatchObservableCollectionTests
{
    [Fact]
    public void SmallBatch_IsReportedItemByItem()
    {
        BatchObservableCollection<int> items = Filled(1_000);
        List<NotifyCollectionChangedAction> changes = Record(items);

        items.AddRange(Numbers(1_000, 10));

        Assert.Equal(Enumerable.Repeat(NotifyCollectionChangedAction.Add, 10), changes);
        Assert.Equal(Numbers(0, 1_010), items);
    }

    [Fact]
    public void BatchAboveTheThreshold_IsOneReset()
    {
        BatchObservableCollection<int> items = Filled(1_000);
        List<NotifyCollectionChangedAction> changes = Record(items);

        items.RemoveFirstAndAddRange(1, Numbers(1_000, 200));

        Assert.Equal(NotifyCollectionChangedAction.Reset, Assert.Single(changes));
        Assert.Equal(Numbers(1, 1_199), items);
    }

    [Fact]
    public void LargeSelection_TurnsASmallBatchIntoOneReset()
    {
        // 10 notifications x 1,000 selected rows is more work for the grid than one pass over 2,000 rows.
        BatchObservableCollection<int> items = Filled(2_000);
        items.SelectedCount = 1_000;
        List<NotifyCollectionChangedAction> changes = Record(items);

        items.RemoveFirstAndAddRange(5, Numbers(2_000, 5));

        Assert.Equal(NotifyCollectionChangedAction.Reset, Assert.Single(changes));
        Assert.Equal(Numbers(5, 2_000), items);
    }

    [Fact]
    public void LargeSelection_InAMuchLongerList_StaysItemByItem()
    {
        BatchObservableCollection<int> items = Filled(100_000);
        items.SelectedCount = 1_000;
        List<NotifyCollectionChangedAction> changes = Record(items);

        items.AddRange(Numbers(100_000, 10));

        Assert.Equal(Enumerable.Repeat(NotifyCollectionChangedAction.Add, 10), changes);
    }

    [Fact]
    public void SmallSelection_StaysItemByItem()
    {
        BatchObservableCollection<int> items = Filled(1_000);
        items.SelectedCount = 499;
        List<NotifyCollectionChangedAction> changes = Record(items);

        items.AddRange(Numbers(1_000, 100));

        Assert.Equal(100, changes.Count);
        Assert.All(changes, action => Assert.Equal(NotifyCollectionChangedAction.Add, action));
    }

    [Fact]
    public void SelectionCleared_GoesBackToItemByItem()
    {
        BatchObservableCollection<int> items = Filled(2_000);
        items.SelectedCount = 1_000;
        items.SelectedCount = 0;
        List<NotifyCollectionChangedAction> changes = Record(items);

        items.AddRange(Numbers(2_000, 5));

        Assert.Equal(Enumerable.Repeat(NotifyCollectionChangedAction.Add, 5), changes);
    }

    private static BatchObservableCollection<int> Filled(int count)
    {
        var items = new BatchObservableCollection<int>();
        items.ReplaceAll(Numbers(0, count));
        return items;
    }

    private static List<int> Numbers(int start, int count) => Enumerable.Range(start, count).ToList();

    private static List<NotifyCollectionChangedAction> Record(BatchObservableCollection<int> items)
    {
        var changes = new List<NotifyCollectionChangedAction>();
        items.CollectionChanged += (_, e) => changes.Add(e.Action);
        return changes;
    }
}
