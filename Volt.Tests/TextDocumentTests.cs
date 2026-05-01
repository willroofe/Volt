using System.IO;
using System.Text;
using Volt;
using Xunit;

namespace Volt.Tests;

public class TextDocumentTests
{
    [Fact]
    public void TextBuffer_ImplementsLongRangeOperations()
    {
        ITextDocument doc = new TextBuffer();
        doc.Insert(new TextPoint(0, 0), "alpha\nbeta\ngamma");

        Assert.Equal(3, doc.LineCount);
        Assert.Equal("beta", doc.GetLineSlice(1, 0, 100));

        doc.Replace(new TextRange(new TextPoint(1, 1), new TextPoint(2, 2)), "ETA\nGA");

        Assert.Equal($"alpha{Environment.NewLine}bETA{Environment.NewLine}GAmma", doc.GetContent());
    }

    [Fact]
    public async Task PieceTreeTextDocument_OpenAsync_StreamsFileContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"volt-doc-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(path, "one\r\ntwo\r\nthree", new UTF8Encoding(false));

            var doc = await PieceTreeTextDocument.OpenAsync(path, new UTF8Encoding(false), tabSize: 4);

            Assert.IsType<PieceTreeTextDocument>(doc);
            Assert.Equal(3, doc.LineCount);
            Assert.Equal("CRLF", doc.LineEndingDisplay);
            Assert.Equal("wo", doc.GetLineSlice(1, 1, 2));
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task PieceTreeTextDocument_EditsSplitOriginalLinePieces()
    {
        var path = Path.Combine(Path.GetTempPath(), $"volt-piece-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(path, "alpha\nbeta\ngamma\ndelta", new UTF8Encoding(false));

            ITextDocument doc = await PieceTreeTextDocument.OpenAsync(path, new UTF8Encoding(false), tabSize: 4);
            doc.InsertAt(1, 4, "-edited");
            doc.InsertLine(2, "inserted");
            doc.RemoveAt(3);

            Assert.Equal(4, doc.LineCount);
            Assert.Equal("alpha", doc[0]);
            Assert.Equal("beta-edited", doc[1]);
            Assert.Equal("inserted", doc[2]);
            Assert.Equal("delta", doc[3]);
            Assert.True(doc.IsDirty);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

    [Fact]
    public async Task PieceTreeTextDocument_SaveAsync_StreamsOriginalAndEditedPieces()
    {
        var source = Path.Combine(Path.GetTempPath(), $"volt-piece-source-{Guid.NewGuid():N}.txt");
        var saved = Path.Combine(Path.GetTempPath(), $"volt-piece-saved-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(source, "one\ntwo\nthree", new UTF8Encoding(false));

            ITextDocument doc = await PieceTreeTextDocument.OpenAsync(source, new UTF8Encoding(false), tabSize: 4);
            doc.ReplaceAt(1, 0, 3, "TWO");
            await doc.SaveAsync(saved);

            Assert.Equal("one\nTWO\nthree", await File.ReadAllTextAsync(saved, Encoding.UTF8));
            Assert.False(doc.IsDirty);
        }
        finally
        {
            try { File.Delete(source); } catch { }
            try { File.Delete(saved); } catch { }
        }
    }

    [Fact]
    public async Task PieceTreeTextDocument_SaveAsync_ReindexesSavedFileForLaterEdits()
    {
        var source = Path.Combine(Path.GetTempPath(), $"volt-piece-reindex-{Guid.NewGuid():N}.txt");
        try
        {
            await File.WriteAllTextAsync(source, "short\nmiddle\nlast", new UTF8Encoding(false));

            ITextDocument doc = await PieceTreeTextDocument.OpenAsync(source, new UTF8Encoding(false), tabSize: 4);
            doc.ReplaceAt(0, 0, 5, "a much longer first line");
            await doc.SaveAsync(source);

            doc.ReplaceAt(2, 0, 4, "LAST");
            await doc.SaveAsync(source);

            Assert.Equal("a much longer first line\nmiddle\nLAST", await File.ReadAllTextAsync(source, Encoding.UTF8));
        }
        finally
        {
            try { File.Delete(source); } catch { }
        }
    }

    [Fact]
    public async Task SaveAsync_WritesDocumentWithoutMaterializingCallerContent()
    {
        var path = Path.Combine(Path.GetTempPath(), $"volt-save-{Guid.NewGuid():N}.txt");
        try
        {
            ITextDocument doc = new TextBuffer { Encoding = new UTF8Encoding(false) };
            doc.Insert(new TextPoint(0, 0), "first\nsecond");

            await doc.SaveAsync(path);

            Assert.Equal("first\r\nsecond", await File.ReadAllTextAsync(path, Encoding.UTF8));
            Assert.False(doc.IsDirty);
        }
        finally
        {
            try { File.Delete(path); } catch { }
        }
    }

}
