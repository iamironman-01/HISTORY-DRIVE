using DriveMonitor.Core.Models;

namespace DriveMonitor.Tests.Models;

/// <summary>The derived properties of a drive description.</summary>
public sealed class DriveDescriptorTests
{
    [Theory]
    [InlineData(DriveKind.Removable, StorageBus.Unknown, true)]
    [InlineData(DriveKind.Removable, StorageBus.Sata, true)]
    [InlineData(DriveKind.Fixed, StorageBus.Usb, true)]
    [InlineData(DriveKind.Fixed, StorageBus.Sd, true)]
    [InlineData(DriveKind.Fixed, StorageBus.Mmc, true)]
    [InlineData(DriveKind.Fixed, StorageBus.Ieee1394, true)]
    [InlineData(DriveKind.Fixed, StorageBus.Sata, false)]
    [InlineData(DriveKind.Fixed, StorageBus.Nvme, false)]
    [InlineData(DriveKind.Optical, StorageBus.Atapi, false)]
    [InlineData(DriveKind.Network, StorageBus.Unknown, false)]
    public void IsExternal_RemovableMediaOrExternalBus(DriveKind kind, StorageBus bus, bool expected)
    {
        var drive = new DriveDescriptor { Letter = 'E', Kind = kind, Bus = bus };

        Assert.Equal(expected, drive.IsExternal);
    }

    [Theory]
    [InlineData(null, "E:")]
    [InlineData("", "E:")]
    [InlineData("   ", "E:")]
    [InlineData("MY STICK", "E: MY STICK")]
    public void DisplayName_IsTheLetterPlusTheLabel(string? label, string expected)
    {
        var drive = new DriveDescriptor { Letter = 'E', Label = label };

        Assert.Equal(expected, drive.DisplayName);
    }

    [Fact]
    public void RootPath_IsTheDriveRoot()
    {
        Assert.Equal(@"E:\", new DriveDescriptor { Letter = 'E' }.RootPath);
    }
}
