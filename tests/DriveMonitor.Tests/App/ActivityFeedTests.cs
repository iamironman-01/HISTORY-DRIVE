using System.ComponentModel;
using DriveMonitor.App.ViewModels;
using DriveMonitor.Core.Models;

namespace DriveMonitor.Tests.App;

/// <summary>The Live activity list: grouping repeated operations into rows, and sorting the rows.</summary>
public sealed class ActivityFeedTests
{
    private static readonly DateTime T0 = new(2026, 10, 31, 12, 0, 0);

    private long _sequence;

    [Fact]
    public void ResortChangedRows_MovesAGroupThatGrew()
    {
        var feed = new ActivityFeed(_ => true, maxRows: 1_000);
        feed.View.CustomSort = RowSorting.ForActivity(nameof(ActivityRowViewModel.Count), ListSortDirection.Descending);
        feed.Add([Read(@"E:\a.txt"), Read(@"E:\b.txt")]);
        Assert.Equal(new[] { @"E:\b.txt", @"E:\a.txt" }, Visible(feed)); // equal counts: the newer row first

        feed.Add([Read(@"E:\a.txt", ms: 10), Read(@"E:\a.txt", ms: 20)]);
        Assert.Equal(3, feed.View.Cast<ActivityRowViewModel>().Single(row => row.Path == @"E:\a.txt").Count);
        Assert.Equal(new[] { @"E:\b.txt", @"E:\a.txt" }, Visible(feed)); // grouped in place: not moved yet

        feed.ResortChangedRows();

        Assert.Equal(new[] { @"E:\a.txt", @"E:\b.txt" }, Visible(feed));
    }

    private FileActivityEvent Read(string path, int ms = 0, int processId = 100) => new()
    {
        Sequence = ++_sequence,
        Timestamp = T0.AddMilliseconds(ms),
        Operation = FileOperation.Read,
        Path = path,
        Drive = path[0],
        Bytes = 10,
        ProcessId = processId,
        ProcessName = "app.exe",
        Status = IoStatus.Success,
    };

    private static List<string> Visible(ActivityFeed feed) =>
        feed.View.Cast<ActivityRowViewModel>().Select(row => row.Path).ToList();
}
