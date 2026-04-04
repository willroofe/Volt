using System.Collections.Generic;

namespace Volt;

/// <summary>
/// Encapsulates word-wrap layout state and coordinate conversions.
/// Maps between logical lines/columns and visual (wrapped) lines.
/// When word wrap is off, all methods are identity operations.
/// </summary>
internal class WrapLayout
{
    private int _charsPerVisualLine;
    private int[]? _wrapLineCount;      // visual lines per logical line
    private int[]? _wrapCumulOffset;    // cumulative visual line offset
    private int[]? _wrapColStarts;      // starting column for each visual line (word-break mode only)
    private int _totalVisualLines;

    public int CharsPerVisualLine => _charsPerVisualLine;
    public int TotalVisualLines => _totalVisualLines;

    /// <summary>Whether wrap data arrays are valid for the given buffer size.</summary>
    public bool HasValidData(int bufferCount) =>
        _wrapCumulOffset != null && _wrapCumulOffset.Length >= bufferCount;

    /// <summary>
    /// Recalculate wrap data from the buffer. When wrap is off, clears arrays
    /// and sets TotalVisualLines to the buffer line count.
    /// </summary>
    public void Recalculate(bool wordWrap, bool breakAtWords, TextBuffer buffer, double textAreaWidth, double charWidth)
    {
        if (!wordWrap)
        {
            _wrapLineCount = null;
            _wrapCumulOffset = null;
            _wrapColStarts = null;
            _totalVisualLines = buffer.Count;
            return;
        }

        _charsPerVisualLine = Math.Max(1, (int)(textAreaWidth / charWidth));

        int count = buffer.Count;
        if (_wrapLineCount == null || _wrapLineCount.Length < count
            || _wrapCumulOffset == null || _wrapCumulOffset.Length < count
            || _wrapLineCount.Length > count * 2) // shrink if oversized
        {
            _wrapLineCount = new int[count];
            _wrapCumulOffset = new int[count];
        }

        if (breakAtWords)
            RecalcWordBreak(buffer, count);
        else
            RecalcCharBreak(buffer, count);
    }

    private void RecalcCharBreak(TextBuffer buffer, int count)
    {
        _wrapColStarts = null;
        int cumul = 0;
        for (int i = 0; i < count; i++)
        {
            _wrapCumulOffset![i] = cumul;
            int len = buffer[i].Length;
            _wrapLineCount![i] = len <= _charsPerVisualLine ? 1 : (len + _charsPerVisualLine - 1) / _charsPerVisualLine;
            cumul += _wrapLineCount[i];
        }
        _totalVisualLines = cumul;
    }

    private readonly List<int> _colStartBuffer = new();

    private void RecalcWordBreak(TextBuffer buffer, int count)
    {
        _colStartBuffer.Clear();
        int cumul = 0;
        for (int i = 0; i < count; i++)
        {
            _wrapCumulOffset![i] = cumul;
            string line = buffer[i];
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
            while (pos < len)
            {
                _colStartBuffer.Add(pos);
                subLines++;
                int remaining = len - pos;
                if (remaining <= _charsPerVisualLine)
                    break;

                // Find last whitespace within the visual line width to break at
                int limit = pos + _charsPerVisualLine;
                int breakAt = -1;
                for (int j = limit; j > pos; j--)
                {
                    if (line[j - 1] == ' ' || line[j - 1] == '\t')
                    {
                        breakAt = j;
                        break;
                    }
                }
                pos = breakAt > pos ? breakAt : limit;
            }
            _wrapLineCount![i] = subLines;
            cumul += subLines;
        }
        _totalVisualLines = cumul;
        // Copy to array for O(1) lookup
        if (_wrapColStarts == null || _wrapColStarts.Length < cumul || _wrapColStarts.Length > cumul * 2)
            _wrapColStarts = new int[cumul];
        _colStartBuffer.CopyTo(0, _wrapColStarts, 0, cumul);
    }

