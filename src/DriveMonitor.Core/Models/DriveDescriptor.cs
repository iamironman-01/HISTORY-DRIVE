namespace DriveMonitor.Core.Models;

public enum DriveKind : byte
{
    Unknown,
    Removable,
    Fixed,
    Network,
    Optical,
    Ram,
}

/// <summary>Mirrors the Windows STORAGE_BUS_TYPE values that matter for display.</summary>
public enum StorageBus : byte
{
    Unknown = 0,
    Scsi = 1,
    Atapi = 2,
    Ata = 3,
    Ieee1394 = 4,
    Ssa = 5,
    Fibre = 6,
    Usb = 7,
    Raid = 8,
    IScsi = 9,
    Sas = 10,
    Sata = 11,
    Sd = 12,
    Mmc = 13,
    Virtual = 14,
    FileBackedVirtual = 15,
    Spaces = 16,
    Nvme = 17,
    Scm = 18,
    Ufs = 19,
}

/// <summary>A drive letter and what is known about the volume behind it.</summary>
public sealed record DriveDescriptor
{
    public required char Letter { get; init; }

    public string RootPath => $"{Letter}:\\";

    public DriveKind Kind { get; init; }

    public StorageBus Bus { get; init; }

    public bool IsReady { get; init; }

    public string? Label { get; init; }

    public string? FileSystem { get; init; }

    public long TotalBytes { get; init; }

    public long FreeBytes { get; init; }

    /// <summary>Kernel device path, e.g. <c>\Device\HarddiskVolume5</c>.</summary>
    public string? DevicePath { get; init; }

    /// <summary>Vendor and product id reported by the device, e.g. "SanDisk Ultra".</summary>
    public string? Model { get; init; }

    public bool IsSystemDrive { get; init; }

    /// <summary>
    /// True for pendrives, memory cards and external disks: removable media, or a disk
    /// attached over USB / SD / MMC / FireWire.
    /// </summary>
    public bool IsExternal =>
        Kind == DriveKind.Removable || Bus is StorageBus.Usb or StorageBus.Sd or StorageBus.Mmc or StorageBus.Ieee1394;

    public string DisplayName => string.IsNullOrWhiteSpace(Label) ? $"{Letter}:" : $"{Letter}: {Label}";
}
