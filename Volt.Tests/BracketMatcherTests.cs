using Xunit;
using Volt;

namespace Volt.Tests;

public class BracketMatcherTests
{
    private static TextBuffer MakeBuffer(string content)
    {
        var buf = new TextBuffer();
        buf.SetContent(content, tabSize: 4);
        return buf;
    }

    [Fact]
    public void FindMatch_MatchesParensOnSameLine()
    {
        var buf = MakeBuffer("foo(bar)");
        var result = BracketMatcher.FindMatch(buf, 0, 3);

        Assert.NotNull(result);
        Assert.Equal(0, result.Value.line);
        Assert.Equal(3, result.Value.col);
        Assert.Equal(0, result.Value.matchLine);
        Assert.Equal(7, result.Value.matchCol);
    }

    [Fact]
    public void FindMatch_MatchesNestedBrackets()
    {
        var buf = MakeBuffer("{a[b(c)d]e}");
        var result = BracketMatcher.FindMatch(buf, 0, 0);

        Assert.NotNull(result);
        Assert.Equal(0, result.Value.col);
        Assert.Equal(10, result.Value.matchCol);
    }

    [Fact]
    public void FindMatch_CrossLineMatching()
    {
        var buf = MakeBuffer("if {\n  x\n}");
        var result = BracketMatcher.FindMatch(buf, 0, 3);

        Assert.NotNull(result);
        Assert.Equal(0, result.Value.line);
        Assert.Equal(3, result.Value.col);
        Assert.Equal(2, result.Value.matchLine);
        Assert.Equal(0, result.Value.matchCol);
    }

    [Fact]
    public void FindMatch_UnmatchedBracket_ReturnsNull()
    {
        var buf = MakeBuffer("(unclosed");
        var result = BracketMatcher.FindMatch(buf, 0, 0);

        Assert.Null(result);
    }
}
