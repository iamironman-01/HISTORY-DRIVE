using System.Buffers.Binary;
using System.Text;
using DriveMonitor.Core.Interop;
using DriveMonitor.Core.Models;
using Microsoft.Win32.SafeHandles;

namespace DriveMonitor.Core.Volumes;

/// <summary>Reads the bus type and model of the disk behind a drive letter (IOCTL_STORAGE_QUERY_PROPERTY).</summary>
internal static class StorageQuery
{
    // STORAGE_DEVICE_DESCRIPTOR field offsets.
    private const int VendorIdOffsetField = 12;
    private const int ProductIdOffsetField = 16;
    private const int BusTypeField = 28;
    private const int MinimumDescriptorSize = 36;

    internal static (StorageBus Bus, string? Model) TryGetBusAndModel(char driveLetter)
    {
        try
        {
            // Zero access rights: enough for this IOCTL, needs no elevation and never blocks other users of the volume.
            using SafeFileHandle handle = NativeMethods.CreateFileW(
                $@"\\.\{driveLetter}:",
                0,
                NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
                IntPtr.Zero,
                NativeMethods.OPEN_EXISTING,
                0,
                IntPtr.Zero);
            if (handle.IsInvalid)
                return (StorageBus.Unknown, null);

            // STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceProperty (0), QueryType = PropertyStandardQuery (0) }.
            var query = new byte[12];
            var output = new byte[1024];
            if (!NativeMethods.DeviceIoControl(handle, NativeMethods.IOCTL_STORAGE_QUERY_PROPERTY, query, query.Length, output, output.Length, out int returned, IntPtr.Zero)
                || returned < MinimumDescriptorSize)
            {
                return (StorageBus.Unknown, null);
            }

            return Parse(output.AsSpan(0, returned));
        }
        catch (Exception)
        {
            return (StorageBus.Unknown, null);
        }
    }

    /// <summary>Parses a STORAGE_DEVICE_DESCRIPTOR buffer.</summary>
    internal static (StorageBus Bus, string? Model) Parse(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length < MinimumDescriptorSize)
            return (StorageBus.Unknown, null);

        int rawBus = BinaryPrimitives.ReadInt32LittleEndian(descriptor[BusTypeField..]);
        StorageBus bus = rawBus is >= 0 and <= byte.MaxValue && Enum.IsDefined((StorageBus)rawBus)
            ? (StorageBus)rawBus
            : StorageBus.Unknown;

        string? vendor = ReadAnsiString(descriptor, BinaryPrimitives.ReadInt32LittleEndian(descriptor[VendorIdOffsetField..]));
        string? product = ReadAnsiString(descriptor, BinaryPrimitives.ReadInt32LittleEndian(descriptor[ProductIdOffsetField..]));

        string? model = (vendor, product) switch
        {
            (null, null) => null,
            (null, _) => product,
            (_, null) => vendor,
            _ when product!.StartsWith(vendor!, StringComparison.OrdinalIgnoreCase) => product,
            _ => $"{vendor} {product}",
        };
        return (bus, model);
    }

    private static string? ReadAnsiString(ReadOnlySpan<byte> buffer, int offset)
    {
        if (offset <= 0 || offset >= buffer.Length)
            return null;
        ReadOnlySpan<byte> tail = buffer[offset..];
        int end = tail.IndexOf((byte)0);
        string value = Encoding.ASCII.GetString(end < 0 ? tail : tail[..end]).Trim();
        return value.Length == 0 ? null : value;
    }
}
