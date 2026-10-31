using DriveMonitor.Core.Interop;
using DriveMonitor.Core.Models;

namespace DriveMonitor.Core.Volumes;

/// <summary>Enumerates drive letters with their type, bus, size and kernel device name.</summary>
public static class DriveCatalog
{
    public static char SystemDriveLetter { get; } =
        char.ToUpperInvariant(Path.GetPathRoot(Environment.SystemDirectory)?.FirstOrDefault() ?? 'C');

    /// <summary>
    /// Lists every drive letter. Can take a moment for slow or sleeping devices, so call it
    /// off the UI thread.
    /// </summary>
    public static IReadOnlyList<DriveDescriptor> Enumerate(VolumeMapper? mapper = null)
    {
        var drives = new List<DriveDescriptor>();
        foreach (DriveInfo info in DriveInfo.GetDrives())
        {
            char letter = char.ToUpperInvariant(info.Name[0]);
            if (letter is < 'A' or > 'Z')
                continue;
            drives.Add(Describe(info, letter, mapper));
        }

        return drives;
    }

    private static DriveDescriptor Describe(DriveInfo info, char letter, VolumeMapper? mapper)
    {
        DriveKind kind = info.DriveType switch
        {
            DriveType.Removable => DriveKind.Removable,
            DriveType.Fixed => DriveKind.Fixed,
            DriveType.Network => DriveKind.Network,
            DriveType.CDRom => DriveKind.Optical,
            DriveType.Ram => DriveKind.Ram,
            _ => DriveKind.Unknown,
        };

        bool ready = false;
        string? label = null, fileSystem = null;
        long total = 0, free = 0;
        try
        {
            ready = info.IsReady;
            if (ready)
            {
                label = info.VolumeLabel;
                fileSystem = info.DriveFormat;
                total = info.TotalSize;
                free = info.AvailableFreeSpace;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Card reader without media, device being removed, locked BitLocker volume...
        }

        (StorageBus bus, string? model) = kind is DriveKind.Network or DriveKind.Unknown
            ? (StorageBus.Unknown, null)
            : StorageQuery.TryGetBusAndModel(letter);

        return new DriveDescriptor
        {
            Letter = letter,
            Kind = kind,
            Bus = bus,
            IsReady = ready,
            Label = string.IsNullOrWhiteSpace(label) ? null : label,
            FileSystem = fileSystem,
            TotalBytes = total,
            FreeBytes = free,
            DevicePath = mapper?.GetDevicePath(letter) ?? NativeMethods.TryQueryDosDevice(letter),
            Model = model,
            IsSystemDrive = letter == SystemDriveLetter,
        };
    }

    /// <summary>
    /// Default monitoring choice for a drive: everything except the Windows drive and network
    /// shares (pendrives, memory cards, secondary internal and external disks, optical media).
    /// </summary>
    public static bool IsMonitoredByDefault(DriveDescriptor drive) =>
        !drive.IsSystemDrive && drive.Kind is not (DriveKind.Network or DriveKind.Unknown);
}
