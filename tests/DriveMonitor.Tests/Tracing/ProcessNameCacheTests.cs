using DriveMonitor.Core.Tracing;

namespace DriveMonitor.Tests.Tracing;

/// <summary>Process id → image name, from start events, well-known ids, a direct query or the trace's hint.</summary>
public sealed class ProcessNameCacheTests
{
    /// <summary>A multiple of 4 like every real process id, but far beyond any id Windows hands out.</summary>
    private const int MissingProcessId = int.MaxValue - 3;

    [Theory]
    [InlineData(@"\Device\HarddiskVolume3\Windows\explorer.exe", "explorer.exe")]
    [InlineData(@"C:\Windows\System32\notepad.exe", "notepad.exe")]
    [InlineData("chrome.exe", "chrome.exe")]
    public void OnProcessStarted_KeepsOnlyTheFileName(string imageFileName, string expected)
    {
        var cache = new ProcessNameCache();

        cache.OnProcessStarted(1000, imageFileName);

        Assert.Equal(expected, cache.TryGet(1000));
        Assert.Equal(expected, cache.Resolve(1000, "hint.exe"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void OnProcessStarted_WithoutImageName_IsIgnored(string? imageFileName)
    {
        var cache = new ProcessNameCache();

        cache.OnProcessStarted(1000, imageFileName);

        Assert.Null(cache.TryGet(1000));
    }

    [Fact]
    public void OnProcessStarted_ReusedProcessId_ReplacesTheName()
    {
        var cache = new ProcessNameCache();

        cache.OnProcessStarted(1000, "old.exe");
        cache.OnProcessStarted(1000, @"C:\Tools\new.exe");

        Assert.Equal("new.exe", cache.TryGet(1000));
    }

    [Fact]
    public void TryGet_UnknownProcess_IsNull()
    {
        Assert.Null(new ProcessNameCache().TryGet(1000));
    }

    [Theory]
    [InlineData(0, "Idle")]
    [InlineData(4, "System")]
    [InlineData(-1, "unknown")]
    [InlineData(int.MinValue, "unknown")]
    public void Resolve_WellKnownIds_HaveFixedNames(int processId, string expected)
    {
        var cache = new ProcessNameCache();

        Assert.Equal(expected, cache.Resolve(processId, "hint.exe"));
        Assert.Equal(expected, cache.TryGet(processId)); // remembered
    }

    [Fact]
    public void Resolve_ProcessThatCannotBeQueried_UsesTheHint()
    {
        var cache = new ProcessNameCache();

        Assert.Equal("hint.exe", cache.Resolve(MissingProcessId, "hint.exe"));
        Assert.Equal("hint.exe", cache.TryGet(MissingProcessId));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Resolve_ProcessThatCannotBeQueriedWithoutHint_UsesThePid(string? hint)
    {
        var cache = new ProcessNameCache();

        Assert.Equal("PID 2147483644", cache.Resolve(MissingProcessId, hint));
    }

    [Fact]
    public void Resolve_RemembersTheFirstAnswer()
    {
        var cache = new ProcessNameCache();

        cache.Resolve(MissingProcessId, "first.exe");

        Assert.Equal("first.exe", cache.Resolve(MissingProcessId, "second.exe"));
    }

    [Fact]
    public void Resolve_KnownProcess_IgnoresTheHint()
    {
        var cache = new ProcessNameCache();
        cache.OnProcessStarted(1000, "real.exe");

        Assert.Equal("real.exe", cache.Resolve(1000, "hint.exe"));
    }

    [Fact]
    public void Resolve_RunningProcess_QueriesItsImageName()
    {
        var cache = new ProcessNameCache();
        string expected = Path.GetFileName(Environment.ProcessPath!);

        Assert.Equal(expected, cache.Resolve(Environment.ProcessId, "hint.exe"), ignoreCase: true);
    }

    [Fact]
    public void Cache_StartsOverWhenItHoldsTheMaximumNumberOfEntries()
    {
        // ProcessNameCache keeps at most 65,536 names and clears itself when that is reached.
        var cache = new ProcessNameCache();
        for (int pid = 1; pid <= 65_536; pid++)
            cache.OnProcessStarted(pid, "p.exe");
        Assert.Equal("p.exe", cache.TryGet(1));

        cache.OnProcessStarted(100_000, "new.exe");

        Assert.Null(cache.TryGet(1));
        Assert.Null(cache.TryGet(65_536));
        Assert.Equal("new.exe", cache.TryGet(100_000));
    }
}
