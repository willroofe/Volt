using Xunit;
using Volt;

namespace Volt.Tests;

public class WrapLayoutTests
{
    [Fact]
    public void WrapOff_VisualLineEqualsLogicalLine()
    {
        var buf = TestHelpers.MakeBuffer("short\nlines\nhere");
        var wrap = new WrapLayout();

        wrap.Recalculate(wordWrap: false, breakAtWords: false, wrapIndent: false, buf, textAreaWidth: 500, charWidth: 8);

        Assert.Equal(3, wrap.TotalVisualLines);
        Assert.Equal(0, wrap.LogicalToVisualLine(wordWrap: false, 0));
        Assert.Equal(1, wrap.LogicalToVisualLine(wordWrap: false, 1));
        Assert.Equal(2, wrap.LogicalToVisualLine(wordWrap: false, 2));
    }

    [Fact]
    public void WrapOn_LongLineProducesMultipleVisualLines()
    {
        var buf = TestHelpers.MakeBuffer("12345678901234567890\nshort");
        var wrap = new WrapLayout();

        wrap.Recalculate(wordWrap: true, breakAtWords: false, wrapIndent: false, buf, textAreaWidth: 80, charWidth: 8);

        Assert.Equal(10, wrap.CharsPerVisualLine);
        Assert.Equal(2, wrap.VisualLineCount(wordWrap: true, 0));
        Assert.Equal(1, wrap.VisualLineCount(wordWrap: true, 1));
        Assert.Equal(3, wrap.TotalVisualLines);
    }

    [Fact]
    public void VisualToLogical_Roundtrip()
    {
        var buf = TestHelpers.MakeBuffer("12345678901234567890\nshort");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, breakAtWords: false, wrapIndent: false, buf, textAreaWidth: 80, charWidth: 8);

        var (log0, wrapIdx0) = wrap.VisualToLogical(wordWrap: true, 0, buf.Count);
        Assert.Equal(0, log0);
        Assert.Equal(0, wrapIdx0);

        var (log1, wrapIdx1) = wrap.VisualToLogical(wordWrap: true, 1, buf.Count);
        Assert.Equal(0, log1);
        Assert.Equal(1, wrapIdx1);

