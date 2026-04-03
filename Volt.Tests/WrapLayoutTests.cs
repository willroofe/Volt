using Xunit;
using Volt;

namespace Volt.Tests;

public class WrapLayoutTests
{
    private static TextBuffer MakeBuffer(string content)
    {
        var buf = new TextBuffer();
        buf.SetContent(content, tabSize: 4);
        return buf;
    }

    [Fact]
    public void WrapOff_VisualLineEqualsLogicalLine()
    {
        var buf = MakeBuffer("short\nlines\nhere");
        var wrap = new WrapLayout();

        wrap.Recalculate(wordWrap: false, buf, textAreaWidth: 500, charWidth: 8);

        Assert.Equal(3, wrap.TotalVisualLines);
        Assert.Equal(0, wrap.LogicalToVisualLine(wordWrap: false, 0));
        Assert.Equal(1, wrap.LogicalToVisualLine(wordWrap: false, 1));
        Assert.Equal(2, wrap.LogicalToVisualLine(wordWrap: false, 2));
    }

    [Fact]
    public void WrapOn_LongLineProducesMultipleVisualLines()
    {
        var buf = MakeBuffer("12345678901234567890\nshort");
        var wrap = new WrapLayout();

        wrap.Recalculate(wordWrap: true, buf, textAreaWidth: 80, charWidth: 8);

        Assert.Equal(10, wrap.CharsPerVisualLine);
        Assert.Equal(2, wrap.VisualLineCount(wordWrap: true, 0));
        Assert.Equal(1, wrap.VisualLineCount(wordWrap: true, 1));
        Assert.Equal(3, wrap.TotalVisualLines);
    }

    [Fact]
    public void VisualToLogical_Roundtrip()
    {
        var buf = MakeBuffer("12345678901234567890\nshort");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, buf, textAreaWidth: 80, charWidth: 8);

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
        var buf = MakeBuffer("line1\nline2");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, buf, textAreaWidth: 500, charWidth: 8);

        var (logLine, _) = wrap.VisualToLogical(wordWrap: true, 999, buf.Count);
        Assert.True(logLine < buf.Count);
    }

    [Fact]
    public void LogicalToVisualLine_WithColumn_SelectsCorrectWrapLine()
    {
        var buf = MakeBuffer("12345678901234567890"); // 20 chars, wraps at 10
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, buf, textAreaWidth: 80, charWidth: 8);

        Assert.Equal(0, wrap.LogicalToVisualLine(wordWrap: true, 0, col: 0));
        Assert.Equal(0, wrap.LogicalToVisualLine(wordWrap: true, 0, col: 9));
        Assert.Equal(1, wrap.LogicalToVisualLine(wordWrap: true, 0, col: 15));
    }

    [Fact]
    public void WrapColStart_ReturnsCorrectOffset()
    {
        var buf = MakeBuffer("12345678901234567890\nshort");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, buf, textAreaWidth: 80, charWidth: 8);

        Assert.Equal(0, wrap.WrapColStart(wordWrap: true, 0, 0));
        Assert.Equal(10, wrap.WrapColStart(wordWrap: true, 0, 1));
    }

    [Fact]
    public void GetPixelForPosition_WrappedLine()
    {
        var buf = MakeBuffer("12345678901234567890"); // 20 chars
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, buf, textAreaWidth: 80, charWidth: 8);

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
        var buf = MakeBuffer("hello\nworld");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: true, buf, textAreaWidth: 500, charWidth: 8);

        Assert.True(wrap.HasValidData(buf.Count));
    }

    [Fact]
    public void WrapOff_GetPixelForPosition_IdentityMapping()
    {
        var buf = MakeBuffer("hello\nworld");
        var wrap = new WrapLayout();
        wrap.Recalculate(wordWrap: false, buf, textAreaWidth: 500, charWidth: 8);

        var (x, y) = wrap.GetPixelForPosition(
            wordWrap: false, line: 1, col: 3,
            gutterWidth: 40, gutterPadding: 8, charWidth: 8, lineHeight: 20,
            offsetX: 0, offsetY: 0);

        Assert.Equal(40 + 8 + 3 * 8, x);
        Assert.Equal(20.0, y); // Line 1 * lineHeight
    }
}
