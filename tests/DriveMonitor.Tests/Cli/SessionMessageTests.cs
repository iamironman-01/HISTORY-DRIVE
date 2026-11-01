using DriveMonitor.Cli;

namespace DriveMonitor.Tests.Cli;

/// <summary>drivemon's lines for a kernel session that stopped: the exception's message can quote a file name.</summary>
public sealed class SessionMessageTests
{
    private const string Hostile = "E:\\a\r\nFAKE LINE\u001b[2K\u202Etxt.exe";
    private const string Cleaned = "E:\\a??FAKE LINE?[2K?txt.exe";

    [Fact]
    public void FaultedLine_ReplacesControlAndBidiCharacters()
    {
        Assert.Equal($"Monitoring stopped: {Cleaned}", LiveMonitor.FaultedLine(new IOException(Hostile)));
    }

    [Fact]
    public void RestartedLine_ReplacesControlAndBidiCharacters()
    {
        Assert.Equal(
            $"The kernel session stopped ({Cleaned}) and was restarted; some events may be missing.",
            LiveMonitor.RestartedLine(new IOException(Hostile)));
    }
}
