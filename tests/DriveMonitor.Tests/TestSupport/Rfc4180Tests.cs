namespace DriveMonitor.Tests.TestSupport;

/// <summary>Checks the test-side CSV reader, so the CSV tests can trust it.</summary>
public sealed class Rfc4180Tests
{
    [Fact]
    public void Parse_HandlesQuotedCommasQuotesLineBreaksAndEmptyFields()
    {
        List<string[]> records = Rfc4180.Parse("a,\"b,c\",\"say \"\"hi\"\"\",\"x\r\ny\"\r\n,\r\n");

        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { "a", "b,c", "say \"hi\"", "x\r\ny" }, records[0]);
        Assert.Equal(new[] { "", "" }, records[1]);
    }

    [Fact]
    public void Parse_EmptyText_HasNoRecords()
    {
        Assert.Empty(Rfc4180.Parse(""));
    }

    [Theory]
    [InlineData("a,b")]
    [InlineData("a,")]
    [InlineData("a\nb\r\n")]
    [InlineData("a\rb\r\n")]
    [InlineData("a,\r")]
    [InlineData("a\"b\r\n")]
    [InlineData("\"a\"b\r\n")]
    [InlineData("\"a\r\n")]
    public void Parse_MalformedText_Throws(string text)
    {
        Assert.Throws<FormatException>(() => Rfc4180.Parse(text));
    }
}
