using DriveMonitor.Cli;

namespace DriveMonitor.Tests.Cli;

/// <summary>drivemon's command line: values that would crash later must be usage errors.</summary>
public sealed class CliOptionsTests
{
    [Theory]
    [InlineData("1", 1.0)]
    [InlineData("0.5", 0.5)]
    [InlineData("1e3", 1000.0)]
    [InlineData("31536000", 31_536_000.0)]
    public void Duration_SecondsUpTo365Days_AreAccepted(string text, double seconds)
    {
        CliOptions options = CliOptions.Parse(["--duration", text]);

        Assert.Equal(TimeSpan.FromSeconds(seconds), options.Duration);
    }

    [Theory]
    [InlineData("Infinity")] // a valid double; TimeSpan.FromSeconds threw OverflowException
    [InlineData("1e400")] // rounds to infinity
    [InlineData("1e12")] // beyond TimeSpan's range
    [InlineData("3e11")] // a valid TimeSpan, but now + duration overflowed DateTime after the session started
    [InlineData("31536001")]
    [InlineData("NaN")]
    [InlineData("-Infinity")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("ten")]
    public void Duration_NotAUsableNumberOfSeconds_IsAUsageError(string text)
    {
        ArgumentException error = Assert.Throws<ArgumentException>(() => CliOptions.Parse(["-t", text]));

        Assert.Contains($"'{text}'", error.Message);
        Assert.Contains(CliOptions.MaxDurationSeconds.ToString(), error.Message);
    }
}
