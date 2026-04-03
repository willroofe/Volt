using Xunit;
using Volt;

namespace Volt.Tests;

public class TextBufferTests
{
    [Fact]
    public void SetContent_GetContent_Roundtrip()
    {
        var buf = new TextBuffer();
        buf.SetContent("hello\nworld\nfoo", tabSize: 4);

        Assert.Equal(3, buf.Count);
        Assert.Equal("hello", buf[0]);
        Assert.Equal("world", buf[1]);
        Assert.Equal("foo", buf[2]);
        Assert.Equal("hello\nworld\nfoo", buf.GetContent());
    }

    [Fact]
    public void InsertAt_InsertsTextAtColumn()
    {
        var buf = new TextBuffer();
        buf.SetContent("abcd", tabSize: 4);

        buf.InsertAt(0, 2, "XY");

        Assert.Equal("abXYcd", buf[0]);
    }

    [Fact]
    public void DeleteAt_RemovesCharacters()
    {
        var buf = new TextBuffer();
        buf.SetContent("abcdef", tabSize: 4);

        buf.DeleteAt(0, 1, 3);

        Assert.Equal("aef", buf[0]);
    }

    [Fact]
    public void ReplaceAt_ReplacesRange()
    {
        var buf = new TextBuffer();
        buf.SetContent("hello world", tabSize: 4);

        buf.ReplaceAt(0, 6, 5, "there");

        Assert.Equal("hello there", buf[0]);
    }

    [Fact]
    public void JoinWithNext_MergesLines()
    {
        var buf = new TextBuffer();
        buf.SetContent("hello\nworld", tabSize: 4);

        buf.JoinWithNext(0);

        Assert.Equal(1, buf.Count);
        Assert.Equal("helloworld", buf[0]);
    }

    [Fact]
    public void IsDirty_SetContentClearsFlag_SetterTogglesIt()
    {
        var buf = new TextBuffer();
        buf.SetContent("hello", tabSize: 4);
        Assert.False(buf.IsDirty);

        buf.IsDirty = true;
        Assert.True(buf.IsDirty);

        buf.IsDirty = false;
        Assert.False(buf.IsDirty);

        buf.SetContent("new content", tabSize: 4);
        Assert.False(buf.IsDirty);
    }

    [Fact]
    public void ExpandTabs_ConvertsTabsToSpaces()
    {
        Assert.Equal("    hello", TextBuffer.ExpandTabs("\thello", 4));
        Assert.Equal("a   b", TextBuffer.ExpandTabs("a\tb", 4));
    }

    [Fact]
    public void SetContent_DetectsCRLF()
    {
        var buf = new TextBuffer();
        buf.SetContent("line1\r\nline2", tabSize: 4);

        Assert.Equal("\r\n", buf.LineEnding);
        Assert.Equal("CRLF", buf.LineEndingDisplay);
        Assert.Equal(2, buf.Count);
        Assert.Equal("line1\r\nline2", buf.GetContent());
    }

    [Fact]
    public void SetContent_DetectsLF()
    {
        var buf = new TextBuffer();
        buf.SetContent("line1\nline2", tabSize: 4);

        Assert.Equal("\n", buf.LineEnding);
        Assert.Equal("LF", buf.LineEndingDisplay);
    }

    [Fact]
    public void SetContent_NoLineEndings_UsesEnvironmentDefault()
    {
        var buf = new TextBuffer();
        buf.SetContent("single line", tabSize: 4);

        Assert.Equal(Environment.NewLine, buf.LineEnding);
    }

    [Fact]
    public void CharCount_IncludesLineEndings()
    {
        var buf = new TextBuffer();
        buf.SetContent("ab\ncd", tabSize: 4);

        // "ab" (2) + "\n" (1) + "cd" (2) = 5
        Assert.Equal(5, buf.CharCount);
    }

    [Fact]
    public void CharCount_UpdatesAfterMutation()
    {
        var buf = new TextBuffer();
        buf.SetContent("hello", tabSize: 4);
        long before = buf.CharCount;

        buf.InsertAt(0, 5, " world");

        Assert.True(buf.CharCount > before);
        Assert.Equal(11, buf.CharCount);
    }

    [Fact]
    public void MaxLineLength_TracksLongestLine()
    {
        var buf = new TextBuffer();
        buf.SetContent("short\nthis is longer\nmed", tabSize: 4);

        Assert.Equal(14, buf.MaxLineLength);
    }

    [Fact]
    public void MaxLineLength_UpdatesOnLongerInsert()
    {
        var buf = new TextBuffer();
        buf.SetContent("abc\ndef", tabSize: 4);

        buf.InsertAt(0, 3, "0123456789");
        buf.UpdateMaxForLine(0);

        Assert.Equal(13, buf.MaxLineLength);
    }

    [Fact]
    public void TruncateAt_SplitsLine()
    {
        var buf = new TextBuffer();
        buf.SetContent("hello world", tabSize: 4);

        string tail = buf.TruncateAt(0, 5);

        Assert.Equal("hello", buf[0]);
        Assert.Equal(" world", tail);
    }

    [Fact]
    public void ReplaceLines_ReplacesRange()
    {
        var buf = new TextBuffer();
        buf.SetContent("aaa\nbbb\nccc\nddd", tabSize: 4);

        buf.ReplaceLines(1, 2, ["XXX"]);

        Assert.Equal(3, buf.Count);
        Assert.Equal("aaa", buf[0]);
        Assert.Equal("XXX", buf[1]);
        Assert.Equal("ddd", buf[2]);
    }

    [Fact]
    public void Clear_ResetsToSingleEmptyLine()
    {
        var buf = new TextBuffer();
        buf.SetContent("aaa\nbbb\nccc", tabSize: 4);

        buf.Clear();

        Assert.Equal(1, buf.Count);
        Assert.Equal("", buf[0]);
    }

    [Fact]
    public void AppendContent_JoinsOntoLastLine()
    {
        var buf = new TextBuffer();
        buf.SetContent("line1\npartial", tabSize: 4);

        int start = buf.AppendContent(" end\nline3", tabSize: 4);

        Assert.Equal(1, start);
        Assert.Equal(3, buf.Count);
        Assert.Equal("partial end", buf[1]);
        Assert.Equal("line3", buf[2]);
    }
}
