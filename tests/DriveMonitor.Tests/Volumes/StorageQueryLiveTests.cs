using DriveMonitor.Core.Interop;
using DriveMonitor.Core.Models;
using DriveMonitor.Core.Volumes;

namespace DriveMonitor.Tests.Volumes;

/// <summary>IOCTL_STORAGE_QUERY_PROPERTY against real devices; it needs no administrator rights.</summary>
public sealed class StorageQueryLiveTests
{
    [Fact]
    public void TryGetBusAndModel_OnTheWindowsDrive_ReportsItsBus()
    {
        (StorageBus bus, _) = StorageQuery.TryGetBusAndModel(DriveCatalog.SystemDriveLetter);

        Assert.NotEqual(StorageBus.Unknown, bus);
    }

    [Fact]
    public void TryGetBusAndModel_OnAnUnusedLetter_ReturnsUnknownWithoutThrowing()
    {
        char unused = Enumerable.Range('A', 26)
            .Select(i => (char)i)
            .Reverse()
            .First(letter => !Directory.Exists($@"{letter}:\") && NativeMethods.TryQueryDosDevice(letter) is null);

        (StorageBus bus, string? model) = StorageQuery.TryGetBusAndModel(unused);

        Assert.Equal(StorageBus.Unknown, bus);
        Assert.Null(model);
    }
}
