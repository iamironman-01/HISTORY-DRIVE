using DriveMonitor.Core;
using DriveMonitor.Tests.TestSupport;

namespace DriveMonitor.Tests;

/// <summary>Byte counts and transfer rates as shown to the user.</summary>
public sealed class FormattingTests
{
    [Theory]
    [InlineData(0L, "0 B")]
    [InlineData(1L, "1 B")]
    [InlineData(1023L, "1023 B")]
    [InlineData(1024L, "1 KB")]
    [InlineData(1264L, "1.23 KB")] // 1.234375: two decimals below 10
    [InlineData(1280L, "1.25 KB")]
    [InlineData(1536L, "1.5 KB")]
    [InlineData(10_240L, "10 KB")]
    [InlineData(10_291L, "10 KB")] // 10.05: one decimal from 10 to 100
    [InlineData(10_752L, "10.5 KB")]
    [InlineData(102_400L, "100 KB")] // no decimals from 100
    [InlineData(1_048_576L, "1 MB")]
    [InlineData(1_572_864L, "1.5 MB")]
    [InlineData(1_073_741_824L, "1 GB")]
    [InlineData(1_099_511_627_776L, "1 TB")]
    [InlineData(1_125_899_906_842_624L, "1 PB")]
    [InlineData(1_152_921_504_606_846_976L, "1024 PB")] // nothing above PB
    [InlineData(long.MaxValue, "8192 PB")]
    [InlineData(-1L, "-1 B")]
    [InlineData(-1536L, "-1.5 KB")]
    public void Bytes_UsesBinaryUnits(long bytes, string expected)
    {
        using CultureScope culture = CultureScope.Invariant();

        Assert.Equal(expected, Formatting.Bytes(bytes));
    }

    [Fact]
    public void Bytes_FollowsTheCurrentCulture()
    {
        using var culture = new CultureScope(CultureScope.Unusual());

        Assert.Equal("1,5 KB", Formatting.Bytes(1536));
        Assert.Equal("-1,5 KB", Formatting.Bytes(-1536)); // the minus sign is added by Bytes itself
    }

    [Fact]
    public void Bytes_LongMinValue_IsFormattedLikeItsMagnitude()
    {
        using CultureScope culture = CultureScope.Invariant();

        Assert.Equal("-8192 PB", Formatting.Bytes(long.MinValue));
    }

    [Theory]
    [InlineData(0.0, "0 B/s")]
    [InlineData(0.4, "0 B/s")]
    [InlineData(2.5, "2 B/s")] // Math.Round rounds half to even
    [InlineData(1023.4, "1023 B/s")]
    [InlineData(1023.5, "1 KB/s")]
    [InlineData(1536.0, "1.5 KB/s")]
    [InlineData(12_897_485.0, "12.3 MB/s")]
    [InlineData(-5.0, "0 B/s")]
    [InlineData(double.NegativeInfinity, "0 B/s")]
    [InlineData(double.PositiveInfinity, "8192 PB/s")] // .NET 9 converts out-of-range doubles to long by saturating
    [InlineData(double.NaN, "0 B/s")] // ...and NaN to 0
    public void Rate_IsWholeBytesPerSecond(double bytesPerSecond, string expected)
    {
        using CultureScope culture = CultureScope.Invariant();

        Assert.Equal(expected, Formatting.Rate(bytesPerSecond));
    }
}
