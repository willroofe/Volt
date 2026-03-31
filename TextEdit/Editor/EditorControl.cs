using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace TextEdit;

public class EditorControl : FrameworkElement, IScrollInfo
{
    // ── Extracted components ─────────────────────────────────────────
    private readonly TextBuffer _buffer = new();
    private readonly UndoManager _undoManager = new();
    private readonly SelectionManager _selection = new();

    // ── Managers (set by MainWindow after construction) ─────────────
    public ThemeManager ThemeManager { get; set; } = null!;
    public SyntaxManager SyntaxManager { get; set; } = null!;

    // ── Caret ────────────────────────────────────────────────────────
    private int _caretLine;
    private int _caretCol;
    private int _preferredCol = -1; // sticky column for vertical movement

    // ── Bracket match cache ─────────────────────────────────────────
    private const int MaxBracketScanLines = 500;
    private (int line, int col, int matchLine, int matchCol)? _bracketMatchCache;
    private bool _bracketMatchDirty = true;

    // ── Settings ───────────────────────────────────────────────────────
    public int TabSize { get; set; } = 4;
    public bool BlockCaret { get; set; }

    private int _caretBlinkMs = 500;
    public int CaretBlinkMs
    {
        get => _caretBlinkMs;
        set
        {
            _caretBlinkMs = value;
            _blinkTimer.Stop();
            if (value > 0)
            {
                _blinkTimer.Interval = TimeSpan.FromMilliseconds(value);
                if (IsKeyboardFocused) _blinkTimer.Start();
            }
            else
            {
                _caretVisible = true;
                InvalidateVisual();
            }
        }
    }

    // ── Rendering (font) ─────────────────────────────────────────────
    private Typeface _monoTypeface = null!;
    private GlyphTypeface _glyphTypeface = null!;
    private double _fontSize = 14;
    private const double GutterPadding = 4;
    private const double GutterRightMargin = 8;
    private const double GutterSeparatorThickness = 0.5;
    private const double HorizontalScrollPadding = 50;
    private const double BarCaretWidth = 1;
    private const double DefaultFontSize = 14;
    private const double MouseWheelDeltaUnit = 120.0;
    private const int ScrollWheelLines = 3;
    private double _charWidth;
    private double _lineHeight;
    private double _glyphBaseline;

    // ── Cached pens / metrics ───────────────────────────────────────
    private Pen _gutterSepPen = new(Brushes.Gray, GutterSeparatorThickness);
    private int _gutterDigits;
    private double _dpi = 1.0;

    // ── Syntax token cache ────────────────────────────────────────────
    private readonly Dictionary<int, (string content, LineState inState, List<SyntaxToken> tokens)> _tokenCache = new();
    private bool _tokenCacheDirty;

    // ── Multi-line syntax state ────────────────────────────────────
    private readonly List<LineState> _lineStates = new();
    private int _lineStatesDirtyFrom = int.MaxValue;

    // ── Layered rendering visuals ───────────────────────────────────
    private readonly DrawingVisual _textVisual = new();
    private readonly DrawingVisual _gutterVisual = new();
    private readonly DrawingVisual _caretVisual = new();
    private readonly TranslateTransform _textTransform = new();
    private readonly TranslateTransform _gutterTransform = new();
    private readonly RectangleGeometry _textClipGeom = new();
    private bool _textVisualDirty = true;
    private bool _gutterVisualDirty = true;
    private int _renderedFirstLine = -1;
    private int _renderedLastLine = -1;
    private int _gutterRenderedFirstLine = -1;
    private int _gutterRenderedLastLine = -1;
    private const int RenderBufferLines = 50;

    public string FontFamilyName
    {
        get => _monoTypeface.FontFamily.Source;
        set { ApplyFont(value, _fontSize, _fontWeight); }
    }

    public double EditorFontSize
    {
        get => _fontSize;
        set { ApplyFont(_monoTypeface.FontFamily.Source, value, _fontWeight); }
    }

    private FontWeight _fontWeight = FontWeights.Normal;

    public string EditorFontWeight
    {
        get => new FontWeightConverter().ConvertToString(_fontWeight)!;
        set
        {
            var fw = (FontWeight)new FontWeightConverter().ConvertFromString(value)!;
            ApplyFont(_monoTypeface.FontFamily.Source, _fontSize, fw);
        }
    }

    private void ApplyFont(string familyName, double size, FontWeight weight)
    {
        // Preserve the top visible line so the view doesn't jump
        int topLine = _lineHeight > 0 ? (int)(_offset.Y / _lineHeight) : 0;

        _fontWeight = weight;
        _monoTypeface = new Typeface(new FontFamily(familyName), FontStyles.Normal, weight, FontStretches.Normal);
        _fontSize = size;
        _dpi = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;

        GlyphTypeface? gt = null;
        if (_monoTypeface.TryGetGlyphTypeface(out gt)) { }
        else
        {
            foreach (var face in new FontFamily(familyName).GetTypefaces())
                if (face.TryGetGlyphTypeface(out gt)) break;
        }
        if (gt != null) _glyphTypeface = gt;

        var sample = new FormattedText("X", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _monoTypeface, _fontSize, Brushes.White, _dpi);
        _charWidth = Math.Round(sample.WidthIncludingTrailingWhitespace * _dpi) / _dpi;
        _lineHeight = Math.Round(sample.Height * _dpi) / _dpi;
        _glyphBaseline = sample.Baseline;

        _tokenCacheDirty = true;
        _gutterDigits = 0;
        UpdateExtent();

        // Restore scroll position to keep the same line at the top
        double newOffset = topLine * _lineHeight;
        newOffset = Math.Clamp(newOffset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        _offset.Y = Math.Round(newOffset * _dpi) / _dpi;
        ScrollOwner?.InvalidateScrollInfo();

        InvalidateText();
    }

    private static List<string>? _monoFontCache;

    public static List<string> GetMonospaceFonts()
    {
        if (_monoFontCache != null) return _monoFontCache;

        var dpi = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;
        var mono = new List<string>();
        foreach (var family in Fonts.SystemFontFamilies.OrderBy(f => f.Source))
        {
            try
            {
                var typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                var narrow = new FormattedText("i", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, DefaultFontSize, Brushes.White, dpi);
                var wide = new FormattedText("M", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, DefaultFontSize, Brushes.White, dpi);
                if (Math.Abs(narrow.WidthIncludingTrailingWhitespace - wide.WidthIncludingTrailingWhitespace) < 0.01)
                    mono.Add(family.Source);
            }
            catch { }
        }
        _monoFontCache = mono;
        return _monoFontCache;
    }

    // ── Caret blink ──────────────────────────────────────────────────
    private readonly DispatcherTimer _blinkTimer;
    private bool _caretVisible = true;

    // ── IScrollInfo state ────────────────────────────────────────────
    private Vector _offset;
    private Size _viewport;
    private Size _extent;
    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }

    // ── Public API (delegates to buffer) ─────────────────────────────
    public bool IsDirty => _buffer.IsDirty;
    public event EventHandler? DirtyChanged;
    public event EventHandler? CaretMoved;

    public int CaretLine => _caretLine;
    public int CaretCol => _caretCol;