        var (log2, wrapIdx2) = wrap.VisualToLogical(wordWrap: true, 2, buf.Count);
        Assert.Equal(1, log2);
        Assert.Equal(0, wrapIdx2);
    }

    [Fact]
    public void VisualToLogical_BeyondBufferBounds_Clamps()
    {
        var buf = TestHelpers.MakeBuffer("line1\nline2");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, breakAtWords: false, wrapIndent: false, buf, textAreaWidth: 500, charWidth: 8);

        var (logLine, _) = wrap.VisualToLogical(wordWrap: true, 999, buf.Count);
        Assert.True(logLine < buf.Count);
    }

    [Fact]
    public void LogicalToVisualLine_WithColumn_SelectsCorrectWrapLine()
    {
        var buf = TestHelpers.MakeBuffer("12345678901234567890"); // 20 chars, wraps at 10
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, breakAtWords: false, wrapIndent: false, buf, textAreaWidth: 80, charWidth: 8);

        Assert.Equal(0, wrap.LogicalToVisualLine(wordWrap: true, 0, col: 0));
        Assert.Equal(0, wrap.LogicalToVisualLine(wordWrap: true, 0, col: 9));
        Assert.Equal(1, wrap.LogicalToVisualLine(wordWrap: true, 0, col: 15));
    }

    [Fact]
    public void WrapColStart_ReturnsCorrectOffset()
    {
        var buf = TestHelpers.MakeBuffer("12345678901234567890\nshort");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, breakAtWords: false, wrapIndent: false, buf, textAreaWidth: 80, charWidth: 8);

        Assert.Equal(0, wrap.WrapColStart(wordWrap: true, 0, 0));
        Assert.Equal(10, wrap.WrapColStart(wordWrap: true, 0, 1));
    }

    [Fact]
    public void GetPixelForPosition_WrappedLine()
    {
        var buf = TestHelpers.MakeBuffer("12345678901234567890"); // 20 chars
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, breakAtWords: false, wrapIndent: false, buf, textAreaWidth: 80, charWidth: 8);

        var (x, y) = wrap.GetPixelForPosition(
            wordWrap: true, line: 0, col: 15,
            gutterWidth: 40, gutterPadding: 8, charWidth: 8, lineHeight: 20,
            offsetX: 0, offsetY: 0);

        // col 15 is on wrap line 1, local col = 5
        Assert.Equal(40 + 8 + 5 * 8, x);
        Assert.Equal(1 * 20.0, y); // Second visual line
    }

    [Fact]
    public void HasValidData_FalseBeforeRecalculate()
    {
        var wrap = new WrapLayout();
        Assert.False(wrap.HasValidData(5));
    }

    [Fact]
    public void HasValidData_TrueAfterRecalculate()
    {
        var buf = TestHelpers.MakeBuffer("hello\nworld");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, breakAtWords: false, wrapIndent: false, buf, textAreaWidth: 500, charWidth: 8);

        Assert.True(wrap.HasValidData(buf.Count));
    }

    [Fact]
    public void WrapOff_GetPixelForPosition_IdentityMapping()
    {
        var buf = TestHelpers.MakeBuffer("hello\nworld");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: false, breakAtWords: false, wrapIndent: false, buf, textAreaWidth: 500, charWidth: 8);

        var (x, y) = wrap.GetPixelForPosition(
            wordWrap: false, line: 1, col: 3,
            gutterWidth: 40, gutterPadding: 8, charWidth: 8, lineHeight: 20,
            offsetX: 0, offsetY: 0);

        Assert.Equal(40 + 8 + 3 * 8, x);
        Assert.Equal(20.0, y); // Line 1 * lineHeight
    }

    [Fact]
    public void WordBreak_BreaksAtWordBoundary()
    {
        // "hello world test" = 16 chars, charsPerLine = 10
        // Should break: "hello " (6) | "world test" (10) — breaks after last space within limit
        var buf = TestHelpers.MakeBuffer("hello world test");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, breakAtWords: true, wrapIndent: false, buf, textAreaWidth: 80, charWidth: 8);

        Assert.Equal(2, wrap.VisualLineCount(wordWrap: true, 0));
        Assert.Equal(0, wrap.WrapColStart(wordWrap: true, 0, 0));
        // Break should be after "hello " (at column 6)
        Assert.Equal(6, wrap.WrapColStart(wordWrap: true, 0, 1));
    }

    [Fact]
    public void WordBreak_FallsBackToCharBreakForLongWord()
    {
        // "abcdefghijklmnopqrst" = 20 chars, no spaces, charsPerLine = 10
        var buf = TestHelpers.MakeBuffer("abcdefghijklmnopqrst");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, breakAtWords: true, wrapIndent: false, buf, textAreaWidth: 80, charWidth: 8);

        Assert.Equal(2, wrap.VisualLineCount(wordWrap: true, 0));
        Assert.Equal(0, wrap.WrapColStart(wordWrap: true, 0, 0));
        Assert.Equal(10, wrap.WrapColStart(wordWrap: true, 0, 1)); // hard break at 10
    }

    [Fact]
    public void WordBreak_ShortLineFitsOnOneLine()
    {
        var buf = TestHelpers.MakeBuffer("short");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, breakAtWords: true, wrapIndent: false, buf, textAreaWidth: 80, charWidth: 8);

        Assert.Equal(1, wrap.VisualLineCount(wordWrap: true, 0));
    }

    [Fact]
    public void WordBreak_GetPixelForPosition_CorrectForWrappedColumn()
    {
        // "hello world test" → breaks at col 6: "hello " | "world test"
        var buf = TestHelpers.MakeBuffer("hello world test");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, breakAtWords: true, wrapIndent: false, buf, textAreaWidth: 80, charWidth: 8);

        // col 8 = "or" in "world" → wrap line 1, local col 2
        var (x, y) = wrap.GetPixelForPosition(
            wordWrap: true, line: 0, col: 8,
            gutterWidth: 40, gutterPadding: 8, charWidth: 8, lineHeight: 20,
            offsetX: 0, offsetY: 0);

        Assert.Equal(40 + 8 + 2 * 8, x); // col 8 - wrapStart 6 = local col 2
        Assert.Equal(20.0, y);            // second visual line
    }

    [Fact]
    public void WordBreak_LogicalToVisualLine_CorrectMapping()
    {
        // "hello world test" → breaks at col 6
        var buf = TestHelpers.MakeBuffer("hello world test");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, breakAtWords: true, wrapIndent: false, buf, textAreaWidth: 80, charWidth: 8);

        Assert.Equal(0, wrap.LogicalToVisualLine(wordWrap: true, 0, col: 0));
        Assert.Equal(0, wrap.LogicalToVisualLine(wordWrap: true, 0, col: 5));
        Assert.Equal(1, wrap.LogicalToVisualLine(wordWrap: true, 0, col: 8));
    }

    [Fact]
    public void WrapIndent_ContinuationLinesHaveReducedWidth()
    {
        // "    1234567890abcdef" = 4 indent + 16 content = 20 chars
        // charsPerLine = 10, indent = 4
        // First sub-line: 10 chars (full width) → "    123456"
        // Continuation: 10-4 = 6 chars available → "7890ab" then "cdef"
        var buf = TestHelpers.MakeBuffer("    1234567890abcdef");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, breakAtWords: false, wrapIndent: true, buf, textAreaWidth: 80, charWidth: 8);

        Assert.Equal(3, wrap.VisualLineCount(wordWrap: true, 0));
        Assert.Equal(0, wrap.WrapColStart(wordWrap: true, 0, 0));
        Assert.Equal(10, wrap.WrapColStart(wordWrap: true, 0, 1));
        Assert.Equal(16, wrap.WrapColStart(wordWrap: true, 0, 2));
    }

    [Fact]
    public void WrapIndent_PixelOffsetAppliedToContinuationLines()
    {
        var buf = TestHelpers.MakeBuffer("    1234567890abcdef");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, breakAtWords: false, wrapIndent: true, buf, textAreaWidth: 80, charWidth: 8);

        // First sub-line: no indent offset
        Assert.Equal(0, wrap.WrapIndentPx(wordWrap: true, 0, 0, charWidth: 8));
        // Continuation: indent = 4, so offset = 4 * 8 = 32
        Assert.Equal(32, wrap.WrapIndentPx(wordWrap: true, 0, 1, charWidth: 8));
    }

    [Fact]
    public void WrapIndent_GetPixelForPosition_IncludesIndentOffset()
    {
        // "    1234567890abcdef" wraps at 10, indent 4
        var buf = TestHelpers.MakeBuffer("    1234567890abcdef");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, breakAtWords: false, wrapIndent: true, buf, textAreaWidth: 80, charWidth: 8);

        // col 12 is on sub-line 1 (starts at col 10), local col = 2
        // x = gutter + padding + indent(4*8=32) + 2*8
        var (x, y) = wrap.GetPixelForPosition(
            wordWrap: true, line: 0, col: 12,
            gutterWidth: 40, gutterPadding: 8, charWidth: 8, lineHeight: 20,
            offsetX: 0, offsetY: 0);

        Assert.Equal(40 + 8 + 32 + 2 * 8, x);
        Assert.Equal(20.0, y);
    }

    [Fact]
    public void WrapIndent_NoIndentLine_NoContinuationOffset()
    {
        // "1234567890abcdef" = 0 indent, 16 chars
        var buf = TestHelpers.MakeBuffer("1234567890abcdef");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, breakAtWords: false, wrapIndent: true, buf, textAreaWidth: 80, charWidth: 8);

        Assert.Equal(2, wrap.VisualLineCount(wordWrap: true, 0));
        Assert.Equal(0, wrap.WrapIndentPx(wordWrap: true, 0, 0, charWidth: 8));
        Assert.Equal(0, wrap.WrapIndentPx(wordWrap: true, 0, 1, charWidth: 8));
    }
}
