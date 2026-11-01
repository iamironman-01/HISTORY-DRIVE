using System.ComponentModel;
using System.Runtime.InteropServices;
using DriveMonitor.Core.Interop;
using Microsoft.Win32.SafeHandles;

namespace DriveMonitor.Core.IO;

/// <summary>
/// A folder whose files are opened, renamed and deleted without following links: junctions, symbolic links
/// and other reparse points are refused, never followed.
/// </summary>
/// <remarks>
/// An elevated process that writes into a folder a standard user controls, such as
/// <c>%LOCALAPPDATA%\DriveMonitor</c>, must not follow a link planted there. A junction on the folder (for
/// example to <c>\RPC Control</c>, where object-manager symbolic links can name any file) or a link on a file
/// would redirect its writes, truncations, renames and deletes to files only administrators may change.
/// Checking a path and then using it leaves a race, so everything is checked on open handles instead:
/// <see cref="Open"/> checks the folder's handle and keeps it, which also keeps the folder from being
/// renamed or deleted and replaced; files are opened relative to that handle, so the folder's path is never
/// looked up again, and with FILE_OPEN_REPARSE_POINT, so a link is opened as itself and refused; renames
/// stay in the folder, and deletes go through the handle of the file that was checked.
/// <para>
/// <see cref="Open"/> checks only the folder itself and follows a link on a folder above it. Where the user
/// also controls the folders above, <see cref="OpenBelow"/> starts from a root the caller trusts, such as
/// <c>%LOCALAPPDATA%</c>, and opens (or creates) each folder below it relative to its parent's handle in the
/// same way, so no link below the root is followed.
/// </para>
/// <para>
/// A file system without reparse points may reject FILE_OPEN_REPARSE_POINT as an invalid parameter, as
/// DeleteFile also allows for. The open is then repeated without it: there is no link to open as itself, and
/// the handle is still checked for being a folder or having other names.
/// </para>
/// </remarks>
public sealed class LinkSafeFolder : IDisposable
{
    private const uint Delete = 0x0001_0000;
    private const uint Synchronize = 0x0010_0000;
    private const uint GenericRead = 0x8000_0000;
    private const uint GenericWrite = 0x4000_0000;
    private const uint FileListDirectory = 0x1;
    private const uint FileReadAttributes = 0x80;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeReparsePoint = 0x400;

    // NtCreateFile
    private const uint FileOpenDisposition = 1;
    private const uint FileCreateDisposition = 2;
    private const uint FileOpenIfDisposition = 3;
    private const uint FileDirectoryFile = 0x1;
    private const uint FileSynchronousIoNonAlert = 0x20;
    private const uint FileNonDirectoryFile = 0x40;
    private const uint FileOpenReparsePoint = 0x0020_0000;
    private const uint ObjCaseInsensitive = 0x40;
    private const int FileRenameInformationClass = 10;
    private const int FileDispositionInformationClass = 13;
    private const int FileDispositionInformationExClass = 64;
    private const uint FileDispositionDelete = 0x1;
    private const uint FileDispositionPosixSemantics = 0x2;
    private const int StatusInvalidParameter = unchecked((int)0xC000_000D);
    private const int StatusObjectNameNotFound = unchecked((int)0xC000_0034);
    private const int StatusFileIsADirectory = unchecked((int)0xC000_00BA);
    private const int StatusNotADirectory = unchecked((int)0xC000_0103);
    private const int ErrorAccessDenied = 5;
    private const int ErrorInvalidParameter = 87;

    /// <summary>The longest path CreateFileW takes without the \\?\ prefix, as .NET's own file APIs count it.</summary>
    private const int MaxShortPath = 260;

    private readonly SafeFileHandle _handle;

    private LinkSafeFolder(string fullPath, SafeFileHandle handle)
    {
        FullPath = fullPath;
        _handle = handle;
    }

    public string FullPath { get; }

