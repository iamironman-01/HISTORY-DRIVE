using DriveMonitor.Cli;
using DriveMonitor.Core.Models;
using DriveMonitor.Tests.TestSupport;

namespace DriveMonitor.Tests.Cli;

/// <summary>Text that other users control must not be able to forge, erase or disguise drivemon's console lines.</summary>
public sealed class ConsoleTextTests
{
    [Theory]
    [InlineData("evil.exe\r\u001b[2K", "evil.exe??[2K")] // return to column 0, then erase the line
    [InlineData("a\nb\u000bc\u000cd", "a?b?c?d")]
    [InlineData("bell\u0007 back\b del\u007f", "bell? back? del?")]
    [InlineData("next\u0085 csi\u009b", "next? csi?")] // C1 controls, including the 8-bit escape sequence start
    [InlineData("invoice\u202Etxt.exe", "invoice?txt.exe")] // right-to-left override: shows as "invoiceexe.txt"
    [InlineData("\u202A\u202B\u202C\u202D", "????")]
    [InlineData("\u2066x\u2067y\u2068z\u2069", "?x?y?z?")]
    [InlineData("\u200Emarks\u200F\u061C", "?marks??")]
    [InlineData("line\u2028paragraph\u2029", "line?paragraph?")]
    public void Clean_ReplacesEachControlAndBidiCharacterWithAQuestionMark(string text, string expected)
    {
        Assert.Equal(expected, ConsoleText.Clean(text));
    }

    [Theory]
    [InlineData(@"E:\photos\img_0042.jpg")]
    [InlineData("R\u00e9sum\u00e9 \u2013 \u6771\u4eac \U0001F4C1.txt")] // accents, a dash, CJK, an emoji (surrogate pair)
    [InlineData("\u05e9\u05dc\u05d5\u05dd.txt")] // right-to-left letters are text, not formatting
    [InlineData("pattern *.docx")]
    public void Clean_OrdinaryText_IsReturnedUnchanged(string text)
    {
        Assert.Same(text, ConsoleText.Clean(text));
    }

    [Fact]
    public void Clean_NullOrEmpty_IsEmpty()
    {
        Assert.Equal("", ConsoleText.Clean(null));
        Assert.Equal("", ConsoleText.Clean(""));
    }

    [Fact]
    public void Describe_ForgedProcessNameAndPattern_CannotStartANewOrErasedLine()
    {
        using CultureScope culture = CultureScope.Invariant();
        var ev = new FileActivityEvent
        {
            Operation = FileOperation.List,
            Path = "E:\\docs\\report\u202Etxt.exe",
            ProcessName = "evil.exe\r\u001b[2K12:00:00.000  DELETE             E:\\docs\\thesis.docx  explorer.exe",
            ProcessId = 6060,
            Flags = FileEventFlags.Directory,
            Detail = "pattern *\u001b[1A\r\n",
        };

        string line = EventPrinter.Describe(ev);

        Assert.DoesNotContain(line, ConsoleText.IsUnsafe);
        Assert.Equal(
            new string(' ', 11) + "E:\\docs\\report?txt.exe  " +
            "evil.exe??[2K12:00:00.000  DELETE             E:\\docs\\thesis.docx  explorer.exe (6060)  pattern *?[1A??",
            line);
    }

    [Fact]
    public void Describe_OrdinaryRead_KeepsItsColumns()
    {
        using CultureScope culture = CultureScope.Invariant();
        var ev = new FileActivityEvent
        {
            Operation = FileOperation.Read,
            Path = @"E:\photos\img_0042.jpg",
            Bytes = 4096,
            Offset = 8192,
            ProcessName = "explorer.exe",
            ProcessId = 4,
        };

        Assert.Equal(@"     4 KB  E:\photos\img_0042.jpg  @8,192  explorer.exe (4)", EventPrinter.Describe(ev));
    }
}
