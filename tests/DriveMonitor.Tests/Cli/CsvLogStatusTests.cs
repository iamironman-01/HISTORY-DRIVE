using DriveMonitor.Cli;
using DriveMonitor.Core.Export;
using DriveMonitor.Core.Models;
using DriveMonitor.Tests.TestSupport;

namespace DriveMonitor.Tests.Cli;

/// <summary>drivemon --csv: a log that stops on an error is reported at once, summarized at the end and fails the run.</summary>
public sealed class CsvLogStatusTests
{
    [Fact]
    public void FailureLine_WhileMonitoring_SaysWhyAndHowManyRowsReachedWhichFile()
    {
        using CultureScope culture = CultureScope.Invariant();

        string line = CsvLogStatus.FailureLine(
            @"E:\logs\activity.csv", 12_345, 67, new IOException("The device is not ready."), monitoring: true);

        Assert.Equal(
            @"The CSV log stopped: The device is not ready. 12,345 rows were written to E:\logs\activity.csv; later events are not logged.",
            line);
    }

    [Fact]
    public void FailureLine_AtTheEnd_SaysHowManyRowsWereDropped()
    {
        using CultureScope culture = CultureScope.Invariant();
        var error = new IOException("The device is not ready.");

        Assert.Equal(
            @"The CSV log stopped: The device is not ready. 12,345 rows were written to E:\a.csv and 1,067 were dropped.",
            CsvLogStatus.FailureLine(@"E:\a.csv", 12_345, 1_067, error, monitoring: false));
        Assert.Equal(
            @"The CSV log stopped: The device is not ready. 1 row was written to E:\a.csv and 1 was dropped.",
            CsvLogStatus.FailureLine(@"E:\a.csv", 1, 1, error, monitoring: false));
    }

    [Theory]
    [InlineData("The device is not ready.", "The device is not ready.")]
    [InlineData("There is not enough space on the disk. : 'E:\\a.csv'", "There is not enough space on the disk. : 'E:\\a.csv'.")]
    [InlineData("Is the drive still there?", "Is the drive still there?")]
    [InlineData("first line\r\nsecond line\r\n", "first line??second line.")]
    public void Sentence_EndsTheMessageLikeASentenceOnOneLine(string message, string expected)
    {
        Assert.Equal(expected, CsvLogStatus.Sentence(message));
    }

    [Theory]
    [InlineData(@"E:\logs\activity.csv", @"E:\logs\activity.csv", @"E:\logs\activity.csv")]
    [InlineData(@"E:\logs\activity.csv", @"E:\logs\activity.012.csv", @"E:\logs\activity.csv through activity.012.csv")]
    [InlineData("E:\\shared\\x\u202Evsc.csv", "E:\\shared\\x\u202Evsc.002.csv", @"E:\shared\x?vsc.csv through x?vsc.002.csv")]
    public void DescribeFiles_NamesTheFirstAndTheCurrentFileOnceTheLogContinued(string first, string current, string expected)
    {
        Assert.Equal(expected, CsvLogStatus.DescribeFiles(first, current));
    }

    [Fact]
    public void SummaryLine_GivesTheCountsAndTheErrorOnOneLine()
    {
        using CultureScope culture = CultureScope.Invariant();

        Assert.Equal(
            @"CSV log: E:\a.csv  rows written: 12,345  rows dropped: 0",
            CsvLogStatus.SummaryLine(@"E:\a.csv", 12_345, 0, null));
        Assert.Equal(
            @"CSV log: E:\a.csv  rows written: 1,000,000  rows dropped: 67  error: first line??second line",
            CsvLogStatus.SummaryLine(@"E:\a.csv", 1_000_000, 67, new IOException("first line\r\nsecond line\r\n")));
    }

