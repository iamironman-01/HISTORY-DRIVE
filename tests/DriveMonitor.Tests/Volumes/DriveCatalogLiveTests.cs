using DriveMonitor.Core.Models;
using DriveMonitor.Core.Volumes;

namespace DriveMonitor.Tests.Volumes;

/// <summary>
/// Enumerates the real drives of the machine running the tests. Needs no administrator rights,
/// and every Windows machine, CI runners included, has at least the Windows drive.
/// </summary>
public sealed class DriveCatalogLiveTests
{
    [Fact]
    public void Enumerate_DescribesTheWindowsDrive()
    {
        DriveDescriptor system = Assert.Single(DriveCatalog.Enumerate(), d => d.IsSystemDrive);

        Assert.Equal(DriveCatalog.SystemDriveLetter, system.Letter);
        Assert.Equal(DriveKind.Fixed, system.Kind);
        Assert.True(system.IsReady);
        Assert.True(system.TotalBytes > 0);
        Assert.InRange(system.FreeBytes, 0, system.TotalBytes);
        Assert.False(string.IsNullOrEmpty(system.FileSystem));
        Assert.StartsWith(@"\Device\", system.DevicePath);
        Assert.False(DriveCatalog.IsMonitoredByDefault(system));
    }

    [Fact]
    public void Enumerate_ListsEachLetterOnceInUpperCase()
    {
        IReadOnlyList<DriveDescriptor> drives = DriveCatalog.Enumerate();

        Assert.NotEmpty(drives);
        Assert.All(drives, d => Assert.InRange(d.Letter, 'A', 'Z'));
        Assert.Equal(drives.Count, drives.Select(d => d.Letter).Distinct().Count());
    }

    [Fact]
    public void Enumerate_WithAVolumeMapper_TakesTheDevicePathFromIt()
    {
        var mapper = new VolumeMapper(letter => letter == DriveCatalog.SystemDriveLetter ? @"\Device\HarddiskVolumeTest" : null);

        DriveDescriptor system = Assert.Single(DriveCatalog.Enumerate(mapper), d => d.IsSystemDrive);

        Assert.Equal(@"\Device\HarddiskVolumeTest", system.DevicePath);
    }
}
