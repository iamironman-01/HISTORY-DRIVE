using DriveMonitor.Core.Models;
using DriveMonitor.Core.Volumes;

namespace DriveMonitor.Tests.Volumes;

/// <summary>The default monitoring choice; drive enumeration itself needs real drives and is not tested here.</summary>
public sealed class DriveCatalogTests
{
    [Theory]
    [InlineData(DriveKind.Removable, false, true)]
    [InlineData(DriveKind.Fixed, false, true)]
    [InlineData(DriveKind.Optical, false, true)]
    [InlineData(DriveKind.Ram, false, true)]
    [InlineData(DriveKind.Fixed, true, false)]
    [InlineData(DriveKind.Removable, true, false)]
    [InlineData(DriveKind.Network, false, false)]
    [InlineData(DriveKind.Unknown, false, false)]
    public void IsMonitoredByDefault_EverythingButTheWindowsDriveAndNetworkShares(DriveKind kind, bool isSystemDrive, bool expected)
    {
        var drive = new DriveDescriptor { Letter = 'E', Kind = kind, IsSystemDrive = isSystemDrive };

        Assert.Equal(expected, DriveCatalog.IsMonitoredByDefault(drive));
    }

    [Fact]
    public void SystemDriveLetter_IsTheUpperCaseLetterOfTheWindowsFolder()
    {
        Assert.Equal(char.ToUpperInvariant(Environment.SystemDirectory[0]), DriveCatalog.SystemDriveLetter);
    }
}
