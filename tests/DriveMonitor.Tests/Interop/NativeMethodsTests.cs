using DriveMonitor.Core.Interop;
using DriveMonitor.Core.Tracing;
using DriveMonitor.Core.Volumes;

namespace DriveMonitor.Tests.Interop;

/// <summary>The Win32 lookups the engine depends on, exercised against the real system.</summary>
public sealed class NativeMethodsTests
{
    private const int ProcessThatCannotExist = int.MaxValue - 3; // process ids are multiples of 4 well below this

    [Fact]
    public void TryQueryDosDevice_WindowsDrive_IsAKernelDevice()
    {
        Assert.StartsWith(@"\Device\", NativeMethods.TryQueryDosDevice(DriveCatalog.SystemDriveLetter));
    }

    [Fact]
    public void TryGetProcessImagePath_OwnProcess_IsTheRunningExecutable()
    {
        string? path = NativeMethods.TryGetProcessImagePath(Environment.ProcessId);

        Assert.NotNull(path);
        Assert.Equal(Path.GetFileName(Environment.ProcessPath), Path.GetFileName(path), ignoreCase: true);
    }

    [Fact]
    public void TryGetProcessImagePath_NoSuchProcess_IsNull()
    {
        Assert.Null(NativeMethods.TryGetProcessImagePath(ProcessThatCannotExist));
    }

    [Fact]
    public void ProcessNameCache_RunningProcessNotSeenInTheTrace_IsNamedFromItsImage()
    {
        var cache = new ProcessNameCache();

        Assert.Equal(Path.GetFileName(Environment.ProcessPath), cache.Resolve(Environment.ProcessId), ignoreCase: true);
    }
}
