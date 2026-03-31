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
    // ── Text buffer ──────────────────────────────────────────────────
    private readonly List<string> _lines = new();
    private int _maxLineLength;
    private bool _maxLineLengthDirty = true;
    private string _lineEnding = "\r\n"; // detected line ending style

    // ── Caret / selection ────────────────────────────────────────────
    private int _caretLine;
    private int _caretCol;
    private int _anchorLine;
    private int _anchorCol;
    private bool _hasSelection;
    private int _preferredCol = -1; // sticky column for vertical movement

    // ── Undo / redo ──────────────────────────────────────────────────
    private const int MaxUndoEntries = 200;
    private const int MaxBracketScanLines = 500;
    private readonly List<UndoEntry> _undoStack = new();
    private readonly List<UndoEntry> _redoStack = new();

    // ── Bracket match cache ─────────────────────────────────────────────
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
    private double _fontSize = 14;
    private const double GutterPadding = 4;
    private double _charWidth;
    private double _lineHeight;

    // ── Cached pens / metrics ───────────────────────────────────────
    private Pen _gutterSepPen = null!;
    private int _gutterDigits;
    private double _dpi = 1.0;

    // ── FormattedText cache ──────────────────────────────────────────
    private readonly Dictionary<int, (string content, LineState inState, FormattedText ft)> _ftCache = new();
    private readonly Dictionary<int, (Brush brush, FormattedText ft)> _lineNumCache = new();
    private bool _ftCacheDirty;

    // ── Multi-line syntax state ────────────────────────────────────
    private readonly List<LineState> _lineStates = new();
    private int _lineStatesDirtyFrom = int.MaxValue;

    // ── Layered rendering visuals ───────────────────────────────────
    // OnRender draws only bg/selection/brackets (cheap rectangles).
    // Text and gutter live in separate DrawingVisuals so caret-only
    // movements skip the expensive text redraw entirely.
    // Text/gutter are drawn at absolute document positions and scroll
    // is handled by transforms (avoids expensive DrawText on scroll).
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
        _fontWeight = weight;
        _monoTypeface = new Typeface(new FontFamily(familyName), FontStyles.Normal, weight, FontStretches.Normal);
        _fontSize = size;
        _dpi = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;
        var sample = new FormattedText("X", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _monoTypeface, _fontSize, Brushes.White, _dpi);
        _charWidth = sample.WidthIncludingTrailingWhitespace;
        _lineHeight = sample.Height;
        _ftCacheDirty = true;
        _gutterDigits = 0; // force gutter width recalc with new charWidth
        UpdateExtent();
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
                    FlowDirection.LeftToRight, typeface, 14, Brushes.White, dpi);
                var wide = new FormattedText("M", CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, typeface, 14, Brushes.White, dpi);
                if (Math.Abs(narrow.WidthIncludingTrailingWhitespace - wide.WidthIncludingTrailingWhitespace) < 0.01)
                    mono.Add(family.Source);
            }
            catch { }
        }
        _monoFontCache = mono;
        return _monoFontCache;
    }

    // ── Colours (provided by ThemeManager) ──────────────────────────

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

    // ── Dirty state ───────────────────────────────────────────────────
    private bool _isDirty;
    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (_isDirty == value) return;
            _isDirty = value;
            DirtyChanged?.Invoke(this, EventArgs.Empty);
        }
    }
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
        _ftCacheDirty = true;
        RebuildGutterPen();
        InvalidateText();
    }

    public EditorControl()
    {
        ApplyFont(DefaultFontFamily(), 14, FontWeights.Normal);
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        ThemeManager.ThemeChanged += OnThemeChanged;
        RebuildGutterPen();

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _blinkTimer.Tick += (_, _) =>
        {
            _caretVisible = !_caretVisible;
            UpdateCaretVisual();
        };

        _textVisual.Transform = _textTransform;
        _textVisual.Clip = _textClipGeom;
        _gutterVisual.Transform = _gutterTransform;
        AddVisualChild(_textVisual);
        AddVisualChild(_gutterVisual);
        AddVisualChild(_caretVisual);

        _lines.Add("");

        Loaded += (_, _) =>
        {
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
        0 => _textVisual,    // behind gutter (text clips under gutter bg)
        1 => _gutterVisual,  // covers leftmost text area
        2 => _caretVisual,   // on top of everything
        _ => throw new ArgumentOutOfRangeException(nameof(index))
    };

    /// <summary>
    /// Mark text layer for redraw. Call this (instead of bare InvalidateVisual)
    /// whenever line content, scroll position, or font/theme changes.
    /// </summary>
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
                if (_caretCol < _lines[_caretLine].Length)
                {
                    var charText = new FormattedText(
                        _lines[_caretLine][_caretCol].ToString(), CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, _monoTypeface, _fontSize, ThemeManager.EditorBg, _dpi);
                    dc.DrawText(charText, new Point(caretX, caretY));
                }
            }
            else
            {
                dc.DrawRectangle(ThemeManager.CaretBrush, null,
                    new Rect(caretX, caretY, 1, _lineHeight));
            }
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _dpi = newDpi.PixelsPerDip;
        _ftCacheDirty = true;
        InvalidateLineStates();
        InvalidateText();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Undo / Redo types
    // ──────────────────────────────────────────────────────────────────
    private record UndoEntry(List<string> Snapshot, int CaretLine, int CaretCol);

    private void PushUndo()
    {
        _textVisualDirty = true; // text is about to change
        _undoStack.Add(new UndoEntry(
            new List<string>(_lines), _caretLine, _caretCol));
        if (_undoStack.Count > MaxUndoEntries)
            _undoStack.RemoveAt(0);
        _redoStack.Clear();
        _bracketMatchDirty = true;
        // Invalidate syntax states from the earliest affected line forward
        int from = _hasSelection ? Math.Min(_caretLine, _anchorLine) : _caretLine;
        InvalidateLineStatesFrom(from);
        IsDirty = true;
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Add(new UndoEntry(
            new List<string>(_lines), _caretLine, _caretCol));
        var entry = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _lines.Clear();
        _lines.AddRange(entry.Snapshot);
        _caretLine = entry.CaretLine;
        _caretCol = entry.CaretCol;
        ClearSelection();
        _maxLineLengthDirty = true;
        _bracketMatchDirty = true;
        InvalidateLineStates();
        UpdateExtent();
        InvalidateText();
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Add(new UndoEntry(
            new List<string>(_lines), _caretLine, _caretCol));
        var entry = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _lines.Clear();
        _lines.AddRange(entry.Snapshot);
        _caretLine = entry.CaretLine;
        _caretCol = entry.CaretCol;
        ClearSelection();
        _maxLineLengthDirty = true;
        _bracketMatchDirty = true;
        InvalidateLineStates();
        UpdateExtent();
        InvalidateText();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Selection helpers
    // ──────────────────────────────────────────────────────────────────
    private void ClearSelection() => _hasSelection = false;

    private void StartSelection()
    {
        if (!_hasSelection)
        {
            _anchorLine = _caretLine;
            _anchorCol = _caretCol;
            _hasSelection = true;
        }
    }

    private (int startLine, int startCol, int endLine, int endCol) GetOrderedSelection()
    {
        if (_anchorLine < _caretLine || (_anchorLine == _caretLine && _anchorCol < _caretCol))
            return (_anchorLine, _anchorCol, _caretLine, _caretCol);
        return (_caretLine, _caretCol, _anchorLine, _anchorCol);
    }

    private string GetSelectedText()
    {
        if (!_hasSelection) return "";
        var (sl, sc, el, ec) = GetOrderedSelection();
        if (sl == el)
            return _lines[sl].Substring(sc, ec - sc);

        var parts = new List<string>();
        parts.Add(_lines[sl][sc..]);
        for (int i = sl + 1; i < el; i++)
            parts.Add(_lines[i]);
        parts.Add(_lines[el][..ec]);
        return string.Join(Environment.NewLine, parts);
    }

    private void DeleteSelection()
    {
        if (!_hasSelection) return;
        var (sl, sc, el, ec) = GetOrderedSelection();
        if (sl == el)
        {
            if (_lines[sl].Length >= _maxLineLength) _maxLineLengthDirty = true;
            _lines[sl] = _lines[sl][..sc] + _lines[sl][ec..];
        }
        else
        {
            _lines[sl] = _lines[sl][..sc] + _lines[el][ec..];
            _lines.RemoveRange(sl + 1, el - sl);
            _maxLineLengthDirty = true;
            _ftCacheDirty = true;
        }
        _caretLine = sl;
        _caretCol = sc;
        ClearSelection();
    }

    // ──────────────────────────────────────────────────────────────────
    //  String/comment detection (for suppressing auto-close)
    // ──────────────────────────────────────────────────────────────────
    private bool IsCaretInsideString(string line, int caretCol)
    {
        // Use syntax tokenizer when a grammar is active — handles all string
        // types, comments, backticks, heredocs, etc. for the current language.
        // Include multi-line state so continuation lines are detected correctly.
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

        // Fallback for plain text (no grammar): simple quote scanning
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
        line = Math.Clamp(line, 0, _lines.Count - 1);

        double textX = pos.X + _offset.X - _gutterWidth - GutterPadding;
        int col = (int)Math.Round(textX / _charWidth);
        col = Math.Clamp(col, 0, _lines[line].Length);
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
        var text = _lines[line];
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

    /// <summary>
    /// Finds the closest enclosing bracket pair around the caret.
    /// First checks brackets adjacent to the caret, then scans backward
    /// to find the innermost unmatched opener and its matching closer.
    /// </summary>
    private (int line, int col, int matchLine, int matchCol)? FindMatchingBracket()
    {
        // Priority 1: check brackets immediately adjacent to the caret
        int[] colsToCheck = _caretCol < _lines[_caretLine].Length && _caretCol > 0
            ? [_caretCol, _caretCol - 1]
            : _caretCol < _lines[_caretLine].Length
                ? [_caretCol]
                : _caretCol > 0
                    ? [_caretCol - 1]
                    : [];

        foreach (int checkCol in colsToCheck)
        {
            char ch = _lines[_caretLine][checkCol];

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

        // Priority 2: scan backward from caret to find the innermost enclosing bracket pair
        return FindEnclosingBracket();
    }

    /// <summary>
    /// Scans backward from the caret to find the nearest unmatched opening bracket,
    /// then finds its matching closer forward.
    /// </summary>
    private (int line, int col, int matchLine, int matchCol)? FindEnclosingBracket()
    {
        // Track nesting depth per bracket type as we scan backward
        var depths = new Dictionary<char, int>();
        foreach (var opener in BracketPairs.Keys)
            depths[opener] = 0;

        int line = _caretLine;
        int col = _caretCol - 1;
        int minLine = Math.Max(0, _caretLine - MaxBracketScanLines);

        while (true)
        {
            while (col < 0 || _lines[line].Length == 0)
            {
                line--;
                if (line < minLine) return null;
                col = _lines[line].Length - 1;
            }

            char ch = _lines[line][col];

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
                    // Found an unmatched opener — this is our enclosing bracket
                    var match = ScanForBracket(ch, closer, line, col, forward: true);
                    if (match != null)
                        return (line, col, match.Value.line, match.Value.col);
                    // If no match found, reset and keep scanning
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
        int maxLine = Math.Min(_lines.Count - 1, startLine + MaxBracketScanLines);
        int minLine = Math.Max(0, startLine - MaxBracketScanLines);

        while (true)
        {
            if (forward)
            {
                col++;
                while (col >= _lines[line].Length)
                {
                    line++;
                    if (line > maxLine) return null;
                    col = 0;
                }
            }
            else
            {
                col--;
                while (col < 0 || _lines[line].Length == 0)
                {
                    line--;
                    if (line < minLine) return null;
                    col = _lines[line].Length - 1;
                }
            }

            char ch = _lines[line][col];
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
        // Gutter width: use monospace charWidth × digit count (avoids FormattedText allocation)
        int digits = _lines.Count > 0
            ? (int)Math.Floor(Math.Log10(_lines.Count)) + 1
            : 1;
        if (digits != _gutterDigits)
        {
            _gutterDigits = digits;
            _gutterWidth = digits * _charWidth + 8;
        }

        if (_maxLineLengthDirty)
        {
            _maxLineLength = _lines.Count > 0 ? _lines.Max(l => l.Length) : 0;
            _maxLineLengthDirty = false;
        }
        else
        {
            // Fast path: only check the current line
            int currentLen = _caretLine < _lines.Count ? _lines[_caretLine].Length : 0;
            if (currentLen > _maxLineLength)
                _maxLineLength = currentLen;
        }

        var newExtent = new Size(
            _gutterWidth + GutterPadding + _maxLineLength * _charWidth + 50,
            _lines.Count * _lineHeight);

        // Only notify ScrollOwner when extent actually changed
        if (Math.Abs(newExtent.Width - _extent.Width) > 0.5
            || Math.Abs(newExtent.Height - _extent.Height) > 0.5)
        {
            _extent = newExtent;
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    private void EnsureCaretVisible()
    {
        // Vertical
        double caretTop = _caretLine * _lineHeight;
        double caretBottom = caretTop + _lineHeight;
        if (caretTop < _offset.Y)
            SetVerticalOffset(caretTop);
        else if (caretBottom > _offset.Y + _viewport.Height)
            SetVerticalOffset(caretBottom - _viewport.Height);

        // Horizontal
        double caretX = _gutterWidth + GutterPadding + _caretCol * _charWidth;
        if (caretX - _offset.X < _gutterWidth + GutterPadding)
            SetHorizontalOffset(caretX - _gutterWidth - GutterPadding);
        else if (caretX - _offset.X > _viewport.Width - _charWidth)
            SetHorizontalOffset(caretX - _viewport.Width + _charWidth * 2);
    }

    /// <summary>Measure the pixel X offset of a buffer column using FormattedText (handles tabs correctly).</summary>
    private double ColToPixelX(string line, int col)
    {
        if (col <= 0) return 0;
        int end = Math.Min(col, line.Length);
        var sub = line[..end];
        var ft = new FormattedText(sub, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _monoTypeface, _fontSize, Brushes.White, _dpi);
        double w = ft.WidthIncludingTrailingWhitespace;
        // If col extends past end of line, add remaining as charWidth each
        if (col > line.Length)
            w += (col - line.Length) * _charWidth;
        return w;
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
        _gutterSepPen = new Pen(ThemeManager.GutterFg, 0.5);
        _gutterSepPen.Freeze();
    }

    /// <summary>
    /// Ensure _lineStates[0..throughLine] are computed. Entry i holds the
    /// input state for line i (i.e. the output state of line i-1).
    /// _lineStates[0] is always DefaultState.
    /// </summary>
    private void EnsureLineStates(int throughLine)
    {
        if (_lineStates.Count == 0)
            _lineStates.Add(SyntaxManager.DefaultState);

        // Revalidate dirty range: recompute states from the edit point,
        // stopping early if the output state converges with what was cached.
        if (_lineStatesDirtyFrom < int.MaxValue)
        {
            int from = _lineStatesDirtyFrom;
            _lineStatesDirtyFrom = int.MaxValue;

            // If line count changed (insert/delete), states beyond the edit
            // point are indexed wrong — truncate and let the extend loop below
            // recompute them on demand.
            // Expected: _lineStates.Count == _lines.Count + 1 when fully computed.
            bool lineCountShifted = _lineStates.Count > 1
                && _lineStates.Count - 1 != _lines.Count;

            if (lineCountShifted)
            {
                int keepCount = from + 1;
                if (keepCount < _lineStates.Count)
                    _lineStates.RemoveRange(keepCount, _lineStates.Count - keepCount);
            }
            else
            {
                // Same number of lines — revalidate in-place with convergence.
                // For a typical single-char edit this stops after 1 line.
                for (int i = from; i < _lines.Count && i + 1 < _lineStates.Count; i++)
                {
                    SyntaxManager.Tokenize(_lines[i], _lineStates[i], out var outState);
                    if (_lineStates[i + 1] == outState)
                        break; // converged — all subsequent states still valid
                    _lineStates[i + 1] = outState;
                }
            }
        }

        // Extend: compute states for lines not yet processed
        while (_lineStates.Count <= throughLine && _lineStates.Count <= _lines.Count)
        {
            int lineIdx = _lineStates.Count - 1;
            var inState = _lineStates[lineIdx];
            SyntaxManager.Tokenize(_lines[lineIdx], inState, out var outState);
            _lineStates.Add(outState);
        }
    }

    private void InvalidateLineStates()
    {
        _lineStates.Clear();
        _lineStatesDirtyFrom = int.MaxValue;
    }

    /// <summary>
    /// Mark line states from a specific line forward as needing revalidation.
    /// Actual recomputation is deferred to EnsureLineStates (after the edit).
    /// </summary>
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
        int last = Math.Min(_lines.Count - 1,
            (int)((_offset.Y + _viewport.Height) / _lineHeight));
        return (first, last);
    }

    // ──────────────────────────────────────────────────────────────────
    //  OnRender — background layer only (cheap rectangles).
    //  Text and gutter are in separate DrawingVisuals updated below.
    // ──────────────────────────────────────────────────────────────────
    protected override void OnRender(DrawingContext dc)
    {
        if (_ftCacheDirty)
        {
            _ftCache.Clear();
            _lineNumCache.Clear();
            _ftCacheDirty = false;
            _textVisualDirty = true;
        }

        var (firstLine, lastLine) = VisibleLineRange();

        // Editor background
        dc.DrawRectangle(ThemeManager.EditorBg, null,
            new Rect(0, 0, ActualWidth, ActualHeight));

        // Current line highlight
        if (_caretLine >= firstLine && _caretLine <= lastLine)
        {
            double curLineY = _caretLine * _lineHeight - _offset.Y;
            dc.DrawRectangle(ThemeManager.CurrentLineBrush, null,
                new Rect(0, curLineY, ActualWidth, _lineHeight));
        }

        // Selection highlights
        if (_hasSelection)
        {
            var (sl, sc, el, ec) = GetOrderedSelection();
            for (int i = firstLine; i <= lastLine; i++)
            {
                if (i < sl || i > el) continue;
                int selStart = i == sl ? sc : 0;
                int selEnd = i == el ? ec : _lines[i].Length;

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

        // Find match highlights
        if (_findMatches.Count > 0)
        {
            for (int m = 0; m < _findMatches.Count; m++)
            {
                var (mLine, mCol, mLen) = _findMatches[m];
                if (mLine < firstLine || mLine > lastLine) continue;
                double pxStart = ColToPixelX(_lines[mLine], mCol);
                double pxEnd = ColToPixelX(_lines[mLine], mCol + mLen);
                double mx = _gutterWidth + GutterPadding + pxStart - _offset.X;
                double my = mLine * _lineHeight - _offset.Y;
                var brush = m == _currentMatchIndex
                    ? ThemeManager.FindMatchCurrentBrush
                    : ThemeManager.FindMatchBrush;
                dc.DrawRectangle(brush, null,
                    new Rect(mx, my, pxEnd - pxStart, _lineHeight));
            }
        }

        // Bracket match highlights
        if (IsKeyboardFocused && !_hasSelection)
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

        // Update transforms for scroll offset (cheap — no re-render)
        _textTransform.X = -_offset.X;
        _textTransform.Y = -_offset.Y;
        _gutterTransform.Y = -_offset.Y;

        // Update text clip (prevents text bleeding into gutter area).
        // Clip is in the visual's local (absolute) coordinate space.
        _textClipGeom.Rect = new Rect(
            _gutterWidth + _offset.X, _offset.Y,
            Math.Max(0, ActualWidth - _gutterWidth), ActualHeight);

        // Re-render text only if needed (new lines visible beyond buffer, or content changed)
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

    // ──────────────────────────────────────────────────────────────────
    //  Text layer — drawn at absolute document positions.
    //  Scroll is handled by TranslateTransform on the visual, so this
    //  only re-renders when new lines enter the buffered region or
    //  content/font/theme changes.
    // ──────────────────────────────────────────────────────────────────
    private void RenderTextVisual(int firstLine, int lastLine)
    {
        double dpi = _dpi;
        int drawFirst = Math.Max(0, firstLine - RenderBufferLines);
        int drawLast = Math.Min(_lines.Count - 1, lastLine + RenderBufferLines);

        using var dc = _textVisual.RenderOpen();

        EnsureLineStates(drawLast);
        for (int i = drawFirst; i <= drawLast; i++)
        {
            if (_lines[i].Length == 0) continue;
            double y = i * _lineHeight; // absolute position

            var inState = i < _lineStates.Count ? _lineStates[i] : SyntaxManager.DefaultState;
            if (!_ftCache.TryGetValue(i, out var cached)
                || cached.content != _lines[i] || cached.inState != inState)
            {
                var ft = new FormattedText(
                    _lines[i], CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _monoTypeface, _fontSize,
                    ThemeManager.EditorFg, dpi);
                var tokens = SyntaxManager.Tokenize(_lines[i], inState, out _);
                foreach (var token in tokens)
                    ft.SetForegroundBrush(
                        ThemeManager.GetScopeBrush(token.Scope), token.Start, token.Length);
                _ftCache[i] = (_lines[i], inState, ft);
                cached = _ftCache[i];
            }
            dc.DrawText(cached.ft,
                new Point(_gutterWidth + GutterPadding, y)); // absolute X
        }

        _renderedFirstLine = drawFirst;
        _renderedLastLine = drawLast;

        // Prune caches of entries far from the rendered region
        if (_ftCache.Count > (drawLast - drawFirst + 1) * 3)
        {
            int margin = drawLast - drawFirst + 1;
            int pruneBelow = drawFirst - margin;
            int pruneAbove = drawLast + margin;
            var keysToRemove = new List<int>();
            foreach (var key in _ftCache.Keys)
                if (key < pruneBelow || key > pruneAbove)
                    keysToRemove.Add(key);
            foreach (var key in keysToRemove)
            {
                _ftCache.Remove(key);
                _lineNumCache.Remove(key);
            }
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  Gutter layer — background, separator, line numbers
    // ──────────────────────────────────────────────────────────────────
    private void RenderGutterVisual(int firstLine, int lastLine)
    {
        double dpi = _dpi;
        int drawFirst = Math.Max(0, firstLine - RenderBufferLines);
        int drawLast = Math.Min(_lines.Count - 1, lastLine + RenderBufferLines);

        using var dc = _gutterVisual.RenderOpen();

        // Gutter background covers the full buffered range plus padding
        double bgTop = drawFirst * _lineHeight;
        double bgBottom = (drawLast + 1) * _lineHeight;
        dc.DrawRectangle(ThemeManager.EditorBg, null,
            new Rect(0, bgTop, _gutterWidth, bgBottom - bgTop));
        dc.DrawLine(_gutterSepPen,
            new Point(_gutterWidth, bgTop), new Point(_gutterWidth, bgBottom));

        for (int i = drawFirst; i <= drawLast; i++)
        {
            double y = i * _lineHeight; // absolute position
            var brush = i == _caretLine
                ? ThemeManager.ActiveLineNumberFg : ThemeManager.GutterFg;
            if (!_lineNumCache.TryGetValue(i, out var lnCached)
                || lnCached.brush != brush)
            {
                var ft = new FormattedText(
                    (i + 1).ToString(), CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _monoTypeface, _fontSize, brush, dpi);
                _lineNumCache[i] = (brush, ft);
                lnCached = _lineNumCache[i];
            }
            dc.DrawText(lnCached.ft,
                new Point(_gutterWidth - lnCached.ft.WidthIncludingTrailingWhitespace - 4, y));
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
            // Double-click: select word
            var (ws, we) = GetWordAt(line, col);
            _caretLine = line;
            _anchorLine = line;
            _anchorCol = ws;
            _caretCol = we;
            _hasSelection = ws != we;
        }
        else
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                StartSelection();
            }
            else
            {
                ClearSelection();
                _anchorLine = line;
                _anchorCol = col;
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

        // Skip render if caret hasn't actually moved to a new position
        if (line == _caretLine && col == _caretCol)
        {
            e.Handled = true;
            return;
        }

        if (line != _anchorLine || col != _anchorCol)
            _hasSelection = true;

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

        // Over-type closing bracket or quote: if the char at cursor already matches, just skip over it
        if (e.Text.Length == 1 && (ClosingBrackets.Contains(ch) || AutoCloseQuotes.Contains(ch)) && !_hasSelection)
        {
            var line = _lines[_caretLine];
            if (_caretCol < line.Length && line[_caretCol] == ch)
            {
                _caretCol++;
                ResetPreferredCol();
                ClearSelection();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                return;
            }
        }

        ResetPreferredCol();
        PushUndo();
        if (_hasSelection) DeleteSelection();

        var currentLine = _lines[_caretLine];
        bool insideString = IsCaretInsideString(currentLine, _caretCol);

        // Auto-close opening bracket: insert both opener and closer, cursor between
        if (!insideString && e.Text.Length == 1 && BracketPairs.TryGetValue(ch, out char closer))
        {
            _lines[_caretLine] = currentLine[.._caretCol] + ch + closer + currentLine[_caretCol..];
            _caretCol++; // position between the pair
        }
        // Auto-close quotes: insert pair, cursor between
        else if (!insideString && e.Text.Length == 1 && AutoCloseQuotes.Contains(ch))
        {
            _lines[_caretLine] = currentLine[.._caretCol] + ch.ToString() + ch + currentLine[_caretCol..];
            _caretCol++; // position between the pair
        }
        else
        {
            _lines[_caretLine] = currentLine[.._caretCol] + e.Text + currentLine[_caretCol..];
            _caretCol += e.Text.Length;
        }

        ClearSelection();
        UpdateExtent();
        EnsureCaretVisible();
        ResetCaret();
        e.Handled = true;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Keyboard
    // ──────────────────────────────────────────────────────────────────
    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;

        switch (e.Key)
        {
            case Key.Return:
                ResetPreferredCol();
                PushUndo();
                if (_hasSelection) DeleteSelection();
                var indent = _lines[_caretLine][..^(_lines[_caretLine].TrimStart().Length)];
                var rest = _lines[_caretLine][_caretCol..];
                _lines[_caretLine] = _lines[_caretLine][.._caretCol];

                // Smart Enter between brackets: if char before caret is an opening bracket
                // and char after (first char of rest) is its matching closer, create an indented block
                bool betweenBrackets = _caretCol > 0 && rest.Length > 0
                    && BracketPairs.TryGetValue(_lines[_caretLine][^1], out char expectedCloser)
                    && rest[0] == expectedCloser;

                // If the character before the caret is an opening bracket, increase indent
                bool afterOpen = _caretCol > 0
                    && BracketPairs.ContainsKey(_lines[_caretLine][_caretCol - 1]);

                if (betweenBrackets)
                {
                    var innerIndent = indent + new string(' ', TabSize);
                    _caretLine++;
                    _lines.Insert(_caretLine, innerIndent);         // cursor line (indented)
                    _lines.Insert(_caretLine + 1, indent + rest);   // closing bracket line (original indent)
                    _caretCol = innerIndent.Length;
                }
                else if (afterOpen)
                {
                    var innerIndent = indent + new string(' ', TabSize);
                    _caretLine++;
                    _lines.Insert(_caretLine, innerIndent + rest);
                    _caretCol = innerIndent.Length;
                }
                else
                {
                    _caretLine++;
                    _lines.Insert(_caretLine, indent + rest);
                    _caretCol = indent.Length;
                }

                ClearSelection();
                _maxLineLengthDirty = true;
                _ftCacheDirty = true;
                UpdateExtent();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                break;

            case Key.Back:
                ResetPreferredCol();
                PushUndo();
                if (_hasSelection)
                {
                    DeleteSelection();
                }
                else if (_caretCol > 0)
                {
                    var line = _lines[_caretLine];
                    if (line.Length >= _maxLineLength) _maxLineLengthDirty = true;

                    // Delete both characters of an auto-closed pair (cursor between opener and closer)
                    bool deletedPair = false;
                    if (_caretCol < line.Length)
                    {
                        char before = line[_caretCol - 1];
                        char after = line[_caretCol];
                        bool isPair = (BracketPairs.TryGetValue(before, out char expected) && after == expected)
                                      || (AutoCloseQuotes.Contains(before) && after == before);
                        if (isPair)
                        {
                            _lines[_caretLine] = line[..(_caretCol - 1)] + line[(_caretCol + 1)..];
                            _caretCol--;
                            deletedPair = true;
                        }
                    }

                    if (!deletedPair)
                    {
                        // Smart backspace: if caret is in leading whitespace, snap to previous tab stop
                        int leadingSpaces = line.Length - line.TrimStart().Length;
                        int remove = 1;
                        if (_caretCol <= leadingSpaces && line[.._caretCol].All(c => c == ' '))
                        {
                            int prevStop = (_caretCol - 1) / TabSize * TabSize;
                            remove = _caretCol - prevStop;
                        }
                        _lines[_caretLine] = line[..(_caretCol - remove)] + line[_caretCol..];
                        _caretCol -= remove;
                    }
                }
                else if (_caretLine > 0)
                {
                    _caretCol = _lines[_caretLine - 1].Length;
                    _lines[_caretLine - 1] += _lines[_caretLine];
                    _lines.RemoveAt(_caretLine);
                    _caretLine--;
                    _maxLineLengthDirty = true;
                    _ftCacheDirty = true;
                }
                ClearSelection();
                UpdateExtent();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                break;

            case Key.Delete:
                ResetPreferredCol();
                PushUndo();
                if (_hasSelection)
                {
                    DeleteSelection();
                }
                else if (_caretCol < _lines[_caretLine].Length)
                {
                    if (_lines[_caretLine].Length >= _maxLineLength) _maxLineLengthDirty = true;
                    _lines[_caretLine] = _lines[_caretLine][.._caretCol] + _lines[_caretLine][(_caretCol + 1)..];
                }
                else if (_caretLine < _lines.Count - 1)
                {
                    _lines[_caretLine] += _lines[_caretLine + 1];
                    _lines.RemoveAt(_caretLine + 1);
                    _maxLineLengthDirty = true;
                    _ftCacheDirty = true;
                }
                ClearSelection();
                UpdateExtent();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                break;

            case Key.Tab:
                ResetPreferredCol();
                PushUndo();
                if (_hasSelection)
                {
                    var (sl, _, el, _) = GetOrderedSelection();
                    if (sl != el)
                    {
                        // Block indent/outdent
                        for (int i = sl; i <= el; i++)
                        {
                            if (shift)
                            {
                                // Outdent: remove up to TabSize leading spaces
                                int remove = 0;
                                while (remove < TabSize && remove < _lines[i].Length && _lines[i][remove] == ' ')
                                    remove++;
                                if (remove > 0)
                                {
                                    _lines[i] = _lines[i][remove..];
                                    if (i == _caretLine) _caretCol = Math.Max(0, _caretCol - remove);
                                    if (i == _anchorLine) _anchorCol = Math.Max(0, _anchorCol - remove);
                                }
                            }
                            else
                            {
                                // Indent: prepend TabSize spaces
                                _lines[i] = new string(' ', TabSize) + _lines[i];
                                if (i == _caretLine) _caretCol += TabSize;
                                if (i == _anchorLine) _anchorCol += TabSize;
                            }
                        }
                        _hasSelection = true;
                        UpdateExtent();
                        EnsureCaretVisible();
                        ResetCaret();
                        e.Handled = true;
                        break;
                    }
                }
                if (!shift)
                {
                    if (_hasSelection) DeleteSelection();
                    int spacesToInsert = TabSize - (_caretCol % TabSize);
                    _lines[_caretLine] = _lines[_caretLine][.._caretCol] + new string(' ', spacesToInsert) + _lines[_caretLine][_caretCol..];
                    _caretCol += spacesToInsert;
                }
                ClearSelection();
                UpdateExtent();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                break;

            case Key.Left:
                ResetPreferredCol();
                if (shift) StartSelection();
                if (ctrl)
                    _caretCol = WordLeft(_lines[_caretLine], _caretCol);
                else if (_caretCol > 0)
                    _caretCol--;
                else if (_caretLine > 0)
                {
                    _caretLine--;
                    _caretCol = _lines[_caretLine].Length;
                }
                if (!shift) ClearSelection();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                break;

            case Key.Right:
                ResetPreferredCol();
                if (shift) StartSelection();
                if (ctrl)
                    _caretCol = WordRight(_lines[_caretLine], _caretCol);
                else if (_caretCol < _lines[_caretLine].Length)
                    _caretCol++;
                else if (_caretLine < _lines.Count - 1)
                {
                    _caretLine++;
                    _caretCol = 0;
                }
                if (!shift) ClearSelection();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                break;

            case Key.Up:
                if (shift) StartSelection();
                if (_caretLine > 0)
                {
                    if (_preferredCol < 0) _preferredCol = _caretCol;
                    _caretLine--;
                    _caretCol = Math.Min(_preferredCol, _lines[_caretLine].Length);
                }
                if (!shift) ClearSelection();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                break;

            case Key.Down:
                if (shift) StartSelection();
                if (_caretLine < _lines.Count - 1)
                {
                    if (_preferredCol < 0) _preferredCol = _caretCol;
                    _caretLine++;
                    _caretCol = Math.Min(_preferredCol, _lines[_caretLine].Length);
                }
                if (!shift) ClearSelection();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                break;

            case Key.Home:
                ResetPreferredCol();
                if (shift) StartSelection();
                if (ctrl) _caretLine = 0;
                _caretCol = 0;
                if (!shift) ClearSelection();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                break;

            case Key.End:
                ResetPreferredCol();
                if (shift) StartSelection();
                if (ctrl) _caretLine = _lines.Count - 1;
                _caretCol = _lines[_caretLine].Length;
                if (!shift) ClearSelection();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                break;

            case Key.PageUp:
            case Key.PageDown:
            {
                int visibleLines = Math.Max(1, (int)(_viewport.Height / _lineHeight) - 1);
                if (shift) StartSelection();
                if (e.Key == Key.PageUp)
                    _caretLine = Math.Max(0, _caretLine - visibleLines);
                else
                    _caretLine = Math.Min(_lines.Count - 1, _caretLine + visibleLines);
                _caretCol = Math.Min(_caretCol, _lines[_caretLine].Length);
                if (!shift) ClearSelection();
                EnsureCaretVisible();
                ResetCaret();
                e.Handled = true;
                break;
            }

            case Key.A when ctrl:
                _anchorLine = 0;
                _anchorCol = 0;
                _caretLine = _lines.Count - 1;
                _caretCol = _lines[^1].Length;
                _hasSelection = true;
                InvalidateVisual();
                e.Handled = true;
                break;

            case Key.C when ctrl:
                try
                {
                    if (_hasSelection)
                        Clipboard.SetText(GetSelectedText());
                }
                catch (System.Runtime.InteropServices.ExternalException) { }
                e.Handled = true;
                break;

            case Key.X when ctrl:
                try
                {
                    if (_hasSelection)
                    {
                        ResetPreferredCol();
                        PushUndo();
                        Clipboard.SetText(GetSelectedText());
                        DeleteSelection();
                        UpdateExtent();
                        EnsureCaretVisible();
                        ResetCaret();
                    }
                }
                catch (System.Runtime.InteropServices.ExternalException) { }
                e.Handled = true;
                break;

            case Key.V when ctrl:
                try { if (!Clipboard.ContainsText()) break; }
                catch (System.Runtime.InteropServices.ExternalException) { break; }
                {
                    ResetPreferredCol();
                    PushUndo();
                    if (_hasSelection) DeleteSelection();
                    string text;
                    try { text = Clipboard.GetText(); }
                    catch (System.Runtime.InteropServices.ExternalException) { break; }
                    var pasteLines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                    for (int pi = 0; pi < pasteLines.Length; pi++)
                        pasteLines[pi] = ExpandTabs(pasteLines[pi]);
                    if (pasteLines.Length == 1)
                    {
                        _lines[_caretLine] = _lines[_caretLine][.._caretCol] + pasteLines[0] + _lines[_caretLine][_caretCol..];
                        _caretCol += pasteLines[0].Length;
                    }
                    else
                    {
                        var after = _lines[_caretLine][_caretCol..];
                        _lines[_caretLine] = _lines[_caretLine][.._caretCol] + pasteLines[0];
                        for (int i = 1; i < pasteLines.Length; i++)
                        {
                            _caretLine++;
                            _lines.Insert(_caretLine, pasteLines[i]);
                        }
                        _caretCol = _lines[_caretLine].Length;
                        _lines[_caretLine] += after;
                        _ftCacheDirty = true;
                    }
                    ClearSelection();
                    UpdateExtent();
                    EnsureCaretVisible();
                    ResetCaret();
                }
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

    // ──────────────────────────────────────────────────────────────────
    //  Mouse wheel
    // ──────────────────────────────────────────────────────────────────
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        SetVerticalOffset(_offset.Y - e.Delta / 120.0 * _lineHeight * 3);
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
        if (Math.Abs(offset - _offset.X) < 0.01) return;
        _offset.X = offset;
        ScrollOwner?.InvalidateScrollInfo();
        // Transforms handle scroll — only InvalidateVisual for bg layer.
        // Text re-render is gated by buffer check in OnRender.
        InvalidateVisual();
    }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
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
            _textVisualDirty = true; // more/fewer lines may be visible
            _gutterVisualDirty = true;
            ScrollOwner?.InvalidateScrollInfo();
        }
        return finalSize;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Public API for file operations
    // ──────────────────────────────────────────────────────────────────
    public string LineEnding => _lineEnding == "\n" ? "LF" : "CRLF";

    public void SetContent(string text)
    {
        // Detect dominant line ending before normalizing
        _lineEnding = DetectLineEnding(text);

        _lines.Clear();
        // Split on line endings without intermediate full-string copies
        var rawLines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        for (int i = 0; i < rawLines.Length; i++)
            rawLines[i] = ExpandTabs(rawLines[i]);
        _lines.AddRange(rawLines);
        if (_lines.Count == 0) _lines.Add("");
        _caretLine = 0;
        _caretCol = 0;
        ClearSelection();
        _undoStack.Clear();
        _redoStack.Clear();
        _findMatches.Clear();
        _currentMatchIndex = 0;
        IsDirty = false;
        _maxLineLengthDirty = true;
        _ftCacheDirty = true;
        InvalidateLineStates();
        UpdateExtent();
        SetVerticalOffset(0);
        SetHorizontalOffset(0);
        InvalidateText();
    }

    public string GetContent()
    {
        return string.Join(_lineEnding, _lines);
    }

    private string ExpandTabs(string line)
    {
        if (!line.Contains('\t')) return line;
        var sb = new System.Text.StringBuilder(line.Length + 16);
        foreach (char c in line)
        {
            if (c == '\t')
            {
                int spaces = TabSize - (sb.Length % TabSize);
                sb.Append(' ', spaces);
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    private static string DetectLineEnding(string text)
    {
        int crlf = 0, lf = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                crlf++;
                i++; // skip the \n
            }
            else if (text[i] == '\n')
            {
                lf++;
            }
        }
        // Default to platform line ending if no line breaks found
        if (crlf == 0 && lf == 0) return Environment.NewLine;
        return lf > crlf ? "\n" : "\r\n";
    }

    public void InvalidateSyntax()
    {
        InvalidateLineStates();
        _ftCacheDirty = true;
        InvalidateText();
    }

    public void MarkClean()
    {
        IsDirty = false;
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
        for (int line = 0; line < _lines.Count; line++)
        {
            int pos = 0;
            while (pos < _lines[line].Length)
            {
                int idx = _lines[line].IndexOf(query, pos, comparison);
                if (idx < 0) break;
                _findMatches.Add((line, idx, query.Length));
                pos = idx + 1;
            }
        }

        // Select the first match at or after the caret; wrap to start if none found
        if (_findMatches.Count > 0)
        {
            _currentMatchIndex = 0; // default: wrap to first match
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

    private void NavigateToCurrentMatch()
    {
        if (_currentMatchIndex < 0 || _currentMatchIndex >= _findMatches.Count) return;
        var (line, col, _) = _findMatches[_currentMatchIndex];
        _caretLine = line;
        _caretCol = col;
        ClearSelection();
        CentreLineInViewport(line);
        ResetCaret();
    }

    private void CentreLineInViewport(int line)
    {
        double targetY = line * _lineHeight - (_viewport.Height - _lineHeight) / 2;
        SetVerticalOffset(targetY);

        // Centre horizontally on the caret position
        double caretX = _gutterWidth + GutterPadding + ColToPixelX(_lines[line], _caretCol);
        double textAreaWidth = _viewport.Width - _gutterWidth - GutterPadding;
        double targetX = caretX - _gutterWidth - GutterPadding - textAreaWidth / 2;
        SetHorizontalOffset(targetX);
    }
}
