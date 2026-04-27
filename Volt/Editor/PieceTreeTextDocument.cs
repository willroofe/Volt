using System.IO;
using System.Text;

namespace Volt;

/// <summary>
/// File-backed line piece document. Original file lines remain on disk and are
/// read on demand; edits are stored as added line pieces. This keeps opening and
/// scrolling large files from requiring full text materialization.
/// </summary>
public sealed class PieceTreeTextDocument : ITextDocument
{
    private const int StreamBufferSize = 1 << 20;

    private enum PieceKind { Original, Add }

    private sealed class LinePiece(PieceKind kind, int start, int count)
    {
        public PieceKind Kind { get; } = kind;
        public int Start { get; set; } = start;
        public int Count { get; set; } = count;
    }

    private sealed class LineIndex
    {
        public required long ContentStartOffset { get; init; }
        public required List<long> Starts { get; init; }
        public required long FileLength { get; init; }
        public required int MaxLineBytes { get; init; }
    }

    private readonly List<LinePiece> _pieces = [];
    private readonly List<string> _addLines = [];
    private readonly Dictionary<int, string> _originalLineCache = new();
    private FileStream? _sourceStream;
    private LineIndex _index = null!;
    private string _lineEnding = Environment.NewLine;
    private bool _isDirty;
    private long _editGeneration;
    private int _lineCount;
    private int _maxLineLength;

    public string? SourcePath { get; private set; }

    public long LineCount => _lineCount;
    public int Count => _lineCount;
    public long EditGeneration => _editGeneration;
    public string LineEnding => _lineEnding;
    public string LineEndingDisplay => _lineEnding == "\n" ? "LF" : "CRLF";
    public Encoding Encoding { get; set; } = new UTF8Encoding(false);
    public long CharCount => Math.Max(0, _index.FileLength - _index.ContentStartOffset);
    public int MaxLineLength => _maxLineLength;

    public bool IsDirty
    {
        get => _isDirty;
        set
        {
            if (_isDirty == value) return;
            _isDirty = value;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string this[int index]
    {
        get => GetLine(index);
        set => ReplaceLines(index, 1, [value]);
    }

    public event EventHandler? DirtyChanged;
    public event EventHandler<TextDocumentChangedEventArgs>? Changed;

    public static async Task<PieceTreeTextDocument> OpenAsync(
        string path,
        Encoding encoding,
        int tabSize,
        CancellationToken cancellationToken = default)
    {
        var fullPath = Path.GetFullPath(path);
        var lineEnding = FileHelper.DetectLineEnding(fullPath, encoding);
        var index = await Task.Run(() => BuildLineIndex(fullPath, encoding, cancellationToken), cancellationToken);

        if (index.Starts.Count > int.MaxValue)
            throw new InvalidDataException("This file has too many lines for the current editor UI.");

        var document = new PieceTreeTextDocument
        {
            SourcePath = fullPath,
            Encoding = encoding,
            _lineEnding = lineEnding,
            _index = index,
            _lineCount = index.Starts.Count,
            _maxLineLength = index.MaxLineBytes
        };

        document._pieces.Add(new LinePiece(PieceKind.Original, 0, document._lineCount));
        return document;
    }

    private static LineIndex BuildLineIndex(string path, Encoding encoding, CancellationToken cancellationToken)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, StreamBufferSize, FileOptions.SequentialScan);

        long contentStart = DetectPreambleLength(stream, encoding);
        stream.Position = contentStart;

        var starts = new List<long>(capacity: 4096) { contentStart };
        var buffer = new byte[StreamBufferSize];
        long absolute = contentStart;
        long currentLineStart = contentStart;
        int maxLineBytes = 0;
        byte previousByte = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0) break;

            for (int i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n') continue;
                long nextStart = absolute + i + 1;
                long length = nextStart - currentLineStart - 1; // LF
                byte beforeLf = i > 0 ? buffer[i - 1] : previousByte;
                if (length > 0 && beforeLf == (byte)'\r')
                    length--; // CR
                if (length > maxLineBytes)
                    maxLineBytes = (int)Math.Min(int.MaxValue, length);
                starts.Add(nextStart);
                currentLineStart = nextStart;
            }

