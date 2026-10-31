using DriveMonitor.Cli;
using DriveMonitor.Core;
using DriveMonitor.Tests.TestSupport;

namespace DriveMonitor.Tests.Cli;

/// <summary>drivemon --csv: a file that cannot be written is a one-line error, not a crash.</summary>
public sealed class CsvLogStartTests
{
    [Fact]
    public void TryStartLog_WritableFile_StartsTheLog()
    {
        using var temp = new TempDirectory();
        using var engine = new MonitorEngine();
        var error = new StringWriter();

        Assert.True(LiveMonitor.TryStartLog(engine, temp.PathOf("activity.csv"), error));

        Assert.Equal(temp.PathOf("activity.csv"), engine.Log?.FilePath);
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void TryStartLog_FileOpenInAnotherProgram_FailsWithOneLine()
    {
        using var temp = new TempDirectory();
        string path = temp.PathOf("activity.csv");
        using var spreadsheet = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var engine = new MonitorEngine();
        var error = new StringWriter();

        Assert.False(LiveMonitor.TryStartLog(engine, path, error));

        Assert.Null(engine.Log);
        AssertOneLine(error.ToString());
    }

    [Fact]
    public void TryStartLog_PathIsAFolder_FailsWithOneLine()
    {
        using var temp = new TempDirectory();
        using var engine = new MonitorEngine();
        var error = new StringWriter();

        Assert.False(LiveMonitor.TryStartLog(engine, temp.FullPath, error));

        Assert.Null(engine.Log);
        AssertOneLine(error.ToString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("activity\0.csv")]
    public void TryStartLog_InvalidPath_FailsWithOneLine(string path)
    {
        using var engine = new MonitorEngine();
        var error = new StringWriter();

        Assert.False(LiveMonitor.TryStartLog(engine, path, error));

        Assert.Null(engine.Log);
        AssertOneLine(error.ToString());
    }

    private static void AssertOneLine(string text)
    {
        Assert.StartsWith("Cannot write the CSV file: ", text);
        Assert.EndsWith(Environment.NewLine, text);
        Assert.DoesNotContain('\n', text.TrimEnd());
    }
}
