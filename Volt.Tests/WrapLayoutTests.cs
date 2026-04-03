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
}
