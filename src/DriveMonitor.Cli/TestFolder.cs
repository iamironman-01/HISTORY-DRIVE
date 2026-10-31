using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace DriveMonitor.Cli;

/// <summary>
/// The self-test's working folder. The test runs as administrator in the root of a drive that other users can
/// usually write to, so the folder is made such that nothing of theirs can be in it or behind it, and the
/// cleanup deletes nothing but what the test created.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><description>New: the name is random, and creating fails when anything of that name exists, so a
/// folder, junction or link that someone prepared is never adopted.</description></item>
/// <item><description>Private: its access list, set by the call that creates it and not inherited from the
/// drive root, lets only administrators and SYSTEM change it. FAT and exFAT have no access lists, but they
/// have no junctions or links either.</description></item>
/// <item><description>Pinned: the handle that creating it returns is kept, without FILE_SHARE_DELETE, until the
/// cleanup, so the folder cannot be renamed, deleted or replaced while the test works with its path.</description></item>
/// <item><description>Where it was asked for: the folder that handle refers to must have the requested path, so
/// the path leads through no junction, symbolic link or SUBST drive.</description></item>
/// <item><description>Cleaned up narrowly: only files recorded as created by the test are deleted, then the
/// folder itself only if it is empty. Nothing is deleted recursively, so no link inside is ever followed;
/// anything else is reported and left alone.</description></item>
/// </list>
/// </remarks>
internal sealed class TestFolder : IDisposable
{
    /// <summary>
    /// Full control for administrators and SYSTEM only, inherited by the files created inside. The owner may
    /// read the access list but not change it: without the OWNER RIGHTS entry an owning user account would keep
    /// an implicit right to rewrite it, from an unelevated process too.
    /// </summary>
    internal const string PrivateSecurity = "D:P(A;OICI;FA;;;BA)(A;OICI;FA;;;SY)(A;OICI;RC;;;OW)";

    internal const string NamePrefix = ".drivemonitor-selftest-";

    // NtCreateFile arguments.
    private const uint FileListDirectory = 0x1;
    private const uint FileReadAttributes = 0x80;
    private const uint Synchronize = 0x100000;
    private const uint FileAttributeNormal = 0x80;
    private const uint FileShareRead = 0x1;
    private const uint FileShareWrite = 0x2;
    private const uint FileCreate = 2;
    private const uint FileDirectoryFile = 0x1;
    private const uint FileSynchronousIoNonAlert = 0x20;
    private const uint FileOpenReparsePoint = 0x200000;
    private const uint ObjCaseInsensitive = 0x40;
    private const int StatusInvalidParameter = unchecked((int)0xC000000D);
    private const int StatusObjectNameCollision = unchecked((int)0xC0000035);
    private const int HResultDirectoryNotEmpty = unchecked((int)0x80070091);

    // GetFinalPathNameByHandle flags, both with VOLUME_NAME_DOS (0).
    private const uint FileNameNormalized = 0x0;
    private const uint FileNameOpened = 0x8;

    private readonly object _lock = new();
    private readonly HashSet<string> _created = new(StringComparer.OrdinalIgnoreCase);
    private SafeFileHandle? _pin;

    private TestFolder(string fullPath, SafeFileHandle pin)
    {
        FullPath = fullPath;
        _pin = pin;
    }

    public string FullPath { get; }

    /// <summary>Creates a new folder with a random name in <paramref name="parent"/>, e.g. <c>E:\</c>.</summary>
    /// <param name="securityDescriptor">The folder's access list as SDDL; null inherits the parent's (tests only).</param>
    /// <exception cref="IOException">The folder could not be created, or its path leads through a link.</exception>
    public static TestFolder Create(string parent, string? securityDescriptor = PrivateSecurity) =>
        Create(parent, NamePrefix + RandomNumberGenerator.GetHexString(16, lowercase: true), securityDescriptor);

