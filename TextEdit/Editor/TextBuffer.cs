namespace TextEdit;

/// <summary>
/// Manages the text content as a list of lines with dirty tracking,
/// line ending detection, tab expansion, and max line length caching.
/// </summary>
public class TextBuffer
{
    private readonly List<string> _lines = [""];
    private int _maxLineLength;
    private bool _maxLineLengthDirty = true;
    private string _lineEnding = "\r\n";
    private bool _isDirty;

    public int Count => _lines.Count;

    public string this[int index]
    {
        get => _lines[index];
        set => _lines[index] = value;
    }

    public string LineEnding => _lineEnding;
    public string LineEndingDisplay => _lineEnding == "\n" ? "LF" : "CRLF";

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

    // ── Max line length tracking ────────────────────────────────────
    public int MaxLineLength
    {
        get
        {
            if (_maxLineLengthDirty)
            {
                _maxLineLength = _lines.Count > 0 ? _lines.Max(l => l.Length) : 0;
                _maxLineLengthDirty = false;
            }
            return _maxLineLength;
        }
    }

    public void InvalidateMaxLineLength() => _maxLineLengthDirty = true;

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
        if (!_maxLineLengthDirty && lineIndex < _lines.Count
            && _lines[lineIndex].Length >= _maxLineLength)
            _maxLineLengthDirty = true;
    }

    // ── Line manipulation ───────────────────────────────────────────
    public void InsertLine(int index, string line)
    {
        _lines.Insert(index, line);
        _maxLineLengthDirty = true;
    }

    public void RemoveAt(int index)
    {
        _lines.RemoveAt(index);
        _maxLineLengthDirty = true;
    }

    public void RemoveRange(int index, int count)
    {
        _lines.RemoveRange(index, count);
        _maxLineLengthDirty = true;
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
        if (removeCount > 0)
            _lines.RemoveRange(start, removeCount);
        _lines.InsertRange(start, newLines);
        if (_lines.Count == 0) _lines.Add("");
        _maxLineLengthDirty = true;
    }

    // ── Inline text mutations ──────────────────────────────────────

    /// <summary>Insert text at a column position in a line.</summary>
    public void InsertAt(int line, int col, string text)
    {
        NotifyLineChanging(line);
        _lines[line] = _lines[line][..col] + text + _lines[line][col..];
    }

    /// <summary>Delete a range of characters from a line.</summary>
    public void DeleteAt(int line, int col, int length)
    {
        NotifyLineChanging(line);
        _lines[line] = _lines[line][..col] + _lines[line][(col + length)..];
    }

    /// <summary>Replace a range of characters in a line with new text.</summary>
    public void ReplaceAt(int line, int col, int length, string text)
    {
        NotifyLineChanging(line);
        _lines[line] = _lines[line][..col] + text + _lines[line][(col + length)..];
    }

    /// <summary>Join a line with the next line, removing the next line.</summary>
    public void JoinWithNext(int line)
    {
        _lines[line] += _lines[line + 1];
        _lines.RemoveAt(line + 1);
        _maxLineLengthDirty = true;
    }

    /// <summary>Truncate a line at the given column, returning the removed tail.</summary>
    public string TruncateAt(int line, int col)
    {
        var tail = _lines[line][col..];
        NotifyLineChanging(line);
        _lines[line] = _lines[line][..col];
        return tail;
    }

    // ── Content get/set ─────────────────────────────────────────────
    public void SetContent(string text, int tabSize)
    {
        _lineEnding = DetectLineEnding(text);
        _lines.Clear();
        var rawLines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        for (int i = 0; i < rawLines.Length; i++)
            rawLines[i] = ExpandTabs(rawLines[i], tabSize);
        _lines.AddRange(rawLines);
        if (_lines.Count == 0) _lines.Add("");
        _maxLineLengthDirty = true;
        IsDirty = false;
    }

    public string GetContent() => string.Join(_lineEnding, _lines);

    public void Clear()
    {
        _lines.Clear();
        _lines.Add("");
        _maxLineLengthDirty = true;
    }

    // ── Tab expansion ───────────────────────────────────────────────
    public static string ExpandTabs(string line, int tabSize)
    {
        if (!line.Contains('\t')) return line;
        var sb = new System.Text.StringBuilder(line.Length + 16);
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
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                crlf++;
                i++;
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }
        if (crlf == 0 && lf == 0) return Environment.NewLine;
        return lf > crlf ? "\n" : "\r\n";
    }
}