    [Fact]
    public void ReportFailure_WhileTheLogWorks_SaysNothing()
    {
        using var temp = new TempDirectory();
        using var log = new CsvActivityLog(temp.PathOf("activity.csv"));
        var status = new CsvLogStatus(log);
        var error = new StringWriter();

        log.Write(Event(0));
        status.ReportFailure(error);

        Assert.False(status.Failed);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void ReportFailure_AfterTheLogFailed_SaysSoOnceOnOneLine()
    {
        using var temp = new TempDirectory();
        using CsvActivityLog log = FailedLog(temp);
        var status = new CsvLogStatus(log);
        var error = new StringWriter();

        status.ReportFailure(error);
        status.ReportFailure(error);

        Assert.True(status.Failed);
        Assert.Equal(
            $"The CSV log stopped: {CsvLogStatus.Sentence(log.Error!.Message)} 0 rows were written to {log.FilePath}; " +
            "later events are not logged." + Environment.NewLine,
            error.ToString());
    }

    [Fact]
    public void Finish_StopsTheLogBeforeTheSummaryReadsItsCounts()
    {
        using CultureScope culture = CultureScope.Invariant();
        using var temp = new TempDirectory();
        using var log = new CsvActivityLog(temp.PathOf("activity.csv"));
        var status = new CsvLogStatus(log) { FinishingNoticeDelay = TimeSpan.FromMinutes(1) };
        for (int i = 0; i < 1_000; i++)
            log.Write(Event(i));
        var output = new StringWriter();
        var error = new StringWriter();

        status.Finish(log.Dispose, output, error);

        Assert.Equal($"CSV log: {log.FilePath}  rows written: 1,000  rows dropped: 0", status.SummaryLine());
        Assert.Equal("", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void Finish_WhenStoppingTakesLong_SaysThatTheLogIsBeingFinished()
    {
        using var temp = new TempDirectory();
        using var log = new CsvActivityLog(temp.PathOf("activity.csv"));
        var status = new CsvLogStatus(log) { FinishingNoticeDelay = TimeSpan.FromMilliseconds(20) };
        using var output = new SignallingWriter();
        bool stopped = false;

        // A drive slower than the notice delay: the log is stopped only once the notice is out.
        status.Finish(
            () =>
            {
                output.LineWritten.Wait(TimeSpan.FromSeconds(10));
                log.Dispose();
                stopped = true;
            },
            output,
            TextWriter.Null);

        Assert.True(stopped);
        Assert.Equal("Finishing the CSV log..." + Environment.NewLine, output.ToString());
    }

    [Fact]
    public void Finish_AfterAFailureNotSaidYet_SaysItWithTheDroppedRows()
    {
        using var temp = new TempDirectory();
        using CsvActivityLog log = FailedLog(temp);
        var status = new CsvLogStatus(log);
        var error = new StringWriter();

        status.Finish(log.Dispose, TextWriter.Null, error);

        Assert.Equal(
            $"The CSV log stopped: {CsvLogStatus.Sentence(log.Error!.Message)} 0 rows were written to {log.FilePath} " +
            "and 1 was dropped." + Environment.NewLine,
            error.ToString());
    }

    [Fact]
    public void Finish_AfterAFailureAlreadySaid_DoesNotSayItAgain()
    {
        using var temp = new TempDirectory();
        using CsvActivityLog log = FailedLog(temp);
        var status = new CsvLogStatus(log);
        var error = new StringWriter();
        status.ReportFailure(error);
        string reported = error.ToString();

        log.Write(Event(1)); // refused by the stopped log, so dropped
        status.Finish(log.Dispose, TextWriter.Null, error);

        Assert.Equal(reported, error.ToString());
        Assert.Equal($"CSV log: {log.FilePath}  rows written: 0  rows dropped: 2  error: {log.Error!.Message}", status.SummaryLine());
    }

    [Fact]
    public void SummaryLine_LogThatContinuedInMoreFiles_NamesTheFirstAndTheCurrentFile()
    {
        using var temp = new TempDirectory();
        using var log = new CsvActivityLog(temp.PathOf("activity.csv"), maxFileBytes: 1); // a file for every row
        var status = new CsvLogStatus(log);
        for (int i = 0; i < 3; i++)
            log.Write(Event(i));

        status.Finish(log.Dispose, TextWriter.Null, TextWriter.Null);

        Assert.Equal(temp.PathOf("activity.003.csv"), log.CurrentFilePath);
        Assert.Equal($"CSV log: {temp.PathOf("activity.csv")} through activity.003.csv  rows written: 3  rows dropped: 0", status.SummaryLine());
    }

    [Fact]
    public void ExitCode_IsOneOnlyWhenTheLogFailed()
    {
        using var temp = new TempDirectory();
        using var working = new CsvActivityLog(temp.PathOf("working.csv"));
        using CsvActivityLog failed = FailedLog(temp, "failed.csv");

        Assert.Equal(0, LiveMonitor.ExitCode(null));
        Assert.Equal(0, LiveMonitor.ExitCode(new CsvLogStatus(working)));
        Assert.Equal(1, LiveMonitor.ExitCode(new CsvLogStatus(failed)));
    }

    /// <summary>A log that stopped on an error at its first write, as when its drive has filled up.</summary>
    private static CsvActivityLog FailedLog(TempDirectory temp, string name = "activity.csv")
    {
        var log = new CsvActivityLog(temp.PathOf(name), minFreeBytes: 1024) { FreeSpaceProbe = _ => 1023 };
        log.Write(Event(0));
        Assert.True(SpinWait.SpinUntil(() => log.Error is not null, TimeSpan.FromSeconds(5)), "the log did not stop");
        return log;
    }

    private static FileActivityEvent Event(int i) => new()
    {
        Sequence = i,
        Timestamp = new DateTime(2026, 10, 31, 12, 0, 0),
        Operation = FileOperation.Write,
        Path = $@"E:\photos\img_{i:D4}.jpg",
        Drive = 'E',
        Bytes = 4096,
        ProcessName = "copy.exe",
        ProcessId = 4242,
    };

    /// <summary>Collects what is written and signals the first line.</summary>
    private sealed class SignallingWriter : StringWriter
    {
        public ManualResetEventSlim LineWritten { get; } = new();

        public override void WriteLine(string? value)
        {
            base.WriteLine(value);
            LineWritten.Set();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                LineWritten.Dispose();
            base.Dispose(disposing);
        }
    }
}