    /// <summary>Visual line index for a logical line + column.</summary>
    public int LogicalToVisualLine(bool wordWrap, int logLine, int col = 0)
    {
        if (!wordWrap) return logLine;
        if (_wrapCumulOffset == null || logLine >= _wrapCumulOffset.Length) return logLine;
        if (_wrapColStarts != null)
        {
            int baseVis = _wrapCumulOffset[logLine];
            int subCount = _wrapLineCount![logLine];
            int wrapIndex = 0;
            for (int s = subCount - 1; s > 0; s--)
            {
                if (col >= _wrapColStarts[baseVis + s])
                {
                    wrapIndex = s;
                    break;
                }
            }
            return baseVis + wrapIndex;
        }
        int wi = _charsPerVisualLine > 0 ? Math.Min(col / _charsPerVisualLine, _wrapLineCount![logLine] - 1) : 0;
        return _wrapCumulOffset[logLine] + wi;
    }

    /// <summary>Pixel Y for a logical position.</summary>
    public double GetVisualY(bool wordWrap, int logLine, double lineHeight, int col = 0)
    {
        if (wordWrap && (_wrapCumulOffset == null || logLine >= _wrapCumulOffset.Length))
            return logLine * lineHeight;
        return LogicalToVisualLine(wordWrap, logLine, col) * lineHeight;
    }

    /// <summary>Logical line and wrap sub-index from a visual line index.</summary>
    public (int logLine, int wrapIndex) VisualToLogical(bool wordWrap, int visualLine, int bufferCount)
    {
        if (!wordWrap) return (Math.Clamp(visualLine, 0, bufferCount - 1), 0);
        if (_wrapCumulOffset == null || bufferCount > _wrapCumulOffset.Length)
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
        if (!wordWrap) return 1;
        if (_wrapCumulOffset == null || logLine >= _wrapCumulOffset.Length) return 1;
        return _wrapLineCount![logLine];
    }

    /// <summary>Column offset at the start of a wrap sub-line.</summary>
    public int WrapColStart(bool wordWrap, int logLine, int wrapIndex)
    {
        if (!wordWrap) return 0;
        if (_wrapCumulOffset == null || logLine >= _wrapCumulOffset.Length) return 0;
        if (_wrapColStarts != null)
            return _wrapColStarts[_wrapCumulOffset[logLine] + wrapIndex];
        return wrapIndex * _charsPerVisualLine;
    }

    /// <summary>Number of columns in a given wrap sub-line.</summary>
    public int WrapSubLineLength(bool wordWrap, int logLine, int wrapIndex, int lineLength)
    {
        if (!wordWrap) return lineLength;
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
        if (!wordWrap)
        {
            return (gutterWidth + gutterPadding + col * charWidth - offsetX,
                    line * lineHeight - offsetY);
        }
        if (_wrapCumulOffset == null || line >= _wrapCumulOffset.Length)
        {
            return (gutterWidth + gutterPadding + col * charWidth - offsetX,
                    line * lineHeight - offsetY);
        }

        int wrapIndex;
        int colInWrap;
        if (_wrapColStarts != null)
        {
            int baseVis = _wrapCumulOffset[line];
            int subCount = _wrapLineCount![line];
            wrapIndex = 0;
            for (int s = subCount - 1; s > 0; s--)
            {
                if (col >= _wrapColStarts[baseVis + s])
                {
                    wrapIndex = s;
                    break;
                }
            }
            colInWrap = col - _wrapColStarts[baseVis + wrapIndex];
        }
        else
        {
            wrapIndex = Math.Min(col / _charsPerVisualLine, _wrapLineCount![line] - 1);
            colInWrap = col - wrapIndex * _charsPerVisualLine;
        }

        double x = gutterWidth + gutterPadding + colInWrap * charWidth;
        double y = (_wrapCumulOffset[line] + wrapIndex) * lineHeight - offsetY;
        return (x, y);
    }

    /// <summary>Cumulative visual line offset for a logical line. Used by rendering code.</summary>
    public int CumulOffset(int logLine)
    {
        if (_wrapCumulOffset == null || logLine >= _wrapCumulOffset.Length) return logLine;
        return _wrapCumulOffset[logLine];
    }
}
