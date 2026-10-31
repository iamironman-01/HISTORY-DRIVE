using DriveMonitor.Core.Aggregation;
using DriveMonitor.Core.Models;

namespace DriveMonitor.Tests.Aggregation;

/// <summary>
/// <see cref="FileStatsSnapshot.ProcessNames"/>: every process name stored for a file, so that a search can find
/// processes the shortened Processes text leaves out.
/// </summary>
public sealed class FileProcessNamesTests
{
    private const string FileA = @"E:\movie.mp4";

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void ProcessNames_IsNullWhileTheTextNamesEveryProcess(int processes)
    {
        ActivityAggregator aggregator = Touched(Names(processes));

        FileStatsSnapshot file = Assert.Single(aggregator.GetAllFiles());
        Assert.Null(file.ProcessNames);
        Assert.Equal(string.Join(", ", Names(processes)), file.Processes);
    }

    [Fact]
    public void ProcessNames_HoldsEveryNameOnceTheTextShortensThem()
    {
        string[] names = ["explorer.exe", "MsMpEng.exe", "SearchProtocolHost.exe", "vlc.exe"];
        ActivityAggregator aggregator = Touched(names);

        FileStatsSnapshot file = Assert.Single(aggregator.GetAllFiles());
        Assert.Equal("explorer.exe, MsMpEng.exe, SearchProtocolHost.exe +1 more", file.Processes);
        Assert.Equal(names, file.ProcessNames);
    }

    [Fact]
    public void ProcessNames_IncludesTheNamesThatAreOnlyCounted()
    {
        string[] names = Names(20); // 16 are listed internally, the rest only counted
        ActivityAggregator aggregator = Touched(names);

        FileStatsSnapshot file = Assert.Single(aggregator.GetAllFiles());
        Assert.Equal(20, file.ProcessCount);
        Assert.Equal(names, file.ProcessNames);
    }

    [Fact]
    public void ProcessNames_IsCopiedAgainOnlyWhenANameArrives()
    {
        ActivityAggregator aggregator = Touched(Names(4));
        IReadOnlyList<string>? before = Assert.Single(aggregator.TakeChangedFiles()).ProcessNames;

        aggregator.Record(Read(FileA, "P0.EXE", processId: 1)); // a repeat in another case
        Assert.Same(before, Assert.Single(aggregator.TakeChangedFiles()).ProcessNames);

        aggregator.Record(Read(FileA, "late.exe", processId: 99));
        IReadOnlyList<string>? after = Assert.Single(aggregator.TakeChangedFiles()).ProcessNames;

        Assert.Equal(Names(4).Append("late.exe"), after);
        Assert.Equal(Names(4), before); // a snapshot already handed out never changes
    }

    private static string[] Names(int count) => Enumerable.Range(0, count).Select(i => $"p{i}.exe").ToArray();

    private static ActivityAggregator Touched(IReadOnlyList<string> processNames)
    {
        var aggregator = new ActivityAggregator();
        for (int i = 0; i < processNames.Count; i++)
            aggregator.Record(Read(FileA, processNames[i], processId: 1 + i));
        return aggregator;
    }

    private static FileActivityEvent Read(string path, string processName, int processId) => new()
    {
        Operation = FileOperation.Read,
        Path = path,
        Drive = path[0],
        Bytes = 1,
        Status = IoStatus.Success,
        ProcessId = processId,
        ProcessName = processName,
        Timestamp = new DateTime(2026, 10, 31, 12, 0, 0),
    };
}