    // ── Auto-close bracket/quote pairs ─────────────────────────────
    private static readonly Dictionary<char, char> BracketPairs = new()
    {
        { '(', ')' },
        { '{', '}' },
        { '[', ']' },
    };
    private static readonly HashSet<char> ClosingBrackets = new(BracketPairs.Values);
    private static readonly Dictionary<char, char> ReverseBracketPairs =
        BracketPairs.ToDictionary(kv => kv.Value, kv => kv.Key);
    private static readonly HashSet<char> AutoCloseQuotes = ['\'', '"', '`'];

    // ── Find matches ──────────────────────────────────────────────────
    private List<(int Line, int Col, int Length)> _findMatches = [];
    private int _currentMatchIndex = -1;

    // ── Mouse drag ───────────────────────────────────────────────────
    private bool _isDragging;

    // ── Gutter width (computed) ──────────────────────────────────────
    private double _gutterWidth;

    private static string DefaultFontFamily()
    {
        return Fonts.SystemFontFamilies.Any(f =>
            f.Source.Equals("Cascadia Code", StringComparison.OrdinalIgnoreCase))
            ? "Cascadia Code" : "Consolas";
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _tokenCacheDirty = true;
        RebuildGutterPen();
        InvalidateText();
    }

    public EditorControl()
    {
        ApplyFont(DefaultFontFamily(), DefaultFontSize, FontWeights.Normal);
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;

        _buffer.DirtyChanged += (_, _) => DirtyChanged?.Invoke(this, EventArgs.Empty);

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _blinkTimer.Tick += (_, _) =>
        {
            _caretVisible = !_caretVisible;
            UpdateCaretVisual();
        };

        _textVisual.Transform = _textTransform;
        _textVisual.Clip = _textClipGeom;
        _gutterVisual.Transform = _gutterTransform;
        TextOptions.SetTextRenderingMode(_textVisual, TextRenderingMode.ClearType);
        TextOptions.SetTextRenderingMode(_gutterVisual, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(_textVisual, TextHintingMode.Fixed);
        TextOptions.SetTextHintingMode(_gutterVisual, TextHintingMode.Fixed);
        AddVisualChild(_textVisual);
        AddVisualChild(_gutterVisual);
        AddVisualChild(_caretVisual);

        Loaded += (_, _) =>
        {
            ThemeManager.ThemeChanged += OnThemeChanged;
            RebuildGutterPen();
            _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            Keyboard.Focus(this);
            _blinkTimer.Start();
            UpdateExtent();
        };

        Unloaded += (_, _) =>
        {
            _blinkTimer.Stop();
            ThemeManager.ThemeChanged -= OnThemeChanged;
        };
    }

    // ── Visual tree (layered children: text → gutter → caret) ─────
    protected override int VisualChildrenCount => 3;
    protected override Visual GetVisualChild(int index) => index switch
    {
        0 => _textVisual,
        1 => _gutterVisual,
        2 => _caretVisual,
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    private void InvalidateText()
    {
        _textVisualDirty = true;
        _gutterVisualDirty = true;
        _renderedFirstLine = -1;
        _renderedLastLine = -1;
        _gutterRenderedFirstLine = -1;
        _gutterRenderedLastLine = -1;
        InvalidateVisual();
    }

    private void UpdateCaretVisual()
    {
        using var dc = _caretVisual.RenderOpen();
        if (!IsKeyboardFocused || !_caretVisible) return;

        double caretX = _gutterWidth + GutterPadding + _caretCol * _charWidth - _offset.X;
        double caretY = _caretLine * _lineHeight - _offset.Y;
        if (caretX >= _gutterWidth && caretY + _lineHeight > 0 && caretY < ActualHeight)
        {
            if (BlockCaret)
            {
                dc.DrawRectangle(ThemeManager.CaretBrush, null,
                    new Rect(caretX, caretY, _charWidth, _lineHeight));
                if (_caretCol < _buffer[_caretLine].Length)
                {
                    var ch = _buffer[_caretLine][_caretCol].ToString();
                    DrawGlyphRun(dc, ch, 0, 1, caretX, caretY, ThemeManager.EditorBg);
                }
            }
            else
            {
                dc.DrawRectangle(ThemeManager.CaretBrush, null,
                    new Rect(caretX, caretY, BarCaretWidth, _lineHeight));
            }
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _dpi = newDpi.PixelsPerDip;
        _tokenCacheDirty = true;
        InvalidateLineStates();
        InvalidateText();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Undo / Redo helpers (region-based)
    // ──────────────────────────────────────────────────────────────────

    /// <summary>Snapshot of the buffer region before an edit, used by BeginEdit/EndEdit.</summary>
    private readonly record struct EditScope(
        int StartLine, int LineCount, int BufferCount,
        List<string> Before, int CaretLine, int CaretCol);

    /// <summary>
    /// Call before an edit. Snapshots the lines in [startLine, endLine] (inclusive)
    /// and records the caret position and buffer size.
    /// </summary>
    private EditScope BeginEdit(int startLine, int endLine)
    {
        int count = endLine - startLine + 1;
        return new EditScope(startLine, count, _buffer.Count,
            _buffer.GetLines(startLine, count), _caretLine, _caretCol);
    }

    /// <summary>
    /// Call after an edit. Snapshots the new state of the affected region
    /// and pushes a region-based undo entry.
    /// </summary>
    private void EndEdit(EditScope scope)
    {
        int lineDelta = _buffer.Count - scope.BufferCount;
        int afterCount = scope.LineCount + lineDelta;
        var after = _buffer.GetLines(scope.StartLine, afterCount);
        _undoManager.Push(new UndoManager.UndoEntry(
            scope.StartLine, scope.Before, after,
            scope.CaretLine, scope.CaretCol, _caretLine, _caretCol));

        _textVisualDirty = true;
        _bracketMatchDirty = true;
        InvalidateLineStatesFrom(scope.StartLine);
        _buffer.IsDirty = true;
    }

    private void Undo()
    {
        var entry = _undoManager.Undo();
        if (entry == null) return;
        _buffer.ReplaceLines(entry.StartLine, entry.After.Count, entry.Before);
        _caretLine = entry.CaretLineBefore;
        _caretCol = entry.CaretColBefore;
        ClampCaret();
        _selection.Clear();
        _bracketMatchDirty = true;
        InvalidateLineStates();
        UpdateExtent();
        InvalidateText();
    }

    private void Redo()
    {
        var entry = _undoManager.Redo();
        if (entry == null) return;
        _buffer.ReplaceLines(entry.StartLine, entry.Before.Count, entry.After);
        _caretLine = entry.CaretLineAfter;
        _caretCol = entry.CaretColAfter;
        ClampCaret();
        _selection.Clear();
        _bracketMatchDirty = true;
        InvalidateLineStates();
        UpdateExtent();
        InvalidateText();
    }

    // ──────────────────────────────────────────────────────────────────
    //  String/comment detection (for suppressing auto-close)
    // ──────────────────────────────────────────────────────────────────
    private bool IsCaretInsideString(string line, int caretCol)
    {
        EnsureLineStates(_caretLine);
        var inState = _caretLine < _lineStates.Count ? _lineStates[_caretLine] : SyntaxManager.DefaultState;
        var tokens = SyntaxManager.Tokenize(line, inState, out _);
        if (tokens.Count > 0)
        {
            foreach (var token in tokens)
            {
                if (caretCol > token.Start && caretCol < token.Start + token.Length)
                {
                    if (token.Scope is "string" or "comment")
                        return true;
                }
            }
            return false;
        }

        char? openQuote = null;
        for (int i = 0; i < caretCol && i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && openQuote != null) { i++; continue; }
            if (openQuote == null)
            {
                if (c == '\'' || c == '"' || c == '`') openQuote = c;
            }
            else if (c == openQuote)
            {
                openQuote = null;
            }
        }
        return openQuote != null;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Hit testing (pixel → line/col)
    // ──────────────────────────────────────────────────────────────────
    private (int line, int col) HitTest(Point pos)
    {
        int line = (int)((pos.Y + _offset.Y) / _lineHeight);
        line = Math.Clamp(line, 0, _buffer.Count - 1);

        double textX = pos.X + _offset.X - _gutterWidth - GutterPadding;
        int col = (int)Math.Round(textX / _charWidth);
        col = Math.Clamp(col, 0, _buffer[line].Length);
        return (line, col);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Word boundary helpers
    // ──────────────────────────────────────────────────────────────────
    private static int WordLeft(string line, int col)
    {
        if (col <= 0) return 0;
        int i = col - 1;
        while (i > 0 && char.IsWhiteSpace(line[i])) i--;
        while (i > 0 && !char.IsWhiteSpace(line[i - 1]) && !IsPunctuation(line[i - 1])) i--;
        return i;
    }

    private static int WordRight(string line, int col)
    {
        if (col >= line.Length) return line.Length;
        int i = col;
        while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
        while (i < line.Length && !char.IsWhiteSpace(line[i]) && !IsPunctuation(line[i])) i++;
        return i;
    }

    private static bool IsPunctuation(char c) =>
        char.IsPunctuation(c) || char.IsSymbol(c);

    private (int start, int end) GetWordAt(int line, int col)
    {
        var text = _buffer[line];
        if (text.Length == 0) return (0, 0);
        col = Math.Clamp(col, 0, Math.Max(0, text.Length - 1));

        int start = col, end = col;
        if (char.IsLetterOrDigit(text[col]) || text[col] == '_')
        {
            while (start > 0 && (char.IsLetterOrDigit(text[start - 1]) || text[start - 1] == '_')) start--;
            while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
        }
        else
        {
            end = col + 1;
        }
        return (start, end);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Bracket matching
    // ──────────────────────────────────────────────────────────────────
    private (int line, int col, int matchLine, int matchCol)? FindMatchingBracket()
    {
        ClampCaret();
        int[] colsToCheck = _caretCol < _buffer[_caretLine].Length && _caretCol > 0
            ? [_caretCol, _caretCol - 1]
            : _caretCol < _buffer[_caretLine].Length
                ? [_caretCol]
                : _caretCol > 0
                    ? [_caretCol - 1]
                    : [];

        foreach (int checkCol in colsToCheck)
        {
            char ch = _buffer[_caretLine][checkCol];

            if (BracketPairs.TryGetValue(ch, out char closer))
            {
                var match = ScanForBracket(ch, closer, _caretLine, checkCol, forward: true);
                if (match != null)
                    return (_caretLine, checkCol, match.Value.line, match.Value.col);
            }
            else if (ReverseBracketPairs.TryGetValue(ch, out char opener))
            {
                var match = ScanForBracket(ch, opener, _caretLine, checkCol, forward: false);
                if (match != null)
                    return (_caretLine, checkCol, match.Value.line, match.Value.col);
            }
        }

        return FindEnclosingBracket();
    }

    private (int line, int col, int matchLine, int matchCol)? FindEnclosingBracket()
    {
        var depths = new Dictionary<char, int>();
        foreach (var opener in BracketPairs.Keys)
            depths[opener] = 0;

        int line = _caretLine;
        int col = _caretCol - 1;
        int minLine = Math.Max(0, _caretLine - MaxBracketScanLines);

        while (true)
        {
            while (col < 0 || _buffer[line].Length == 0)
            {
                line--;
                if (line < minLine) return null;
                col = _buffer[line].Length - 1;
            }

            char ch = _buffer[line][col];

            if (ClosingBrackets.Contains(ch))
            {
                var opener = ReverseBracketPairs[ch];
                depths[opener]++;
            }
            else if (BracketPairs.TryGetValue(ch, out char closer))
            {
                depths[ch]--;
                if (depths[ch] < 0)
                {
                    var match = ScanForBracket(ch, closer, line, col, forward: true);
                    if (match != null)
                        return (line, col, match.Value.line, match.Value.col);
                    depths[ch] = 0;
                }
            }

            col--;
        }
    }

    private (int line, int col)? ScanForBracket(char bracket, char target, int startLine, int startCol, bool forward)
    {
        int depth = 1;
        int line = startLine;
        int col = startCol;
        int maxLine = Math.Min(_buffer.Count - 1, startLine + MaxBracketScanLines);
        int minLine = Math.Max(0, startLine - MaxBracketScanLines);

        while (true)
        {
            if (forward)
            {
                col++;
                while (col >= _buffer[line].Length)
                {
                    line++;
                    if (line > maxLine) return null;
                    col = 0;
                }
            }
            else
            {
                col--;
                while (col < 0 || _buffer[line].Length == 0)
                {
                    line--;
                    if (line < minLine) return null;
                    col = _buffer[line].Length - 1;
                }
            }

            char ch = _buffer[line][col];
            if (ch == bracket) depth++;
            else if (ch == target) depth--;

            if (depth == 0) return (line, col);
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Scroll / extent management
    // ──────────────────────────────────────────────────────────────────
    private void UpdateExtent()
    {
        int digits = _buffer.Count > 0
            ? (int)Math.Floor(Math.Log10(_buffer.Count)) + 1
            : 1;
        if (digits != _gutterDigits)
        {
            _gutterDigits = digits;
            _gutterWidth = digits * _charWidth + GutterRightMargin;
        }

        // Use the fast path when possible, full recalc only when dirty
        int maxLen = _buffer.UpdateMaxForLine(_caretLine);

        var newExtent = new Size(
            _gutterWidth + GutterPadding + maxLen * _charWidth + HorizontalScrollPadding,
            _buffer.Count * _lineHeight);

        if (Math.Abs(newExtent.Width - _extent.Width) > 0.5
            || Math.Abs(newExtent.Height - _extent.Height) > 0.5)
        {
            _extent = newExtent;
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    private void EnsureCaretVisible()
    {
        double caretTop = _caretLine * _lineHeight;
        double caretBottom = caretTop + _lineHeight;
        if (caretTop < _offset.Y)
            SetVerticalOffset(caretTop);
        else if (caretBottom > _offset.Y + _viewport.Height)
            SetVerticalOffset(caretBottom - _viewport.Height);

        double caretX = _gutterWidth + GutterPadding + _caretCol * _charWidth;
        if (caretX - _offset.X < _gutterWidth + GutterPadding)
            SetHorizontalOffset(caretX - _gutterWidth - GutterPadding);
        else if (caretX - _offset.X > _viewport.Width - _charWidth)
            SetHorizontalOffset(caretX - _viewport.Width + _charWidth * 2);
    }

    private double ColToPixelX(string line, int col)
    {
        return col * _charWidth;
    }

    private void DrawGlyphRun(DrawingContext dc, string text, int startIndex, int length,
                              double x, double y, Brush brush)
    {
        if (length <= 0) return;
        var map = _glyphTypeface.CharacterToGlyphMap;
        var glyphIndices = new ushort[length];
        var advanceWidths = new double[length];
        for (int i = 0; i < length; i++)
        {
            char ch = text[startIndex + i];
            glyphIndices[i] = map.TryGetValue(ch, out var gi) ? gi : (ushort)0;
            advanceWidths[i] = _charWidth;
        }
        double snapX = Math.Round(x * _dpi) / _dpi;
        double snapY = Math.Round((y + _glyphBaseline) * _dpi) / _dpi;
        var origin = new Point(snapX, snapY);
        var run = new GlyphRun(
            _glyphTypeface, 0, false, _fontSize, (float)_dpi,
            glyphIndices, origin, advanceWidths,
            null, null, null, null, null, null);
        dc.DrawGlyphRun(brush, run);
    }

    private void ClampCaret()
    {
        _caretLine = Math.Clamp(_caretLine, 0, Math.Max(0, _buffer.Count - 1));
        _caretCol = Math.Clamp(_caretCol, 0, _buffer[_caretLine].Length);
    }

    private void ResetPreferredCol() => _preferredCol = -1;

    private int _prevCaretLine = -1;

    private void ResetCaret()
    {
        _caretVisible = true;
        _blinkTimer.Stop();
        if (_caretBlinkMs > 0) _blinkTimer.Start();
        _bracketMatchDirty = true;
        if (_caretLine != _prevCaretLine)
        {
            _gutterVisualDirty = true;
            _prevCaretLine = _caretLine;
        }
        InvalidateVisual();
        CaretMoved?.Invoke(this, EventArgs.Empty);
    }

    private void RebuildGutterPen()
    {
        _gutterSepPen = new Pen(ThemeManager.GutterFg, GutterSeparatorThickness);
        _gutterSepPen.Freeze();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Multi-line syntax state
    // ──────────────────────────────────────────────────────────────────
    private void EnsureLineStates(int throughLine)
    {
        if (_lineStates.Count == 0)
            _lineStates.Add(SyntaxManager.DefaultState);

        if (_lineStatesDirtyFrom < int.MaxValue)
        {
            int from = _lineStatesDirtyFrom;
            _lineStatesDirtyFrom = int.MaxValue;

            bool lineCountShifted = _lineStates.Count > 1
                && _lineStates.Count - 1 != _buffer.Count;

            if (lineCountShifted)
            {
                int keepCount = from + 1;
                if (keepCount < _lineStates.Count)
                    _lineStates.RemoveRange(keepCount, _lineStates.Count - keepCount);
            }
            else
            {
                for (int i = from; i < _buffer.Count && i + 1 < _lineStates.Count; i++)
                {
                    SyntaxManager.Tokenize(_buffer[i], _lineStates[i], out var outState);
                    if (_lineStates[i + 1] == outState)
                        break;
                    _lineStates[i + 1] = outState;
                }
            }
        }

        while (_lineStates.Count <= throughLine && _lineStates.Count <= _buffer.Count)
        {
            int lineIdx = _lineStates.Count - 1;
            var inState = _lineStates[lineIdx];
            SyntaxManager.Tokenize(_buffer[lineIdx], inState, out var outState);
            _lineStates.Add(outState);
        }
    }

    private void InvalidateLineStates()
    {
        _lineStates.Clear();
        _lineStatesDirtyFrom = int.MaxValue;
    }

    private void InvalidateLineStatesFrom(int lineIndex)
    {
        _lineStatesDirtyFrom = Math.Min(_lineStatesDirtyFrom, lineIndex);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Rendering (layered: background → text → gutter → caret)
    // ──────────────────────────────────────────────────────────────────
    private (int first, int last) VisibleLineRange()
    {
        int first = Math.Max(0, (int)(_offset.Y / _lineHeight));
        int last = Math.Min(_buffer.Count - 1,
            (int)((_offset.Y + _viewport.Height) / _lineHeight));
        return (first, last);
    }

    protected override void OnRender(DrawingContext dc)
    {
        ClampCaret();

        if (_tokenCacheDirty)
        {
            _tokenCache.Clear();
            _tokenCacheDirty = false;
            _textVisualDirty = true;
            _gutterVisualDirty = true;
        }

        var (firstLine, lastLine) = VisibleLineRange();

        dc.DrawRectangle(ThemeManager.EditorBg, null,
            new Rect(0, 0, ActualWidth, ActualHeight));

        if (_caretLine >= firstLine && _caretLine <= lastLine)
        {
            double curLineY = _caretLine * _lineHeight - _offset.Y;
            dc.DrawRectangle(ThemeManager.CurrentLineBrush, null,
                new Rect(0, curLineY, ActualWidth, _lineHeight));
        }

        if (_selection.HasSelection)
        {
            var (sl, sc, el, ec) = _selection.GetOrdered(_caretLine, _caretCol);
            for (int i = firstLine; i <= lastLine; i++)
            {
                if (i < sl || i > el) continue;
                int selStart = i == sl ? sc : 0;
                int selEnd = i == el ? ec : _buffer[i].Length;

                double y = i * _lineHeight - _offset.Y;
                double x1 = _gutterWidth + GutterPadding + selStart * _charWidth - _offset.X;
                double x2 = _gutterWidth + GutterPadding + selEnd * _charWidth - _offset.X;

                if (i > sl && i < el) x2 = Math.Max(x2, ActualWidth);
                if (i == sl && i != el) x2 = Math.Max(x2, ActualWidth);

                dc.DrawRectangle(ThemeManager.SelectionBrush, null,
                    new Rect(Math.Max(x1, _gutterWidth + GutterPadding), y,
                             Math.Max(0, x2 - Math.Max(x1, _gutterWidth + GutterPadding)),
                             _lineHeight));
            }
        }

        if (_findMatches.Count > 0)
        {
            for (int m = 0; m < _findMatches.Count; m++)
            {
                var (mLine, mCol, mLen) = _findMatches[m];
                if (mLine < firstLine || mLine > lastLine) continue;
                double pxStart = ColToPixelX(_buffer[mLine], mCol);
                double pxEnd = ColToPixelX(_buffer[mLine], mCol + mLen);
                double mx = _gutterWidth + GutterPadding + pxStart - _offset.X;
                double my = mLine * _lineHeight - _offset.Y;
                var brush = m == _currentMatchIndex
                    ? ThemeManager.FindMatchCurrentBrush
                    : ThemeManager.FindMatchBrush;
                dc.DrawRectangle(brush, null,
                    new Rect(mx, my, pxEnd - pxStart, _lineHeight));
            }
        }

        if (IsKeyboardFocused && !_selection.HasSelection)
        {
            if (_bracketMatchDirty)
            {
                _bracketMatchCache = FindMatchingBracket();
                _bracketMatchDirty = false;
            }
            if (_bracketMatchCache is var (bl, bc, ml, mc))
            {
                foreach (var (bLine, bCol) in new[] { (bl, bc), (ml, mc) })
                {
                    if (bLine >= firstLine && bLine <= lastLine)
                    {
                        double bx = _gutterWidth + GutterPadding + bCol * _charWidth - _offset.X;
                        double by = bLine * _lineHeight - _offset.Y;
                        dc.DrawRectangle(ThemeManager.MatchingBracketBrush,
                            ThemeManager.MatchingBracketPen,
                            new Rect(bx, by, _charWidth, _lineHeight));
                    }
                }
            }
        }

        _textTransform.X = -_offset.X;
        _textTransform.Y = -_offset.Y;
        _gutterTransform.Y = -_offset.Y;

        _textClipGeom.Rect = new Rect(
            _gutterWidth + _offset.X, _offset.Y,
            Math.Max(0, ActualWidth - _gutterWidth), ActualHeight);

        if (_textVisualDirty
            || firstLine < _renderedFirstLine
            || lastLine > _renderedLastLine)
        {
            RenderTextVisual(firstLine, lastLine);
            _textVisualDirty = false;
        }
        if (_gutterVisualDirty
            || firstLine < _gutterRenderedFirstLine
            || lastLine > _gutterRenderedLastLine)
        {
            RenderGutterVisual(firstLine, lastLine);
            _gutterVisualDirty = false;
        }
        UpdateCaretVisual();
    }

    private void RenderTextVisual(int firstLine, int lastLine)
    {
        int drawFirst = Math.Max(0, firstLine - RenderBufferLines);
        int drawLast = Math.Min(_buffer.Count - 1, lastLine + RenderBufferLines);

        using var dc = _textVisual.RenderOpen();

        if (drawLast < drawFirst) return;

        EnsureLineStates(drawLast);
        for (int i = drawFirst; i <= drawLast; i++)
        {
            var line = _buffer[i];
            if (line.Length == 0) continue;
            double y = i * _lineHeight;
            double x = _gutterWidth + GutterPadding;

            var inState = i < _lineStates.Count ? _lineStates[i] : SyntaxManager.DefaultState;
            if (!_tokenCache.TryGetValue(i, out var cached)
                || cached.content != line || cached.inState != inState)
            {
                var tokens = SyntaxManager.Tokenize(line, inState, out _);
                _tokenCache[i] = (line, inState, tokens);
                cached = _tokenCache[i];
            }

            if (cached.tokens.Count == 0)
            {
                DrawGlyphRun(dc, line, 0, line.Length, x, y, ThemeManager.EditorFg);
            }
            else
            {
                int pos = 0;
                foreach (var token in cached.tokens)
                {
                    if (token.Start > pos)
                        DrawGlyphRun(dc, line, pos, token.Start - pos,
                            x + pos * _charWidth, y, ThemeManager.EditorFg);
                    var brush = ThemeManager.GetScopeBrush(token.Scope);
                    DrawGlyphRun(dc, line, token.Start, token.Length,
                        x + token.Start * _charWidth, y, brush);
                    pos = token.Start + token.Length;
                }
                if (pos < line.Length)
                    DrawGlyphRun(dc, line, pos, line.Length - pos,
                        x + pos * _charWidth, y, ThemeManager.EditorFg);
            }
        }

        _renderedFirstLine = drawFirst;
        _renderedLastLine = drawLast;

        if (_tokenCache.Count > (drawLast - drawFirst + 1) * 3)
        {
            int margin = drawLast - drawFirst + 1;
            int pruneBelow = drawFirst - margin;
            int pruneAbove = drawLast + margin;
            var keysToRemove = new List<int>();
            foreach (var key in _tokenCache.Keys)
                if (key < pruneBelow || key > pruneAbove)
                    keysToRemove.Add(key);
            foreach (var key in keysToRemove)
                _tokenCache.Remove(key);
        }
    }

    private void RenderGutterVisual(int firstLine, int lastLine)
    {
        int drawFirst = Math.Max(0, firstLine - RenderBufferLines);
        int drawLast = Math.Min(_buffer.Count - 1, lastLine + RenderBufferLines);

        using var dc = _gutterVisual.RenderOpen();

        if (drawLast < drawFirst) return;

        double bgTop = drawFirst * _lineHeight;
        double bgBottom = (drawLast + 1) * _lineHeight;
        dc.DrawRectangle(ThemeManager.EditorBg, null,
            new Rect(0, bgTop, _gutterWidth, bgBottom - bgTop));
        dc.DrawLine(_gutterSepPen,
            new Point(_gutterWidth, bgTop), new Point(_gutterWidth, bgBottom));

        for (int i = drawFirst; i <= drawLast; i++)
        {
            double y = i * _lineHeight;
            var brush = i == _caretLine
                ? ThemeManager.ActiveLineNumberFg : ThemeManager.GutterFg;
            var numStr = (i + 1).ToString();
            double numWidth = numStr.Length * _charWidth;
            DrawGlyphRun(dc, numStr, 0, numStr.Length,
                _gutterWidth - numWidth - GutterPadding, y, brush);
        }

        _gutterRenderedFirstLine = drawFirst;
        _gutterRenderedLastLine = drawLast;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Focus
    // ──────────────────────────────────────────────────────────────────
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        _caretVisible = true;
        if (_caretBlinkMs > 0) _blinkTimer.Start();
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        _blinkTimer.Stop();
        _caretVisible = false;
        InvalidateVisual();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Mouse
    // ──────────────────────────────────────────────────────────────────
    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        Keyboard.Focus(this);
        CaptureMouse();

        var pos = e.GetPosition(this);
        var (line, col) = HitTest(pos);

        if (e.ClickCount == 2)
        {
            var (ws, we) = GetWordAt(line, col);
            _caretLine = line;
            _selection.SetAnchor(line, ws);
            _caretCol = we;
            _selection.HasSelection = ws != we;
        }
        else
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                _selection.Start(_caretLine, _caretCol);
            }
            else
            {
                _selection.Clear();
                _selection.SetAnchor(line, col);
            }
            _caretLine = line;
            _caretCol = col;
        }
        _isDragging = true;
        ResetPreferredCol();
        ResetCaret();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!_isDragging) return;
        var pos = e.GetPosition(this);
        var (line, col) = HitTest(pos);

        if (line == _caretLine && col == _caretCol)
        {
            e.Handled = true;
            return;
        }

        if (line != _selection.AnchorLine || col != _selection.AnchorCol)
            _selection.HasSelection = true;

        _caretLine = line;
        _caretCol = col;
        _bracketMatchDirty = true;
        EnsureCaretVisible();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _isDragging = false;
        ReleaseMouseCapture();
        e.Handled = true;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Text input (printable characters)
    // ──────────────────────────────────────────────────────────────────
    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (string.IsNullOrEmpty(e.Text) || e.Text[0] < ' ') return;

        char ch = e.Text[0];

        // Over-type closing bracket or quote
        if (e.Text.Length == 1 && (ClosingBrackets.Contains(ch) || AutoCloseQuotes.Contains(ch)) && !_selection.HasSelection)
        {
            var line = _buffer[_caretLine];
            if (_caretCol < line.Length && line[_caretCol] == ch)
            {
                _caretCol++;
                ResetPreferredCol();
                _selection.Clear();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                return;
            }
        }

        ResetPreferredCol();
        int sl = _caretLine, el = _caretLine;
        if (_selection.HasSelection)
        {
            var (s, _, e2, _) = _selection.GetOrdered(_caretLine, _caretCol);
            sl = s; el = e2;
        }
        var scope = BeginEdit(sl, el);

        if (_selection.HasSelection)
            (_caretLine, _caretCol) = _selection.DeleteSelection(_buffer, _caretLine, _caretCol);

        var currentLine = _buffer[_caretLine];
        bool insideString = IsCaretInsideString(currentLine, _caretCol);

        if (!insideString && e.Text.Length == 1 && BracketPairs.TryGetValue(ch, out char closer))
        {
            _buffer.InsertAt(_caretLine, _caretCol, ch.ToString() + closer);
            _caretCol++;
        }
        else if (!insideString && e.Text.Length == 1 && AutoCloseQuotes.Contains(ch))
        {
            _buffer.InsertAt(_caretLine, _caretCol, ch.ToString() + ch);
            _caretCol++;
        }
        else
        {
            _buffer.InsertAt(_caretLine, _caretCol, e.Text);
            _caretCol += e.Text.Length;
        }

        EndEdit(scope);
        _selection.Clear();
        UpdateExtent();
        EnsureCaretVisible();
        ResetCaret();
        e.Handled = true;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Keyboard — dispatch to handler methods
    // ──────────────────────────────────────────────────────────────────
    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
        {
            case Key.Return:
                HandleReturn();
                e.Handled = true;
                break;

            case Key.Back:
                HandleBackspace();
                e.Handled = true;
                break;

            case Key.Delete:
                HandleDelete();
                e.Handled = true;
                break;

            case Key.Tab:
                HandleTab(shift);
                e.Handled = true;
                break;

            case Key.Left:
            case Key.Right:
            case Key.Up:
            case Key.Down:
            case Key.Home:
            case Key.End:
            case Key.PageUp:
            case Key.PageDown:
                HandleNavigation(e.Key, shift, ctrl);
                e.Handled = true;
                break;

            case Key.A when ctrl:
                HandleSelectAll();
                e.Handled = true;
                break;

            case Key.C when ctrl:
                HandleCopy();
                e.Handled = true;
                break;

            case Key.X when ctrl:
                HandleCut();
                e.Handled = true;
                break;

            case Key.V when ctrl:
                HandlePaste();
                e.Handled = true;
                break;

            case Key.Z when ctrl:
                ResetPreferredCol();
                Undo();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                break;

            case Key.Y when ctrl:
                ResetPreferredCol();
                Redo();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                break;
        }
    }

    // ── Key handlers ─────────────────────────────────────────────────

    private void HandleReturn()
    {
        ResetPreferredCol();
        int sl = _caretLine, el = _caretLine;
        if (_selection.HasSelection)
        {
            var (s, _, e2, _) = _selection.GetOrdered(_caretLine, _caretCol);
            sl = s; el = e2;
        }
        var scope = BeginEdit(sl, el);
        if (_selection.HasSelection)
            (_caretLine, _caretCol) = _selection.DeleteSelection(_buffer, _caretLine, _caretCol);

        var indent = _buffer[_caretLine][..^(_buffer[_caretLine].TrimStart().Length)];
        var rest = _buffer.TruncateAt(_caretLine, _caretCol);

        bool betweenBrackets = _caretCol > 0 && rest.Length > 0
            && BracketPairs.TryGetValue(_buffer[_caretLine][^1], out char expectedCloser)
            && rest[0] == expectedCloser;

        bool afterOpen = _caretCol > 0
            && BracketPairs.ContainsKey(_buffer[_caretLine][_caretCol - 1]);

        if (betweenBrackets)
        {
            var innerIndent = indent + new string(' ', TabSize);
            _caretLine++;
            _buffer.InsertLine(_caretLine, innerIndent);
            _buffer.InsertLine(_caretLine + 1, indent + rest);
            _caretCol = innerIndent.Length;
        }
        else if (afterOpen)
        {
            var innerIndent = indent + new string(' ', TabSize);
            _caretLine++;
            _buffer.InsertLine(_caretLine, innerIndent + rest);
            _caretCol = innerIndent.Length;
        }
        else
        {
            _caretLine++;
            _buffer.InsertLine(_caretLine, indent + rest);
            _caretCol = indent.Length;
        }

        EndEdit(scope);
        _selection.Clear();
        _tokenCacheDirty = true;
        UpdateExtent();
        EnsureCaretVisible();
        ResetCaret();
    }

    private void HandleBackspace()
    {
        ResetPreferredCol();
        int sl = _caretLine, el = _caretLine;
        if (_selection.HasSelection)
        {
            var (s, _, e2, _) = _selection.GetOrdered(_caretLine, _caretCol);
            sl = s; el = e2;
        }
        else if (_caretCol == 0 && _caretLine > 0)
        {
            sl = _caretLine - 1; // line join: affects previous + current
        }
        var scope = BeginEdit(sl, el);

        if (_selection.HasSelection)
        {
            (_caretLine, _caretCol) = _selection.DeleteSelection(_buffer, _caretLine, _caretCol);
        }
        else if (_caretCol > 0)
        {
            var line = _buffer[_caretLine];

            bool deletedPair = false;
            if (_caretCol < line.Length)
            {
                char before = line[_caretCol - 1];
                char after = line[_caretCol];
                bool isPair = (BracketPairs.TryGetValue(before, out char expected) && after == expected)
                              || (AutoCloseQuotes.Contains(before) && after == before);
                if (isPair)
                {
                    _buffer.DeleteAt(_caretLine, _caretCol - 1, 2);
                    _caretCol--;
                    deletedPair = true;
                }
            }

            if (!deletedPair)
            {
                int leadingSpaces = line.Length - line.TrimStart().Length;
                int remove = 1;
                if (_caretCol <= leadingSpaces && line[.._caretCol].All(c => c == ' '))
                {
                    int prevStop = (_caretCol - 1) / TabSize * TabSize;
                    remove = _caretCol - prevStop;
                }
                _buffer.DeleteAt(_caretLine, _caretCol - remove, remove);
                _caretCol -= remove;
            }
        }
        else if (_caretLine > 0)
        {
            _caretCol = _buffer[_caretLine - 1].Length;
            _buffer.JoinWithNext(_caretLine - 1);
            _caretLine--;
            _tokenCacheDirty = true;
        }
        EndEdit(scope);
        _selection.Clear();
        UpdateExtent();
        EnsureCaretVisible();
        ResetCaret();
    }

    private void HandleDelete()
    {
        ResetPreferredCol();
        int sl = _caretLine, el = _caretLine;
        if (_selection.HasSelection)
        {
            var (s, _, e2, _) = _selection.GetOrdered(_caretLine, _caretCol);
            sl = s; el = e2;
        }
        else if (_caretCol >= _buffer[_caretLine].Length && _caretLine < _buffer.Count - 1)
        {
            el = _caretLine + 1; // line join: affects current + next
        }
        var scope = BeginEdit(sl, el);

        if (_selection.HasSelection)
        {
            (_caretLine, _caretCol) = _selection.DeleteSelection(_buffer, _caretLine, _caretCol);
        }
        else if (_caretCol < _buffer[_caretLine].Length)
        {
            _buffer.DeleteAt(_caretLine, _caretCol, 1);
        }
        else if (_caretLine < _buffer.Count - 1)
        {
            _buffer.JoinWithNext(_caretLine);
            _tokenCacheDirty = true;
        }
        EndEdit(scope);
        _selection.Clear();
        UpdateExtent();
        EnsureCaretVisible();
        ResetCaret();
    }

    private void HandleTab(bool shift)
    {
        ResetPreferredCol();
        int sl = _caretLine, el = _caretLine;
        if (_selection.HasSelection)
        {
            var (s, _, e2, _) = _selection.GetOrdered(_caretLine, _caretCol);
            sl = s; el = e2;
        }
        var scope = BeginEdit(sl, el);

        if (_selection.HasSelection)
        {
            if (sl != el)
            {
                for (int i = sl; i <= el; i++)
                {
                    if (shift)
                    {
                        int remove = 0;
                        while (remove < TabSize && remove < _buffer[i].Length && _buffer[i][remove] == ' ')
                            remove++;
                        if (remove > 0)
                        {
                            _buffer.DeleteAt(i, 0, remove);
                            if (i == _caretLine) _caretCol = Math.Max(0, _caretCol - remove);
                            if (i == _selection.AnchorLine) _selection.AnchorCol = Math.Max(0, _selection.AnchorCol - remove);
                        }
                    }
                    else
                    {
                        _buffer.InsertAt(i, 0, new string(' ', TabSize));
                        if (i == _caretLine) _caretCol += TabSize;
                        if (i == _selection.AnchorLine) _selection.AnchorCol += TabSize;
                    }
                }
                EndEdit(scope);
                _selection.HasSelection = true;
                UpdateExtent();
                EnsureCaretVisible();
                ResetCaret();
                return;
            }
        }
        if (!shift)
        {
            if (_selection.HasSelection)
                (_caretLine, _caretCol) = _selection.DeleteSelection(_buffer, _caretLine, _caretCol);
            int spacesToInsert = TabSize - (_caretCol % TabSize);
            _buffer.InsertAt(_caretLine, _caretCol, new string(' ', spacesToInsert));
            _caretCol += spacesToInsert;
        }
        EndEdit(scope);
        _selection.Clear();
        UpdateExtent();
        EnsureCaretVisible();
        ResetCaret();
    }

    private void HandleNavigation(Key key, bool shift, bool ctrl)
    {
        switch (key)
        {
            case Key.Left:
                ResetPreferredCol();
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (ctrl)
                    _caretCol = WordLeft(_buffer[_caretLine], _caretCol);
                else if (_caretCol > 0)
                    _caretCol--;
                else if (_caretLine > 0)
                {
                    _caretLine--;
                    _caretCol = _buffer[_caretLine].Length;
                }
                if (!shift) _selection.Clear();
                break;

            case Key.Right:
                ResetPreferredCol();
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (ctrl)
                    _caretCol = WordRight(_buffer[_caretLine], _caretCol);
                else if (_caretCol < _buffer[_caretLine].Length)
                    _caretCol++;
                else if (_caretLine < _buffer.Count - 1)
                {
                    _caretLine++;
                    _caretCol = 0;
                }
                if (!shift) _selection.Clear();
                break;

            case Key.Up:
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (_caretLine > 0)
                {
                    if (_preferredCol < 0) _preferredCol = _caretCol;
                    _caretLine--;
                    _caretCol = Math.Min(_preferredCol, _buffer[_caretLine].Length);
                }
                if (!shift) _selection.Clear();
                break;

            case Key.Down:
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (_caretLine < _buffer.Count - 1)
                {
                    if (_preferredCol < 0) _preferredCol = _caretCol;
                    _caretLine++;
                    _caretCol = Math.Min(_preferredCol, _buffer[_caretLine].Length);
                }
                if (!shift) _selection.Clear();
                break;

            case Key.Home:
                ResetPreferredCol();
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (ctrl) _caretLine = 0;
                _caretCol = 0;
                if (!shift) _selection.Clear();
                break;

            case Key.End:
                ResetPreferredCol();
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (ctrl) _caretLine = _buffer.Count - 1;
                _caretCol = _buffer[_caretLine].Length;
                if (!shift) _selection.Clear();
                break;

            case Key.PageUp:
            case Key.PageDown:
            {
                int visibleLines = Math.Max(1, (int)(_viewport.Height / _lineHeight) - 1);
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (key == Key.PageUp)
                    _caretLine = Math.Max(0, _caretLine - visibleLines);
                else
                    _caretLine = Math.Min(_buffer.Count - 1, _caretLine + visibleLines);
                _caretCol = Math.Min(_caretCol, _buffer[_caretLine].Length);
                if (!shift) _selection.Clear();
                break;
            }
        }
        EnsureCaretVisible();
        ResetCaret();
    }

    private void HandleSelectAll()
    {
        _selection.AnchorLine = 0;
        _selection.AnchorCol = 0;
        _caretLine = _buffer.Count - 1;
        _caretCol = _buffer[_buffer.Count - 1].Length;
        _selection.HasSelection = true;
        InvalidateVisual();
    }

    private void HandleCopy()
    {
        try
        {
            if (_selection.HasSelection)
                Clipboard.SetText(_selection.GetSelectedText(_buffer, _caretLine, _caretCol));
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    private void HandleCut()
    {
        try
        {
            if (_selection.HasSelection)
            {
                ResetPreferredCol();
                var (sl, _, el, _) = _selection.GetOrdered(_caretLine, _caretCol);
                var scope = BeginEdit(sl, el);
                Clipboard.SetText(_selection.GetSelectedText(_buffer, _caretLine, _caretCol));
                (_caretLine, _caretCol) = _selection.DeleteSelection(_buffer, _caretLine, _caretCol);
                EndEdit(scope);
                UpdateExtent();
                EnsureCaretVisible();
                ResetCaret();
            }
        }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }

    private void HandlePaste()
    {
        try { if (!Clipboard.ContainsText()) return; }
        catch (System.Runtime.InteropServices.ExternalException) { return; }

        ResetPreferredCol();
        int sl = _caretLine, el = _caretLine;
        if (_selection.HasSelection)
        {
            var (s, _, e2, _) = _selection.GetOrdered(_caretLine, _caretCol);
            sl = s; el = e2;
        }
        var scope = BeginEdit(sl, el);

        if (_selection.HasSelection)
            (_caretLine, _caretCol) = _selection.DeleteSelection(_buffer, _caretLine, _caretCol);

        string text;
        try { text = Clipboard.GetText(); }
        catch (System.Runtime.InteropServices.ExternalException) { return; }

        var pasteLines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        for (int pi = 0; pi < pasteLines.Length; pi++)
            pasteLines[pi] = TextBuffer.ExpandTabs(pasteLines[pi], TabSize);

        if (pasteLines.Length == 1)
        {
            _buffer.InsertAt(_caretLine, _caretCol, pasteLines[0]);
            _caretCol += pasteLines[0].Length;
        }
        else
        {
            var after = _buffer.TruncateAt(_caretLine, _caretCol);
            _buffer[_caretLine] += pasteLines[0];
            for (int i = 1; i < pasteLines.Length; i++)
            {
                _caretLine++;
                _buffer.InsertLine(_caretLine, pasteLines[i]);
            }
            _caretCol = _buffer[_caretLine].Length;
            _buffer[_caretLine] += after;
            _tokenCacheDirty = true;
        }
        EndEdit(scope);
        _selection.Clear();
        UpdateExtent();
        EnsureCaretVisible();
        ResetCaret();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Mouse wheel
    // ──────────────────────────────────────────────────────────────────
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        SetVerticalOffset(_offset.Y - e.Delta / MouseWheelDeltaUnit * _lineHeight * ScrollWheelLines);
        e.Handled = true;
    }

    // ──────────────────────────────────────────────────────────────────
    //  IScrollInfo
    // ──────────────────────────────────────────────────────────────────
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    public void SetHorizontalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Width - _viewport.Width));
        offset = Math.Round(offset * _dpi) / _dpi;
        if (Math.Abs(offset - _offset.X) < 0.01) return;
        _offset.X = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        offset = Math.Round(offset * _dpi) / _dpi;
        if (Math.Abs(offset - _offset.Y) < 0.01) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;

    public void LineUp() => SetVerticalOffset(_offset.Y - _lineHeight);
    public void LineDown() => SetVerticalOffset(_offset.Y + _lineHeight);
    public void LineLeft() => SetHorizontalOffset(_offset.X - _charWidth * TabSize);
    public void LineRight() => SetHorizontalOffset(_offset.X + _charWidth * TabSize);
    public void PageUp() => SetVerticalOffset(_offset.Y - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset.Y + _viewport.Height);
    public void PageLeft() => SetHorizontalOffset(_offset.X - _viewport.Width);
    public void PageRight() => SetHorizontalOffset(_offset.X + _viewport.Width);
    public void MouseWheelUp() => LineUp();
    public void MouseWheelDown() => LineDown();
    public void MouseWheelLeft() => LineLeft();
    public void MouseWheelRight() => LineRight();

    // ──────────────────────────────────────────────────────────────────
    //  Layout
    // ──────────────────────────────────────────────────────────────────
    protected override Size MeasureOverride(Size availableSize)
    {
        if (Math.Abs(availableSize.Width - _viewport.Width) > 0.5
            || Math.Abs(availableSize.Height - _viewport.Height) > 0.5)
        {
            _textVisualDirty = true;
            _gutterVisualDirty = true;
        }
        _viewport = availableSize;
        UpdateExtent();
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        bool viewportChanged = Math.Abs(finalSize.Width - _viewport.Width) > 0.5
                            || Math.Abs(finalSize.Height - _viewport.Height) > 0.5;
        _viewport = finalSize;
        if (viewportChanged)
        {
            _textVisualDirty = true;
            _gutterVisualDirty = true;
            ScrollOwner?.InvalidateScrollInfo();
        }
        return finalSize;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Public API for file operations
    // ──────────────────────────────────────────────────────────────────
    public string LineEnding => _buffer.LineEndingDisplay;

    public void SetContent(string text)
    {
        _buffer.SetContent(text, TabSize);
        _caretLine = 0;
        _caretCol = 0;
        _selection.Clear();
        _undoManager.Clear();
        _findMatches.Clear();
        _currentMatchIndex = 0;
        _tokenCacheDirty = true;
        InvalidateLineStates();
        UpdateExtent();
        SetVerticalOffset(0);
        SetHorizontalOffset(0);
        InvalidateText();
    }

    public string GetContent() => _buffer.GetContent();

    public void InvalidateSyntax()
    {
        InvalidateLineStates();
        _tokenCacheDirty = true;
        InvalidateText();
    }

    public void MarkClean()
    {
        _buffer.IsDirty = false;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Find support
    // ──────────────────────────────────────────────────────────────────

    public int FindMatchCount => _findMatches.Count;
    public int CurrentMatchIndex => _currentMatchIndex;

    public void SetFindMatches(string query, bool matchCase)
    {
        _findMatches.Clear();
        _currentMatchIndex = -1;

        if (string.IsNullOrEmpty(query))
        {
            InvalidateVisual();
            return;
        }

        var comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        for (int line = 0; line < _buffer.Count; line++)
        {
            int pos = 0;
            while (pos < _buffer[line].Length)
            {
                int idx = _buffer[line].IndexOf(query, pos, comparison);
                if (idx < 0) break;
                _findMatches.Add((line, idx, query.Length));
                pos = idx + 1;
            }
        }

        if (_findMatches.Count > 0)
        {
            _currentMatchIndex = 0;
            for (int i = 0; i < _findMatches.Count; i++)
            {
                var (ml, mc, _) = _findMatches[i];
                if (ml > _caretLine || (ml == _caretLine && mc >= _caretCol))
                {
                    _currentMatchIndex = i;
                    break;
                }
            }
            NavigateToCurrentMatch();
        }

        InvalidateVisual();
    }

    public void ClearFindMatches()
    {
        _findMatches.Clear();
        _currentMatchIndex = -1;
        InvalidateVisual();
    }

    public void FindNext()
    {
        if (_findMatches.Count == 0) return;
        _currentMatchIndex = (_currentMatchIndex + 1) % _findMatches.Count;
        NavigateToCurrentMatch();
        InvalidateVisual();
    }

    public void FindPrevious()
    {
        if (_findMatches.Count == 0) return;
        _currentMatchIndex = (_currentMatchIndex - 1 + _findMatches.Count) % _findMatches.Count;
        NavigateToCurrentMatch();
        InvalidateVisual();
    }

    public void ReplaceCurrent(string replacement)
    {
        if (_currentMatchIndex < 0 || _currentMatchIndex >= _findMatches.Count) return;
        var (line, col, len) = _findMatches[_currentMatchIndex];
        var scope = BeginEdit(line, line);
        _buffer.ReplaceAt(line, col, len, replacement);
        EndEdit(scope);
        _buffer.InvalidateMaxLineLength();
        _gutterVisualDirty = true;
        InvalidateVisual();
    }

    public void ReplaceAll(string query, string replacement, bool matchCase)
    {
        if (_findMatches.Count == 0) return;
        int firstLine = _findMatches[0].Line;
        int lastLine = _findMatches[^1].Line;
        var scope = BeginEdit(firstLine, lastLine);
        for (int i = _findMatches.Count - 1; i >= 0; i--)
        {
            var (line, col, len) = _findMatches[i];
            _buffer.ReplaceAt(line, col, len, replacement);
        }
        EndEdit(scope);
        _buffer.InvalidateMaxLineLength();
        _tokenCacheDirty = true;
        InvalidateLineStates();
        _gutterVisualDirty = true;
        InvalidateVisual();
    }

    private void NavigateToCurrentMatch()
    {
        if (_currentMatchIndex < 0 || _currentMatchIndex >= _findMatches.Count) return;
        var (line, col, _) = _findMatches[_currentMatchIndex];
        _caretLine = line;
        _caretCol = col;
        _selection.Clear();
        CentreLineInViewport(line);
        ResetCaret();
    }

    private void CentreLineInViewport(int line)
    {
        double targetY = line * _lineHeight - (_viewport.Height - _lineHeight) / 2;
        SetVerticalOffset(targetY);

        double caretX = _gutterWidth + GutterPadding + ColToPixelX(_buffer[line], _caretCol);
        double textAreaWidth = _viewport.Width - _gutterWidth - GutterPadding;
        double targetX = caretX - _gutterWidth - GutterPadding - textAreaWidth / 2;
        SetHorizontalOffset(targetX);
    }
}
