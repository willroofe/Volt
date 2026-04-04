using Xunit;
using Volt;

namespace Volt.Tests;

public class BracketMatcherTests
{
    private static void AssertMatch(
        (int line, int col, int matchLine, int matchCol)? result,
        int line, int col, int matchLine, int matchCol)
    {
        Assert.NotNull(result);
        Assert.Equal(line, result.Value.line);
        Assert.Equal(col, result.Value.col);
        Assert.Equal(matchLine, result.Value.matchLine);
        Assert.Equal(matchCol, result.Value.matchCol);
    }

    [Fact]
    public void FindMatch_MatchesParensOnSameLine()
    {
        var buf = TestHelpers.MakeBuffer("foo(bar)");
        var result = BracketMatcher.FindMatch(buf, 0, 3);
        AssertMatch(result, 0, 3, 0, 7);
    }

    [Fact]
    public void FindMatch_MatchesNestedBrackets()
    {
        var buf = TestHelpers.MakeBuffer("{a[b(c)d]e}");
        var result = BracketMatcher.FindMatch(buf, 0, 0);
        AssertMatch(result, 0, 0, 0, 10);
    }

    [Fact]
    public void FindMatch_CrossLineMatching()
    {
        var buf = TestHelpers.MakeBuffer("if {\n  x\n}");
        var result = BracketMatcher.FindMatch(buf, 0, 3);
        AssertMatch(result, 0, 3, 2, 0);
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
        AssertMatch(result, 0, 6, 0, 0);
    }

    [Fact]
    public void FindMatch_MixedBracketTypes()
    {
        var buf = TestHelpers.MakeBuffer("{[()]}");
        var result = BracketMatcher.FindMatch(buf, 0, 0);
        AssertMatch(result, 0, 0, 0, 5);
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
        AssertMatch(result, 2, 0, 0, 0);
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
