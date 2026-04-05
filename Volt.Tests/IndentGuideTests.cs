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

    [Theory]
    [InlineData("if (true) {", true)]
    [InlineData("sub foo {", true)]
    [InlineData("else {", true)]
    [InlineData("{", true)]
    [InlineData("    {", true)]
    [InlineData("if (true) {  ", true)]    // trailing whitespace
    [InlineData("x = { a: 1 }", false)]    // } is last, not {
    [InlineData("hello", false)]
    [InlineData("}", false)]
    [InlineData("", false)]
    [InlineData("$main::{ '_<' . $file }", false)]
    [InlineData("{    #{ vi", true)]            // first non-ws is '{'
    [InlineData("if (x) { # comment", false)]  // last non-ws is 't', first non-ws is 'i'
    [InlineData("foo(); // comment", false)]    // no brace at end or start
    [InlineData("bar() { // start", false)]     // last non-ws is 't', first non-ws is 'b'
    public void IsBlockOpener_DetectsLastOrFirstNonWhitespace(string line, bool expected)
    {
        Assert.Equal(expected, EditorControl.IsBlockOpener(line));
    }

    [Theory]
    [InlineData("}", true)]
    [InlineData("    }", true)]
    [InlineData("} ## end if", true)]
    [InlineData("} else {", true)]
    [InlineData("    } else {", true)]
    [InlineData("x = }", false)]           // } is not first non-ws
    [InlineData("{", false)]
    [InlineData("hello", false)]
    [InlineData("", false)]
    [InlineData("} ## end if (!defined $main::{", true)] // } is first non-ws
    public void IsBlockCloser_DetectsFirstNonWhitespace(string line, bool expected)
    {
        Assert.Equal(expected, EditorControl.IsBlockCloser(line));
    }
}
