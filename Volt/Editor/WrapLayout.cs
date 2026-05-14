using System.Collections.Generic;

namespace Volt;

/// <summary>
/// Encapsulates word-wrap layout state and coordinate conversions.
/// Maps between logical lines/columns and visual (wrapped) lines.
/// When word wrap is off, all methods are identity operations.
/// </summary>
internal class WrapLayout
{
    private const int LongLineThreshold = 500_000;
    private int _charsPerVisualLine;
    private int[]? _wrapLineCount;      // visual lines per logical line
    private int[]? _wrapCumulOffset;    // cumulative visual line offset
    private int[]? _wrapColStarts;      // starting column for each visual line (word-break mode only)
    private int[]? _wrapIndent;         // indent chars per logical line (wrap-indent mode only)
    private readonly List<int> _colStartBuffer = new();
    private int _totalVisualLines;
    private bool _hasCurrentLayout;
    private bool _lastWordWrap;
    private bool _lastBreakAtWords;
    private bool _lastWrapIndent;
    private long _lastBufferGeneration;
    private int _lastBufferCount;
    private int _lastCharsPerVisualLine;

    private void EnsureArrays(int count)
    {
        if (_wrapLineCount == null || _wrapLineCount.Length < count
            || _wrapCumulOffset == null || _wrapCumulOffset.Length < count
            || _wrapLineCount.Length > count * 2)
        {
            _wrapLineCount = new int[count];
            _wrapCumulOffset = new int[count];
        }
    }

    public int CharsPerVisualLine => _charsPerVisualLine;
    public int TotalVisualLines => _totalVisualLines;

    /// <summary>Whether wrap data arrays are valid for the given buffer size.</summary>
    public bool HasValidData(int bufferCount) =>
        _wrapCumulOffset != null && _wrapCumulOffset.Length >= bufferCount;

    /// <summary>
    /// Recalculate wrap data from the buffer. When wrap is off, clears arrays and sets
    /// TotalVisualLines to the buffer line count.
    /// </summary>
    public void Recalculate(bool wordWrap, bool breakAtWords, bool wrapIndent,
        TextBuffer buffer, double textAreaWidth, double charWidth)
    {
        int charsPerVisualLine = wordWrap
            ? Math.Max(1, (int)(textAreaWidth / charWidth))
            : 0;

        int maxLineLength = wordWrap ? buffer.MaxLineLength : 0;

        // Very long physical lines use arithmetic wrapping to avoid allocating
        // per-wrap-line column starts for files such as minified JSON.
        bool useArithmeticWrap = wordWrap && maxLineLength > LongLineThreshold;
        bool effectiveBreakAtWords = breakAtWords && !useArithmeticWrap;
        bool effectiveWrapIndent = wrapIndent && !useArithmeticWrap;

        if (IsCurrent(wordWrap, effectiveBreakAtWords, effectiveWrapIndent, buffer, charsPerVisualLine))
            return;

        if (!wordWrap)
        {
            _wrapLineCount = null;
            _wrapCumulOffset = null;
            _wrapColStarts = null;
            _wrapIndent = null;
            _totalVisualLines = buffer.Count;
            StoreCurrent(wordWrap, effectiveBreakAtWords, effectiveWrapIndent, buffer, charsPerVisualLine);
            return;
        }

        _charsPerVisualLine = charsPerVisualLine;

        int count = buffer.Count;
        EnsureArrays(count);

        if (maxLineLength <= charsPerVisualLine)
        {
            RecalcUnwrappedLines(count);
            StoreCurrent(wordWrap, effectiveBreakAtWords, effectiveWrapIndent, buffer, charsPerVisualLine);
            return;
        }

        EnsureWrapIndent(effectiveWrapIndent, count);

        if (effectiveBreakAtWords)
        {
            RecalcWordBreak(buffer, count, effectiveWrapIndent);
        }
        else
        {
            if (effectiveWrapIndent)
                ComputeIndents(buffer, count);

            RecalcCharBreak(buffer, count);
        }

        StoreCurrent(wordWrap, effectiveBreakAtWords, effectiveWrapIndent, buffer, charsPerVisualLine);
    }

