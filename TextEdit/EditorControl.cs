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

    // ── Caret / selection ────────────────────────────────────────────
    private int _caretLine;
    private int _caretCol;
    private int _anchorLine;
    private int _anchorCol;
    private bool _hasSelection;
    private int _preferredCol = -1; // sticky column for vertical movement

    // ── Undo / redo ──────────────────────────────────────────────────
    private readonly Stack<UndoEntry> _undoStack = new();
    private readonly Stack<UndoEntry> _redoStack = new();

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
        var sample = new FormattedText("X", CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _monoTypeface, _fontSize, Brushes.White,
            VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip);
        _charWidth = sample.WidthIncludingTrailingWhitespace;
        _lineHeight = sample.Height;
        UpdateExtent();
        InvalidateVisual();
    }

    public static List<string> GetMonospaceFonts()
    {
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
        return mono;
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

    public EditorControl()
    {
        ApplyFont(DefaultFontFamily(), 14, FontWeights.Normal);
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        ThemeManager.ThemeChanged += (_, _) => InvalidateVisual();

        _blinkTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _blinkTimer.Tick += (_, _) =>
        {
            _caretVisible = !_caretVisible;
            InvalidateVisual();
        };

        // Load placeholder content
        var placeholder = """
            using System;

            namespace HelloWorld;

            public class Program
            {
                public static void Main(string[] args)
                {
                    Console.WriteLine("Hello, World!");

                    for (int i = 0; i < 10; i++)
                    {
                        Console.WriteLine($"Line {i}");
                    }
                }
            }
            """;
        _lines.AddRange(placeholder.Split('\n').Select(l => l.Replace("\r", "")));
        if (_lines.Count == 0) _lines.Add("");

        Loaded += (_, _) =>
        {
            Keyboard.Focus(this);
            UpdateExtent();
        };
    }

    // ──────────────────────────────────────────────────────────────────
    //  Undo / Redo types
    // ──────────────────────────────────────────────────────────────────
    private record UndoEntry(List<string> Snapshot, int CaretLine, int CaretCol);

    private void PushUndo()
    {
        _undoStack.Push(new UndoEntry(
            _lines.Select(l => l).ToList(), _caretLine, _caretCol));
        _redoStack.Clear();
        IsDirty = true;
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        _redoStack.Push(new UndoEntry(
            _lines.Select(l => l).ToList(), _caretLine, _caretCol));
        var entry = _undoStack.Pop();
        _lines.Clear();
        _lines.AddRange(entry.Snapshot);
        _caretLine = entry.CaretLine;
        _caretCol = entry.CaretCol;
        ClearSelection();
        UpdateExtent();
        InvalidateVisual();
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        _undoStack.Push(new UndoEntry(
            _lines.Select(l => l).ToList(), _caretLine, _caretCol));
        var entry = _redoStack.Pop();
        _lines.Clear();
        _lines.AddRange(entry.Snapshot);
        _caretLine = entry.CaretLine;
        _caretCol = entry.CaretCol;
        ClearSelection();
        UpdateExtent();
        InvalidateVisual();
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
            _lines[sl] = _lines[sl][..sc] + _lines[sl][ec..];
        }
        else
        {
            _lines[sl] = _lines[sl][..sc] + _lines[el][ec..];
            _lines.RemoveRange(sl + 1, el - sl);
        }
        _caretLine = sl;
        _caretCol = sc;
        ClearSelection();
    }

    // ──────────────────────────────────────────────────────────────────
    //  String detection (for suppressing auto-close inside strings)
    // ──────────────────────────────────────────────────────────────────
    private static bool IsCaretInsideString(string line, int caretCol)
    {
        char? openQuote = null;
        for (int i = 0; i < caretCol && i < line.Length; i++)
        {
            char c = line[i];
            if (c == '\\' && openQuote != null) { i++; continue; } // skip escaped char
            if (openQuote == null)
            {
                if (c == '\'' || c == '"') openQuote = c;
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

        while (true)
        {
            while (col < 0)
            {
                line--;
                if (line < 0) return null;
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

        while (true)
        {
            if (forward)
            {
                col++;
                while (col >= _lines[line].Length)
                {
                    line++;
                    if (line >= _lines.Count) return null;
                    col = 0;
                }
            }
            else
            {
                col--;
                while (col < 0)
                {
                    line--;
                    if (line < 0) return null;
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
        _gutterWidth = new FormattedText(
            _lines.Count.ToString(), CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, _monoTypeface, _fontSize, ThemeManager.GutterFg,
            VisualTreeHelper.GetDpi(this).PixelsPerDip).WidthIncludingTrailingWhitespace + 8;

        int maxLen = _lines.Count > 0 ? _lines.Max(l => l.Length) : 0;
        _extent = new Size(
            _gutterWidth + GutterPadding + maxLen * _charWidth + 50,
            _lines.Count * _lineHeight);
        ScrollOwner?.InvalidateScrollInfo();
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

    private void ResetPreferredCol() => _preferredCol = -1;

    private void ResetCaret()
    {
        _caretVisible = true;
        _blinkTimer.Stop();
        if (_caretBlinkMs > 0) _blinkTimer.Start();
        InvalidateVisual();
        CaretMoved?.Invoke(this, EventArgs.Empty);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Rendering
    // ──────────────────────────────────────────────────────────────────
    protected override void OnRender(DrawingContext dc)
    {
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(ThemeManager.EditorBg, null, bounds);

        int firstLine = Math.Max(0, (int)(_offset.Y / _lineHeight));
        int lastLine = Math.Min(_lines.Count - 1,
            (int)((_offset.Y + _viewport.Height) / _lineHeight));

        // Draw current line highlight
        if (_caretLine >= firstLine && _caretLine <= lastLine)
        {
            double curLineY = _caretLine * _lineHeight - _offset.Y;
            dc.DrawRectangle(ThemeManager.CurrentLineBrush, null,
                new Rect(0, curLineY, ActualWidth, _lineHeight));
        }

        // Draw selection highlights
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

                // For lines between start and end, extend selection to edge
                if (i > sl && i < el)
                    x2 = Math.Max(x2, ActualWidth);
                if (i == sl && i != el)
                    x2 = Math.Max(x2, ActualWidth);

                dc.DrawRectangle(ThemeManager.SelectionBrush, null,
                    new Rect(Math.Max(x1, _gutterWidth + GutterPadding), y,
                             Math.Max(0, x2 - Math.Max(x1, _gutterWidth + GutterPadding)),
                             _lineHeight));
            }
        }

        // Draw matching bracket highlights
        if (IsKeyboardFocused && !_hasSelection)
        {
            var bracketMatch = FindMatchingBracket();
            if (bracketMatch is var (bl, bc, ml, mc))
            {
                foreach (var (bLine, bCol) in new[] { (bl, bc), (ml, mc) })
                {
                    if (bLine >= firstLine && bLine <= lastLine)
                    {
                        double bx = _gutterWidth + GutterPadding + bCol * _charWidth - _offset.X;
                        double by = bLine * _lineHeight - _offset.Y;
                        var rect = new Rect(bx, by, _charWidth, _lineHeight);
                        dc.DrawRectangle(ThemeManager.MatchingBracketBrush, ThemeManager.MatchingBracketPen, rect);
                    }
                }
            }
        }

        // Draw text (clipped to area right of gutter)
        var textClip = new RectangleGeometry(new Rect(_gutterWidth, 0, Math.Max(0, ActualWidth - _gutterWidth), ActualHeight));
        dc.PushClip(textClip);
        for (int i = firstLine; i <= lastLine; i++)
        {
            double y = i * _lineHeight - _offset.Y;
            if (_lines[i].Length == 0) continue;

            var tokens = SyntaxManager.Tokenize(_lines[i]);
            if (tokens.Count == 0)
            {
                // No syntax — draw entire line in default color
                var ft = new FormattedText(
                    _lines[i], CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _monoTypeface, _fontSize, ThemeManager.EditorFg, dpi);
                dc.DrawText(ft, new Point(_gutterWidth + GutterPadding - _offset.X, y));
            }
            else
            {
                // Build a single FormattedText and color spans
                var ft = new FormattedText(
                    _lines[i], CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight, _monoTypeface, _fontSize, ThemeManager.EditorFg, dpi);
                foreach (var token in tokens)
                {
                    var brush = ThemeManager.GetScopeBrush(token.Scope);
                    ft.SetForegroundBrush(brush, token.Start, token.Length);
                }
                dc.DrawText(ft, new Point(_gutterWidth + GutterPadding - _offset.X, y));
            }
        }
        dc.Pop();

        // Draw gutter background
        dc.DrawRectangle(ThemeManager.EditorBg, null, new Rect(0, 0, _gutterWidth, ActualHeight));

        // Draw gutter separator line
        var sepPen = new Pen(ThemeManager.GutterFg, 0.5);
        sepPen.Freeze();
        dc.DrawLine(sepPen, new Point(_gutterWidth, 0), new Point(_gutterWidth, ActualHeight));

        // Draw line numbers
        for (int i = firstLine; i <= lastLine; i++)
        {
            double y = i * _lineHeight - _offset.Y;
            var lineNumBrush = i == _caretLine ? ThemeManager.ActiveLineNumberFg : ThemeManager.GutterFg;
            var lineNum = new FormattedText(
                (i + 1).ToString(), CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, _monoTypeface, _fontSize, lineNumBrush, dpi);
            dc.DrawText(lineNum, new Point(_gutterWidth - lineNum.WidthIncludingTrailingWhitespace - 4, y));
        }

        // Draw caret
        if (IsKeyboardFocused && _caretVisible)
        {
            double caretX = _gutterWidth + GutterPadding + _caretCol * _charWidth - _offset.X;
            double caretY = _caretLine * _lineHeight - _offset.Y;
            if (caretX >= _gutterWidth && caretY + _lineHeight > 0 && caretY < ActualHeight)
            {
                if (BlockCaret)
                {
                    dc.DrawRectangle(ThemeManager.CaretBrush, null,
                        new Rect(caretX, caretY, _charWidth, _lineHeight));
                    // Draw the character under the block caret in the background color
                    if (_caretCol < _lines[_caretLine].Length)
                    {
                        var charText = new FormattedText(
                            _lines[_caretLine][_caretCol].ToString(), CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight, _monoTypeface, _fontSize, ThemeManager.EditorBg, dpi);
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

        if (line != _anchorLine || col != _anchorCol || line != _caretLine || col != _caretCol)
            _hasSelection = true;

        _caretLine = line;
        _caretCol = col;
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
                    _lines[_caretLine] = _lines[_caretLine][.._caretCol] + _lines[_caretLine][(_caretCol + 1)..];
                }
                else if (_caretLine < _lines.Count - 1)
                {
                    _lines[_caretLine] += _lines[_caretLine + 1];
                    _lines.RemoveAt(_caretLine + 1);
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
                    _lines[_caretLine] = _lines[_caretLine][.._caretCol] + new string(' ', TabSize) + _lines[_caretLine][_caretCol..];
                    _caretCol += TabSize;
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
                if (_hasSelection)
                    Clipboard.SetText(GetSelectedText());
                e.Handled = true;
                break;

            case Key.X when ctrl:
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
                e.Handled = true;
                break;

            case Key.V when ctrl:
                if (Clipboard.ContainsText())
                {
                    ResetPreferredCol();
                    PushUndo();
                    if (_hasSelection) DeleteSelection();
                    var text = Clipboard.GetText();
                    var pasteLines = text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
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
        _viewport = availableSize;
        UpdateExtent();
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewport = finalSize;
        UpdateExtent();
        return finalSize;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Public API for file operations
    // ──────────────────────────────────────────────────────────────────
    public void SetContent(string text)
    {
        _lines.Clear();
        _lines.AddRange(text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'));
        if (_lines.Count == 0) _lines.Add("");
        _caretLine = 0;
        _caretCol = 0;
        ClearSelection();
        _undoStack.Clear();
        _redoStack.Clear();
        IsDirty = false;
        UpdateExtent();
        SetVerticalOffset(0);
        SetHorizontalOffset(0);
        InvalidateVisual();
    }

    public string GetContent()
    {
        return string.Join(Environment.NewLine, _lines);
    }

    public void MarkClean()
    {
        IsDirty = false;
    }
}
