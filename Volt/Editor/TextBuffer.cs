using System.IO;
using System.Text;

namespace Volt;

/// <summary>
/// Manages text content as a sequence of line pieces. Pieces may reference an
/// indexed file or in-memory edit text, so the editor can keep one buffer model
/// without materializing large files into a single managed string.
/// </summary>
public class TextBuffer
{
    internal sealed record LinePiece(ITextSource Source, int StartLine, int LineCount);

    public sealed class LineSnapshot : ILanguageTextSource, ILanguageTextStreamSource
    {
        internal LineSnapshot(List<LinePiece> pieces, int count)
        {
            Pieces = pieces;
            Count = count;
            _pieceStartLines = new int[pieces.Count];
            int startLine = 0;
            long charCount = 0;
            for (int i = 0; i < pieces.Count; i++)
            {
                LinePiece piece = pieces[i];
                _pieceStartLines[i] = startLine;
                startLine += piece.LineCount;
                charCount += piece.Source.GetCharCountWithoutLineEndings(piece.StartLine, piece.LineCount);
            }

            CharCountWithoutLineEndings = charCount;
        }

        private readonly int[] _pieceStartLines;

        internal List<LinePiece> Pieces { get; }
        public int Count { get; }
        public int LineCount => Count;
        public long CharCountWithoutLineEndings { get; }

        public int GetLineLength(int line)
        {
            var (pieceIndex, offset) = FindPiece(line);
            LinePiece piece = Pieces[pieceIndex];
            return piece.Source.GetLineLength(piece.StartLine + offset);
        }

        public string GetLineSegment(int line, int startColumn, int length)
        {
            var (pieceIndex, offset) = FindPiece(line);
            LinePiece piece = Pieces[pieceIndex];
            return piece.Source.GetLineSegment(piece.StartLine + offset, startColumn, length);
        }

        bool ILanguageTextStreamSource.TryCreateTextStream(int startLine, int lineCount, out ILanguageTextStream stream)
        {
            stream = null!;
            if (lineCount <= 0 || startLine < 0 || startLine + lineCount > Count)
                return false;

            var (pieceIndex, offset) = FindPiece(startLine);
            LinePiece piece = Pieces[pieceIndex];
            if (offset + lineCount > piece.LineCount
                || piece.Source is not ILanguageTextStreamSource streamSource)
            {
                return false;
            }

            return streamSource.TryCreateTextStream(piece.StartLine + offset, lineCount, out stream);
        }

        private (int pieceIndex, int offset) FindPiece(int line)
        {
            if ((uint)line >= (uint)Count)
                throw new ArgumentOutOfRangeException(nameof(line));

            int low = 0;
            int high = _pieceStartLines.Length - 1;
            while (low <= high)
            {
                int mid = low + (high - low) / 2;
                if (_pieceStartLines[mid] <= line)
                    low = mid + 1;
                else
                    high = mid - 1;
            }

            int pieceIndex = Math.Max(0, high);
            return (pieceIndex, line - _pieceStartLines[pieceIndex]);
        }
    }

    private readonly List<LinePiece> _pieces = [];
    private int _lineCount;
    private int _maxLineLength;
    private bool _maxLineLengthDirty = true;
    private long _charCount;
    private bool _charCountDirty = true;
    private string _lineEnding = "\r\n";
    private bool _isDirty;
    private long _editGeneration;

    public TextBuffer()
    {
        ResetToSingleEmptyLine();
    }

    public int Count => _lineCount;

    /// <summary>Incremented on every mutation. Used for cache invalidation.</summary>
    public long EditGeneration => _editGeneration;

    public string this[int index]
    {
        get
        {
            var (pieceIndex, offset) = FindPiece(index);
            LinePiece piece = _pieces[pieceIndex];
            return piece.Source.GetLine(piece.StartLine + offset);
        }
        set => ReplaceLines(index, 1, [value]);
    }

    public int GetLineLength(int index)
    {
        var (pieceIndex, offset) = FindPiece(index);
        LinePiece piece = _pieces[pieceIndex];
        return piece.Source.GetLineLength(piece.StartLine + offset);
    }

