using System.Collections;
using Xunit;
using Volt;

namespace Volt.Tests;

public class CodeFoldingTests
{
    [Fact]
    public void FoldOnly_HiddenLinesGetZeroVisualLines()
    {
        var buf = TestHelpers.MakeBuffer(
            "if (x) {",
            "    foo();",
            "    bar();",
            "}",
            "end");
        var wrap = new WrapLayout();
        var hidden = new BitArray(5);
        hidden[1] = true; // hide "    foo();"
        hidden[2] = true; // hide "    bar();"
        // line 3 "}" stays visible (closer is not hidden)

        wrap.Recalculate(wordWrap: false, breakAtWords: false, wrapIndent: false,
            buf, textAreaWidth: 500, charWidth: 8, hiddenLines: hidden);

        Assert.Equal(3, wrap.TotalVisualLines); // lines 0, 3, and 4 visible
        Assert.Equal(0, wrap.CumulOffset(0));   // line 0 → display 0
        Assert.Equal(1, wrap.CumulOffset(1));   // line 1 hidden, offset = 1 (after line 0)
        Assert.Equal(1, wrap.CumulOffset(2));   // line 2 hidden, same
        Assert.Equal(1, wrap.CumulOffset(3));   // line 3 "}" → display 1
        Assert.Equal(2, wrap.CumulOffset(4));   // line 4 → display 2
    }

    [Fact]
    public void FoldOnly_VisualToLogical_SkipsHiddenLines()
    {
        var buf = TestHelpers.MakeBuffer(
            "line0",
            "line1",
            "line2",
            "line3",
            "line4");
        var wrap = new WrapLayout();
        var hidden = new BitArray(5);
        hidden[1] = true;
        hidden[2] = true;

        wrap.Recalculate(wordWrap: false, breakAtWords: false, wrapIndent: false,
            buf, textAreaWidth: 500, charWidth: 8, hiddenLines: hidden);

        Assert.Equal(3, wrap.TotalVisualLines);
        // Visual line 0 → logical line 0
        var (log0, _) = wrap.VisualToLogical(false, 0, 5);
        Assert.Equal(0, log0);
        // Visual line 1 → logical line 3 (skips hidden 1,2)
        var (log1, _) = wrap.VisualToLogical(false, 1, 5);
        Assert.Equal(3, log1);
        // Visual line 2 → logical line 4
        var (log2, _) = wrap.VisualToLogical(false, 2, 5);
        Assert.Equal(4, log2);
    }

    [Fact]
    public void FoldOnly_LogicalToVisualLine_AccountsForHidden()
    {
        var buf = TestHelpers.MakeBuffer("a", "b", "c", "d", "e");
        var wrap = new WrapLayout();
        var hidden = new BitArray(5);
        hidden[1] = true;
        hidden[2] = true;

        wrap.Recalculate(wordWrap: false, breakAtWords: false, wrapIndent: false,
            buf, textAreaWidth: 500, charWidth: 8, hiddenLines: hidden);

        Assert.Equal(0, wrap.LogicalToVisualLine(false, 0));
        Assert.Equal(1, wrap.LogicalToVisualLine(false, 1)); // hidden, but offset is 1
        Assert.Equal(1, wrap.LogicalToVisualLine(false, 2)); // hidden, same
        Assert.Equal(1, wrap.LogicalToVisualLine(false, 3)); // first visible after hidden
        Assert.Equal(2, wrap.LogicalToVisualLine(false, 4));
    }

    [Fact]
    public void FoldOnly_VisualLineCount_ZeroForHidden()
    {
        var buf = TestHelpers.MakeBuffer("a", "b", "c");
        var wrap = new WrapLayout();
        var hidden = new BitArray(3);
        hidden[1] = true;

        wrap.Recalculate(wordWrap: false, breakAtWords: false, wrapIndent: false,
            buf, textAreaWidth: 500, charWidth: 8, hiddenLines: hidden);

        Assert.Equal(1, wrap.VisualLineCount(false, 0));
        Assert.Equal(0, wrap.VisualLineCount(false, 1));
        Assert.Equal(1, wrap.VisualLineCount(false, 2));
    }

    [Fact]
    public void NoFolds_NonWrap_ArraysNotAllocated()
    {
        var buf = TestHelpers.MakeBuffer("a", "b", "c");
        var wrap = new WrapLayout();

        wrap.Recalculate(wordWrap: false, breakAtWords: false, wrapIndent: false,
            buf, textAreaWidth: 500, charWidth: 8, hiddenLines: null);

        Assert.Equal(3, wrap.TotalVisualLines);
        // CumulOffset falls back to identity when arrays are null
        Assert.Equal(0, wrap.CumulOffset(0));
        Assert.Equal(1, wrap.CumulOffset(1));
        Assert.Equal(2, wrap.CumulOffset(2));
    }

    [Fact]
    public void WrapWithFolds_HiddenLinesSkipped()
    {
        // "12345678901234567890" wraps to 2 visual lines at width 80 (10 chars)
        var buf = TestHelpers.MakeBuffer(
            "12345678901234567890",
            "hidden",
            "short");
        var wrap = new WrapLayout();
        var hidden = new BitArray(3);
        hidden[1] = true;

        wrap.Recalculate(wordWrap: true, breakAtWords: false, wrapIndent: false,
            buf, textAreaWidth: 80, charWidth: 8, hiddenLines: hidden);

        // Line 0 wraps to 2 visual lines, line 1 hidden (0), line 2 = 1
        Assert.Equal(3, wrap.TotalVisualLines);
        Assert.Equal(0, wrap.CumulOffset(0));
        Assert.Equal(2, wrap.CumulOffset(1)); // hidden but offset continues
        Assert.Equal(2, wrap.CumulOffset(2)); // starts right after line 0's 2 visual lines
    }

    [Fact]
    public void IsBlockOpener_And_FindStructuralCloser_WorkTogether()
    {
        Assert.True(EditorControl.IsBlockOpener("if (x) {"));
        Assert.True(EditorControl.IsBlockCloser("}"));
        Assert.False(EditorControl.IsBlockOpener("x = 1;"));
        Assert.False(EditorControl.IsBlockCloser("x = 1;"));
    }

    [Fact]
    public void GetPixelForPosition_FoldOnly_UsesHorizontalScroll()
    {
        var buf = TestHelpers.MakeBuffer("a", "hidden", "b");
        var wrap = new WrapLayout();
        var hidden = new BitArray(3);
        hidden[1] = true;

        wrap.Recalculate(wordWrap: false, breakAtWords: false, wrapIndent: false,
            buf, textAreaWidth: 500, charWidth: 8, hiddenLines: hidden);

        // Line 2 should be at display line 1 (line 1 is hidden)
        var (_, y) = wrap.GetPixelForPosition(false, 2, 0, 40, 4, 8, 20, 0, 0);
        Assert.Equal(20, y); // display line 1 × lineHeight 20

        // Horizontal scroll should still work (offsetX = 10)
        var (x, _) = wrap.GetPixelForPosition(false, 0, 5, 40, 4, 8, 20, 10, 0);
        Assert.Equal(40 + 4 + 5 * 8 - 10, x);
    }
}
