using DriveMonitor.Core.Tracing;

namespace DriveMonitor.Tests.Tracing;

/// <summary>NT_SUCCESS and the short names for common NTSTATUS failures.</summary>
public sealed class NtStatusTextTests
{
    [Theory]
    [InlineData(0, true)]
    [InlineData(0x00000103, true)] // STATUS_PENDING
    [InlineData(0x40000000, true)] // informational
    [InlineData(int.MaxValue, true)]
    [InlineData(unchecked((int)0x80000005), false)] // warning: STATUS_BUFFER_OVERFLOW
    [InlineData(NtStatusText.StatusNoMoreFiles, false)]
    [InlineData(unchecked((int)0xC0000022), false)] // error: STATUS_ACCESS_DENIED
    [InlineData(-1, false)]
    public void IsSuccess_IsTrueForNonNegativeStatuses(int status, bool expected)
    {
        Assert.Equal(expected, NtStatusText.IsSuccess(status));
    }

    [Theory]
    [InlineData(unchecked((int)0x80000005), "buffer overflow")]
    [InlineData(NtStatusText.StatusNoMoreFiles, "no more files")]
    [InlineData(NtStatusText.StatusNoSuchFile, "no such file")]
    [InlineData(unchecked((int)0xC0000022), "access denied")]
    [InlineData(unchecked((int)0xC0000034), "not found")]
    [InlineData(unchecked((int)0xC0000035), "already exists")]
    [InlineData(unchecked((int)0xC000003A), "path not found")]
    [InlineData(unchecked((int)0xC0000043), "sharing violation")]
    [InlineData(unchecked((int)0xC000007F), "disk full")]
    [InlineData(unchecked((int)0xC00000A2), "write protected")]
    [InlineData(unchecked((int)0xC0000185), "device I/O error")]
    [InlineData(unchecked((int)0xC000026E), "volume dismounted")]
    public void Describe_KnownStatus_GivesItsShortName(int status, string expected)
    {
        Assert.Equal(expected, NtStatusText.Describe(status));
    }

    [Theory]
    [InlineData(0, "status 0x00000000")]
    [InlineData(0x00000103, "status 0x00000103")]
    [InlineData(unchecked((int)0xC0001234), "status 0xC0001234")]
    [InlineData(-1, "status 0xFFFFFFFF")]
    public void Describe_UnknownStatus_ShowsTheCodeInHex(int status, string expected)
    {
        Assert.Equal(expected, NtStatusText.Describe(status));
    }
}
