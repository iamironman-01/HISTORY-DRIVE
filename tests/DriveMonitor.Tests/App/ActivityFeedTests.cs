using System.ComponentModel;
using DriveMonitor.App.ViewModels;
using DriveMonitor.Core.Models;

namespace DriveMonitor.Tests.App;

/// <summary>The Live activity list: grouping repeated operations into rows, and sorting the rows.</summary>
public sealed class ActivityFeedTests
{
    private const string Report = @"E:\report.docx";
    private const int StatusAccessDenied = unchecked((int)0xC0000022);
    private const int StatusSharingViolation = unchecked((int)0xC0000043);

    private static readonly DateTime T0 = new(2026, 10, 31, 12, 0, 0);

    private long _sequence;

    [Fact]
    public void RepeatedReads_ShareOneRow()
    {
        var feed = new ActivityFeed(_ => true, maxRows: 1_000);

        feed.Add([Read(Report), Read(Report, ms: 100), Read(Report, ms: 200)]);

        ActivityRowViewModel row = Assert.Single(Rows(feed));
        Assert.Equal(3, row.Count);
        Assert.Equal(30, row.Bytes);
    }

    [Fact]
    public void DifferentKindsOfChange_GetTheirOwnRows()
    {
        // Truncated to 0 bytes, then its timestamps set, within the same second.
        var feed = new ActivityFeed(_ => true, maxRows: 1_000);

        feed.Add([SizeChange(Report, 0), Change(Report, "attributes or timestamps", ms: 50), Change(Report, "delete cancelled", ms: 90)]);

        List<ActivityRowViewModel> rows = Rows(feed);
        Assert.Equal(3, rows.Count);
        Assert.Equal("size → 0 bytes", rows[0].Detail);
        Assert.Equal("0 B", rows[0].SizeText);
        Assert.Equal("attributes or timestamps", rows[1].Detail);
        Assert.Null(rows[1].SizeText);
        Assert.Equal("delete cancelled", rows[2].Detail);
    }

    [Fact]
    public void RepeatedSizeChanges_ShareOneRowWithTheLatestSize()
    {
        var feed = new ActivityFeed(_ => true, maxRows: 1_000);

        feed.Add([SizeChange(Report, 100), SizeChange(Report, 4_096, ms: 10), SizeChange(Report, 200, ms: 20)]);

        ActivityRowViewModel row = Assert.Single(Rows(feed));
        Assert.Equal(3, row.Count);
        Assert.Equal(200, row.Bytes);
        Assert.Equal("size → 200 bytes", row.Detail);
    }

    [Fact]
    public void FailuresWithDifferentStatuses_GetTheirOwnRows()
    {
        var feed = new ActivityFeed(_ => true, maxRows: 1_000);

        feed.Add(
        [
            FailedOpen(Report, StatusSharingViolation, "sharing violation"),
            FailedOpen(Report, StatusAccessDenied, "access denied", ms: 10),
            FailedOpen(Report, StatusAccessDenied, "access denied", ms: 20),
        ]);

        List<ActivityRowViewModel> rows = Rows(feed);
        Assert.Equal(2, rows.Count);
        Assert.Equal((1, "failed: sharing violation"), (rows[0].Count, rows[0].DetailsText));
        Assert.Contains("NTSTATUS 0xC0000043", rows[0].ToolTip);
        Assert.Equal((2, "failed: access denied"), (rows[1].Count, rows[1].DetailsText));
        Assert.Contains("NTSTATUS 0xC0000022", rows[1].ToolTip);
    }

    [Fact]
    public void ResortChangedRows_MovesAGroupThatGrew()
    {
        var feed = new ActivityFeed(_ => true, maxRows: 1_000);
        feed.View.CustomSort = RowSorting.ForActivity(nameof(ActivityRowViewModel.Count), ListSortDirection.Descending);
        feed.Add([Read(@"E:\a.txt"), Read(@"E:\b.txt")]);
        Assert.Equal(new[] { @"E:\b.txt", @"E:\a.txt" }, Visible(feed)); // equal counts: the newer row first

        feed.Add([Read(@"E:\a.txt", ms: 10), Read(@"E:\a.txt", ms: 20)]);
        Assert.Equal(3, Rows(feed).Single(row => row.Path == @"E:\a.txt").Count);
        Assert.Equal(new[] { @"E:\b.txt", @"E:\a.txt" }, Visible(feed)); // grouped in place: not moved yet

        feed.ResortChangedRows();

        Assert.Equal(new[] { @"E:\a.txt", @"E:\b.txt" }, Visible(feed));
    }

    private FileActivityEvent Read(string path, int ms = 0) => Event(FileOperation.Read, path, ms) with { Bytes = 10 };

    private FileActivityEvent SizeChange(string path, long size, int ms = 0) =>
        Event(FileOperation.SetInfo, path, ms) with
        {
            Bytes = size,
            Flags = FileEventFlags.SizeChange,
            Detail = $"size → {size:N0} bytes",
        };

    private FileActivityEvent Change(string path, string detail, int ms = 0) => Event(FileOperation.SetInfo, path, ms) with { Detail = detail };

    private FileActivityEvent FailedOpen(string path, int ntStatus, string reason, int ms = 0) =>
        Event(FileOperation.Open, path, ms) with { Status = IoStatus.Failed, NtStatus = ntStatus, Detail = reason };

    private FileActivityEvent Event(FileOperation operation, string path, int ms) => new()
    {
        Sequence = ++_sequence,
        Timestamp = T0.AddMilliseconds(ms),
        Operation = operation,
        Path = path,
        Drive = path[0],
        ProcessId = 100,
        ProcessName = "app.exe",
        Status = IoStatus.Success,
    };

    private static List<ActivityRowViewModel> Rows(ActivityFeed feed) => feed.View.Cast<ActivityRowViewModel>().ToList();

    private static List<string> Visible(ActivityFeed feed) => Rows(feed).Select(row => row.Path).ToList();
}