    /// <summary>Opens an existing folder that is not itself a link.</summary>
    /// <exception cref="IOException">The folder is a junction, symbolic link or other reparse point, is not a folder, or cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the folder is denied.</exception>
    public static LinkSafeFolder Open(string path)
    {
        string fullPath = Path.GetFullPath(path);

        // FILE_FLAG_OPEN_REPARSE_POINT opens a link itself rather than what it points to.
        SafeFileHandle handle = OpenFolder(fullPath, FileFlagOpenReparsePoint, out int error);
        if (handle.IsInvalid && error == ErrorInvalidParameter)
        {
            // A file system without reparse points may reject the flag; see the remarks.
            handle.Dispose();
            handle = OpenFolder(fullPath, 0, out error);
        }

        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw ErrorFromWin32(error, fullPath);
        }

        try
        {
            ThrowUnlessPlainFolder(handle, fullPath);
            return new LinkSafeFolder(fullPath, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Opens a folder below a trusted root, and creates the missing folders on the way when asked, without
    /// following any link below the root.
    /// </summary>
    /// <remarks>
    /// The root is opened by its path, following links: the caller trusts whatever it is (an administrator may
    /// have redirected <c>%LOCALAPPDATA%</c>, say), so it only has to be a folder. Each folder below it is then
    /// opened, or created, relative to its parent's handle with FILE_OPEN_REPARSE_POINT, and refused as
    /// <see cref="Open"/> refuses its folder when it is a junction, symbolic link or other reparse point. Every
    /// parent stays open, so that it cannot be renamed and replaced, until its child is open. The folder returned
    /// is held and used like one from <see cref="Open"/>.
    /// </remarks>
    /// <param name="trustedRoot">An existing folder whose path the caller trusts, such as <c>%LOCALAPPDATA%</c>.</param>
    /// <param name="path">The folder: the root or a folder below it, as a full path or relative to the root.</param>
    /// <param name="create">Creates the folders below the root that do not exist.</param>
    /// <exception cref="ArgumentException"><paramref name="path"/> is neither the root nor below it, or has a name that is not a folder name.</exception>
    /// <exception cref="IOException">
    /// The root is not a folder or cannot be opened; or a folder below it is a junction, symbolic link or other
    /// reparse point, is not a folder, is missing and not to be created, or cannot be opened or created.
    /// </exception>
    /// <exception cref="UnauthorizedAccessException">Access to a folder on the way is denied.</exception>
    public static LinkSafeFolder OpenBelow(string trustedRoot, string path, bool create = false)
    {
        string root = FullFolderPath(trustedRoot);
        string fullPath = FullFolderPath(Path.Combine(root, path));
        string[] names = NamesBelow(root, fullPath)
            ?? throw new ArgumentException($"'{fullPath}' is not in '{root}'.", nameof(path));
        foreach (string name in names)
        {
            if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException($"'{name}' in '{fullPath}' is not a folder name.", nameof(path));
        }

        // Trusted: a link on the root's own path is followed.
        SafeFileHandle handle = OpenFolder(root, 0, out int error);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            throw ErrorFromWin32(error, root);
        }

        LinkSafeFolder folder;
        try
        {
            if ((Query(handle, root).FileAttributes & FileAttributeDirectory) == 0)
                throw new IOException($"'{root}' is not a folder.");
            folder = new LinkSafeFolder(root, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }

        try
        {
            foreach (string name in names)
            {
                LinkSafeFolder child = folder.OpenSubfolder(name, create);
                folder.Dispose();
                folder = child;
            }

            return folder;
        }
        catch
        {
            folder.Dispose();
            throw;
        }
    }

    /// <summary>Opens or creates a plain file directly in the folder.</summary>
    /// <param name="name">A file name without any folder.</param>
    /// <param name="mode">
    /// <see cref="FileMode.Open"/>, <see cref="FileMode.OpenOrCreate"/> or <see cref="FileMode.CreateNew"/>. The
    /// other modes would truncate an existing file before it is checked; call <see cref="FileStream.SetLength"/>.
    /// </param>
    /// <param name="access">Read, write or both.</param>
    /// <param name="share">What others may do with the file while it is open.</param>
    /// <param name="allowRename">Also opens it for <see cref="Rename"/>.</param>
    /// <param name="options"><see cref="FileOptions.None"/>, or <see cref="FileOptions.Asynchronous"/> for a stream whose writes can be cancelled.</param>
    /// <returns>An unbuffered stream at the start of the file.</returns>
    /// <exception cref="IOException">The name is a link, a folder or a file with other names (hard links), or the file cannot be opened.</exception>
    /// <exception cref="UnauthorizedAccessException">Access to the file is denied.</exception>
    public FileStream OpenFile(string name, FileMode mode, FileAccess access, FileShare share, bool allowRename = false, FileOptions options = FileOptions.None)
    {
        ValidateName(name);
        uint disposition = mode switch
        {
            FileMode.Open => FileOpenDisposition,
            FileMode.OpenOrCreate => FileOpenIfDisposition,
            FileMode.CreateNew => FileCreateDisposition,
            _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Only Open, OpenOrCreate and CreateNew: the others truncate the file before it is checked."),
        };
        if ((options & ~FileOptions.Asynchronous) != 0)
            throw new ArgumentOutOfRangeException(nameof(options), options, "Only None and Asynchronous.");
        bool asynchronous = options == FileOptions.Asynchronous;
        uint desiredAccess = Synchronize | FileReadAttributes
            | ((access & FileAccess.Read) != 0 ? GenericRead : 0)
            | ((access & FileAccess.Write) != 0 ? GenericWrite : 0)
            | (allowRename ? Delete : 0);

        // Without FILE_SYNCHRONOUS_IO_NONALERT the handle does overlapped I/O, which FileStream can cancel.
        string fullPath = Path.Combine(FullPath, name);
        int status = OpenRelative(
            name, desiredAccess, share, disposition,
            FileNonDirectoryFile | (asynchronous ? 0 : FileSynchronousIoNonAlert) | FileOpenReparsePoint, out SafeFileHandle handle);
        if (status < 0)
        {
            handle.Dispose();

            // A junction on the name ends here too: it is a folder, opened as itself.
            if (status == StatusFileIsADirectory)
                throw new IOException($"'{fullPath}' is a folder or a junction, not a file.");
            throw ErrorFromNtStatus(status, fullPath);
        }

        try
        {
            ThrowUnlessPlainFile(handle, fullPath);
            return new FileStream(handle, access, bufferSize: 0, asynchronous);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Deletes a plain file directly in the folder, through a handle to the file it checked; a link of that
    /// name is refused, not deleted.
    /// </summary>
    /// <param name="name">A file name without any folder.</param>
    /// <returns>False when nothing has that name.</returns>
    /// <exception cref="IOException">The name is a link, a folder or a file with other names (hard links), or the file is in use or cannot be deleted.</exception>
    /// <exception cref="UnauthorizedAccessException">The file is read-only, or access to it is denied.</exception>
    public bool DeleteFile(string name)
    {
        ValidateName(name);
        string fullPath = Path.Combine(FullPath, name);

        // Sharing all access, as DeleteFile does: a program that has the file open without FILE_SHARE_DELETE
        // still makes this fail.
        int status = OpenRelative(
            name, Delete | FileReadAttributes | Synchronize, FileShare.ReadWrite | FileShare.Delete, FileOpenDisposition,
            FileNonDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint, out SafeFileHandle handle);
        using (handle)
        {
            if (status == StatusObjectNameNotFound)
                return false;
            if (status == StatusFileIsADirectory)
                throw new IOException($"'{fullPath}' is a folder or a junction, not a file.");
            if (status < 0)
                throw ErrorFromNtStatus(status, fullPath);

            ThrowUnlessPlainFile(handle, fullPath);

            // POSIX semantics remove the name at once, even while another program has the file open, as
            // DeleteFile does where the file system offers them. One that does not (FAT, exFAT, many network
            // servers) rejects the class or the flag, each with its own status; the classic disposition then
            // deletes the file when its last handle closes, and reports any real reason not to.
            uint flags = FileDispositionDelete | FileDispositionPosixSemantics;
            if (NtSetInformationFile(handle, out _, ref flags, sizeof(uint), FileDispositionInformationExClass) < 0)
            {
                byte deleteFile = 1;
                status = NtSetInformationFile(handle, out _, ref deleteFile, sizeof(byte), FileDispositionInformationClass);
                if (status < 0)
                    throw ErrorFromNtStatus(status, fullPath);
            }

            return true;
        }
    }

    /// <summary>
    /// Renames a file opened by <see cref="OpenFile"/> with <c>allowRename</c> to <paramref name="newName"/> in
    /// this folder, replacing a plain file of that name.
    /// </summary>
    /// <exception cref="IOException"><paramref name="newName"/> is a link, a folder or a file with other names, or the rename failed.</exception>
    public void Rename(FileStream file, string newName)
    {
        ArgumentNullException.ThrowIfNull(file);
        ValidateName(newName);
        string fullPath = Path.Combine(FullPath, newName);

        // The rename itself would replace a link, not follow it; refusing one keeps the rule simple.
        int status = OpenRelative(
            newName, FileReadAttributes | Synchronize, FileShare.ReadWrite | FileShare.Delete, FileOpenDisposition,
            FileSynchronousIoNonAlert | FileOpenReparsePoint, out SafeFileHandle existing);
        using (existing)
        {
            if (status >= 0)
                ThrowUnlessPlainFile(existing, fullPath);
            else if (status != StatusObjectNameNotFound)
                throw ErrorFromNtStatus(status, fullPath);
        }

        // FILE_RENAME_INFORMATION, with the new name relative to the folder's handle. Its declared size
        // (FileName[1] and padding included) is the least NtSetInformationFile accepts.
        int nameOffset = (int)Marshal.OffsetOf<RenameInformation>(nameof(RenameInformation.FileNameLength)) + sizeof(uint);
        int declaredSize = (nameOffset + sizeof(char) + IntPtr.Size - 1) / IntPtr.Size * IntPtr.Size;
        int length = Math.Max(nameOffset + newName.Length * sizeof(char), declaredSize);
        IntPtr buffer = Marshal.AllocHGlobal(length);
        bool addedReference = false;
        try
        {
            _handle.DangerousAddRef(ref addedReference);
            var information = new RenameInformation
            {
                ReplaceIfExists = 1,
                RootDirectory = _handle.DangerousGetHandle(),
                FileNameLength = (uint)(newName.Length * sizeof(char)),
            };
            Marshal.StructureToPtr(information, buffer, fDeleteOld: false);
            Marshal.Copy(newName.ToCharArray(), 0, buffer + nameOffset, newName.Length);
            status = NtSetInformationFile(file.SafeFileHandle, out _, buffer, (uint)length, FileRenameInformationClass);
            if (status < 0)
                throw ErrorFromNtStatus(status, fullPath);
        }
        finally
        {
            if (addedReference)
                _handle.DangerousRelease();
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>
    /// Whether <paramref name="path"/>, as a full path or relative to the root, is below
    /// <paramref name="trustedRoot"/>: in it or in a folder below it. The root itself is not below itself.
    /// </summary>
    internal static bool IsBelow(string trustedRoot, string path)
    {
        string root = FullFolderPath(trustedRoot);
        return NamesBelow(root, FullFolderPath(Path.Combine(root, path))) is { Length: > 0 };
    }

    private static void ValidateName(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException($"'{name}' is not a file name.", nameof(name));
    }

    private static void ThrowUnlessPlainFile(SafeFileHandle handle, string fullPath)
    {
        ByHandleFileInformation information = Query(handle, fullPath);
        if ((information.FileAttributes & FileAttributeReparsePoint) != 0)
            throw new IOException($"'{fullPath}' is a junction, symbolic link or other reparse point; it is not followed.");
        if ((information.FileAttributes & FileAttributeDirectory) != 0)
            throw new IOException($"'{fullPath}' is a folder, not a file.");
        if (information.NumberOfLinks != 1)
            throw new IOException($"'{fullPath}' has other names (hard links); it is not used.");
    }

    private static void ThrowUnlessPlainFolder(SafeFileHandle handle, string fullPath)
    {
        uint attributes = Query(handle, fullPath).FileAttributes;
        if ((attributes & FileAttributeReparsePoint) != 0)
            throw new IOException($"'{fullPath}' is a junction, symbolic link or other reparse point; it is not followed.");
        if ((attributes & FileAttributeDirectory) == 0)
            throw new IOException($"'{fullPath}' is not a folder.");
    }

    private static ByHandleFileInformation Query(SafeFileHandle handle, string fullPath)
    {
        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation information))
            throw ErrorFromWin32(Marshal.GetLastPInvokeError(), fullPath);
        return information;
    }

    /// <summary>
    /// Opens the folder to list it. Without FILE_SHARE_DELETE nobody can rename or delete the folder while it is
    /// open; that takes a handle with data access (FILE_LIST_DIRECTORY), as one without any is left out of
    /// sharing checks.
    /// </summary>
    private static SafeFileHandle OpenFolder(string fullPath, uint flags, out int error)
    {
        SafeFileHandle handle = NativeMethods.CreateFileW(
            ExtendedPath(fullPath), FileListDirectory | FileReadAttributes | Synchronize, NativeMethods.FILE_SHARE_READ | NativeMethods.FILE_SHARE_WRITE,
            IntPtr.Zero, NativeMethods.OPEN_EXISTING, FileFlagBackupSemantics | flags, IntPtr.Zero);
        error = handle.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        return handle;
    }

    /// <summary>
    /// A full path with the \\?\ prefix when it is too long for CreateFileW without one, as .NET's own file APIs
    /// add it; device paths stay as they are.
    /// </summary>
    private static string ExtendedPath(string fullPath)
    {
        if (fullPath.Length < MaxShortPath
            || fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)
            || fullPath.StartsWith(@"\\.\", StringComparison.Ordinal)
            || fullPath.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        return fullPath.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + fullPath[2..] : @"\\?\" + fullPath;
    }

    /// <summary>A full path without a separator at its end, unless that separator is the root's.</summary>
    private static string FullFolderPath(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>
    /// The names of the folders from <paramref name="root"/> down to <paramref name="fullPath"/>, both made by
    /// <see cref="FullFolderPath"/>: none for the root itself, null when the path is not below the root.
    /// </summary>
    /// <remarks>
    /// <see cref="Path.GetFullPath(string)"/> leaves an extended path (\\?\) as it is, so its '.', '..' and empty
    /// names would reach the walk, which opens each name in turn: such a path is not below the root either.
    /// </remarks>
    private static string[]? NamesBelow(string root, string fullPath)
    {
        if (string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase))
            return [];

        string prefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        if (fullPath.Length <= prefix.Length || !fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;

        string[] names = fullPath[prefix.Length..].Split(Path.DirectorySeparatorChar);
        return Array.Exists(names, name => name is "" or "." or "..") ? null : names;
    }

    /// <summary>
    /// Opens a folder directly in this one, or creates it, refusing a link as <see cref="Open"/> does. The handle
    /// has the rights and sharing that <see cref="OpenFolder"/> gives, so the folder is held in the same way.
    /// </summary>
    private LinkSafeFolder OpenSubfolder(string name, bool create)
    {
        string fullPath = Path.Combine(FullPath, name);
        int status = OpenRelative(
            name, FileListDirectory | FileReadAttributes | Synchronize, FileShare.ReadWrite,
            create ? FileOpenIfDisposition : FileOpenDisposition,
            FileDirectoryFile | FileSynchronousIoNonAlert | FileOpenReparsePoint, out SafeFileHandle handle);
        if (status < 0)
        {
            handle.Dispose();
            if (status == StatusNotADirectory)
                throw new IOException($"'{fullPath}' is not a folder.");
            throw ErrorFromNtStatus(status, fullPath);
        }

        try
        {
            ThrowUnlessPlainFolder(handle, fullPath);
            return new LinkSafeFolder(fullPath, handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// NtCreateFile of <paramref name="name"/> relative to the folder's handle; without FILE_OPEN_REPARSE_POINT
    /// where the file system rejects it (see the remarks).
    /// </summary>
    private int OpenRelative(string name, uint desiredAccess, FileShare share, uint disposition, uint options, out SafeFileHandle handle)
    {
        IntPtr nameBuffer = Marshal.StringToHGlobalUni(name);
        IntPtr objectName = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        bool addedReference = false;
        try
        {
            _handle.DangerousAddRef(ref addedReference);
            var unicodeName = new UnicodeString
            {
                Length = (ushort)(name.Length * sizeof(char)),
                MaximumLength = (ushort)((name.Length + 1) * sizeof(char)),
                Buffer = nameBuffer,
            };
            Marshal.StructureToPtr(unicodeName, objectName, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = _handle.DangerousGetHandle(),
                ObjectName = objectName,
                Attributes = ObjCaseInsensitive,
            };
            uint shareAccess = (uint)(share & (FileShare.Read | FileShare.Write | FileShare.Delete));
            int status = NtCreateFile(
                out handle, desiredAccess, ref attributes, out _, IntPtr.Zero, 0, shareAccess, disposition, options, IntPtr.Zero, 0);
            if (status == StatusInvalidParameter && (options & FileOpenReparsePoint) != 0)
            {
                handle.Dispose();
                status = NtCreateFile(
                    out handle, desiredAccess, ref attributes, out _, IntPtr.Zero, 0, shareAccess, disposition,
                    options & ~FileOpenReparsePoint, IntPtr.Zero, 0);
            }

            return status;
        }
        finally
        {
            if (addedReference)
                _handle.DangerousRelease();
            Marshal.FreeHGlobal(objectName);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static Exception ErrorFromNtStatus(int status, string fullPath) => ErrorFromWin32(RtlNtStatusToDosError(status), fullPath);

    private static Exception ErrorFromWin32(int error, string fullPath)
    {
        string message = $"{new Win32Exception(error).Message} : '{fullPath}'";
        return error == ErrorAccessDenied
            ? new UnauthorizedAccessException(message)
            : new IOException(message, unchecked((int)0x8007_0000) | (error & 0xFFFF));
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(
        SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock, IntPtr fileInformation, uint length, int fileInformationClass);

    /// <summary>For FILE_DISPOSITION_INFORMATION_EX, whose only member is its flags.</summary>
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(
        SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock, ref uint fileInformation, uint length, int fileInformationClass);

    /// <summary>For FILE_DISPOSITION_INFORMATION, whose only member is the BOOLEAN DeleteFile.</summary>
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(
        SafeFileHandle fileHandle, out IoStatusBlock ioStatusBlock, ref byte fileInformation, uint length, int fileInformationClass);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out ByHandleFileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    /// <summary>FILE_RENAME_INFORMATION up to its variable-length FileName.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct RenameInformation
    {
        public uint ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint FileNameLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public uint CreationTimeLow;
        public uint CreationTimeHigh;
        public uint LastAccessTimeLow;
        public uint LastAccessTimeHigh;
        public uint LastWriteTimeLow;
        public uint LastWriteTimeHigh;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