    public string GetLineSegment(int index, int startColumn, int length)
    {
        var (pieceIndex, offset) = FindPiece(index);
        LinePiece piece = _pieces[pieceIndex];
        return piece.Source.GetLineSegment(piece.StartLine + offset, startColumn, length);
    }

    public string LineEnding => _lineEnding;
    public string LineEndingDisplay => _lineEnding == "\n" ? "LF" : "CRLF";

    /// <summary>Total character count including line endings.</summary>
    public long CharCount
    {
        get
        {
            if (_charCountDirty)
            {
                long total = 0;
                foreach (LinePiece piece in _pieces)
                    total += piece.Source.GetCharCountWithoutLineEndings(piece.StartLine, piece.LineCount);
                total += Math.Max(0, _lineCount - 1L) * _lineEnding.Length;
                _charCount = total;
                _charCountDirty = false;
            }
            return _charCount;
        }
    }

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

    public event EventHandler? DirtyChanged;

    // -- Max line length tracking ------------------------------------
    public int MaxLineLength
    {
        get
        {
            if (_maxLineLengthDirty)
            {
                int max = 0;
                foreach (LinePiece piece in _pieces)
                    max = Math.Max(max, piece.Source.GetMaxLineLength(piece.StartLine, piece.LineCount));
                _maxLineLength = max;
                _maxLineLengthDirty = false;
            }
            return _maxLineLength;
        }
    }

    public void InvalidateMaxLineLength()
    {
        _maxLineLengthDirty = true;
        _charCountDirty = true;
    }

    /// <summary>
    /// Fast path: only check a specific line against the cached max.
    /// Returns the current max (may be stale if lines were shortened).
    /// </summary>
    public int UpdateMaxForLine(int lineIndex)
    {
        if (_maxLineLengthDirty) return MaxLineLength;
        if (lineIndex < _lineCount)
            _maxLineLength = Math.Max(_maxLineLength, GetLineLength(lineIndex));
        return _maxLineLength;
    }

    /// <summary>
    /// Call before modifying a line that might currently be the longest.
    /// Marks max dirty if the line's current length equals or exceeds the cached max.
    /// </summary>
    public void NotifyLineChanging(int lineIndex)
    {
        _charCountDirty = true;
        _editGeneration++;
        if (!_maxLineLengthDirty && lineIndex < _lineCount
            && GetLineLength(lineIndex) >= _maxLineLength)
            _maxLineLengthDirty = true;
    }

    // -- Line manipulation -------------------------------------------
    public void InsertLine(int index, string line)
    {
        InsertLines(index, [line]);
    }

    public void RemoveAt(int index)
    {
        RemoveRange(index, 1);
    }

    public void RemoveRange(int index, int count)
    {
        if (count <= 0) return;
        ValidateRange(index, count);

        int removeEnd = index + count;
        int globalLine = 0;
        var next = new List<LinePiece>(_pieces.Count + 1);

        foreach (LinePiece piece in _pieces)
        {
            int pieceStart = globalLine;
            int pieceEnd = globalLine + piece.LineCount;

            if (pieceEnd <= index || pieceStart >= removeEnd)
            {
                next.Add(piece);
            }
            else
            {
                int leftCount = Math.Max(0, index - pieceStart);
                if (leftCount > 0)
                    next.Add(piece with { LineCount = leftCount });

                int rightCount = Math.Max(0, pieceEnd - removeEnd);
                if (rightCount > 0)
                {
                    int consumedFromPiece = removeEnd - pieceStart;
                    next.Add(piece with
                    {
                        StartLine = piece.StartLine + consumedFromPiece,
                        LineCount = rightCount
                    });
                }
            }

            globalLine = pieceEnd;
        }

        _pieces.Clear();
        _pieces.AddRange(next);
        _lineCount -= count;
        if (_lineCount == 0)
            ResetToSingleEmptyLine();
        else
            NormalizePieces();

        MarkStructureChanged();
    }