    private bool IsCurrent(
        bool wordWrap,
        bool breakAtWords,
        bool wrapIndent,
        TextBuffer buffer,
        int charsPerVisualLine) =>
        _hasCurrentLayout
        && _lastWordWrap == wordWrap
        && _lastBreakAtWords == breakAtWords
        && _lastWrapIndent == wrapIndent
        && _lastBufferGeneration == buffer.EditGeneration
        && _lastBufferCount == buffer.Count
        && _lastCharsPerVisualLine == charsPerVisualLine;

    private void StoreCurrent(
        bool wordWrap,
        bool breakAtWords,
        bool wrapIndent,
        TextBuffer buffer,
        int charsPerVisualLine)
    {
        _hasCurrentLayout = true;
        _lastWordWrap = wordWrap;
        _lastBreakAtWords = breakAtWords;
        _lastWrapIndent = wrapIndent;
        _lastBufferGeneration = buffer.EditGeneration;
        _lastBufferCount = buffer.Count;
        _lastCharsPerVisualLine = charsPerVisualLine;
    }

    private void RecalcUnwrappedLines(int count)
    {
        _wrapColStarts = null;
        _wrapIndent = null;

        for (int i = 0; i < count; i++)
        {
            _wrapCumulOffset![i] = i;
            _wrapLineCount![i] = 1;
        }

        _totalVisualLines = count;
    }

