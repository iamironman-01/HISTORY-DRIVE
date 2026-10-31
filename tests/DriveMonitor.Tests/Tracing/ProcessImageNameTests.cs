using DriveMonitor.Core.Tracing;

namespace DriveMonitor.Tests.Tracing;

/// <summary>Choosing a process name from the kernel's 15-character image name and the command line.</summary>
public sealed class ProcessImageNameTests
{
    [Theory]
    [InlineData("SearchProtocolH", "\"C:\\Windows\\system32\\SearchProtocolHost.exe\" Global\\UsGthrFltPipe", "SearchProtocolHost.exe")]
    [InlineData("svchost.exe", @"C:\WINDOWS\system32\svchost.exe -k netsvcs -p", "svchost.exe")]
    [InlineData("MicrosoftEdgeUp", @"C:\Program Files (x86)\Microsoft\EdgeUpdate\MicrosoftEdgeUpdate.exe /c", "MicrosoftEdgeUpdate.exe")]
    [InlineData("notepad.exe", @"notepad.exe C:\notes.txt", "notepad.exe")]
    [InlineData(@"\Device\HarddiskVolume3\Windows\explorer.exe", @"C:\Windows\Explorer.EXE", "Explorer.EXE")]
    public void BestImageName_PrefersTheFullNameFromTheCommandLine(string imageFileName, string commandLine, string expected)
    {
        Assert.Equal(expected, ProcessNameCache.BestImageName(imageFileName, commandLine));
    }

    [Theory]
    [InlineData("System", null)]
    [InlineData("System", "")]
    [InlineData("System", "   ")]
    public void BestImageName_WithoutACommandLine_KeepsTheKernelName(string imageFileName, string? commandLine)
    {
        Assert.Equal("System", ProcessNameCache.BestImageName(imageFileName, commandLine));
    }

    [Fact]
    public void BestImageName_CommandLineNamingAnotherProgram_KeepsTheKernelName()
    {
        Assert.Equal("python.exe", ProcessNameCache.BestImageName("python.exe", "\"C:\\Tools\\app.exe\" run"));
    }

    [Fact]
    public void BestImageName_WithoutAKernelName_UsesTheCommandLine()
    {
        Assert.Equal("tool.exe", ProcessNameCache.BestImageName(null, "\"C:\\x\\tool.exe\""));
        Assert.Equal("tool.exe", ProcessNameCache.BestImageName("", "\"C:\\x\\tool.exe\" --flag"));
    }

    [Fact]
    public void BestImageName_NothingKnown_IsNull()
    {
        Assert.Null(ProcessNameCache.BestImageName("", ""));
        Assert.Null(ProcessNameCache.BestImageName(null, null));
    }

    [Fact]
    public void BestImageName_UnterminatedQuote_UsesTheRestOfTheLine()
    {
        Assert.Equal("Long Name Tool.exe", ProcessNameCache.BestImageName("Long Name Tool.", "\"C:\\x\\Long Name Tool.exe"));
    }
}
