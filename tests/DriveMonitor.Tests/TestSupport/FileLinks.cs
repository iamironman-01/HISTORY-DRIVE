using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using DriveMonitor.Core.Interop;
using Microsoft.Win32.SafeHandles;

namespace DriveMonitor.Tests.TestSupport;

/// <summary>Plants the links a standard user could plant: junctions, hard links and, where Windows allows it, symbolic links.</summary>
internal static class FileLinks
{
    private const uint GenericWrite = 0x4000_0000;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FsctlSetReparsePoint = 0x0009_00A4;
    private const uint IoReparseTagMountPoint = 0xA000_0003;

    /// <summary>
    /// Makes <paramref name="junction"/> a junction to the folder <paramref name="target"/>. Needs no privilege. An
    /// existing empty folder becomes one too, even while another handle that shares writing holds it open.
    /// </summary>
    /// <returns>Removes the junction again, which a recursive delete cannot do without administrator rights.</returns>
    public static IDisposable CreateJunction(string junction, string target)
    {
        string printName = Path.GetFullPath(target);
        byte[] substituteName = Encoding.Unicode.GetBytes(@"\??\" + printName);
        byte[] print = Encoding.Unicode.GetBytes(printName);

        // REPARSE_DATA_BUFFER with a MountPointReparseBuffer: both names, each followed by a NUL.
        int pathBufferLength = substituteName.Length + 2 + print.Length + 2;
        var data = new byte[16 + pathBufferLength];
        BitConverter.TryWriteBytes(data.AsSpan(0), IoReparseTagMountPoint);
        BitConverter.TryWriteBytes(data.AsSpan(4), (ushort)(8 + pathBufferLength));
        BitConverter.TryWriteBytes(data.AsSpan(8), (ushort)0);
        BitConverter.TryWriteBytes(data.AsSpan(10), (ushort)substituteName.Length);
        BitConverter.TryWriteBytes(data.AsSpan(12), (ushort)(substituteName.Length + 2));
        BitConverter.TryWriteBytes(data.AsSpan(14), (ushort)print.Length);
        substituteName.CopyTo(data, 16);
        print.CopyTo(data, 16 + substituteName.Length + 2);

        Directory.CreateDirectory(junction);
        using SafeFileHandle handle = NativeMethods.CreateFileW(
            junction, GenericWrite, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE, IntPtr.Zero,
            NativeMethods.OPEN_EXISTING, FileFlagBackupSemantics | FileFlagOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (!NativeMethods.DeviceIoControl(handle, FsctlSetReparsePoint, data, data.Length, null!, 0, out _, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        return new Junction(junction);
    }

    /// <summary>Gives <paramref name="existingFile"/> a second name. Needs no privilege.</summary>
    public static void CreateHardLink(string link, string existingFile)
    {
        if (!CreateHardLinkW(link, existingFile, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
    }

    /// <summary>False when Windows refuses: a symbolic link needs administrator rights or Developer Mode.</summary>
    public static bool TryCreateFileSymbolicLink(string link, string targetFile)
    {
        try
        {
            File.CreateSymbolicLink(link, targetFile);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// False when Windows refuses, as for <see cref="TryCreateFileSymbolicLink"/>. A recursive delete removes the
    /// link itself, without administrator rights and without following it.
    /// </summary>
    public static bool TryCreateDirectorySymbolicLink(string link, string targetFolder)
    {
        try
        {
            Directory.CreateSymbolicLink(link, targetFolder);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string fileName, string existingFileName, IntPtr securityAttributes);

    private sealed class Junction(string path) : IDisposable
    {
        // Deleting the junction itself (not recursively) leaves its target alone.
        public void Dispose() => Directory.Delete(path);
    }
}