    private static int MeasureIndent(string line)
    {
        int indent = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == ' ' || line[i] == '\t')
                indent++;
            else
                break;
        }
        return indent;
    }

    private void EnsureWrapIndent(bool enabled, int count)
    {
        if (!enabled)
        {
            _wrapIndent = null;
            return;
        }

        if (_wrapIndent == null || _wrapIndent.Length < count || _wrapIndent.Length > count * 2)
            _wrapIndent = new int[count];
    }

    private void ComputeIndents(TextBuffer buffer, int count)
    {
        int lineIndex = 0;
        foreach (string line in buffer.EnumerateLines(0, count, cache: false))
        {
            _wrapIndent![lineIndex] = MeasureIndent(line);
            lineIndex++;
        }

        for (; lineIndex < count; lineIndex++)
            _wrapIndent![lineIndex] = 0;
    }

    /// <summary>
    /// Number of characters available on a given sub-line.
    /// First sub-line gets full width; continuations are reduced by indent.
    /// </summary>
    private int CharsForSubLine(int logLine, int wrapIndex)
    {
        if (wrapIndex == 0 || _wrapIndent == null) return _charsPerVisualLine;
        int indent = _wrapIndent[logLine];
        // Ensure at least 1 char of usable width even for deeply indented lines
        return Math.Max(1, _charsPerVisualLine - indent);
    }

    private void RecalcCharBreak(TextBuffer buffer, int count)
    {
        _wrapColStarts = null;
        if (_wrapIndent != null)
        {
            RecalcCharBreakIndented(buffer, count);
            return;
        }
        int cumul = 0;
        for (int i = 0; i < count; i++)
        {
            _wrapCumulOffset![i] = cumul;
            int len = buffer.GetLineLength(i);
            _wrapLineCount![i] = len <= _charsPerVisualLine ? 1 : 1 + (len - 1) / _charsPerVisualLine;
            cumul += _wrapLineCount[i];
        }
        _totalVisualLines = cumul;
    }

    private void RecalcCharBreakIndented(TextBuffer buffer, int count)
    {
        _colStartBuffer.Clear();
        int cumul = 0;
        for (int i = 0; i < count; i++)
        {
            _wrapCumulOffset![i] = cumul;
            int len = buffer.GetLineLength(i);

            if (len <= _charsPerVisualLine)
            {
                _wrapLineCount![i] = 1;
                _colStartBuffer.Add(0);
                cumul++;
                continue;
            }

            int subLines = 0;
            int pos = 0;
            while (pos < len)
            {
                _colStartBuffer.Add(pos);
                int avail = CharsForSubLine(i, subLines);
                subLines++;
                int remaining = len - pos;
                if (remaining <= avail)
                    break;
                pos += avail;
            }

            _wrapLineCount![i] = subLines;
            cumul += subLines;
        }

        CommitColumnStarts(cumul);
    }

    private void CommitColumnStarts(int count)
    {
        _totalVisualLines = count;
        if (_wrapColStarts == null || _wrapColStarts.Length < count || _wrapColStarts.Length > count * 2)
            _wrapColStarts = new int[count];
        _colStartBuffer.CopyTo(0, _wrapColStarts, 0, count);
    }

    private void RecalcWordBreak(TextBuffer buffer, int count, bool updateIndent)
    {
        _colStartBuffer.Clear();
        int cumul = 0;
        using IEnumerator<string> lines = buffer.EnumerateLines(0, count, cache: false).GetEnumerator();
        for (int i = 0; i < count; i++)
        {
            string line = lines.MoveNext() ? lines.Current : "";
            if (updateIndent)
                _wrapIndent![i] = MeasureIndent(line);

            _wrapCumulOffset![i] = cumul;
            int len = line.Length;
            if (len <= _charsPerVisualLine)
            {
                _wrapLineCount![i] = 1;
                _colStartBuffer.Add(0);
                cumul++;
                continue;
            }

            int subLines = 0;
            int pos = 0;
            var span = line.AsSpan();
            while (pos < len)
            {
                _colStartBuffer.Add(pos);
                int avail = CharsForSubLine(i, subLines);
                subLines++;
                int remaining = len - pos;
                if (remaining <= avail)
                    break;

                // Find last whitespace within the visual line width to break at
                int limit = pos + avail;
                int idx = span.Slice(pos, limit - pos).LastIndexOfAny(' ', '\t');
                pos = idx >= 0 ? pos + idx + 1 : limit;
            }
            _wrapLineCount![i] = subLines;
            cumul += subLines;
        }
        CommitColumnStarts(cumul);
    }

    /// <summary>
    /// Pixel indent offset for a wrap sub-line. Zero for the first sub-line;
    /// for continuations, returns indentChars * charWidth.
    /// </summary>
    public double WrapIndentPx(bool wordWrap, int logLine, int wrapIndex, double charWidth)
    {
        if (!wordWrap || wrapIndex == 0 || _wrapIndent == null) return 0;
        if (logLine >= _wrapIndent.Length) return 0;
        return _wrapIndent[logLine] * charWidth;
    }

    /// <summary>Visual line index for a logical line + column.</summary>
    public int LogicalToVisualLine(bool wordWrap, int logLine, int col = 0)
    {
        if (_wrapCumulOffset == null) return logLine;
        if (logLine >= _wrapCumulOffset.Length) return logLine;
        if (_wrapColStarts != null)
        {
            int wrapIndex = GetWrapIndexForColumn(logLine, col);
            int baseVis = _wrapCumulOffset[logLine];
            return baseVis + wrapIndex;
        }
        int wi = _charsPerVisualLine > 0 ? Math.Min(col / _charsPerVisualLine, _wrapLineCount![logLine] - 1) : 0;
        return _wrapCumulOffset[logLine] + wi;
    }

    /// <summary>Pixel Y for a logical position.</summary>
    public double GetVisualY(bool wordWrap, int logLine, double lineHeight, int col = 0)
    {
        if (_wrapCumulOffset != null && logLine >= _wrapCumulOffset.Length)
            return logLine * lineHeight;
        return LogicalToVisualLine(wordWrap, logLine, col) * lineHeight;
    }

    /// <summary>Logical line and wrap sub-index from a visual line index.</summary>
    public (int logLine, int wrapIndex) VisualToLogical(bool wordWrap, int visualLine, int bufferCount)
    {
        if (_wrapCumulOffset == null) return (Math.Clamp(visualLine, 0, bufferCount - 1), 0);
        if (bufferCount > _wrapCumulOffset.Length)
            return (Math.Clamp(visualLine, 0, bufferCount - 1), 0);
        int lo = 0, hi = bufferCount - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_wrapCumulOffset[mid] <= visualLine) lo = mid; else hi = mid - 1;
        }
        return (lo, visualLine - _wrapCumulOffset[lo]);
    }

    /// <summary>Number of visual lines for a logical line.</summary>
    public int VisualLineCount(bool wordWrap, int logLine)
    {
        if (_wrapCumulOffset == null) return 1;
        if (logLine >= _wrapCumulOffset.Length) return 1;
        return _wrapLineCount![logLine];
    }

    /// <summary>Column offset at the start of a wrap sub-line.</summary>
    public int WrapColStart(bool wordWrap, int logLine, int wrapIndex)
    {
        if (_wrapCumulOffset == null) return 0;
        if (logLine >= _wrapCumulOffset.Length) return 0;
        if (_wrapColStarts != null)
            return _wrapColStarts[_wrapCumulOffset[logLine] + wrapIndex];
        return wrapIndex * _charsPerVisualLine;
    }

    /// <summary>Number of columns in a given wrap sub-line.</summary>
    public int WrapSubLineLength(bool wordWrap, int logLine, int wrapIndex, int lineLength)
    {
        if (_wrapCumulOffset == null) return lineLength;
        int start = WrapColStart(wordWrap, logLine, wrapIndex);
        int subCount = VisualLineCount(wordWrap, logLine);
        int end = wrapIndex + 1 < subCount ? WrapColStart(wordWrap, logLine, wrapIndex + 1) : lineLength;
        return end - start;
    }

    /// <summary>Pixel X and Y for a caret/selection position, accounting for wrap.</summary>
    public (double x, double y) GetPixelForPosition(bool wordWrap, int line, int col,
        double gutterWidth, double gutterPadding, double charWidth, double lineHeight,
        double offsetX, double offsetY)
    {
        if (_wrapCumulOffset == null)
        {
            return (gutterWidth + gutterPadding + col * charWidth - offsetX,
                    line * lineHeight - offsetY);
        }
        if (line >= _wrapCumulOffset.Length)
        {
            return (gutterWidth + gutterPadding + col * charWidth - offsetX,
                    line * lineHeight - offsetY);
        }
        int wrapIndex;
        int colInWrap;
        if (_wrapColStarts != null)
        {
            int baseVis = _wrapCumulOffset[line];
            wrapIndex = GetWrapIndexForColumn(line, col);
            colInWrap = col - _wrapColStarts[baseVis + wrapIndex];
        }
        else
        {
            wrapIndex = Math.Min(col / _charsPerVisualLine, _wrapLineCount![line] - 1);
            colInWrap = col - wrapIndex * _charsPerVisualLine;
        }

        double indentPx = WrapIndentPx(true, line, wrapIndex, charWidth);
        double x = gutterWidth + gutterPadding + indentPx + colInWrap * charWidth;
        double y = (_wrapCumulOffset[line] + wrapIndex) * lineHeight - offsetY;
        return (x, y);
    }

    private int GetWrapIndexForColumn(int logLine, int col)
    {
        int baseVis = _wrapCumulOffset![logLine];
        int lo = 0;
        int hi = _wrapLineCount![logLine] - 1;

        while (lo < hi)
        {
            int mid = lo + (hi - lo + 1) / 2;
            if (col >= _wrapColStarts![baseVis + mid])
                lo = mid;
            else
                hi = mid - 1;
        }

        return lo;
    }

    /// <summary>Cumulative visual line offset for a logical line. Used by rendering code.</summary>
    public int CumulOffset(int logLine)
    {
        if (_wrapCumulOffset == null || logLine >= _wrapCumulOffset.Length) return logLine;
        return _wrapCumulOffset[logLine];
    }
}
