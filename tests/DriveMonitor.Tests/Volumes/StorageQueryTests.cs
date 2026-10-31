using System.Buffers.Binary;
using System.Text;
using DriveMonitor.Core.Models;
using DriveMonitor.Core.Volumes;

namespace DriveMonitor.Tests.Volumes;

/// <summary>Parsing synthetic STORAGE_DEVICE_DESCRIPTOR buffers (IOCTL_STORAGE_QUERY_PROPERTY output).</summary>
public sealed class StorageQueryTests
{
    private const int HeaderSize = 36;
    private const int VendorIdOffsetField = 12;
    private const int ProductIdOffsetField = 16;
    private const int ProductRevisionOffsetField = 20;
    private const int SerialNumberOffsetField = 24;
    private const int BusTypeField = 28;

    [Fact]
    public void Parse_UsbStick_GivesUsbAndVendorPlusProduct()
    {
        (StorageBus bus, string? model) = StorageQuery.Parse(Descriptor(7, "SanDisk", "Ultra"));

        Assert.Equal(StorageBus.Usb, bus);
        Assert.Equal("SanDisk Ultra", model);
    }

    [Fact]
    public void Parse_NvmeWithoutVendor_GivesTheProduct()
    {
        (StorageBus bus, string? model) = StorageQuery.Parse(Descriptor(17, vendor: null, "Samsung SSD 980 PRO 1TB"));

        Assert.Equal(StorageBus.Nvme, bus);
        Assert.Equal("Samsung SSD 980 PRO 1TB", model);
    }

    [Fact]
    public void Parse_SataDisk_GivesSata()
    {
        (StorageBus bus, string? model) = StorageQuery.Parse(Descriptor(11, vendor: null, "WDC WD10EZEX-08WN4A0"));

        Assert.Equal(StorageBus.Sata, bus);
        Assert.Equal("WDC WD10EZEX-08WN4A0", model);
    }

    [Theory]
    [InlineData(0, StorageBus.Unknown)]
    [InlineData(1, StorageBus.Scsi)]
    [InlineData(3, StorageBus.Ata)]
    [InlineData(4, StorageBus.Ieee1394)]
    [InlineData(7, StorageBus.Usb)]
    [InlineData(11, StorageBus.Sata)]
    [InlineData(12, StorageBus.Sd)]
    [InlineData(13, StorageBus.Mmc)]
    [InlineData(14, StorageBus.Virtual)]
    [InlineData(17, StorageBus.Nvme)]
    [InlineData(19, StorageBus.Ufs)]
    public void Parse_ReadsTheBusTypeLittleEndianAtOffset28(int rawBus, StorageBus expected)
    {
        Assert.Equal(expected, StorageQuery.Parse(Descriptor(rawBus, "V", "P")).Bus);
    }

    [Theory]
    [InlineData(20)] // newer than DriveMonitor knows
    [InlineData(0x7F)] // BusTypeMaxReserved
    [InlineData(-1)]
    [InlineData(0x107)] // the low byte alone would read as USB
    public void Parse_UndefinedBusType_IsUnknown(int rawBus)
    {
        Assert.Equal(StorageBus.Unknown, StorageQuery.Parse(Descriptor(rawBus, "V", "P")).Bus);
    }

    [Theory]
    [InlineData("SanDisk", "Ultra", "SanDisk Ultra")]
    [InlineData("SanDisk ", "Ultra           ", "SanDisk Ultra")] // fixed-width fields are space padded
    [InlineData("SanDisk", "SanDisk 3.2Gen1", "SanDisk 3.2Gen1")] // the product already names the vendor
    [InlineData("SANDISK", "SanDisk Cruzer", "SanDisk Cruzer")]
    [InlineData("Kingston", "DataTraveler 3.0", "Kingston DataTraveler 3.0")]
    [InlineData("Generic", null, "Generic")]
    [InlineData(null, "Flash Disk", "Flash Disk")]
    [InlineData("        ", "Flash Disk", "Flash Disk")]
    [InlineData("   ", "   ", null)]
    [InlineData(null, null, null)]
    public void Parse_MergesVendorAndProduct(string? vendor, string? product, string? expected)
    {
        Assert.Equal(expected, StorageQuery.Parse(Descriptor(7, vendor, product)).Model);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Parse_ProductOffsetOutsideTheBuffer_IsIgnored(int productOffset)
    {
        byte[] buffer = Descriptor(7, "SanDisk", "Ultra");
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(ProductIdOffsetField), productOffset);

        (StorageBus bus, string? model) = StorageQuery.Parse(buffer);

        Assert.Equal(StorageBus.Usb, bus);
        Assert.Equal("SanDisk", model);
    }

    [Fact]
    public void Parse_OffsetEqualToTheLength_IsIgnored()
    {
        byte[] buffer = Descriptor(7, vendor: null, "Ultra");
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(ProductIdOffsetField), buffer.Length);

        Assert.Null(StorageQuery.Parse(buffer).Model);
    }

    [Fact]
    public void Parse_StringWithoutTerminator_RunsToTheEndOfTheBuffer()
    {
        var buffer = new byte[HeaderSize + 5];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(BusTypeField), 7);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(ProductIdOffsetField), HeaderSize);
        Encoding.ASCII.GetBytes("Ultra").CopyTo(buffer, HeaderSize);

        Assert.Equal("Ultra", StorageQuery.Parse(buffer).Model);
    }

    [Fact]
    public void Parse_ExactlyTheFixedHeader_ReadsTheBusType()
    {
        var buffer = new byte[HeaderSize];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(BusTypeField), 7);

        (StorageBus bus, string? model) = StorageQuery.Parse(buffer);

        Assert.Equal(StorageBus.Usb, bus);
        Assert.Null(model);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(28)]
    [InlineData(35)]
    public void Parse_ShorterThanTheFixedHeader_IsUnknown(int length)
    {
        byte[] buffer = Descriptor(7, "SanDisk", "Ultra");

        (StorageBus bus, string? model) = StorageQuery.Parse(buffer.AsSpan(0, length));

        Assert.Equal(StorageBus.Unknown, bus);
        Assert.Null(model);
    }

    /// <summary>
    /// Builds a STORAGE_DEVICE_DESCRIPTOR with NUL-terminated ASCII strings after the fixed
    /// header. A null string gets offset 0, as Windows reports a missing field. The revision and
    /// serial number are filled in so a parser reading the wrong offset would show it.
    /// </summary>
    private static byte[] Descriptor(int busType, string? vendor, string? product)
    {
        var strings = new List<byte>();
        int Append(string? value)
        {
            if (value is null)
                return 0;
            int offset = HeaderSize + strings.Count;
            strings.AddRange(Encoding.ASCII.GetBytes(value));
            strings.Add(0);
            return offset;
        }

        int vendorOffset = Append(vendor);
        int productOffset = Append(product);
        int revisionOffset = Append("1.00");
        int serialOffset = Append("SERIAL0123456789");

        var buffer = new byte[HeaderSize + strings.Count];
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(0), 40); // Version: sizeof(STORAGE_DEVICE_DESCRIPTOR)
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(4), buffer.Length); // Size
        buffer[10] = 1; // RemovableMedia
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(VendorIdOffsetField), vendorOffset);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(ProductIdOffsetField), productOffset);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(ProductRevisionOffsetField), revisionOffset);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(SerialNumberOffsetField), serialOffset);
        BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(BusTypeField), busType);
        strings.CopyTo(buffer, HeaderSize);
        return buffer;
    }
}