    public LineSnapshot SnapshotLines(int start, int count)
    {
        ValidateRange(start, count);
        var snapshotPieces = new List<LinePiece>();

        int remaining = count;
        int currentLine = start;
        while (remaining > 0)
        {
            var (pieceIndex, offset) = FindPiece(currentLine);
            LinePiece piece = _pieces[pieceIndex];
            int take = Math.Min(remaining, piece.LineCount - offset);
            snapshotPieces.Add(piece with
            {
                StartLine = piece.StartLine + offset,
                LineCount = take
            });
            currentLine += take;
            remaining -= take;
        }

        return new LineSnapshot(snapshotPieces, count);
    }

    /// <summary>Get a copy of a range of lines.</summary>
    public List<string> GetLines(int start, int count)
    {
        ValidateRange(start, Math.Max(0, Math.Min(count, _lineCount - start)));
        var result = new List<string>(count);
        foreach (string line in EnumerateLines(start, count))
            result.Add(line);
        return result;
    }

    /// <summary>
    /// Replace a range of lines with new lines (for undo/redo application).
    /// Removes <paramref name="removeCount"/> lines starting at <paramref name="start"/>,
    /// then inserts <paramref name="newLines"/> at that position.
    /// </summary>
    public void ReplaceLines(int start, int removeCount, List<string> newLines)
    {
        if (removeCount > 0)
            RemoveRange(start, removeCount);
        InsertLines(start, newLines);
        if (_lineCount == 0)
            ResetToSingleEmptyLine();
    }

    public void ReplaceLines(int start, int removeCount, LineSnapshot snapshot)
    {
        if (removeCount > 0)
            RemoveRange(start, removeCount);
        InsertLineSnapshot(start, snapshot);
        if (_lineCount == 0)
            ResetToSingleEmptyLine();
    }

    public void InsertLineSnapshot(int index, LineSnapshot snapshot)
    {
        InsertPieces(index, snapshot.Pieces, snapshot.Count);
    }

    public void AddPrefixToLines(int start, int count, string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || count <= 0) return;
        ValidateRange(start, count);

