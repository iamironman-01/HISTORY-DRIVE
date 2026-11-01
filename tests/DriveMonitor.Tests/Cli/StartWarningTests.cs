using DriveMonitor.Cli;
using DriveMonitor.Core;

namespace DriveMonitor.Tests.Cli;

/// <summary>drivemon and drivemon --self-test: what a successful start went ahead despite is said on stderr, in one line.</summary>
public sealed class StartWarningTests
{
    [Fact]
    public void ReportStartWarning_NoWarning_SaysNothing()
    {
        var error = new StringWriter();

        LiveMonitor.ReportStartWarning(null, error);

        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void ReportStartWarning_SessionGuardAnotherProgramHolds_SaysTheEnginesWarningAsItIs()
    {
        string session = "DriveMonitor-Test-" + Guid.NewGuid().ToString("N");
        using var squatter = new Semaphore(0, 1, MonitorEngine.SessionGuardName(session)); // created already taken
        Assert.Null(MonitorEngine.AcquireSessionGuard(session, _ => false, TimeSpan.Zero, out string? warning));
        var error = new StringWriter();

        LiveMonitor.ReportStartWarning(warning, error);

        Assert.NotNull(warning);
        Assert.Equal(warning + Environment.NewLine, error.ToString());
    }

    [Fact]
    public void ReportStartWarning_ControlAndBidiCharacters_CannotBreakOrForgeALine()
    {
        var error = new StringWriter();

        LiveMonitor.ReportStartWarning("held by x‮evil\r\nRESULT: PASS", error);

        Assert.Equal("held by x?evil??RESULT: PASS" + Environment.NewLine, error.ToString());
    }
}
