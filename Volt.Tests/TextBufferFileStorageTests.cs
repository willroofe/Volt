using System.IO;
using System.Text;
using Volt;
using Xunit;

namespace Volt.Tests;

public class TextBufferFileStorageTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "Volt.Tests", Guid.NewGuid().ToString("N"));

    public TextBufferFileStorageTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void PrepareContentFromFile_IndexesLfFileWithoutMaterializingText()
    {
        string path = Write("lf.txt", "alpha\nbeta\ngamma");
        var buffer = new TextBuffer();

        buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, new UTF8Encoding(false), tabSize: 4));

        Assert.Equal(3, buffer.Count);
        Assert.Equal("alpha", buffer[0]);
        Assert.Equal("beta", buffer[1]);
        Assert.Equal("gamma", buffer[2]);
        Assert.Equal("LF", buffer.LineEndingDisplay);
    }

    [Fact]
    public void PrepareContentFromFile_IndexesCrlfAndTrailingEmptyLine()
    {
        string path = Write("crlf.txt", "alpha\r\nbeta\r\n");
        var buffer = new TextBuffer();

        buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, new UTF8Encoding(false), tabSize: 4));

        Assert.Equal(3, buffer.Count);
        Assert.Equal("alpha", buffer[0]);
        Assert.Equal("beta", buffer[1]);
        Assert.Equal("", buffer[2]);
        Assert.Equal("CRLF", buffer.LineEndingDisplay);
    }

    [Fact]
    public void PrepareContentFromFile_SkipsUtf8Bom()
    {
        string path = Path.Combine(_dir, "bom.txt");
        File.WriteAllText(path, "first\nsecond", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        var buffer = new TextBuffer();

        buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, new UTF8Encoding(true), tabSize: 4));

        Assert.Equal("first", buffer[0]);
        Assert.Equal("second", buffer[1]);
    }

    [Fact]
    public void FileBackedBuffer_ReadsAcrossSparseCheckpoints()
    {
        string text = string.Join("\n", Enumerable.Range(0, LargeFileLineIndex.CheckpointInterval + 25)
            .Select(i => $"line {i}"));
        string path = Write("checkpoint.txt", text);
        var buffer = new TextBuffer();

        buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, new UTF8Encoding(false), tabSize: 4));

        Assert.Equal(LargeFileLineIndex.CheckpointInterval + 25, buffer.Count);
        Assert.Equal($"line {LargeFileLineIndex.CheckpointInterval + 12}", buffer[LargeFileLineIndex.CheckpointInterval + 12]);
    }

    [Fact]
    public void PieceEdits_DoNotDisturbFileBackedLines()
    {
        string path = Write("edit.txt", "alpha\nbeta\ngamma");
        var buffer = new TextBuffer();
        buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, new UTF8Encoding(false), tabSize: 4));

        buffer.InsertAt(1, 4, "-edited");
        buffer.InsertLine(2, "inserted");
        buffer.RemoveAt(0);

        Assert.Equal(3, buffer.Count);
        Assert.Equal("beta-edited", buffer[0]);
        Assert.Equal("inserted", buffer[1]);
        Assert.Equal("gamma", buffer[2]);
    }

    [Fact]
    public void SaveToFile_StreamsPiecesAndRebasesBuffer()
    {
        string path = Write("save.txt", "alpha\nbeta\ngamma");
        var buffer = new TextBuffer();
        var encoding = new UTF8Encoding(false);
        buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, encoding, tabSize: 4));

        buffer.ReplaceAt(1, 0, 4, "BETA");
        buffer.InsertLine(2, "inserted");
        buffer.SaveToFile(path, encoding, tabSize: 4);

        Assert.Equal("alpha\nBETA\ninserted\ngamma", File.ReadAllText(path, encoding));
        Assert.False(buffer.IsDirty);
        Assert.Equal("BETA", buffer[1]);
        Assert.Equal("inserted", buffer[2]);
    }

    [Fact]
    public void AddPrefixToLines_WrapsFileBackedRangeWithoutChangingLineCount()
    {
        string path = Write("prefix.txt", "alpha\nbeta\ngamma\ndelta");
        var buffer = new TextBuffer();
        buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, new UTF8Encoding(false), tabSize: 4));

        buffer.AddPrefixToLines(1, 2, "    ");

        Assert.Equal(4, buffer.Count);
        Assert.Equal("alpha", buffer[0]);
        Assert.Equal("    beta", buffer[1]);
        Assert.Equal("    gamma", buffer[2]);
        Assert.Equal("delta", buffer[3]);
    }

    [Fact]
    public void RemoveExactPrefixFromLines_UnwrapsUniformPrefix()
    {
        string path = Write("unprefix.txt", "alpha\nbeta\ngamma");
        var buffer = new TextBuffer();
        buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, new UTF8Encoding(false), tabSize: 4));

        buffer.AddPrefixToLines(0, buffer.Count, "    ");
        buffer.RemoveExactPrefixFromLines(0, buffer.Count, "    ");

        Assert.Equal("alpha", buffer[0]);
        Assert.Equal("beta", buffer[1]);
        Assert.Equal("gamma", buffer[2]);
    }

    [Fact]
    public void RemoveLeadingSpacesFromLines_LazilyUnindentsFileBackedRange()
    {
        string path = Write("leading-spaces.txt", "    alpha\n  beta\ngamma\n        delta");
        var buffer = new TextBuffer();

        buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, new UTF8Encoding(false), tabSize: 4));

        buffer.RemoveLeadingSpacesFromLines(0, buffer.Count, maxSpaces: 4);

        Assert.Equal(4, buffer.Count);
        Assert.Equal("alpha", buffer[0]);
        Assert.Equal("beta", buffer[1]);
        Assert.Equal("gamma", buffer[2]);
        Assert.Equal("    delta", buffer[3]);
    }

    [Fact]
    public void CharCountAfterEditingLastLine_UsesIndexedFileBackedPrefix()
    {
        string text = string.Join("\n", Enumerable.Range(0, LargeFileLineIndex.CheckpointInterval + 17)
            .Select(i => $"line-{i:D5}"));
        string path = Write("char-count-prefix.txt", text);
        var buffer = new TextBuffer();

        buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, new UTF8Encoding(false), tabSize: 4));
        long before = buffer.CharCount;

        int lastLine = buffer.Count - 1;
        buffer.InsertAt(lastLine, buffer.GetLineLength(lastLine), "-edited");

        Assert.Equal(text.Length + "-edited".Length, buffer.CharCount);
        Assert.Equal(before + "-edited".Length, buffer.CharCount);
    }

    [Fact]
    public void LineSnapshot_RestoresFileBackedRangeWithoutMaterializingUndoText()
    {
        string path = Write("snapshot.txt", "alpha\nbeta\ngamma\ndelta");
        var buffer = new TextBuffer();
        buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, new UTF8Encoding(false), tabSize: 4));

        TextBuffer.LineSnapshot before = buffer.SnapshotLines(1, 2);
        buffer.RemoveRange(1, 2);
        buffer.InsertLine(1, "changed");

        buffer.ReplaceLines(1, 1, before);

        Assert.Equal(4, buffer.Count);
        Assert.Equal("alpha", buffer[0]);
        Assert.Equal("beta", buffer[1]);
        Assert.Equal("gamma", buffer[2]);
        Assert.Equal("delta", buffer[3]);
    }

    [Fact]
    public void SaveSnapshotToFile_WritesSnapshotAndReturnsPreparedContentWithoutMutatingBuffer()
    {
        string path = Write("snapshot-save.txt", "alpha\nbeta\ngamma");
        var encoding = new UTF8Encoding(false);
        var buffer = new TextBuffer();
        buffer.SetPreparedContent(TextBuffer.PrepareContentFromFile(path, encoding, tabSize: 4));

        buffer.ReplaceAt(1, 0, 4, "BETA");
        buffer.InsertLine(2, "inserted");
        buffer.IsDirty = true;
        TextBuffer.LineSnapshot snapshot = buffer.SnapshotLines(0, buffer.Count);

        TextBuffer.PreparedContent prepared = TextBuffer.SaveSnapshotToFile(
            path, encoding, tabSize: 4, snapshot, buffer.LineEnding);

        Assert.True(buffer.IsDirty);
        Assert.Equal("alpha\nBETA\ninserted\ngamma", File.ReadAllText(path, encoding));

        buffer.SetPreparedContent(prepared);

        Assert.False(buffer.IsDirty);
        Assert.Equal("BETA", buffer[1]);
        Assert.Equal("inserted", buffer[2]);
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content, new UTF8Encoding(false));
        return path;
    }
}
