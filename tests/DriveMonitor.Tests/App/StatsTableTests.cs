using System.Collections.Specialized;
using DriveMonitor.App.ViewModels;

namespace DriveMonitor.Tests.App;

/// <summary>The Files and Processes tables: rows updated in place, and the view's sorting over them.</summary>
public sealed class StatsTableTests
{
    [Fact]
    public void ResortChangedRows_MovesARowThatGrewInPlace()
    {
        StatsTable<string, (string Name, long Size), Row> table = NewTable();
        table.View.CustomSort = BySizeDescending;
        table.Apply([("a", 300), ("b", 200), ("c", 100)]);

        table.Apply([("c", 5_000_000_000)]);
        Assert.Equal(new[] { "a", "b", "c" }, Visible(table)); // the view only sorts rows as they are added

        table.ResortChangedRows();

        Assert.Equal(new[] { "c", "a", "b" }, Visible(table));
    }

    [Fact]
    public void ResortChangedRows_RefreshesOnlyWhenRowsChangedInPlace()
    {
        StatsTable<string, (string Name, long Size), Row> table = NewTable();
        table.View.CustomSort = BySizeDescending;
        table.Apply([("a", 300), ("b", 200)]);
        List<NotifyCollectionChangedAction> changes = Record(table);

        table.ResortChangedRows(); // nothing changed in place since the sort
        table.Apply([("c", 100)]); // a new row is inserted at its place
        table.ResortChangedRows();

        Assert.Equal(NotifyCollectionChangedAction.Add, Assert.Single(changes));
        Assert.Equal(new[] { "a", "b", "c" }, Visible(table));
    }

    [Fact]
    public void ResortChangedRows_LeavesAnUnsortedViewAlone()
    {
        StatsTable<string, (string Name, long Size), Row> table = NewTable();
        table.Apply([("a", 300), ("b", 200)]);
        List<NotifyCollectionChangedAction> changes = Record(table);

        table.Apply([("b", 900)]);
        table.ResortChangedRows();

        Assert.Empty(changes);
        Assert.Equal(new[] { "a", "b" }, Visible(table));
    }

    [Fact]
    public void Apply_ShowsARowThatStartsMatchingTheSearch()
    {
        StatsTable<string, (string Name, long Size), Row> table = NewTable(row => ((Row)row).Size >= 1_000);
        table.ApplySearch(hasSearch: true);
        table.Apply([("a", 500), ("b", 2_000)]);
        Assert.Equal(new[] { "b" }, Visible(table));

        table.Apply([("a", 5_000)]);

        Assert.Equal(new[] { "a", "b" }, Visible(table));
    }

    [Fact]
    public void Apply_HidesARowThatNoLongerMatchesTheSearch()
    {
        StatsTable<string, (string Name, long Size), Row> table = NewTable(row => ((Row)row).Size >= 1_000);
        table.ApplySearch(hasSearch: true);
        table.Apply([("a", 5_000), ("b", 2_000)]);

        table.Apply([("b", 10), ("c", 3_000)]);

        Assert.Equal(new[] { "a", "c" }, Visible(table));
    }

    [Fact]
    public void Apply_WithoutASearch_ShowsEveryRow()
    {
        StatsTable<string, (string Name, long Size), Row> table = NewTable(row => ((Row)row).Size >= 1_000);
        table.Apply([("a", 5_000), ("b", 2_000)]);
        List<NotifyCollectionChangedAction> changes = Record(table);

        table.Apply([("b", 10)]); // the filter is only installed while a search is active

        Assert.Empty(changes);
        Assert.Equal(new[] { "a", "b" }, Visible(table));
    }

    internal sealed class Row(string name, long size)
    {
        public string Name { get; } = name;

        public long Size { get; set; } = size;
    }

    private static Comparer<object> BySizeDescending { get; } =
        Comparer<object>.Create((x, y) => ((Row)y).Size.CompareTo(((Row)x).Size));

    private static StatsTable<string, (string Name, long Size), Row> NewTable(Predicate<object>? filter = null) =>
        new(s => s.Name, s => new Row(s.Name, s.Size), (row, s) => row.Size = s.Size, filter ?? (_ => true));

    private static List<string> Visible(StatsTable<string, (string Name, long Size), Row> table) =>
        table.View.Cast<Row>().Select(row => row.Name).ToList();

    private static List<NotifyCollectionChangedAction> Record(StatsTable<string, (string Name, long Size), Row> table)
    {
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)table.View).CollectionChanged += (_, e) => changes.Add(e.Action);
        return changes;
    }
}
