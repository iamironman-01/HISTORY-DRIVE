namespace DriveMonitor.Core.Tracing;

/// <summary>Short descriptions for the NTSTATUS codes file operations commonly fail with.</summary>
public static class NtStatusText
{
    public const int StatusNoMoreFiles = unchecked((int)0x80000006);
    public const int StatusNoSuchFile = unchecked((int)0xC000000F);

    private static readonly Dictionary<int, string> Known = new()
    {
        [unchecked((int)0x80000005)] = "buffer overflow",
        [StatusNoMoreFiles] = "no more files",
        [unchecked((int)0xC0000008)] = "invalid handle",
        [unchecked((int)0xC000000D)] = "invalid parameter",
        [unchecked((int)0xC000000E)] = "no such device",
        [StatusNoSuchFile] = "no such file",
        [unchecked((int)0xC0000010)] = "invalid request",
        [unchecked((int)0xC0000011)] = "end of file",
        [unchecked((int)0xC0000022)] = "access denied",
        [unchecked((int)0xC0000033)] = "invalid name",
        [unchecked((int)0xC0000034)] = "not found",
        [unchecked((int)0xC0000035)] = "already exists",
        [unchecked((int)0xC000003A)] = "path not found",
        [unchecked((int)0xC0000043)] = "sharing violation",
        [unchecked((int)0xC0000054)] = "lock conflict",
        [unchecked((int)0xC0000056)] = "delete pending",
        [unchecked((int)0xC000007F)] = "disk full",
        [unchecked((int)0xC000009C)] = "device data error",
        [unchecked((int)0xC00000A2)] = "write protected",
        [unchecked((int)0xC00000B5)] = "I/O timeout",
        [unchecked((int)0xC00000BA)] = "is a directory",
        [unchecked((int)0xC00000BB)] = "not supported",
        [unchecked((int)0xC0000101)] = "directory not empty",
        [unchecked((int)0xC0000103)] = "not a directory",
        [unchecked((int)0xC0000120)] = "cancelled",
        [unchecked((int)0xC0000121)] = "cannot delete",
        [unchecked((int)0xC0000185)] = "device I/O error",
        [unchecked((int)0xC000026E)] = "volume dismounted",
    };

    /// <summary>NT_SUCCESS: success and informational codes are non-negative.</summary>
    public static bool IsSuccess(int status) => status >= 0;

    public static string Describe(int status) =>
        Known.TryGetValue(status, out string? text) ? text : $"status 0x{unchecked((uint)status):X8}";
}