    /// <summary>Creates the folder <paramref name="name"/> in <paramref name="parent"/>; fails if anything has that name.</summary>
    internal static TestFolder Create(string parent, string name, string? securityDescriptor)
    {
        string path = Path.Combine(Path.GetFullPath(parent), name);
        SafeFileHandle pin = CreateDirectory(path, securityDescriptor);

        // The normalized name also expands 8.3 names; a file system that cannot give it still gives the name as
        // opened. GetLongPathName expands 8.3 names (C:\Users\RUNNER~1) too, but follows no link.
        string? actual = FinalPath(pin, FileNameNormalized) ?? FinalPath(pin, FileNameOpened);
        string expected = Path.Combine(LongPath(Path.GetDirectoryName(path)!), name);
        if (actual is null || !string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            pin.Dispose();
            TryRemoveEmpty(path);
            throw new IOException(actual is null
                ? $"Windows cannot tell where {path} really is, so the self-test does not use it."
                : $"{path} is really {actual}: the path leads through a junction, symbolic link or SUBST drive, so the self-test does not use it.");
        }

        return new TestFolder(path, pin);
    }

    /// <summary>The path of a file directly in the folder.</summary>
    public string PathOf(string fileName) => Path.Combine(FullPath, fileName);

    /// <summary>Creates a file that must not exist yet, and records it for <see cref="Remove"/>.</summary>
    public FileStream CreateNew(string path, FileAccess access, FileShare share, FileOptions options = FileOptions.None)
    {
        CheckInFolder(path);
        var stream = new FileStream(path, FileMode.CreateNew, access, share, bufferSize: 0, options);
        lock (_lock)
            _created.Add(path);
        return stream;
    }

    /// <summary>Renames a file the test created; fails if the new name exists.</summary>
    public void Move(string source, string destination)
    {
        CheckCreated(source);
        CheckInFolder(destination);
        File.Move(source, destination, overwrite: false);
        lock (_lock)
        {
            _created.Remove(source);
            _created.Add(destination);
        }
    }

    /// <summary>Deletes a file the test created.</summary>
    public void Delete(string path)
    {
        CheckCreated(path);
        File.Delete(path);
        Forget(path);
    }

    /// <summary>Stops recording a file that is gone, e.g. one deleted when its delete-on-close handle closed.</summary>
    public void Forget(string path)
    {
        lock (_lock)
            _created.Remove(path);
    }

