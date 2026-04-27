using System.IO;
using System.Text;

namespace Volt;

/// <summary>
/// Manages the text content as a list of lines with dirty tracking,
/// line ending detection, tab expansion, and max line length caching.
/// </summary>
public class TextBuffer : ITextDocument
{
    private readonly List<string> _lines = [""];
    private int _maxLineLength;
    private bool _maxLineLengthDirty = true;
    private long _charCount;
    private bool _charCountDirty = true;
    private string _lineEnding = "\r\n";
    private bool _isDirty;
    private long _editGeneration;

    public long LineCount => _lines.Count;
    public int Count => _lines.Count;

    /// <summary>Incremented on every mutation. Used for cache invalidation.</summary>
    public long EditGeneration => _editGeneration;

    public string this[int index]
    {
        get => _lines[index];
        set { NotifyLineChanging(index); _lines[index] = value; }
    }

    public string LineEnding => _lineEnding;
    public string LineEndingDisplay => _lineEnding == "\n" ? "LF" : "CRLF";
    public Encoding Encoding { get; set; } = new UTF8Encoding(false);

    /// <summary>Total character count including line endings.</summary>
    public long CharCount
    {
        get
        {
            if (_charCountDirty)
            {
                long total = 0;
                for (int i = 0; i < _lines.Count; i++)
                    total += _lines[i].Length;
                total += (long)(_lines.Count - 1) * _lineEnding.Length;
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
    public event EventHandler<TextDocumentChangedEventArgs>? Changed;

    // ── Max line length tracking ────────────────────────────────────
    public int MaxLineLength
    {
        get
        {
            if (_maxLineLengthDirty)
            {
                int max = 0;
                for (int i = 0; i < _lines.Count; i++)
                    if (_lines[i].Length > max) max = _lines[i].Length;
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
        if (lineIndex < _lines.Count && _lines[lineIndex].Length > _maxLineLength)
            _maxLineLength = _lines[lineIndex].Length;
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
        if (!_maxLineLengthDirty && lineIndex < _lines.Count
            && _lines[lineIndex].Length >= _maxLineLength)
            _maxLineLengthDirty = true;
    }

    // ── Line manipulation ───────────────────────────────────────────
    public void InsertLine(int index, string line)
    {
        _lines.Insert(index, line);
        _maxLineLengthDirty = true; _charCountDirty = true; _editGeneration++;
        RaiseChanged(index, 1);
    }

    public void RemoveAt(int index)
    {
        _lines.RemoveAt(index);
        _maxLineLengthDirty = true; _charCountDirty = true; _editGeneration++;
        RaiseChanged(index, -1);
    }

    public void RemoveRange(int index, int count)
    {
        _lines.RemoveRange(index, count);
        _maxLineLengthDirty = true; _charCountDirty = true; _editGeneration++;
        RaiseChanged(index, -count);
    }

    /// <summary>Get a copy of a range of lines (for undo snapshots).</summary>
    public List<string> GetLines(int start, int count)
    {
        var result = new List<string>(count);
        for (int i = start; i < start + count && i < _lines.Count; i++)
            result.Add(_lines[i]);
        return result;
    }

    /// <summary>
    /// Replace a range of lines with new lines (for undo/redo application).
    /// Removes <paramref name="removeCount"/> lines starting at <paramref name="start"/>,
    /// then inserts <paramref name="newLines"/> at that position.
    /// </summary>
    public void ReplaceLines(int start, int removeCount, List<string> newLines)
    {
        bool removingMax = false;
        if (!_maxLineLengthDirty)
        {
            for (int i = start; i < start + removeCount && i < _lines.Count; i++)
            {
                if (_lines[i].Length >= _maxLineLength) { removingMax = true; break; }
            }
        }

        if (removeCount > 0)
            _lines.RemoveRange(start, removeCount);
        _lines.InsertRange(start, newLines);
        if (_lines.Count == 0) _lines.Add("");
        _editGeneration++;
        _charCountDirty = true;
        RaiseChanged(start, newLines.Count - removeCount);

        if (_maxLineLengthDirty || removingMax)
        {
            _maxLineLengthDirty = true;
        }
        else
        {
            foreach (var line in newLines)
                if (line.Length > _maxLineLength) _maxLineLength = line.Length;
        }
    }

    // ── Inline text mutations ──────────────────────────────────────

    /// <summary>Insert text at a column position in a line.</summary>
    public void InsertAt(int line, int col, string text)
    {
        NotifyLineChanging(line);
        _lines[line] = string.Concat(_lines[line].AsSpan(0, col), text, _lines[line].AsSpan(col));
        RaiseChanged(line, 0);
    }

    /// <summary>Delete a range of characters from a line.</summary>
    public void DeleteAt(int line, int col, int length)
    {
        NotifyLineChanging(line);
        _lines[line] = string.Concat(_lines[line].AsSpan(0, col), _lines[line].AsSpan(col + length));
        RaiseChanged(line, 0);
    }

    /// <summary>Replace a range of characters in a line with new text.</summary>
    public void ReplaceAt(int line, int col, int length, string text)
    {
        NotifyLineChanging(line);
        _lines[line] = string.Concat(_lines[line].AsSpan(0, col), text, _lines[line].AsSpan(col + length));
        RaiseChanged(line, 0);
    }

    /// <summary>Join a line with the next line, removing the next line.</summary>
    public void JoinWithNext(int line)
    {
        _lines[line] = string.Concat(_lines[line], _lines[line + 1]);
        _lines.RemoveAt(line + 1);
        _maxLineLengthDirty = true; _charCountDirty = true; _editGeneration++;
        RaiseChanged(line, -1);
    }

    /// <summary>Truncate a line at the given column, returning the removed tail.</summary>
    public string TruncateAt(int line, int col)
    {
        var tail = _lines[line][col..];
        NotifyLineChanging(line);
        _lines[line] = _lines[line][..col];
        RaiseChanged(line, 0);
        return tail;
    }

    // ── Content get/set ─────────────────────────────────────────────

    /// <summary>Result of <see cref="PrepareContent"/> — holds parsed lines and detected line ending.</summary>
    public sealed class PreparedContent
    {
        internal List<string> Lines { get; init; } = null!;
        internal string LineEnding { get; init; } = null!;
    }

    /// <summary>
    /// Parse text into lines on a background thread. This does the heavy CPU work
    /// (line splitting, tab expansion, line ending detection) without touching buffer state.
    /// Call <see cref="SetPreparedContent"/> on the UI thread to apply the result.
    /// </summary>
    public static PreparedContent PrepareContent(string text, int tabSize)
    {
        var lineEnding = DetectLineEnding(text);
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
        return new PreparedContent { Lines = lines, LineEnding = lineEnding };
    }

    /// <summary>
    /// Apply pre-parsed content to the buffer. Must be called on the UI thread.
    /// </summary>
    public void SetPreparedContent(PreparedContent prepared)
    {
        _lineEnding = prepared.LineEnding;
        _lines.Clear();
        _lines.AddRange(prepared.Lines);
        _maxLineLengthDirty = true; _charCountDirty = true;
        IsDirty = false;
        _editGeneration++;
        RaiseChanged(0, 0);
    }

    public void SetContent(string text, int tabSize)
    {
        _lineEnding = DetectLineEnding(text);
        _lines.Clear();
        // Split by line breaks using span enumeration to avoid allocating
        // a large intermediate string[] (critical for binary files with many \n bytes).
        var remaining = text.AsSpan();
        while (true)
        {
            int nlIdx = remaining.IndexOfAny('\r', '\n');
            if (nlIdx < 0) break;
            _lines.Add(ExpandTabs(remaining.Slice(0, nlIdx).ToString(), tabSize));
            if (remaining[nlIdx] == '\r' && nlIdx + 1 < remaining.Length && remaining[nlIdx + 1] == '\n')
                remaining = remaining.Slice(nlIdx + 2);
            else
                remaining = remaining.Slice(nlIdx + 1);
        }
        _lines.Add(ExpandTabs(remaining.ToString(), tabSize));
        _maxLineLengthDirty = true; _charCountDirty = true;
        IsDirty = false;
        _editGeneration++;
        RaiseChanged(0, 0);
    }

    /// <summary>
    /// Appends text (read from the tail of a file) to the existing buffer.
    /// The first piece of the new text is joined onto the current last line
    /// (it may complete a partial line from the previous read).
    /// Returns the line index where new content starts.
    /// </summary>
    public int AppendContent(string text, int tabSize)
    {
        var newLines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        for (int i = 0; i < newLines.Length; i++)
            newLines[i] = ExpandTabs(newLines[i], tabSize);

        // Join the first fragment onto the current last line
        int appendStart = _lines.Count - 1;
        _lines[appendStart] += newLines[0];

        // Add any remaining lines
        for (int i = 1; i < newLines.Length; i++)
            _lines.Add(newLines[i]);

        _maxLineLengthDirty = true; _charCountDirty = true;
        _editGeneration++;
        RaiseChanged(appendStart, newLines.Length - 1);
        return appendStart;
    }

    public string GetContent() => string.Join(_lineEnding, _lines);

    public string GetLineSlice(long line, long startColumn, int maxChars)
    {
        if (line < 0 || line >= _lines.Count || maxChars <= 0) return "";
        var text = _lines[(int)line];
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
            var line = _lines[(int)start.Line];
            int length = (int)Math.Min(end.Column - start.Column, maxChars);
            return line.Substring((int)start.Column, length);
        }

        var sb = new StringBuilder();
        AppendCapped(sb, _lines[(int)start.Line][(int)start.Column..], maxChars);
        for (long line = start.Line + 1; line < end.Line && sb.Length < maxChars; line++)
        {
            AppendCapped(sb, _lineEnding, maxChars);
            AppendCapped(sb, _lines[(int)line], maxChars);
        }
        if (sb.Length < maxChars)
        {
            AppendCapped(sb, _lineEnding, maxChars);
            AppendCapped(sb, _lines[(int)end.Line][..(int)end.Column], maxChars);
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
        InsertAt((int)p.Line + lines.Count - 1, _lines[(int)p.Line + lines.Count - 1].Length, tail);
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

        string merged = _lines[(int)start.Line][..(int)start.Column] + _lines[(int)end.Line][(int)end.Column..];
        RemoveRange((int)start.Line + 1, (int)(end.Line - start.Line));
        NotifyLineChanging((int)start.Line);
        _lines[(int)start.Line] = merged;
        _maxLineLengthDirty = true;
        _charCountDirty = true;
        RaiseChanged((int)start.Line, start.Line - end.Line);
    }

    public void Replace(TextRange range, string text)
    {
        Delete(range);
        Insert(range.Start, text);
    }

    public async Task SaveAsync(string path, CancellationToken cancellationToken = default)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tempPath = Path.Combine(dir, Path.GetRandomFileName());
        try
        {
            await using (var stream = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write,
                             FileShare.None, bufferSize: 1 << 20, useAsync: true))
            await using (var writer = new StreamWriter(stream, Encoding, bufferSize: 1 << 20))
            {
                for (int i = 0; i < _lines.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (i > 0)
                        await writer.WriteAsync(_lineEnding.AsMemory(), cancellationToken);
                    await writer.WriteAsync(_lines[i].AsMemory(), cancellationToken);
                }
            }

            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
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

    public void Clear()
    {
        _lines.Clear();
        _lines.TrimExcess();
        _lines.Add("");
        _maxLineLengthDirty = true; _charCountDirty = true;
        _editGeneration++;
        RaiseChanged(0, 0);
    }

    private TextPoint ClampPoint(TextPoint point)
    {
        long line = Math.Clamp(point.Line, 0, Math.Max(0, _lines.Count - 1));
        long col = Math.Clamp(point.Column, 0, _lines[(int)line].Length);
        return new TextPoint(line, col);
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

    private void RaiseChanged(int startLine, long lineDelta)
    {
        Changed?.Invoke(this, new TextDocumentChangedEventArgs(
            new TextRange(new TextPoint(startLine, 0), new TextPoint(startLine, 0)),
            lineDelta,
            _editGeneration));
    }

    // ── Tab expansion ───────────────────────────────────────────────
    [ThreadStatic] private static System.Text.StringBuilder? _expandTabsSb;

    public static string ExpandTabs(string line, int tabSize)
    {
        if (!line.Contains('\t')) return line;
        var sb = _expandTabsSb ??= new System.Text.StringBuilder(256);
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

    // ── Line ending detection ───────────────────────────────────────
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
            // Once we have a clear winner with enough samples, stop early
            if ((crlf | lf) >= 100) break;
        }
        if (crlf == 0 && lf == 0) return Environment.NewLine;
        return lf > crlf ? "\n" : "\r\n";
    }
}
