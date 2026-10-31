using DriveMonitor.Core.Models;

namespace DriveMonitor.Tests.Models;

/// <summary>Defaults and derived properties of an event.</summary>
public sealed class FileActivityEventTests
{
    [Fact]
    public void NewEvent_HasNoOffsetAndEmptyText()
    {
        var ev = new FileActivityEvent();

        Assert.Equal(-1L, ev.Offset);
        Assert.Equal("", ev.Path);
        Assert.Equal("", ev.ProcessName);
        Assert.Null(ev.Detail);
        Assert.Equal(IoStatus.Unknown, ev.Status);
        Assert.Equal(FileEventFlags.None, ev.Flags);
        Assert.False(ev.IsPaging);
        Assert.False(ev.IsDirectory);
    }

    [Theory]
    [InlineData(FileEventFlags.Paging, true, false)]
    [InlineData(FileEventFlags.Directory, false, true)]
    [InlineData(FileEventFlags.Paging | FileEventFlags.NonCached, true, false)]
    [InlineData(FileEventFlags.Directory | FileEventFlags.DeleteOnClose, false, true)]
    [InlineData(FileEventFlags.NonCached | FileEventFlags.SizeChange, false, false)]
    public void IsPagingAndIsDirectory_FollowTheFlags(FileEventFlags flags, bool isPaging, bool isDirectory)
    {
        var ev = new FileActivityEvent { Flags = flags };

        Assert.Equal(isPaging, ev.IsPaging);
        Assert.Equal(isDirectory, ev.IsDirectory);
    }
}
