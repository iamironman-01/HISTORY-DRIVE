using DriveMonitor.App.ViewModels;
using DriveMonitor.Core.Aggregation;
using DriveMonitor.Core.Models;

namespace DriveMonitor.Tests.App;

/// <summary>The search over the Files table, fed by the real aggregator.</summary>
public sealed class ActivityFilterTests
{
    private const string Movie = @"E:\movie.mp4";

    private static readonly string[] FirstThree = ["explorer.exe", "MsMpEng.exe", "SearchProtocolHost.exe"];

    [Fact]
    public void AcceptsFile_FindsAProcessPastTheThreeTheColumnNames()
    {
        var aggregator = new ActivityAggregator();
        foreach (string name in FirstThree.Append("vlc.exe"))
            Record(aggregator, name);
        var row = new FileRowViewModel(Assert.Single(aggregator.GetAllFiles()));
        var filter = new ActivityFilter { Search = "vlc.exe" };

        Assert.DoesNotContain("vlc", row.Processes, StringComparison.OrdinalIgnoreCase);
        Assert.True(filter.AcceptsFile(row));
        Assert.False(new ActivityFilter { Search = "notepad.exe" }.AcceptsFile(row));
    }

    [Fact]
    public void FilesTable_ShowsAFileOnceTheSearchedProcessTouchesIt()
    {
        // Filter to vlc.exe while the file is known only from Explorer, then vlc plays it.
        var aggregator = new ActivityAggregator();
        var filter = new ActivityFilter();
        var files = new StatsTable<string, FileStatsSnapshot, FileRowViewModel>(
            s => s.Path, s => new FileRowViewModel(s), (row, s) => row.Update(s), filter.AcceptsFile, StringComparer.OrdinalIgnoreCase);
        foreach (string name in FirstThree)
            Record(aggregator, name);
        files.Apply(aggregator.TakeChangedFiles());
        filter.Search = "vlc.exe";
        files.ApplySearch(hasSearch: true);
        Assert.Equal(0, files.View.Count);

        Record(aggregator, "vlc.exe");
        files.Apply(aggregator.TakeChangedFiles());

        Assert.Equal(Movie, Assert.Single(files.View.Cast<FileRowViewModel>()).Path);
    }

    private static void Record(ActivityAggregator aggregator, string processName) => aggregator.Record(new FileActivityEvent
    {
        Operation = FileOperation.Read,
        Path = Movie,
        Drive = 'E',
        Bytes = 4_096,
        Status = IoStatus.Success,
        ProcessId = 100,
        ProcessName = processName,
        Timestamp = new DateTime(2026, 10, 31, 12, 0, 0),
    });
}
