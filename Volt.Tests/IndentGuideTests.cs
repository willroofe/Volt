using Xunit;
using Volt;

namespace Volt.Tests;

public class IndentGuideTests
{
    [Theory]
    [InlineData("", 4, 0)]
    [InlineData("hello", 4, 0)]
    [InlineData("    hello", 4, 4)]
    [InlineData("        hello", 4, 8)]
    [InlineData("      hello", 4, 6)]
    [InlineData("  hello", 4, 2)]
    [InlineData("  hello", 2, 2)]
    [InlineData("\thello", 4, 4)]
    [InlineData("\t\thello", 4, 8)]
    [InlineData("\t hello", 4, 5)]
    [InlineData("\t   hello", 4, 7)]
    [InlineData("\t    hello", 4, 8)]
    public void MeasureIndentColumns_ReturnsCorrectCount(string line, int tabSize, int expected)
    {
        Assert.Equal(expected, EditorControl.MeasureIndentColumns(line, tabSize));
    }

    [Fact]
    public void MeasureIndentColumns_AllWhitespace_CountsFullLine()
    {
        Assert.Equal(8, EditorControl.MeasureIndentColumns("        ", 4));
    }

    [Fact]
    public void MeasureIndentColumns_MixedTabsAndSpaces()
    {
        // tab(4) + 2 spaces + tab(to col 8) = 8 columns
        Assert.Equal(8, EditorControl.MeasureIndentColumns("\t  \thello", 4));
    }

    // IsBlockOpen: true when line has more '{' than '}' in code context
    [Theory]
    [InlineData("if (true) {", true)]
    [InlineData("sub foo {", true)]
    [InlineData("else {", true)]
    [InlineData("{", true)]
    [InlineData("    {", true)]
    [InlineData("if (true) {  ", true)]          // trailing whitespace
    [InlineData("x = { a: 1 }", false)]          // balanced braces
    [InlineData("hello", false)]
    [InlineData("}", false)]
    [InlineData("", false)]
    [InlineData("$main::{ '_<' . $file }", false)] // balanced braces
    [InlineData("{    #{ vi", true)]              // two '{' vs zero '}'
    [InlineData("if (x) { # comment", true)]     // one unmatched '{'
    [InlineData("foo(); // comment", false)]      // no braces
    [InlineData("bar() { // start", true)]        // one unmatched '{'
    [InlineData("} else {", false)]               // balanced — continuation
    [InlineData("    } else {", false)]           // balanced — continuation
    [InlineData("} elsif ($x) {", false)]         // balanced — continuation
    public void IsBlockOpen_DetectsNetPositiveBraceBalance(string line, bool expected)
    {
        var buf = TestHelpers.MakeBuffer(line);
        Assert.Equal(expected, BracketMatcher.IsBlockOpen(buf, 0));
    }

    // IsBlockClose: true when line has more '}' than '{'
    [Theory]
    [InlineData("}", true)]
    [InlineData("    }", true)]
    [InlineData("} ## end if", true)]
    [InlineData("} else {", false)]              // balanced — not a net closer
    [InlineData("    } else {", false)]          // balanced
    [InlineData("{", false)]
    [InlineData("hello", false)]
    [InlineData("", false)]
    public void IsBlockClose_DetectsNetNegativeBraceBalance(string line, bool expected)
    {
        var buf = TestHelpers.MakeBuffer(line);
        Assert.Equal(expected, BracketMatcher.IsBlockClose(buf, 0));
    }

    [Fact]
    public void FindBlockCloser_FindsMatchingBrace()
    {
        var buf = TestHelpers.MakeBuffer(
            "if (x) {",
            "    foo();",
            "}");
        Assert.Equal(2, BracketMatcher.FindBlockCloser(buf, 0)?.line);
    }

    [Fact]
    public void FindBlockCloser_HandlesNested()
    {
        var buf = TestHelpers.MakeBuffer(
            "if (x) {",
            "    if (y) {",
            "        bar();",
            "    }",
            "}");
        Assert.Equal(4, BracketMatcher.FindBlockCloser(buf, 0)?.line);
        Assert.Equal(3, BracketMatcher.FindBlockCloser(buf, 1)?.line);
    }

    [Fact]
    public void FindBlockCloser_HandlesContinuationLines()
    {
        var buf = TestHelpers.MakeBuffer(
            "if (x) {",
            "    foo();",
            "} else {",
            "    bar();",
            "}");
        // The '{' on line 0 matches the '}' on line 2 (first unmatched closing brace)
        Assert.Equal(2, BracketMatcher.FindBlockCloser(buf, 0)?.line);
    }

    [Fact]
    public void FindBlockCloser_SkipsStringBraces()
    {
        var buf = TestHelpers.MakeBuffer(
            "if (x) {",
            "    print(\"}\");",
            "}");
        // With a skip predicate that marks the '}' inside the string, it finds line 2
        bool skip(int line, int col) => line == 1 && col >= 10 && col <= 12;
        Assert.Equal(2, BracketMatcher.FindBlockCloser(buf, 0, skip)?.line);
    }

    [Fact]
    public void FindEnclosingOpenBrace_FindsNearestEncloser()
    {
        var buf = TestHelpers.MakeBuffer(
            "sub main {",
            "    if (x) {",
            "        foo();",
            "    }",
            "}");
        // From inside the if block, find the if opener
        Assert.Equal(1, BracketMatcher.FindEnclosingOpenBrace(buf, 2, 0));
        // From after the if block, find the sub opener
        Assert.Equal(0, BracketMatcher.FindEnclosingOpenBrace(buf, 3, 5));
    }

    [Fact]
    public void FindBlockCloser_FindsLastUnmatchedBrace()
    {
        // Perl-style: if (!eval { ... }) {  —  the eval '{' is matched by '})'
        var buf = TestHelpers.MakeBuffer(
            "if (!eval {",
            "    require Foo;",
            "}) {",
            "    bar();",
            "}");
        // Line 0: one '{', no '}' → FindBlockCloser finds the eval's '{'
        // which matches the '}' on line 2 ("})"). Returns (line: 2, col: 0).
        var result = BracketMatcher.FindBlockCloser(buf, 0);
        Assert.NotNull(result);
        Assert.Equal(2, result.Value.line);
        // The '}' at col 0 on "}) {" is NOT the first non-ws char... wait, it IS col 0.
        // But '}' is followed by ')' — it's part of an inline construct.
        // The structural check (first non-ws is '}' at the match col) will pass here,
        // but the if-body block on line 2 ("}) {") is balanced and handled separately.

        // Line 2 ("}) {"): balanced braces → IsBlockOpen = false
        Assert.False(BracketMatcher.IsBlockOpen(buf, 2));
        // But FindBlockCloser finds the last unmatched '{' (the one at end of "}) {")
        var line2Result = BracketMatcher.FindBlockCloser(buf, 2);
        Assert.NotNull(line2Result);
        Assert.Equal(4, line2Result.Value.line); // matches the final '}'
    }

    [Fact]
    public void CodeBraceBalance_CountsOnlyCodeBraces()
    {
        var buf = TestHelpers.MakeBuffer("{ } { # }");
        // Without skip: two '{', two '}' → balance 0
        Assert.Equal(0, BracketMatcher.CodeBraceBalance(buf, 0));
        // With skip marking the '}' inside comment (col 8): two '{', one '}' → balance +1
        bool skip(int line, int col) => col >= 6;
        Assert.Equal(1, BracketMatcher.CodeBraceBalance(buf, 0, skip));
    }
}