    /// <summary>
    /// Deletes the files the test created, then the folder if nothing else is left in it. Safe to call more
    /// than once and from any thread.
    /// </summary>
    /// <returns>Null when everything is gone, otherwise what was left behind and why.</returns>
    public string? Remove()
    {
        lock (_lock)
        {
            if (_pin is null)
                return null;

            var problems = new List<string>();
            foreach (string path in _created)
            {
                try
                {
                    // Deletes a link itself, never its target.
                    File.Delete(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    problems.Add($"could not delete {Path.GetFileName(path)}: {ex.Message}");
                }
            }

            _created.Clear();

            // The pin refuses every DELETE open of the folder, this one included.
            _pin.Dispose();
            _pin = null;
            try
            {
                // Not recursive: only an empty folder goes, or a link that replaced it in the meantime (the link
                // itself, never its target).
                Directory.Delete(FullPath, recursive: false);
            }
            catch (DirectoryNotFoundException)
            {
                // Already gone.
            }
            catch (IOException ex) when (ex.HResult == HResultDirectoryNotEmpty)
            {
                problems.Add($"left {FullPath} in place: it still holds {CountEntries()} item(s) that the self-test did not delete");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                problems.Add($"could not remove {FullPath}: {ex.Message}");
            }

            return problems.Count == 0 ? null : string.Join("; ", problems);
        }
    }

    public void Dispose() => Remove();

    /// <summary>
    /// Creates the folder and opens it in one step, so no other folder can take its place in between. Fails if
    /// any file, folder or link of that name exists.
    /// </summary>
    private static SafeFileHandle CreateDirectory(string path, string? securityDescriptor)
    {
        byte[]? descriptor = null;
        if (securityDescriptor is not null)
        {
            var parsed = new RawSecurityDescriptor(securityDescriptor);
            descriptor = new byte[parsed.BinaryLength];
            parsed.GetBinaryForm(descriptor, 0);
        }

        // FILE_OPEN_REPARSE_POINT keeps a link of that name from being followed, should one appear. A file system
        // without reparse points may reject the flag as an invalid parameter, as DeleteFile also allows for.
        int status = NtCreateDirectory(path, descriptor, FileOpenReparsePoint, out SafeFileHandle handle);
        if (status == StatusInvalidParameter)
        {
            handle.Dispose();
            status = NtCreateDirectory(path, descriptor, 0, out handle);
        }

        if (status < 0)
        {
            handle.Dispose();
            throw new IOException(status == StatusObjectNameCollision
                ? $"{path} already exists; the self-test only works in a folder it has just created."
                : $"Could not create {path}: {Marshal.GetPInvokeErrorMessage(RtlNtStatusToDosError(status))}");
        }

        return handle;
    }

    private static unsafe int NtCreateDirectory(string path, byte[]? descriptor, uint extraOptions, out SafeFileHandle handle)
    {
        string ntPath = @"\??\" + path;
        fixed (char* name = ntPath)
        fixed (byte* security = descriptor)
        {
            ushort nameBytes = checked((ushort)(ntPath.Length * sizeof(char)));
            var objectName = new UnicodeString { Length = nameBytes, MaximumLength = nameBytes, Buffer = name };
            var attributes = new ObjectAttributes
            {
                Length = sizeof(ObjectAttributes),
                RootDirectory = IntPtr.Zero,
                ObjectName = &objectName,
                Attributes = ObjCaseInsensitive,
                SecurityDescriptor = security,
                SecurityQualityOfService = null,
            };
            IntPtr* ioStatusBlock = stackalloc IntPtr[2];

            // No FILE_SHARE_DELETE: renaming or deleting the folder needs a DELETE open, which is refused for as
            // long as this handle is open. Windows ignores the share mode of a handle that only reads attributes,
            // hence FILE_LIST_DIRECTORY. The handle holds no DELETE access itself, so the test's own renames inside
            // the folder, which open it for writing without sharing delete, still work.
            return NtCreateFile(
                out handle,
                FileListDirectory | FileReadAttributes | Synchronize,
                &attributes,
                ioStatusBlock,
                null,
                FileAttributeNormal,
                FileShareRead | FileShareWrite,
                FileCreate,
                FileDirectoryFile | FileSynchronousIoNonAlert | extraOptions,
                null,
                0);
        }
    }

    /// <summary>
    /// The path of the folder that a handle refers to, with junctions, links and SUBST drives resolved, or
    /// null when Windows cannot tell.
    /// </summary>
    private static string? FinalPath(SafeFileHandle handle, uint flags)
    {
        var buffer = new char[512];
        uint length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, flags);
        if (length >= buffer.Length)
        {
            buffer = new char[length];
            length = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, flags);
        }

        if (length == 0 || length >= buffer.Length)
            return null;

        string path = new(buffer, 0, (int)length);
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            return @"\\" + path[8..];
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }

    private static string LongPath(string path)
    {
        var buffer = new char[512];
        uint length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        if (length >= buffer.Length)
        {
            buffer = new char[length];
            length = GetLongPathNameW(path, buffer, (uint)buffer.Length);
        }

        return length == 0 || length >= buffer.Length ? path : new string(buffer, 0, (int)length);
    }

    private static void TryRemoveEmpty(string path)
    {
        try
        {
            Directory.Delete(path, recursive: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Left for the user: it was created empty, so anything in it now came from someone else.
        }
    }

    private void CheckInFolder(string path)
    {
        if (!string.Equals(Path.GetDirectoryName(path), FullPath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"{path} is not directly in the test folder.", nameof(path));
    }

    private void CheckCreated(string path)
    {
        lock (_lock)
        {
            if (!_created.Contains(path))
                throw new InvalidOperationException($"{Path.GetFileName(path)} was not created by the self-test.");
        }
    }

    private string CountEntries()
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(FullPath).Count().ToString(CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "some";
        }
    }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern unsafe int NtCreateFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ObjectAttributes* objectAttributes,
        IntPtr* ioStatusBlock,
        long* allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        void* eaBuffer,
        uint eaLength);

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int RtlNtStatusToDosError(int status);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, char[] path, uint pathLength, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetLongPathNameW(string shortPath, char[] longPath, uint longPathLength);

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public char* Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public UnicodeString* ObjectName;
        public uint Attributes;
        public void* SecurityDescriptor;
        public void* SecurityQualityOfService;
    }
}
