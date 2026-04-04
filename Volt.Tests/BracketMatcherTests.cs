using Xunit;
using Volt;

namespace Volt.Tests;

public class BracketMatcherTests
{
    [Fact]
    public void FindMatch_MatchesParensOnSameLine()
    {
        var buf = TestHelpers.MakeBuffer("foo(bar)");
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
        var buf = TestHelpers.MakeBuffer("{a[b(c)d]e}");
        var result = BracketMatcher.FindMatch(buf, 0, 0);

        Assert.NotNull(result);
        Assert.Equal(0, result.Value.col);
        Assert.Equal(10, result.Value.matchCol);
    }

    [Fact]
    public void FindMatch_CrossLineMatching()
    {
        var buf = TestHelpers.MakeBuffer("if {\n  x\n}");
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
        var buf = TestHelpers.MakeBuffer("(unclosed");
        var result = BracketMatcher.FindMatch(buf, 0, 0);

        Assert.Null(result);
    }

    [Fact]
    public void FindMatch_ClosingBracketMatchesOpener()
    {
        var buf = TestHelpers.MakeBuffer("(hello)");
        var result = BracketMatcher.FindMatch(buf, 0, 6);

        Assert.NotNull(result);
        Assert.Equal(6, result.Value.col);
        Assert.Equal(0, result.Value.matchCol);
    }

    [Fact]
    public void FindMatch_MixedBracketTypes()
    {
        var buf = TestHelpers.MakeBuffer("{[()]}");
        var result = BracketMatcher.FindMatch(buf, 0, 0);

        Assert.NotNull(result);
        Assert.Equal(0, result.Value.col);
        Assert.Equal(5, result.Value.matchCol);
    }

    [Fact]
    public void FindMatch_UnmatchedCloser_ReturnsNull()
    {
        var buf = TestHelpers.MakeBuffer("hello ) world");
        var result = BracketMatcher.FindMatch(buf, 0, 6);

        Assert.Null(result);
    }

    [Fact]
    public void FindMatch_BracketAtStartOfLine()
    {
        var buf = TestHelpers.MakeBuffer("{\n  x\n}");
        var result = BracketMatcher.FindMatch(buf, 2, 0);

        Assert.NotNull(result);
        Assert.Equal(2, result.Value.line);
        Assert.Equal(0, result.Value.col);
        Assert.Equal(0, result.Value.matchLine);
        Assert.Equal(0, result.Value.matchCol);
    }

    [Fact]
    public void FindMatch_EmptyBuffer_ReturnsNull()
    {
        var buf = TestHelpers.MakeBuffer("");
        var result = BracketMatcher.FindMatch(buf, 0, 0);

        Assert.Null(result);
    }

    [Fact]
    public void FindMatch_ClampsOutOfRangePosition()
    {
        var buf = TestHelpers.MakeBuffer("(x)");
        // Out-of-range position should be clamped, not crash
        var result = BracketMatcher.FindMatch(buf, 99, 99);

        // Clamped to end of line — should find closing paren at col 2
        Assert.NotNull(result);
    }
}
