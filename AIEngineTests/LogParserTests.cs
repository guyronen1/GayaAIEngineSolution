using Xunit;
using Shared.Implementations;

public class LogParserTests
{
    [Fact]
    public void ParseLog_SplitsLinesCorrectly()
    {
        var parser = new SimpleLogParser();
        var result = parser.ParseLog("line1\nline2\nline3");
        Assert.Equal(3, result.Length);
        Assert.Equal("line1", result[0]);
    }
}
