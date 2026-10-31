using System.Text;
using DriveMonitor.Core.Tracing;

namespace DriveMonitor.Tests.Tracing;

/// <summary>Parsing the undocumented FileIo_PathOperation payload (rename / hard-link targets).</summary>
public sealed class PathOperationParserTests
{
    private const string KernelPath = @"\Device\HarddiskVolume5\photos\new name.jpg";

    [Theory]
    [InlineData(8)]
    [InlineData(4)]
    public void Parse_ReadsTheRequestAddressAndTheKernelPath(int pointerSize)
    {
        byte[] payload = Payload(pointerSize, irp: 0x1234_5678, KernelPath);

        Assert.True(EtwFileIoSource.TryParsePathOperation(payload, pointerSize, out ulong irp, out string path));
        Assert.Equal(0x1234_5678UL, irp);
        Assert.Equal(KernelPath, path);
    }

    [Fact]
    public void Parse_KeepsTheFull64BitAddress()
    {
        byte[] payload = Payload(8, irp: 0xFFFF_9A00_1234_5600, KernelPath);

        Assert.True(EtwFileIoSource.TryParsePathOperation(payload, 8, out ulong irp, out _));
        Assert.Equal(0xFFFF_9A00_1234_5600UL, irp);
    }

    [Fact]
    public void Parse_NameWithoutTerminator_UsesTheRestOfThePayload()
    {
        byte[] payload = Payload(8, irp: 1, KernelPath, terminate: false);

        Assert.True(EtwFileIoSource.TryParsePathOperation(payload, 8, out _, out string path));
        Assert.Equal(KernelPath, path);
    }

    [Fact]
    public void Parse_StopsAtTheTerminator()
    {
        byte[] payload = [.. Payload(8, irp: 1, KernelPath), .. Encoding.Unicode.GetBytes("garbage")];

        Assert.True(EtwFileIoSource.TryParsePathOperation(payload, 8, out _, out string path));
        Assert.Equal(KernelPath, path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative.txt")]
    [InlineData("E:\\x.txt")]
    public void Parse_NameThatIsNotAKernelPath_IsRejected(string name)
    {
        byte[] payload = Payload(8, irp: 1, name);

        Assert.False(EtwFileIoSource.TryParsePathOperation(payload, 8, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(16)]
    public void Parse_UnexpectedPointerSize_IsRejected(int pointerSize)
    {
        byte[] payload = Payload(8, irp: 1, KernelPath);

        Assert.False(EtwFileIoSource.TryParsePathOperation(payload, pointerSize, out _, out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(39)] // 4 pointers + TTID + InfoClass = 40 bytes, plus at least one character
    [InlineData(41)]
    public void Parse_TooShortPayload_IsRejected(int length)
    {
        byte[] payload = Payload(8, irp: 1, KernelPath)[..length];

        Assert.False(EtwFileIoSource.TryParsePathOperation(payload, 8, out _, out _));
    }

    /// <summary>IrpPtr, FileObject, FileKey, ExtraInfo (pointer-sized), TTID (u32), InfoClass (u32), FileName (UTF-16).</summary>
    private static byte[] Payload(int pointerSize, ulong irp, string name, bool terminate = true)
    {
        var bytes = new List<byte>();
        void Pointer(ulong value) =>
            bytes.AddRange(pointerSize == 8 ? BitConverter.GetBytes(value) : BitConverter.GetBytes((uint)value));

        Pointer(irp);
        Pointer(0xAAAA); // FileObject
        Pointer(0xBBBB); // FileKey
        Pointer(0xCCCC); // ExtraInfo
        bytes.AddRange(BitConverter.GetBytes(4321)); // TTID
        bytes.AddRange(BitConverter.GetBytes(65)); // InfoClass: FileRenameInformationEx
        bytes.AddRange(Encoding.Unicode.GetBytes(name));
        if (terminate)
            bytes.AddRange(new byte[2]);
        return bytes.ToArray();
    }
}