            previousByte = buffer[read - 1];
            absolute += read;
        }

        long lastLength = stream.Length - currentLineStart;
        if (lastLength > maxLineBytes)
            maxLineBytes = (int)Math.Min(int.MaxValue, lastLength);

        return new LineIndex
        {
            ContentStartOffset = contentStart,
            Starts = starts,
            FileLength = stream.Length,
            MaxLineBytes = maxLineBytes
        };
    }

    private static long DetectPreambleLength(FileStream stream, Encoding encoding)
    {
        var preamble = encoding.Preamble;
        if (preamble.Length == 0) return 0;

        Span<byte> actual = stackalloc byte[preamble.Length];
        int read = stream.Read(actual);
        stream.Position = 0;
        return read == preamble.Length && actual.SequenceEqual(preamble) ? preamble.Length : 0;
    }

    public string GetLineSlice(long line, long startColumn, int maxChars)
    {
        if (line < 0 || line >= _lineCount || maxChars <= 0) return "";
        var text = GetLine((int)line);
        if (startColumn >= text.Length) return "";
        int start = (int)Math.Max(0, startColumn);
        int length = Math.Min(maxChars, text.Length - start);
        return text.Substring(start, length);
    }

    public string GetText(TextRange range, int maxChars = int.MaxValue)
    {
        var start = ClampPoint(range.Start);
        var end = ClampPoint(range.End);
        if (Compare(start, end) > 0) (start, end) = (end, start);
        if (maxChars <= 0) return "";

        if (start.Line == end.Line)
        {
            var line = GetLine((int)start.Line);
            int length = (int)Math.Min(end.Column - start.Column, maxChars);
            return line.Substring((int)start.Column, length);
        }

        var sb = new StringBuilder();
        AppendCapped(sb, GetLine((int)start.Line)[(int)start.Column..], maxChars);
        for (long line = start.Line + 1; line < end.Line && sb.Length < maxChars; line++)
        {
            AppendCapped(sb, _lineEnding, maxChars);
            AppendCapped(sb, GetLine((int)line), maxChars);
        }
        if (sb.Length < maxChars)
        {
            AppendCapped(sb, _lineEnding, maxChars);
            AppendCapped(sb, GetLine((int)end.Line)[..(int)end.Column], maxChars);
        }
        return sb.ToString();
    }

    public void Insert(TextPoint point, string text)
    {
        var p = ClampPoint(point);
        var lines = SplitText(text);
        if (lines.Count == 1)
        {
            InsertAt((int)p.Line, (int)p.Column, lines[0]);
            return;
        }

        string tail = TruncateAt((int)p.Line, (int)p.Column);
        InsertAt((int)p.Line, (int)p.Column, lines[0]);
        for (int i = 1; i < lines.Count; i++)
            InsertLine((int)p.Line + i, lines[i]);
        InsertAt((int)p.Line + lines.Count - 1, this[(int)p.Line + lines.Count - 1].Length, tail);
    }

    public void Delete(TextRange range)
    {
        var start = ClampPoint(range.Start);
        var end = ClampPoint(range.End);
        if (Compare(start, end) > 0) (start, end) = (end, start);
        if (Compare(start, end) == 0) return;

        if (start.Line == end.Line)
        {
            DeleteAt((int)start.Line, (int)start.Column, (int)(end.Column - start.Column));
            return;
        }

        string merged = GetLine((int)start.Line)[..(int)start.Column] + GetLine((int)end.Line)[(int)end.Column..];
        RemoveRange((int)start.Line + 1, (int)(end.Line - start.Line));
        this[(int)start.Line] = merged;
    }

    public void Replace(TextRange range, string text)
    {
        Delete(range);
        Insert(range.Start, text);
    }

    public int UpdateMaxForLine(int lineIndex)
    {
        if (lineIndex >= 0 && lineIndex < _lineCount)
            _maxLineLength = Math.Max(_maxLineLength, GetLine(lineIndex).Length);
        return _maxLineLength;
    }

    public void InvalidateMaxLineLength()
    {
        // Keep a conservative monotonic max for file-backed documents. Recomputing
        // by scanning every original line would defeat large-file behavior.
    }

    public void NotifyLineChanging(int lineIndex)
    {
        _editGeneration++;
        IsDirty = true;
        RaiseChanged(lineIndex, 0);
    }

    public void InsertLine(int index, string line) => InsertLines(index, [line]);

    public void RemoveAt(int index) => RemoveRange(index, 1);

    public void RemoveRange(int index, int count)
    {
        if (count <= 0) return;
        index = Math.Clamp(index, 0, _lineCount);
        count = Math.Min(count, _lineCount - index);
        int removed = count;

        while (count > 0)
        {
            var (pieceIndex, piece, offset) = FindPiece(index);
            int take = Math.Min(count, piece.Count - offset);
            RemoveFromPiece(pieceIndex, offset, take);
            count -= take;
        }

        if (_lineCount == 0)
            InsertLines(0, [""]);

        MarkDirtyAndChanged(index, -removed);
    }

    public List<string> GetLines(int start, int count)
    {
        var result = new List<string>(count);
        int end = Math.Min(_lineCount, start + count);
        for (int i = Math.Max(0, start); i < end; i++)
            result.Add(GetLine(i));
        return result;
    }

    public void ReplaceLines(int start, int removeCount, List<string> newLines)
    {
        RemoveRange(start, removeCount);
        if (newLines.Count > 0)
            InsertLines(start, newLines);
        if (_lineCount == 0)
            InsertLines(0, [""]);
    }

    public void InsertAt(int line, int col, string text)
    {
        var current = GetLine(line);
        col = Math.Clamp(col, 0, current.Length);
        this[line] = string.Concat(current.AsSpan(0, col), text, current.AsSpan(col));
    }

    public void DeleteAt(int line, int col, int length)
    {
        var current = GetLine(line);
        col = Math.Clamp(col, 0, current.Length);
        length = Math.Min(length, current.Length - col);
        this[line] = string.Concat(current.AsSpan(0, col), current.AsSpan(col + length));
    }

    public void ReplaceAt(int line, int col, int length, string text)
    {
        var current = GetLine(line);
        col = Math.Clamp(col, 0, current.Length);
        length = Math.Min(length, current.Length - col);
        this[line] = string.Concat(current.AsSpan(0, col), text, current.AsSpan(col + length));
    }

    public void JoinWithNext(int line)
    {
        if (line < 0 || line + 1 >= _lineCount) return;
        this[line] = GetLine(line) + GetLine(line + 1);
        RemoveAt(line + 1);
    }

    public string TruncateAt(int line, int col)
    {
        var current = GetLine(line);
        col = Math.Clamp(col, 0, current.Length);
        string tail = current[col..];
        this[line] = current[..col];
        return tail;
    }

    public int AppendContent(string text, int tabSize)
    {
        var newLines = SplitText(text);
        int appendStart = Math.Max(0, _lineCount - 1);
        this[appendStart] = GetLine(appendStart) + newLines[0];
        if (newLines.Count > 1)
            InsertLines(_lineCount, newLines.Skip(1).ToList());
        return appendStart;
    }

    public string GetContent()
    {
        if (CharCount > int.MaxValue)
            throw new InvalidOperationException("The document is too large to materialize as one string.");

        var sb = new StringBuilder((int)CharCount);
        for (int i = 0; i < _lineCount; i++)
        {
            if (i > 0) sb.Append(_lineEnding);
            sb.Append(GetLine(i));
        }
        return sb.ToString();
    }

    public void Clear()
    {
        CloseSourceStream();
        _pieces.Clear();
        _addLines.Clear();
        _originalLineCache.Clear();
        _lineCount = 0;
        InsertLines(0, [""]);
        IsDirty = false;
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tempPath = Path.Combine(dir, Path.GetRandomFileName());

        try
        {
            CloseSourceStream();
            await using (var output = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, StreamBufferSize, useAsync: true))
            {
                var lineEndingBytes = Encoding.GetBytes(_lineEnding);
                bool first = true;
                foreach (var piece in _pieces)
                {
                    for (int i = 0; i < piece.Count; i++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (piece.Kind == PieceKind.Original)
                        {
                            if (!first)
                                await output.WriteAsync(lineEndingBytes, cancellationToken);
                            await WriteOriginalPieceAsync(output, piece.Start, piece.Count, cancellationToken);
                            first = false;
                            break;
                        }

                        if (!first)
                            await output.WriteAsync(lineEndingBytes, cancellationToken);
                        first = false;
                        var bytes = Encoding.GetBytes(_addLines[piece.Start + i]);
                        await output.WriteAsync(bytes, cancellationToken);
                    }
                }
            }

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    CloseSourceStream();
                    File.Move(tempPath, path, overwrite: true);
                    ResetToSavedFile(path, cancellationToken);
                    IsDirty = false;
                    return;
                }
                catch (Exception) when (attempt < 3)
                {
                    await Task.Delay(50, cancellationToken);
                }
            }
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    private string GetLine(int line)
    {
        if (line < 0 || line >= _lineCount) return "";
        var (_, piece, offset) = FindPiece(line);
        return piece.Kind == PieceKind.Original
            ? ReadOriginalLine(piece.Start + offset)
            : _addLines[piece.Start + offset];
    }

    private string ReadOriginalLine(int originalLine)
    {
        if (_originalLineCache.TryGetValue(originalLine, out var cached))
            return cached;

        var bytes = ReadOriginalLineBytes(originalLine);
        var text = Encoding.GetString(bytes);
        if (_originalLineCache.Count > 512)
            _originalLineCache.Clear();
        _originalLineCache[originalLine] = text;
        return text;
    }

    private byte[] ReadOriginalLineBytes(int originalLine)
    {
        long start = _index.Starts[originalLine];
        long end = originalLine + 1 < _index.Starts.Count
            ? _index.Starts[originalLine + 1]
            : _index.FileLength;
        long length = Math.Max(0, end - start);
        if (length == 0) return [];
        if (length > int.MaxValue)
            throw new InvalidOperationException("Line is too large to materialize.");

        var bytes = new byte[(int)length];
        var stream = GetSourceStream();
        stream.Position = start;
        stream.ReadExactly(bytes);

        int trimmed = bytes.Length;
        if (trimmed > 0 && bytes[trimmed - 1] == (byte)'\n') trimmed--;
        if (trimmed > 0 && bytes[trimmed - 1] == (byte)'\r') trimmed--;
        return trimmed == bytes.Length ? bytes : bytes[..trimmed];
    }

    private async Task WriteOriginalPieceAsync(
        FileStream output,
        int originalStartLine,
        int lineCount,
        CancellationToken cancellationToken)
    {
        if (lineCount <= 0) return;
        long start = _index.Starts[originalStartLine];
        long end = GetOriginalLineContentEndOffset(originalStartLine + lineCount - 1);
        if (end <= start) return;

        await using var source = new FileStream(SourcePath!, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, StreamBufferSize, useAsync: true);
        source.Position = start;

        var buffer = new byte[StreamBufferSize];
        long remaining = end - start;
        while (remaining > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int readSize = (int)Math.Min(buffer.Length, remaining);
            int read = await source.ReadAsync(buffer.AsMemory(0, readSize), cancellationToken);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
        }
    }

    private long GetOriginalLineContentEndOffset(int originalLine)
    {
        long start = _index.Starts[originalLine];
        long end = originalLine + 1 < _index.Starts.Count
            ? _index.Starts[originalLine + 1]
            : _index.FileLength;
        if (end <= start) return end;

        var stream = GetSourceStream();
        stream.Position = end - 1;
        if (stream.ReadByte() == '\n')
            end--;
        if (end > start)
        {
            stream.Position = end - 1;
            if (stream.ReadByte() == '\r')
                end--;
        }
        return end;
    }

    private void ResetToSavedFile(string path, CancellationToken cancellationToken)
    {
        SourcePath = Path.GetFullPath(path);
        _index = BuildLineIndex(SourcePath, Encoding, cancellationToken);
        _lineCount = _index.Starts.Count;
        _maxLineLength = _index.MaxLineBytes;
        _pieces.Clear();
        _pieces.Add(new LinePiece(PieceKind.Original, 0, _lineCount));
        _addLines.Clear();
        _originalLineCache.Clear();
        _editGeneration++;
    }

    private FileStream GetSourceStream()
    {
        if (_sourceStream != null)
            return _sourceStream;

        _sourceStream = new FileStream(SourcePath!, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, StreamBufferSize, FileOptions.RandomAccess);
        return _sourceStream;
    }

    private void CloseSourceStream()
    {
        _sourceStream?.Dispose();
        _sourceStream = null;
    }

    private void InsertLines(int index, List<string> lines)
    {
        if (lines.Count == 0) return;
        index = Math.Clamp(index, 0, _lineCount);
        int addStart = _addLines.Count;
        _addLines.AddRange(lines);
        InsertPiece(index, new LinePiece(PieceKind.Add, addStart, lines.Count));
        foreach (var line in lines)
            _maxLineLength = Math.Max(_maxLineLength, line.Length);
        MarkDirtyAndChanged(index, lines.Count);
    }

    private void InsertPiece(int logicalLine, LinePiece newPiece)
    {
        if (_pieces.Count == 0 || logicalLine == _lineCount)
        {
            _pieces.Add(newPiece);
            _lineCount += newPiece.Count;
            return;
        }

        var (pieceIndex, piece, offset) = FindPiece(logicalLine);
        if (offset == 0)
        {
            _pieces.Insert(pieceIndex, newPiece);
        }
        else if (offset == piece.Count)
        {
            _pieces.Insert(pieceIndex + 1, newPiece);
        }
        else
        {
            var right = new LinePiece(piece.Kind, piece.Start + offset, piece.Count - offset);
            piece.Count = offset;
            _pieces.Insert(pieceIndex + 1, newPiece);
            _pieces.Insert(pieceIndex + 2, right);
        }
        _lineCount += newPiece.Count;
    }

    private void RemoveFromPiece(int pieceIndex, int offset, int count)
    {
        var piece = _pieces[pieceIndex];
        if (offset == 0 && count == piece.Count)
        {
            _pieces.RemoveAt(pieceIndex);
        }
        else if (offset == 0)
        {
            piece.Start += count;
            piece.Count -= count;
        }
        else if (offset + count == piece.Count)
        {
            piece.Count -= count;
        }
        else
        {
            var right = new LinePiece(piece.Kind, piece.Start + offset + count, piece.Count - offset - count);
            piece.Count = offset;
            _pieces.Insert(pieceIndex + 1, right);
        }
        _lineCount -= count;
    }

    private (int pieceIndex, LinePiece piece, int offset) FindPiece(int logicalLine)
    {
        int line = Math.Clamp(logicalLine, 0, Math.Max(0, _lineCount - 1));
        int cursor = 0;
        for (int i = 0; i < _pieces.Count; i++)
        {
            var piece = _pieces[i];
            if (line < cursor + piece.Count)
                return (i, piece, line - cursor);
            cursor += piece.Count;
        }
        var last = _pieces[^1];
        return (_pieces.Count - 1, last, last.Count);
    }

    private TextPoint ClampPoint(TextPoint point)
    {
        long line = Math.Clamp(point.Line, 0, Math.Max(0, _lineCount - 1));
        long col = Math.Clamp(point.Column, 0, GetLine((int)line).Length);
        return new TextPoint(line, col);
    }

    private void MarkDirtyAndChanged(int startLine, long lineDelta)
    {
        _editGeneration++;
        IsDirty = true;
        RaiseChanged(startLine, lineDelta);
    }

    private void RaiseChanged(int startLine, long lineDelta)
    {
        Changed?.Invoke(this, new TextDocumentChangedEventArgs(
            new TextRange(new TextPoint(startLine, 0), new TextPoint(startLine, 0)),
            lineDelta,
            _editGeneration));
    }

    private static int Compare(TextPoint left, TextPoint right)
    {
        int line = left.Line.CompareTo(right.Line);
        return line != 0 ? line : left.Column.CompareTo(right.Column);
    }

    private static List<string> SplitText(string text)
    {
        var lines = new List<string>();
        var remaining = text.AsSpan();
        while (true)
        {
            int nlIdx = remaining.IndexOfAny('\r', '\n');
            if (nlIdx < 0) break;
            lines.Add(remaining[..nlIdx].ToString());
            if (remaining[nlIdx] == '\r' && nlIdx + 1 < remaining.Length && remaining[nlIdx + 1] == '\n')
                remaining = remaining[(nlIdx + 2)..];
            else
                remaining = remaining[(nlIdx + 1)..];
        }
        lines.Add(remaining.ToString());
        return lines;
    }

    private static void AppendCapped(StringBuilder sb, string text, int maxChars)
    {
        int remaining = maxChars - sb.Length;
        if (remaining <= 0) return;
        sb.Append(text.AsSpan(0, Math.Min(remaining, text.Length)));
    }
}
