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

    [Theory]
    [InlineData("Photo Organizer \u2013 R\u00e9sum\u00e9 \u6771\u4eac \U0001F4C1.exe")] // a dash, accents, CJK, an emoji (surrogate pair)
    [InlineData("Arabic Keyboard \u0645\u0631\u062d\u0628\u0627 \u05e9\u05dc\u05d5\u05dd.exe")] // right-to-left letters are text, not formatting
    [InlineData("Setup (x64) [v2] {beta} #1 & 50% off; a=b, c~d.exe")]
    public void BestImageName_OrdinaryNameFromTheCommandLine_IsKept(string name)
    {
        Assert.Equal(name, ProcessNameCache.BestImageName(name[..15], $"\"D:\\Tools\\{name}\" --x"));
    }

    [Fact]
    public void BestImageName_CommandLineThatForgesOutput_KeepsTheKernelName()
    {
        // CR returns to column 0 and ESC[2K erases the line, so the rest would replace drivemon's line.
        string commandLine = "\"evil.exe\r\u001b[2K12:00:00.000  DELETE  thesis.docx  explorer.exe\" --x";

        Assert.Equal("evil.exe", ProcessNameCache.BestImageName("evil.exe", commandLine));
    }

    [Theory]
    [InlineData("\r\u001b[2K")]
    [InlineData("\n")]
    [InlineData("\t")]
    [InlineData("\0")]
    [InlineData("\b")]
    [InlineData("\u007f")] // DEL
    [InlineData("\u0085")] // C1 next line
    [InlineData("\u009b")] // C1 control sequence introducer, the 8-bit ESC[
    [InlineData("\u2028")] // line separator
    [InlineData("\u2029")] // paragraph separator
    [InlineData("\u202A")] // left-to-right embedding
    [InlineData("\u202B")] // right-to-left embedding
    [InlineData("\u202C")] // pop directional formatting
    [InlineData("\u202D")] // left-to-right override
    [InlineData("\u202E")] // right-to-left override: shows "invoice[U+202E]txt.exe" as "invoiceexe.txt"
    [InlineData("\u2066")] // left-to-right isolate
    [InlineData("\u2067")] // right-to-left isolate
    [InlineData("\u2068")] // first strong isolate
    [InlineData("\u2069")] // pop directional isolate
    [InlineData("\u200E")] // left-to-right mark
    [InlineData("\u200F")] // right-to-left mark
    [InlineData("\u061C")] // Arabic letter mark
    public void BestImageName_CommandLineNameWithControlOrBidiCharacters_KeepsTheKernelName(string inserted)
    {
        string name = $"SearchProtocolH{inserted}ost.exe";

        Assert.Equal("SearchProtocolH", ProcessNameCache.BestImageName("SearchProtocolH", $"\"C:\\Windows\\system32\\{name}\" Global"));
        Assert.Equal("SearchProtocolH", ProcessNameCache.BestImageName("SearchProtocolH", $"C:\\Windows\\system32\\{name} Global"));
    }

    [Theory]
    [InlineData("C:\\x\\SearchProtocolH\"ost.exe Global")] // a quote can only be inside an unquoted name
    [InlineData("\"C:\\x\\SearchProtocolH<ost.exe\" Global")]
    [InlineData("\"C:\\x\\SearchProtocolH>ost.exe\" Global")]
    [InlineData("\"C:\\x\\SearchProtocolH|ost.exe\" Global")]
    [InlineData("\"C:\\x\\SearchProtocolH:ost.exe\" Global")]
    [InlineData("\"C:\\x\\SearchProtocolH*ost.exe\" Global")]
    [InlineData("\"C:\\x\\SearchProtocolH?ost.exe\" Global")]
    public void BestImageName_CommandLineNameNoFileCanHave_KeepsTheKernelName(string commandLine)
    {
        Assert.Equal("SearchProtocolH", ProcessNameCache.BestImageName("SearchProtocolH", commandLine));
    }

    [Fact]
    public void BestImageName_UnusableCommandLineNameWithoutAKernelName_IsNull()
    {
        Assert.Null(ProcessNameCache.BestImageName(null, "\"C:\\x\\invoice\u202Etxt.exe\""));
        Assert.Null(ProcessNameCache.BestImageName("", "C:\\x\\tool\r.exe --flag"));
    }
}