        TransformPiecesInRange(start, count, piece =>
            piece with { Source = new PrefixedTextSource(piece.Source, prefix) });
        MarkStructureChanged();
    }

    public void RemoveExactPrefixFromLines(int start, int count, string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || count <= 0) return;
        ValidateRange(start, count);

        TransformPiecesInRange(start, count, piece =>
            piece.Source is PrefixedTextSource prefixed
                ? RemovePrefixFromPrefixedPiece(piece, prefixed, prefix)
                : piece with { Source = new FixedPrefixRemovalTextSource(piece.Source, prefix) });
        MarkStructureChanged();
    }

    public void RemoveLeadingSpacesFromLines(int start, int count, int maxSpaces)
    {
        if (maxSpaces <= 0 || count <= 0) return;
        ValidateRange(start, count);

        TransformPiecesInRange(start, count, piece => RemoveLeadingSpacesFromPiece(piece, maxSpaces));
        MarkStructureChanged();
    }

    // -- Inline text mutations ---------------------------------------

    /// <summary>Insert text at a column position in a line.</summary>
    public void InsertAt(int line, int col, string text)
    {
        string current = this[line];
        ReplaceLineInPlace(line, string.Concat(current.AsSpan(0, col), text, current.AsSpan(col)),
            current.Length);
    }

    /// <summary>Delete a range of characters from a line.</summary>
    public void DeleteAt(int line, int col, int length)
    {
        string current = this[line];
        ReplaceLineInPlace(line, string.Concat(current.AsSpan(0, col), current.AsSpan(col + length)),
            current.Length);
    }

    /// <summary>Replace a range of characters from a line with new text.</summary>
    public void ReplaceAt(int line, int col, int length, string text)
    {
        string current = this[line];
        ReplaceLineInPlace(line, string.Concat(current.AsSpan(0, col), text, current.AsSpan(col + length)),
            current.Length);
    }

    /// <summary>Join a line with the next line, removing the next line.</summary>
    public void JoinWithNext(int line)
    {
        string merged = string.Concat(this[line], this[line + 1]);
        ReplaceLines(line, 2, [merged]);
    }

    /// <summary>Truncate a line at the given column, returning the removed tail.</summary>
    public string TruncateAt(int line, int col)
    {
        string current = this[line];
        string tail = current[col..];
        ReplaceLineInPlace(line, current[..col], current.Length);
        return tail;
    }

    // -- Content get/set ---------------------------------------------

    /// <summary>Result of content preparation on a background thread.</summary>
    public sealed class PreparedContent
    {
        internal ITextSource Source { get; init; } = null!;
        internal string LineEnding { get; init; } = null!;
    }

    /// <summary>
    /// Parse text into line pieces on a background thread. This keeps the same
    /// public preparation API while avoiding a large intermediate string[].
    /// </summary>
    public static PreparedContent PrepareContent(string text, int tabSize,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string lineEnding = DetectLineEnding(text);
        var lines = new List<string>();
        var remaining = text.AsSpan();
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int nlIdx = remaining.IndexOfAny('\r', '\n');
            if (nlIdx < 0) break;
            lines.Add(ExpandTabs(remaining.Slice(0, nlIdx).ToString(), tabSize));
            if (remaining[nlIdx] == '\r' && nlIdx + 1 < remaining.Length && remaining[nlIdx + 1] == '\n')
                remaining = remaining.Slice(nlIdx + 2);
            else
                remaining = remaining.Slice(nlIdx + 1);
        }
        lines.Add(ExpandTabs(remaining.ToString(), tabSize));
        return new PreparedContent { Source = new MemoryTextSource(lines), LineEnding = lineEnding };
    }

    public static PreparedContent PrepareContentFromFile(string path, Encoding encoding, int tabSize,
        CancellationToken cancellationToken = default)
    {
        return PrepareContentFromFile(path, encoding, tabSize, progress: null, cancellationToken);
    }

    internal static PreparedContent PrepareContentFromFile(
        string path,
        Encoding encoding,
        int tabSize,
        IProgress<FileLoadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        using var profile = VoltProfiler.Span("TextBuffer.PrepareContentFromFile", "file", Path.GetFileName(path));
        cancellationToken.ThrowIfCancellationRequested();
        long fileSize = new FileInfo(path).Length;
        if (FileTextSource.SupportsEncoding(encoding))
        {
            progress?.Report(FileLoadProgress.ForBytes("Indexing file", 0, fileSize));
            LargeFileLineIndex index;
            using (VoltProfiler.Span("TextBuffer.BuildLargeFileLineIndex"))
                index = LargeFileLineIndex.Build(path, encoding, progress, cancellationToken);
            progress?.Report(FileLoadProgress.Complete("File indexed", fileSize));
            return new PreparedContent
            {
                Source = new FileTextSource(path, encoding, tabSize, index),
                LineEnding = index.LineEnding
            };
        }

        progress?.Report(FileLoadProgress.Indeterminate("Reading file"));
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 20, FileOptions.SequentialScan);
        using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: false, bufferSize: 1 << 20);
        string text = reader.ReadToEnd();
        cancellationToken.ThrowIfCancellationRequested();
        PreparedContent prepared = PrepareContent(text, tabSize, cancellationToken);
        progress?.Report(FileLoadProgress.Complete("File loaded", fileSize));
        return prepared;
    }

    /// <summary>Apply prepared content to the buffer. Must be called on the UI thread.</summary>
    public void SetPreparedContent(PreparedContent prepared)
    {
        _lineEnding = prepared.LineEnding;
        _pieces.Clear();
        _pieces.Add(new LinePiece(prepared.Source, 0, prepared.Source.LineCount));
        _lineCount = prepared.Source.LineCount;
        _maxLineLengthDirty = true;
        _charCountDirty = true;
        _editGeneration++;
        IsDirty = false;
    }

    public void SetContent(string text, int tabSize)
    {
        SetPreparedContent(PrepareContent(text, tabSize));
    }

    /// <summary>
    /// Appends text read from the tail of a file. The first piece of new text is
    /// joined onto the current last line and remaining pieces become new lines.
    /// </summary>
    public int AppendContent(string text, int tabSize)
    {
        var newLines = SplitLines(text, tabSize);
        int appendStart = _lineCount - 1;
        string joined = this[appendStart] + newLines[0];
        var replacement = new List<string>(newLines.Count) { joined };
        for (int i = 1; i < newLines.Count; i++)
            replacement.Add(newLines[i]);

        ReplaceLines(appendStart, 1, replacement);
        return appendStart;
    }

    public string GetContent()
    {
        if (CharCount > int.MaxValue)
            throw new InvalidOperationException("The document is too large to materialize as a single string.");

        return string.Join(_lineEnding, EnumerateLines(0, _lineCount));
    }

    public void SaveToFile(string path, Encoding encoding, int tabSize)
    {
        LineSnapshot snapshot = SnapshotLines(0, _lineCount);
        SetPreparedContent(SaveSnapshotToFile(path, encoding, tabSize, snapshot, _lineEnding));
    }

    public static PreparedContent SaveSnapshotToFile(
        string path,
        Encoding encoding,
        int tabSize,
        LineSnapshot snapshot,
        string lineEnding)
    {
        string dir = Path.GetDirectoryName(path)!;
        string tempPath = Path.Combine(dir, Path.GetRandomFileName());

        try
        {
            using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                       FileShare.None, bufferSize: 1 << 20, FileOptions.SequentialScan))
            using (var writer = new StreamWriter(stream, encoding, bufferSize: 1 << 20))
            {
                bool first = true;
                foreach (string line in EnumerateSnapshotLines(snapshot))
                {
                    if (!first)
                        writer.Write(lineEnding);
                    writer.Write(line);
                    first = false;
                }
            }

            File.Move(tempPath, path, overwrite: true);
            return PrepareContentFromFile(path, encoding, tabSize);
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    public void Clear()
    {
        _pieces.Clear();
        ResetToSingleEmptyLine();
        _editGeneration++;
    }

    internal IEnumerable<string> EnumerateLines(int startLine, int count)
    {
        if (count <= 0)
            yield break;

        ValidateRange(startLine, Math.Min(count, _lineCount - startLine));

        int remaining = count;
        int currentLine = startLine;
        while (remaining > 0)
        {
            var (pieceIndex, offset) = FindPiece(currentLine);
            LinePiece piece = _pieces[pieceIndex];
            int take = Math.Min(remaining, piece.LineCount - offset);
            foreach (string line in piece.Source.EnumerateLines(piece.StartLine + offset, take))
                yield return line;

            currentLine += take;
            remaining -= take;
        }
    }

    private static IEnumerable<string> EnumerateSnapshotLines(LineSnapshot snapshot)
    {
        foreach (LinePiece piece in snapshot.Pieces)
        {
            foreach (string line in piece.Source.EnumerateLines(piece.StartLine, piece.LineCount))
                yield return line;
        }
    }

    // -- Tab expansion -----------------------------------------------
    [ThreadStatic] private static StringBuilder? _expandTabsSb;

    public static string ExpandTabs(string line, int tabSize)
    {
        if (!line.Contains('\t')) return line;
        var sb = _expandTabsSb ??= new StringBuilder(256);
        sb.Clear();
        sb.EnsureCapacity(line.Length + 16);
        foreach (char c in line)
        {
            if (c == '\t')
            {
                int spaces = tabSize - (sb.Length % tabSize);
                sb.Append(' ', spaces);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // -- Internals ----------------------------------------------------
    private void InsertLines(int index, IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return;
        var source = new MemoryTextSource(lines);
        InsertPieces(index, [new LinePiece(source, 0, source.LineCount)], source.LineCount);
    }

    private void ReplaceLineInPlace(int index, string line, int oldLength)
    {
        var source = new MemoryTextSource([line]);
        var replacement = new LinePiece(source, 0, 1);
        var (pieceIndex, offset) = FindPiece(index);
        LinePiece piece = _pieces[pieceIndex];

        if (piece.LineCount == 1)
        {
            _pieces[pieceIndex] = replacement;
        }
        else
        {
            _pieces.RemoveAt(pieceIndex);
            int insertAt = pieceIndex;

            if (offset > 0)
            {
                _pieces.Insert(insertAt, piece with { LineCount = offset });
                insertAt++;
            }

            _pieces.Insert(insertAt, replacement);
            insertAt++;

            int rightCount = piece.LineCount - offset - 1;
            if (rightCount > 0)
            {
                _pieces.Insert(insertAt, piece with
                {
                    StartLine = piece.StartLine + offset + 1,
                    LineCount = rightCount
                });
            }
        }

        NormalizePieces();
        MarkLineTextChanged(oldLength, line.Length);
    }

    private void InsertPieces(int index, IReadOnlyList<LinePiece> insertPieces, int insertLineCount)
    {
        if (insertLineCount == 0) return;
        if (index < 0 || index > _lineCount)
            throw new ArgumentOutOfRangeException(nameof(index));

        if (_lineCount == 1 && _pieces.Count == 1 && _pieces[0].Source is MemoryTextSource && this[0].Length == 0)
        {
            _pieces.Clear();
            _lineCount = 0;
            index = 0;
        }

        if (index == _lineCount)
        {
            _pieces.AddRange(insertPieces);
        }
        else
        {
            var (pieceIndex, offset) = FindPiece(index);
            LinePiece piece = _pieces[pieceIndex];
            _pieces.RemoveAt(pieceIndex);

            int insertAt = pieceIndex;
            if (offset > 0)
            {
                _pieces.Insert(insertAt, piece with { LineCount = offset });
                insertAt++;
            }

            _pieces.InsertRange(insertAt, insertPieces);
            insertAt += insertPieces.Count;

            int rightCount = piece.LineCount - offset;
            if (rightCount > 0)
            {
                _pieces.Insert(insertAt, piece with
                {
                    StartLine = piece.StartLine + offset,
                    LineCount = rightCount
                });
            }
        }

        _lineCount += insertLineCount;
        NormalizePieces();
        MarkStructureChanged();
    }

    private void TransformPiecesInRange(int start, int count, Func<LinePiece, LinePiece> transform)
    {
        int end = start + count;
        int globalLine = 0;
        var next = new List<LinePiece>(_pieces.Count + 2);

        foreach (LinePiece piece in _pieces)
        {
            int pieceStart = globalLine;
            int pieceEnd = globalLine + piece.LineCount;

            if (pieceEnd <= start || pieceStart >= end)
            {
                next.Add(piece);
            }
            else
            {
                int leftCount = Math.Max(0, start - pieceStart);
                if (leftCount > 0)
                    next.Add(piece with { LineCount = leftCount });

                int overlapStart = Math.Max(start, pieceStart);
                int overlapEnd = Math.Min(end, pieceEnd);
                int overlapOffset = overlapStart - pieceStart;
                int overlapCount = overlapEnd - overlapStart;
                if (overlapCount > 0)
                {
                    next.Add(transform(piece with
                    {
                        StartLine = piece.StartLine + overlapOffset,
                        LineCount = overlapCount
                    }));
                }

                int rightCount = pieceEnd - overlapEnd;
                if (rightCount > 0)
                {
                    int rightOffset = overlapEnd - pieceStart;
                    next.Add(piece with
                    {
                        StartLine = piece.StartLine + rightOffset,
                        LineCount = rightCount
                    });
                }
            }

            globalLine = pieceEnd;
        }

        _pieces.Clear();
        _pieces.AddRange(next);
        NormalizePieces();
    }

    private static LinePiece RemovePrefixFromPrefixedPiece(LinePiece piece, PrefixedTextSource prefixed, string prefix)
    {
        if (prefixed.Prefix == prefix)
            return piece with { Source = prefixed.Inner };

        if (prefixed.Prefix.StartsWith(prefix, StringComparison.Ordinal))
        {
            return piece with
            {
                Source = new PrefixedTextSource(prefixed.Inner, prefixed.Prefix[prefix.Length..])
            };
        }

        return piece with { Source = new FixedPrefixRemovalTextSource(piece.Source, prefix) };
    }

    private static LinePiece RemoveLeadingSpacesFromPiece(LinePiece piece, int maxSpaces)
    {
        if (piece.Source is not PrefixedTextSource prefixed)
            return piece with { Source = new LeadingSpaceRemovalTextSource(piece.Source, maxSpaces) };

        int removeFromPrefix = LeadingSpaceRemovalTextSource.CountLeadingSpaces(prefixed.Prefix, maxSpaces);
        string remainingPrefix = prefixed.Prefix[removeFromPrefix..];
        if (remainingPrefix.Length > 0)
            return piece with { Source = new PrefixedTextSource(prefixed.Inner, remainingPrefix) };

        int removeFromInner = maxSpaces - removeFromPrefix;
        ITextSource source = removeFromInner > 0
            ? new LeadingSpaceRemovalTextSource(prefixed.Inner, removeFromInner)
            : prefixed.Inner;
        return piece with { Source = source };
    }

    private static List<string> SplitLines(string text, int tabSize)
    {
        var lines = new List<string>();
        var remaining = text.AsSpan();
        while (true)
        {
            int nlIdx = remaining.IndexOfAny('\r', '\n');
            if (nlIdx < 0) break;
            lines.Add(ExpandTabs(remaining.Slice(0, nlIdx).ToString(), tabSize));
            if (remaining[nlIdx] == '\r' && nlIdx + 1 < remaining.Length && remaining[nlIdx + 1] == '\n')
                remaining = remaining.Slice(nlIdx + 2);
            else
                remaining = remaining.Slice(nlIdx + 1);
        }
        lines.Add(ExpandTabs(remaining.ToString(), tabSize));
        return lines;
    }

    private (int pieceIndex, int offset) FindPiece(int lineIndex)
    {
        if (lineIndex < 0 || lineIndex >= _lineCount)
            throw new ArgumentOutOfRangeException(nameof(lineIndex));

        int current = 0;
        for (int i = 0; i < _pieces.Count; i++)
        {
            LinePiece piece = _pieces[i];
            int next = current + piece.LineCount;
            if (lineIndex < next)
                return (i, lineIndex - current);
            current = next;
        }

        throw new InvalidOperationException("Text buffer pieces are inconsistent.");
    }

    private void NormalizePieces()
    {
        if (_pieces.Count < 2) return;

        for (int i = _pieces.Count - 2; i >= 0; i--)
        {
            LinePiece left = _pieces[i];
            LinePiece right = _pieces[i + 1];
            if (ReferenceEquals(left.Source, right.Source)
                && left.StartLine + left.LineCount == right.StartLine)
            {
                _pieces[i] = left with { LineCount = left.LineCount + right.LineCount };
                _pieces.RemoveAt(i + 1);
            }
        }
    }

    private void ValidateRange(int start, int count)
    {
        if (start < 0 || count < 0 || start + count > _lineCount)
            throw new ArgumentOutOfRangeException(nameof(start));
    }

    private void MarkStructureChanged()
    {
        _maxLineLengthDirty = true;
        _charCountDirty = true;
        _editGeneration++;
    }

    private void MarkLineTextChanged(int oldLength, int newLength)
    {
        if (!_charCountDirty)
            _charCount += newLength - oldLength;

        if (!_maxLineLengthDirty)
        {
            if (newLength >= _maxLineLength)
                _maxLineLength = newLength;
            else if (oldLength >= _maxLineLength)
                _maxLineLengthDirty = true;
        }

        _editGeneration++;
    }

    private void ResetToSingleEmptyLine()
    {
        var source = new MemoryTextSource([""]);
        _pieces.Clear();
        _pieces.Add(new LinePiece(source, 0, 1));
        _lineCount = 1;
        _maxLineLengthDirty = true;
        _charCountDirty = true;
    }

    // -- Line ending detection ---------------------------------------
    private static string DetectLineEnding(string text)
    {
        int crlf = 0, lf = 0;
        var span = text.AsSpan();
        int pos = 0;
        while (pos < span.Length)
        {
            int idx = span.Slice(pos).IndexOf('\n');
            if (idx < 0) break;
            int absIdx = pos + idx;
            if (absIdx > 0 && span[absIdx - 1] == '\r')
                crlf++;
            else
                lf++;
            pos = absIdx + 1;
            if ((crlf | lf) >= 100) break;
        }
        if (crlf == 0 && lf == 0) return Environment.NewLine;
        return lf > crlf ? "\n" : "\r\n";
    }
}
