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
    public void IsDirty_TracksModifications()
    {
        var buf = new TextBuffer();
        buf.SetContent("hello", tabSize: 4);

        Assert.False(buf.IsDirty);

        buf.InsertAt(0, 5, "!");
        buf.IsDirty = true;

        Assert.True(buf.IsDirty);

        buf.IsDirty = false;
        Assert.False(buf.IsDirty);
    }

    [Fact]
    public void ExpandTabs_ConvertsTabsToSpaces()
    {
        Assert.Equal("    hello", TextBuffer.ExpandTabs("\thello", 4));
        Assert.Equal("a   b", TextBuffer.ExpandTabs("a\tb", 4));
    }
}
