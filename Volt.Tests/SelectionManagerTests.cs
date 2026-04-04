using Xunit;
using Volt;

namespace Volt.Tests;

public class SelectionManagerTests
{
    [Fact]
    public void GetSelectedText_SingleLine()
    {
        var buf = TestHelpers.MakeBuffer("hello world");
        var sel = new SelectionManager();
        sel.AnchorLine = 0;
        sel.AnchorCol = 6;
        sel.HasSelection = true;

        var text = sel.GetSelectedText(buf, caretLine: 0, caretCol: 11);
        Assert.Equal("world", text);
    }

    [Fact]
    public void GetSelectedText_MultiLine()
    {
        var buf = TestHelpers.MakeBuffer("hello\nbeautiful\nworld");
        var sel = new SelectionManager();
        sel.AnchorLine = 0;
        sel.AnchorCol = 2;
        sel.HasSelection = true;

        var text = sel.GetSelectedText(buf, caretLine: 2, caretCol: 3);
        // Should use the buffer's line ending (LF in this case)
        Assert.Equal("llo\nbeautiful\nwor", text);
    }

    [Fact]
    public void GetSelectedText_BackwardSelection()
    {
        var buf = TestHelpers.MakeBuffer("abcdef");
        var sel = new SelectionManager();
        sel.AnchorLine = 0;
        sel.AnchorCol = 5;
        sel.HasSelection = true;

        // Caret before anchor — backward selection
        var text = sel.GetSelectedText(buf, caretLine: 0, caretCol: 1);
        Assert.Equal("bcde", text);
    }

    [Fact]
    public void GetSelectedText_NoSelection_ReturnsEmpty()
    {
        var buf = TestHelpers.MakeBuffer("hello");
        var sel = new SelectionManager();

        var text = sel.GetSelectedText(buf, caretLine: 0, caretCol: 3);
        Assert.Equal("", text);
    }

    [Fact]
    public void DeleteSelection_SingleLine()
    {
        var buf = TestHelpers.MakeBuffer("hello world");
        var sel = new SelectionManager();
        sel.AnchorLine = 0;
        sel.AnchorCol = 5;
        sel.HasSelection = true;

        var (line, col) = sel.DeleteSelection(buf, caretLine: 0, caretCol: 11);

        Assert.Equal("hello", buf[0]);
        Assert.Equal(0, line);
        Assert.Equal(5, col);
        Assert.False(sel.HasSelection);
    }

    [Fact]
    public void DeleteSelection_MultiLine()
    {
        var buf = TestHelpers.MakeBuffer("aaa\nbbb\nccc");
        var sel = new SelectionManager();
        sel.AnchorLine = 0;
        sel.AnchorCol = 1;
        sel.HasSelection = true;

        var (line, col) = sel.DeleteSelection(buf, caretLine: 2, caretCol: 2);

        Assert.Equal(1, buf.Count);
        Assert.Equal("ac", buf[0]);
        Assert.Equal(0, line);
        Assert.Equal(1, col);
    }

    [Fact]
    public void DeleteSelection_NoSelection_DoesNothing()
    {
        var buf = TestHelpers.MakeBuffer("hello");
        var sel = new SelectionManager();

        var (line, col) = sel.DeleteSelection(buf, caretLine: 0, caretCol: 3);

        Assert.Equal("hello", buf[0]);
        Assert.Equal(0, line);
        Assert.Equal(3, col);
    }

    [Fact]
    public void ClampToBuffer_ClampsOutOfRange()
    {
        var buf = TestHelpers.MakeBuffer("short");
        var sel = new SelectionManager();
        sel.AnchorLine = 99;
        sel.AnchorCol = 99;
        int caretLine = 50, caretCol = 50;

        sel.ClampToBuffer(buf, ref caretLine, ref caretCol);

        Assert.Equal(0, sel.AnchorLine);
        Assert.Equal(5, sel.AnchorCol); // "short".Length
        Assert.Equal(0, caretLine);
        Assert.Equal(5, caretCol);
    }

    [Fact]
    public void GetOrdered_ForwardSelection()
    {
        var sel = new SelectionManager();
        sel.AnchorLine = 0;
        sel.AnchorCol = 0;

        var (sl, sc, el, ec) = sel.GetOrdered(caretLine: 2, caretCol: 5);
        Assert.Equal(0, sl);
        Assert.Equal(0, sc);
        Assert.Equal(2, el);
        Assert.Equal(5, ec);
    }

    [Fact]
    public void GetOrdered_BackwardSelection()
    {
        var sel = new SelectionManager();
        sel.AnchorLine = 2;
        sel.AnchorCol = 5;

        var (sl, sc, el, ec) = sel.GetOrdered(caretLine: 0, caretCol: 0);
        Assert.Equal(0, sl);
        Assert.Equal(0, sc);
        Assert.Equal(2, el);
        Assert.Equal(5, ec);
    }

    [Fact]
    public void Start_SetsAnchorAndHasSelection()
    {
        var sel = new SelectionManager();
        sel.Start(3, 7);

        Assert.True(sel.HasSelection);
        Assert.Equal(3, sel.AnchorLine);
        Assert.Equal(7, sel.AnchorCol);
    }

    [Fact]
    public void Start_DoesNotResetAnchor_WhenAlreadyActive()
    {
        var sel = new SelectionManager();
        sel.Start(3, 7);
        sel.Start(0, 0); // Should be ignored

        Assert.Equal(3, sel.AnchorLine);
        Assert.Equal(7, sel.AnchorCol);
    }

    [Fact]
    public void GetSelectedText_UsesBufferLineEnding()
    {
        var buf = new TextBuffer();
        buf.SetContent("line1\r\nline2\r\nline3", tabSize: 4);
        var sel = new SelectionManager();
        sel.AnchorLine = 0;
        sel.AnchorCol = 0;
        sel.HasSelection = true;

        var text = sel.GetSelectedText(buf, caretLine: 2, caretCol: 5);
        // Should use CRLF since the buffer detected CRLF
        Assert.Equal("line1\r\nline2\r\nline3", text);
    }
}
