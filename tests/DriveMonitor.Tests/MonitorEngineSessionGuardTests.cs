using DriveMonitor.Core;

namespace DriveMonitor.Tests;

/// <summary>
/// The machine-wide guard that keeps two DriveMonitors off one kernel session name, and what happens when
/// another program holds it. Each test uses its own session name, and whether a session runs is answered
/// by the test, so no kernel session is needed or touched.
/// </summary>
public sealed class MonitorEngineSessionGuardTests
{
    private static readonly TimeSpan NoWait = TimeSpan.Zero;
    private static readonly TimeSpan LongWait = TimeSpan.FromSeconds(10);

    [Fact]
    public void FreeGuard_IsTakenWithoutListingSessions()
    {
        string session = UniqueSessionName();

        using Semaphore? guard = MonitorEngine.AcquireSessionGuard(
            session, _ => throw new InvalidOperationException("sessions were listed"), NoWait, out string? warning);

        Assert.NotNull(guard);
        Assert.Null(warning);
    }

    [Fact]
    public void GuardOfARunningDriveMonitor_RefusesToStart()
    {
        string session = UniqueSessionName();
        using Semaphore? first = MonitorEngine.AcquireSessionGuard(session, _ => false, NoWait, out _);

        var error = Assert.Throws<MonitorStartException>(() => MonitorEngine.AcquireSessionGuard(session, _ => true, NoWait, out _));

        Assert.Contains("already monitoring", error.Message);
    }

    [Fact]
    public void GuardHeldWithoutASession_IsIgnoredWithAWarning()
    {
        string session = UniqueSessionName();
        using var squatter = new Semaphore(0, 1, MonitorEngine.SessionGuardName(session)); // created already taken

        Semaphore? guard = MonitorEngine.AcquireSessionGuard(session, _ => false, NoWait, out string? warning);

        Assert.Null(guard);
        Assert.NotNull(warning);
        Assert.Contains(session, warning);
    }

    [Fact]
    public void GuardNameTakenByAnotherKindOfObject_IsIgnoredWithAWarning()
    {
        string session = UniqueSessionName();
        using var squatter = new Mutex(false, MonitorEngine.SessionGuardName(session));

        Semaphore? guard = MonitorEngine.AcquireSessionGuard(session, _ => false, NoWait, out string? warning);

        Assert.Null(guard);
        Assert.NotNull(warning);
    }

    [Fact]
    public void HeldGuard_WhenSessionsCannotBeListed_RefusesToStart()
    {
        string session = UniqueSessionName();
        using var holder = new Semaphore(0, 1, MonitorEngine.SessionGuardName(session));

        var error = Assert.Throws<MonitorStartException>(() => MonitorEngine.AcquireSessionGuard(session, _ => null, NoWait, out _));

        Assert.Contains("already monitoring", error.Message);
    }

    [Fact]
    public void HeldGuard_WhoseSessionAppearsWhileWaiting_RefusesToStart()
    {
        // Another DriveMonitor has just taken the guard and is still starting its session.
        string session = UniqueSessionName();
        using var other = new Semaphore(0, 1, MonitorEngine.SessionGuardName(session));
        int checks = 0;

        Assert.Throws<MonitorStartException>(() => MonitorEngine.AcquireSessionGuard(session, _ => ++checks > 1, LongWait, out _));

        Assert.Equal(2, checks);
    }

    [Fact]
    public void HeldGuard_ReleasedWhileWaiting_IsTaken()
    {
        string session = UniqueSessionName();
        using var other = new Semaphore(0, 1, MonitorEngine.SessionGuardName(session));
        int checks = 0;

        using Semaphore? guard = MonitorEngine.AcquireSessionGuard(
            session,
            _ =>
            {
                if (++checks == 1)
                    other.Release(); // the other DriveMonitor stops
                return false;
            },
            LongWait,
            out string? warning);

        Assert.NotNull(guard);
        Assert.Null(warning);
        Assert.Equal(1, checks);
    }

    private static string UniqueSessionName() => "DriveMonitor-Test-" + Guid.NewGuid().ToString("N");
}
