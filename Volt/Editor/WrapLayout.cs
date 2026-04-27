using System.Collections;
using System.Collections.Generic;

namespace Volt;

/// <summary>
/// Encapsulates word-wrap layout state and coordinate conversions.
/// Maps between logical lines/columns and visual (wrapped) lines.
/// When word wrap is off and no folds exist, all methods are identity operations.
/// When folds exist (even without word wrap), arrays are populated so that
/// hidden lines have zero visual lines and coordinate helpers work correctly.
/// </summary>
internal class WrapLayout
{
    private int _charsPerVisualLine;
    private int[]? _wrapLineCount;      // visual lines per logical line
    private int[]? _wrapCumulOffset;    // cumulative visual line offset
    private int[]? _wrapColStarts;      // starting column for each visual line (word-break mode only)
    private int[]? _wrapIndent;         // indent chars per logical line (wrap-indent mode only)
    private int _totalVisualLines;
    private BitArray? _hiddenLines;     // lines hidden by code folding

    private bool IsHidden(int line) => _hiddenLines != null && line < _hiddenLines.Length && _hiddenLines[line];

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
    /// Recalculate wrap data from the buffer. When wrap is off and no folds exist,
    /// clears arrays and sets TotalVisualLines to the buffer line count.
    /// </summary>
    public void Recalculate(bool wordWrap, bool breakAtWords, bool wrapIndent,
        ITextDocument buffer, double textAreaWidth, double charWidth,
        BitArray? hiddenLines = null)
    {
        if (!wordWrap && hiddenLines == null)
        {
            _wrapLineCount = null;
            _wrapCumulOffset = null;
            _wrapColStarts = null;
            _wrapIndent = null;
            _totalVisualLines = buffer.Count;
            return;
        }

        if (!wordWrap)
        {
            // Fold-only mode: each visible line = 1 visual line, hidden = 0
            _wrapColStarts = null;
            _wrapIndent = null;
            int n = buffer.Count;
            EnsureArrays(n);
            int cumul = 0;
            for (int i = 0; i < n; i++)
            {
                _wrapCumulOffset![i] = cumul;
                _wrapLineCount![i] = (hiddenLines != null && i < hiddenLines.Length && hiddenLines[i]) ? 0 : 1;
                cumul += _wrapLineCount[i];
            }
            _totalVisualLines = cumul;
            return;
        }

        _charsPerVisualLine = Math.Max(1, (int)(textAreaWidth / charWidth));
        _hiddenLines = hiddenLines;

        int count = buffer.Count;
        EnsureArrays(count);

        if (wrapIndent)
        {
            if (_wrapIndent == null || _wrapIndent.Length < count || _wrapIndent.Length > count * 2)
                _wrapIndent = new int[count];
            ComputeIndents(buffer, count);
        }
        else
        {
            _wrapIndent = null;
        }

        if (breakAtWords)
            RecalcWordBreak(buffer, count);
        else
            RecalcCharBreak(buffer, count);
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

    private void ComputeIndents(ITextDocument buffer, int count)
    {
        for (int i = 0; i < count; i++)
            _wrapIndent![i] = MeasureIndent(buffer[i]);
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

    private void RecalcCharBreak(ITextDocument buffer, int count)
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
            if (IsHidden(i)) { _wrapLineCount![i] = 0; continue; }
            int len = buffer[i].Length;
            _wrapLineCount![i] = len <= _charsPerVisualLine ? 1 : (len + _charsPerVisualLine - 1) / _charsPerVisualLine;
            cumul += _wrapLineCount[i];
        }
        _totalVisualLines = cumul;
    }

    private void RecalcCharBreakIndented(ITextDocument buffer, int count)
    {
        _colStartBuffer.Clear();
        int cumul = 0;
        for (int i = 0; i < count; i++)
        {
            _wrapCumulOffset![i] = cumul;
            if (IsHidden(i)) { _wrapLineCount![i] = 0; continue; }
            int len = buffer[i].Length;

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
        _totalVisualLines = cumul;
        if (_wrapColStarts == null || _wrapColStarts.Length < cumul || _wrapColStarts.Length > cumul * 2)
            _wrapColStarts = new int[cumul];
        _colStartBuffer.CopyTo(0, _wrapColStarts, 0, cumul);
    }

    private readonly List<int> _colStartBuffer = new();

    private void RecalcWordBreak(ITextDocument buffer, int count)
    {
        _colStartBuffer.Clear();
        int cumul = 0;
        for (int i = 0; i < count; i++)
        {
            _wrapCumulOffset![i] = cumul;
            if (IsHidden(i)) { _wrapLineCount![i] = 0; continue; }
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
        _totalVisualLines = cumul;
        // Copy to array for O(1) lookup
        if (_wrapColStarts == null || _wrapColStarts.Length < cumul || _wrapColStarts.Length > cumul * 2)
            _wrapColStarts = new int[cumul];
        _colStartBuffer.CopyTo(0, _wrapColStarts, 0, cumul);
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

    /// <summary>Number of visual lines for a logical line (0 if hidden by folding).</summary>
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

    /// <summary>Pixel X and Y for a caret/selection position, accounting for wrap and folds.</summary>
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
        // Fold-only mode (no word wrap): use fold-aware Y but keep horizontal scroll
        if (!wordWrap)
        {
            return (gutterWidth + gutterPadding + col * charWidth - offsetX,
                    _wrapCumulOffset[line] * lineHeight - offsetY);
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

        double indentPx = WrapIndentPx(true, line, wrapIndex, charWidth);
        double x = gutterWidth + gutterPadding + indentPx + colInWrap * charWidth;
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
