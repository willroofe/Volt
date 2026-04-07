using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Volt;

public class EditorControl : FrameworkElement, IScrollInfo
{
    // ── Extracted components ─────────────────────────────────────────
    private readonly TextBuffer _buffer = new();
    private readonly UndoManager _undoManager = new();
    private int _cleanUndoDepth; // undo stack depth when last marked clean (-1 = unreachable)
    private readonly SelectionManager _selection = new();
    private readonly FindManager _find = new();
    private readonly FontManager _font = new();

    // ── Managers (injected via constructor) ──────────────────────────
    public ThemeManager ThemeManager { get; }
    public SyntaxManager SyntaxManager { get; }

    private SyntaxDefinition? _grammar;

    public string LanguageName => _grammar?.Name ?? "Plain Text";

    public void SetGrammar(SyntaxDefinition? grammar)
    {
        _grammar = grammar;
        InvalidateSyntax();
    }

    // ── Caret ────────────────────────────────────────────────────────
    private int _caretLine;
    private int _caretCol;
    private int _preferredCol = -1; // sticky column for vertical movement
    private int _prevCaretLine = -1;

    // ── Bracket match cache ─────────────────────────────────────────
    private (int line, int col, int matchLine, int matchCol)? _bracketMatchCache;
    private bool _bracketMatchDirty = true;

    // ── Settings ───────────────────────────────────────────────────────
    public int TabSize { get; set; } = 4;
    public bool BlockCaret { get; set; }
    private bool _indentGuides = true;
    public bool IndentGuides
    {
        get => _indentGuides;
        set
        {
            if (_indentGuides == value) return;
            _indentGuides = value;
            _textVisualDirty = true;
            InvalidateVisual();
        }
    }

    private bool _wordWrapAtWords = true;
    public bool WordWrapAtWords
    {
        get => _wordWrapAtWords;
        set
        {
            if (_wordWrapAtWords == value) return;
            _wordWrapAtWords = value;
            if (_wordWrap) InvalidateWrapLayout();
        }
    }

    private bool _wordWrapIndent = true;
    public bool WordWrapIndent
    {
        get => _wordWrapIndent;
        set
        {
            if (_wordWrapIndent == value) return;
            _wordWrapIndent = value;
            if (_wordWrap) InvalidateWrapLayout();
        }
    }

    private bool _wordWrap;
    public bool WordWrap
    {
        get => _wordWrap;
        set
        {
            if (_wordWrap == value) return;
            // Anchor to top visible line so toggling wrap doesn't shift the viewport
            int anchorLine;
            int anchorWrap = 0;
            double anchorDelta;
            if (_wordWrap && _wrap.HasValidData(_buffer.Count) && _wrap.TotalVisualLines > 0)
            {
                int topVisual = Math.Clamp((int)(_offset.Y / _font.LineHeight), 0, _wrap.TotalVisualLines - 1);
                (anchorLine, anchorWrap) = VisualToLogical(topVisual);
                anchorDelta = _offset.Y - (_wrap.CumulOffset(anchorLine) + anchorWrap) * _font.LineHeight;
            }
            else
            {
                anchorLine = Math.Clamp((int)(_offset.Y / _font.LineHeight), 0, Math.Max(0, _buffer.Count - 1));
                anchorDelta = _offset.Y - anchorLine * _font.LineHeight;
            }
            _wordWrap = value;
            _skipWrapAnchor = true;
            RecalcWrapData();
            if (_wordWrap) SetHorizontalOffset(0);
            _textVisualDirty = true;
            _gutterVisualDirty = true;
            UpdateExtent();
            _skipWrapAnchor = false;
            // Restore scroll so the same logical line stays at the top of the viewport
            if (_wordWrap && _wrap.HasValidData(_buffer.Count))
            {
                int newWrap = Math.Min(anchorWrap, VisualLineCount(anchorLine) - 1);
                double newY = (_wrap.CumulOffset(anchorLine) + newWrap) * _font.LineHeight + anchorDelta;
                double maxY = Math.Max(0, _extent.Height - _viewport.Height);
                _offset.Y = Math.Clamp(newY, 0, maxY);
            }
            else
            {
                double newY = anchorLine * _font.LineHeight + anchorDelta;
                double maxY = Math.Max(0, _extent.Height - _viewport.Height);
                _offset.Y = Math.Clamp(newY, 0, maxY);
            }
            _textTransform.Y = -_offset.Y;
            _gutterTransform.Y = -_offset.Y;
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateVisual();
        }
    }

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

    // ── Code folding ────────────────────────────────────────────────
    private readonly HashSet<int> _foldedLines = new();  // opener lines that are collapsed
    private BitArray? _hiddenLines;                       // derived: lines hidden by folds
    private int _hoverFoldLine = -1;                      // line with fold marker being hovered

    private bool IsLineHidden(int line) =>
        _hiddenLines != null && line < _hiddenLines.Length && _hiddenLines[line];

    /// <summary>Whether layout arrays are active for fold-aware coordinates.</summary>
    private bool HasFoldLayout => _hiddenLines != null;

    // ── Rendering constants ──────────────────────────────────────────
    private const double GutterPadding = 4;
    private const double GutterRightMargin = 8;
    private const double FoldGutterWidth = 14;
    private const double GutterSeparatorThickness = 0.5;
    private const double HorizontalScrollPadding = 50;
    private const double BarCaretWidth = 1;
    private const double MouseWheelDeltaUnit = 120.0;
    private const int ScrollWheelLines = 3;

    // ── Cached pens / metrics ───────────────────────────────────────
    private Pen _gutterSepPen = new(Brushes.Gray, GutterSeparatorThickness);
    private int _gutterDigits;

    // ── Syntax token cache ────────────────────────────────────────────
    private readonly Dictionary<int, (string content, LineState inState, List<SyntaxToken> tokens)> _tokenCache = new();
    private readonly List<int> _pruneKeys = new();
    private bool _tokenCacheDirty;
    private readonly Dictionary<int, string> _lineNumStrings = new();
    private static readonly string[] IndentStrings = Enumerable.Range(0, 9).Select(n => new string(' ', n)).ToArray();

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
    private const int LongLineThreshold = 500_000; // skip expensive processing for lines longer than this
    // Track rendered scroll region for long-line viewport clamping
    private double _renderedScrollX = double.NaN;
    private double _renderedScrollY = double.NaN;
    // Bias subtracted from content-space X coords to keep values small enough for
    // WPF's float32 render pipeline. Zero for normal files.
    private double _textXBias;

    // ── Font property delegation ─────────────────────────────────────
    public string FontFamilyName
    {
        get => _font.FontFamilyName;
        set => _font.FontFamilyName = value;
    }

    public double EditorFontSize
    {
        get => _font.EditorFontSize;
        set => _font.EditorFontSize = value;
    }

    public double LineHeightMultiplier
    {
        get => _font.LineHeightMultiplier;
        set => _font.LineHeightMultiplier = value;
    }

    public string EditorFontWeight
    {
        get => _font.EditorFontWeight;
        set => _font.EditorFontWeight = value;
    }

    public static List<string> GetMonospaceFonts() => FontManager.GetMonospaceFonts();

    // ── Caret blink ──────────────────────────────────────────────────
    private readonly DispatcherTimer _blinkTimer;
    private bool _caretVisible = true;

    // ── IScrollInfo state ────────────────────────────────────────────
    private Vector _offset;
    private Size _viewport;
    private Size _lastArrangeSize;
    private Size _extent;
    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }

    // ── Word wrap state ──────────────────────────────────────────────
    private readonly WrapLayout _wrap = new();
    private bool _skipWrapAnchor;

    // ── Public API (delegates to buffer) ─────────────────────────────
    public bool IsDirty => _buffer.IsDirty;
    public event EventHandler? DirtyChanged;
    public event EventHandler? CaretMoved;

    public int CaretLine => _caretLine;
    public int CaretCol => _caretCol;
    public long CharCount => _buffer.CharCount;

    // ── Mouse drag ───────────────────────────────────────────────────
    private bool _isDragging;

    // ── Gutter width (computed) ──────────────────────────────────────
    private double _gutterWidth;

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        _tokenCacheDirty = true;
        RebuildGutterPen();
        InvalidateText();
    }

    /// <summary>Top visible line captured BEFORE font metrics change, used by OnFontChanged.</summary>
    private int _topLineBeforeFontChange;

    private void OnBeforeFontChanged()
    {
        _topLineBeforeFontChange = _font.LineHeight > 0 ? (int)(_offset.Y / _font.LineHeight) : 0;
    }

    private void OnFontChanged()
    {
        _tokenCacheDirty = true;
        _gutterDigits = 0;
        UpdateExtent();

        // Restore scroll position to keep the same line at the top
        double newOffset = _topLineBeforeFontChange * _font.LineHeight;
        newOffset = Math.Clamp(newOffset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        _offset.Y = Math.Round(newOffset * _font.Dpi) / _font.Dpi;
        ScrollOwner?.InvalidateScrollInfo();

        InvalidateText();
    }

    public EditorControl(ThemeManager themeManager, SyntaxManager syntaxManager)
    {
        ThemeManager = themeManager;
        SyntaxManager = syntaxManager;
        _font.BeforeFontChanged += OnBeforeFontChanged;
        _font.FontChanged += OnFontChanged;
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
            _font.Dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            Keyboard.Focus(this);
            _blinkTimer.Start();
            UpdateExtent();
        };

        Unloaded += (_, _) =>
        {
            _blinkTimer.Stop();
            _font.BeforeFontChanged -= OnBeforeFontChanged;
            _font.FontChanged -= OnFontChanged;
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

    private void InvalidateWrapLayout()
    {
        RecalcWrapData();
        _textVisualDirty = true;
        _gutterVisualDirty = true;
        UpdateExtent();
        InvalidateVisual();
    }

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

        var (caretX, caretY) = GetPixelForPosition(_caretLine, _caretCol);
        if (caretX >= _gutterWidth && caretY + _font.LineHeight > 0 && caretY < ActualHeight)
        {
            if (BlockCaret)
            {
                dc.DrawRectangle(ThemeManager.CaretBrush, null,
                    new Rect(caretX, caretY, _font.CharWidth, _font.LineHeight));
                if (_caretCol < _buffer[_caretLine].Length)
                {
                    _font.DrawGlyphRun(dc, _buffer[_caretLine], _caretCol, 1, caretX, caretY, ThemeManager.EditorBg);
                }
            }
            else
            {
                dc.DrawRectangle(ThemeManager.CaretBrush, null,
                    new Rect(caretX, caretY, BarCaretWidth, _font.LineHeight));
            }
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        _font.Dpi = newDpi.PixelsPerDip;
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

    private EditScope BeginEdit(int startLine, int endLine)
    {
        int count = endLine - startLine + 1;
        return new EditScope(startLine, count, _buffer.Count,
            _buffer.GetLines(startLine, count), _caretLine, _caretCol);
    }

    private void EndEdit(EditScope scope)
    {
        int lineDelta = _buffer.Count - scope.BufferCount;
        int afterCount = scope.LineCount + lineDelta;
        var after = _buffer.GetLines(scope.StartLine, afterCount);
        bool evicted = _undoManager.Push(new UndoManager.UndoEntry(
            scope.StartLine, scope.Before, after,
            scope.CaretLine, scope.CaretCol, _caretLine, _caretCol));
        MarkEditDirty(evicted, scope.StartLine);
        if (lineDelta != 0 && _foldedLines.Count > 0)
            ShiftFolds(scope.StartLine, lineDelta);
    }

    /// <summary>
    /// Shared post-edit bookkeeping: update dirty flags, line states, and clean depth.
    /// Called by both EndEdit (for UndoEntry) and HandleTab (for IndentEntry).
    /// </summary>
    private void MarkEditDirty(bool undoEntryEvicted, int dirtyFromLine)
    {
        if (undoEntryEvicted && _cleanUndoDepth >= 0)
            _cleanUndoDepth--;
        _textVisualDirty = true;
        _bracketMatchDirty = true;
        InvalidateLineStatesFrom(dirtyFromLine);
        _buffer.IsDirty = true;
    }

    /// <summary>
    /// Returns the line range affected by the current selection, or (caretLine, caretLine) if none.
    /// </summary>
    private (int start, int end) GetEditRange()
    {
        if (!_selection.HasSelection) return (_caretLine, _caretLine);
        var (sl, _, el, _) = _selection.GetOrdered(_caretLine, _caretCol);
        return (sl, el);
    }

    private void DeleteSelectionIfPresent()
    {
        if (_selection.HasSelection)
            (_caretLine, _caretCol) = _selection.DeleteSelection(_buffer, _caretLine, _caretCol);
    }

    private void FinishEdit(EditScope scope)
    {
        EndEdit(scope);
        _selection.Clear();
        UpdateExtent();
        EnsureCaretVisible();
        ResetCaret();
    }

    private void Undo()
    {
        var entry = _undoManager.Undo();
        if (entry == null) return;

        switch (entry)
        {
            case UndoManager.UndoEntry ue:
            {
                int delta = ue.Before.Count - ue.After.Count;
                _buffer.ReplaceLines(ue.StartLine, ue.After.Count, ue.Before);
                InvalidateLineStatesFrom(ue.StartLine);
                if (delta != 0 && _foldedLines.Count > 0) ShiftFolds(ue.StartLine, delta);
                break;
            }
            case UndoManager.IndentEntry ie:
                ApplyIndentEntry(ie, reverse: true);
                InvalidateLineStatesFrom(ie.StartLine);
                break;
        }

        FinishUndoRedo(entry.CaretLineBefore, entry.CaretColBefore);
    }

    private void Redo()
    {
        var entry = _undoManager.Redo();
        if (entry == null) return;

        switch (entry)
        {
            case UndoManager.UndoEntry ue:
            {
                int delta = ue.After.Count - ue.Before.Count;
                _buffer.ReplaceLines(ue.StartLine, ue.Before.Count, ue.After);
                InvalidateLineStatesFrom(ue.StartLine);
                if (delta != 0 && _foldedLines.Count > 0) ShiftFolds(ue.StartLine, delta);
                break;
            }
            case UndoManager.IndentEntry ie:
                ApplyIndentEntry(ie, reverse: false);
                InvalidateLineStatesFrom(ie.StartLine);
                break;
        }

        FinishUndoRedo(entry.CaretLineAfter, entry.CaretColAfter);
    }

    private void FinishUndoRedo(int caretLine, int caretCol)
    {
        _caretLine = caretLine;
        _caretCol = caretCol;
        ClampCaret();
        _selection.Clear();
        _bracketMatchDirty = true;
        _tokenCacheDirty = true;
        _buffer.IsDirty = _undoManager.UndoCount != _cleanUndoDepth;
        UpdateExtent();
        InvalidateText();
    }

    private void ApplyIndentEntry(UndoManager.IndentEntry indent, bool reverse)
    {
        bool add = indent.IsIndent != reverse; // indent+undo = remove, unindent+undo = add
        for (int i = 0; i < indent.LineCount; i++)
        {
            int spaces = indent.SpacesPerLine[i];
            if (spaces == 0) continue;
            int lineIdx = indent.StartLine + i;
            if (add)
                _buffer.InsertAt(lineIdx, 0, spaces < IndentStrings.Length ? IndentStrings[spaces] : new string(' ', spaces));
            else
                _buffer.DeleteAt(lineIdx, 0, spaces);
        }
    }

    // ──────────────────────────────────────────────────────────────────
    //  String/comment detection (for suppressing auto-close)
    // ──────────────────────────────────────────────────────────────────
    private bool IsCaretInsideString(string line, int caretCol)
    {
        EnsureLineStates(_caretLine);
        var inState = _caretLine < _lineStates.Count ? _lineStates[_caretLine] : SyntaxManager.DefaultState;
        List<SyntaxToken> tokens;
        if (_tokenCache.TryGetValue(_caretLine, out var cached) && cached.content == line && cached.inState == inState)
            tokens = cached.tokens;
        else
            tokens = SyntaxManager.Tokenize(line, _grammar, inState, out _);
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
        if (!_wordWrap && !HasFoldLayout)
        {
            int line = (int)((pos.Y + _offset.Y) / _font.LineHeight);
            line = Math.Clamp(line, 0, _buffer.Count - 1);
            double textX = pos.X + _offset.X - _gutterWidth - GutterPadding;
            int col = (int)Math.Round(textX / _font.CharWidth);
            col = Math.Clamp(col, 0, _buffer[line].Length);
            return (line, col);
        }

        int visualLine = (int)((pos.Y + _offset.Y) / _font.LineHeight);
        visualLine = Math.Clamp(visualLine, 0, _wrap.TotalVisualLines - 1);
        var (logLine, wrapIndex) = VisualToLogical(visualLine);

        double indentPx = WrapIndentPx(logLine, wrapIndex);
        double tx = pos.X - _gutterWidth - GutterPadding - indentPx;
        int colInWrap = (int)Math.Round(tx / _font.CharWidth);
        colInWrap = Math.Max(0, colInWrap);
        int col2 = WrapColStart(logLine, wrapIndex) + colInWrap;
        col2 = Math.Clamp(col2, 0, _buffer[logLine].Length);
        return (logLine, col2);
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
    //  Scroll / extent management
    // ──────────────────────────────────────────────────────────────────
    private void UpdateExtent()
    {
        // Anchor scroll to the top visible logical line across wrap recalculations
        int anchorLine = -1;
        int anchorWrap = 0;
        double anchorDelta = 0;
        if ((_wordWrap || HasFoldLayout) && !_skipWrapAnchor && _wrap.HasValidData(_buffer.Count) && _wrap.TotalVisualLines > 0)
        {
            int topVisual = Math.Clamp((int)(_offset.Y / _font.LineHeight), 0, _wrap.TotalVisualLines - 1);
            (anchorLine, anchorWrap) = VisualToLogical(topVisual);
            anchorDelta = _offset.Y - (_wrap.CumulOffset(anchorLine) + anchorWrap) * _font.LineHeight;
        }

        RecalcWrapData();

        // Restore scroll position so the same logical line stays at top
        if ((_wordWrap || HasFoldLayout) && anchorLine >= 0 && _wrap.HasValidData(_buffer.Count))
        {
            int newWrap = Math.Min(anchorWrap, VisualLineCount(anchorLine) - 1);
            double newY = (_wrap.CumulOffset(anchorLine) + newWrap) * _font.LineHeight + anchorDelta;
            double maxY = Math.Max(0, _wrap.TotalVisualLines * _font.LineHeight + _viewport.Height / 2 - _viewport.Height);
            _offset.Y = Math.Clamp(newY, 0, maxY);
            _textTransform.Y = -_offset.Y;
            _gutterTransform.Y = -_offset.Y;
        }

        int digits = _buffer.Count > 0
            ? (int)Math.Floor(Math.Log10(_buffer.Count)) + 1
            : 1;
        if (digits != _gutterDigits)
        {
            _gutterDigits = digits;
            _gutterWidth = digits * _font.CharWidth + GutterRightMargin + FoldGutterWidth;
        }

        int maxLen = _buffer.UpdateMaxForLine(_caretLine);

        var newExtent = new Size(
            _wordWrap
                ? _viewport.Width
                : _gutterWidth + GutterPadding + maxLen * _font.CharWidth + HorizontalScrollPadding,
            (_wordWrap || HasFoldLayout ? _wrap.TotalVisualLines : _buffer.Count) * _font.LineHeight + _viewport.Height / 2);

        if (Math.Abs(newExtent.Width - _extent.Width) > 0.5
            || Math.Abs(newExtent.Height - _extent.Height) > 0.5)
        {
            _extent = newExtent;
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    private void RecalcWrapData()
    {
        double textAreaWidth = _viewport.Width - _gutterWidth - GutterPadding;
        _wrap.Recalculate(_wordWrap, _wordWrapAtWords, _wordWrapIndent, _buffer, textAreaWidth, _font.CharWidth, _hiddenLines);
    }

    // ── Wrap coordinate helpers (delegate to WrapLayout) ────────────
    private int LogicalToVisualLine(int logLine, int col = 0) =>
        _wrap.LogicalToVisualLine(_wordWrap, logLine, col);

    private double GetVisualY(int logLine, int col = 0) =>
        _wrap.GetVisualY(_wordWrap, logLine, _font.LineHeight, col);

    private (int logLine, int wrapIndex) VisualToLogical(int visualLine) =>
        _wrap.VisualToLogical(_wordWrap, visualLine, _buffer.Count);

    private int VisualLineCount(int logLine) =>
        _wrap.VisualLineCount(_wordWrap, logLine);

    private int WrapColStart(int logLine, int wrapIndex) =>
        _wrap.WrapColStart(_wordWrap, logLine, wrapIndex);

    private double WrapIndentPx(int logLine, int wrapIndex) =>
        _wrap.WrapIndentPx(_wordWrap, logLine, wrapIndex, _font.CharWidth);

    private (double x, double y) GetPixelForPosition(int line, int col) =>
        _wrap.GetPixelForPosition(_wordWrap, line, col, _gutterWidth, GutterPadding,
            _font.CharWidth, _font.LineHeight, _offset.X, _offset.Y);

    private void EnsureCaretVisible()
    {
        double caretTop = GetVisualY(_caretLine, _caretCol);
        double caretBottom = caretTop + _font.LineHeight;
        if (caretTop < _offset.Y)
            SetVerticalOffset(caretTop);
        else if (caretBottom > _offset.Y + _viewport.Height)
            SetVerticalOffset(caretBottom - _viewport.Height);

        if (!_wordWrap)
        {
            double caretX = _gutterWidth + GutterPadding + _caretCol * _font.CharWidth;
            if (caretX - _offset.X < _gutterWidth + GutterPadding)
                SetHorizontalOffset(caretX - _gutterWidth - GutterPadding);
            else if (caretX - _offset.X > _viewport.Width - _font.CharWidth)
                SetHorizontalOffset(caretX - _viewport.Width + _font.CharWidth * 2);
        }
    }

    private void ClampCaret()
    {
        _caretLine = Math.Clamp(_caretLine, 0, Math.Max(0, _buffer.Count - 1));
        _caretCol = Math.Clamp(_caretCol, 0, _buffer[_caretLine].Length);
    }

    private void ResetPreferredCol() => _preferredCol = -1;

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

    /// <summary>
    /// Returns true if the character at (line, col) falls inside a string or comment token.
    /// Used by bracket matching to skip brackets in non-code contexts.
    /// </summary>
    private bool IsInsideLiteral(int line, int col)
    {
        if (!_tokenCache.TryGetValue(line, out var cached)) return false;
        foreach (var token in cached.tokens)
        {
            if (col < token.Start) break;
            if (col < token.Start + token.Length)
                return token.Scope is "string" or "comment";
        }
        return false;
    }

    /// <summary>Returns the skip delegate for bracket matching, or null when no grammar is active.</summary>
    private Func<int, int, bool>? LiteralSkip => _grammar != null ? IsInsideLiteral : null;

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
                    var outState = _buffer[i].Length > LongLineThreshold
                        ? SyntaxManager.DefaultState
                        : TokenizeLineState(_buffer[i], _lineStates[i]);
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
            var outState = _buffer[lineIdx].Length > LongLineThreshold
                ? SyntaxManager.DefaultState
                : TokenizeLineState(_buffer[lineIdx], inState);
            _lineStates.Add(outState);
        }
    }

    private LineState TokenizeLineState(string line, LineState inState)
    {
        SyntaxManager.Tokenize(line, _grammar, inState, out var outState);
        return outState;
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

    // ── Background line state precomputation ───────────────────────
    private int _precomputeGeneration;
    private const int PrecomputeBatchSize = 5000;

    private void PrecomputeLineStates()
    {
        int gen = ++_precomputeGeneration;

        void ProcessBatch()
        {
            if (gen != _precomputeGeneration) return;
            if (_lineStates.Count > _buffer.Count) return;
            int target = Math.Min(_lineStates.Count + PrecomputeBatchSize - 1, _buffer.Count);
            EnsureLineStates(target);
            if (_lineStates.Count <= _buffer.Count)
                Dispatcher.BeginInvoke(ProcessBatch, DispatcherPriority.ApplicationIdle);
        }

        Dispatcher.BeginInvoke(ProcessBatch, DispatcherPriority.ApplicationIdle);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Rendering (layered: background → text → gutter → caret)
    // ──────────────────────────────────────────────────────────────────
    private (int first, int last) VisibleLineRange()
    {
        if (!_wordWrap && !HasFoldLayout)
        {
            int first = Math.Max(0, (int)(_offset.Y / _font.LineHeight));
            int last = Math.Min(_buffer.Count - 1,
                (int)((_offset.Y + _viewport.Height) / _font.LineHeight));
            return (first, last);
        }

        int firstVisual = Math.Max(0, (int)(_offset.Y / _font.LineHeight));
        int lastVisual = (int)((_offset.Y + _viewport.Height) / _font.LineHeight);
        lastVisual = Math.Min(lastVisual, _wrap.TotalVisualLines - 1);
        if (_wrap.TotalVisualLines == 0) return (0, Math.Max(0, _buffer.Count - 1));

        var (firstLog, _) = VisualToLogical(firstVisual);
        var (lastLog, _) = VisualToLogical(lastVisual);
        return (firstLog, lastLog);
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
            double curLineY = GetVisualY(_caretLine, _caretCol) - _offset.Y;
            dc.DrawRectangle(ThemeManager.CurrentLineBrush, null,
                new Rect(0, curLineY, ActualWidth, _font.LineHeight));
        }

        if (_selection.HasSelection)
        {
            var (sl, sc, el, ec) = _selection.GetOrdered(_caretLine, _caretCol);
            for (int i = Math.Max(firstLine, sl); i <= Math.Min(lastLine, el); i++)
            {
                if (IsLineHidden(i)) continue;
                int selStart = i == sl ? sc : 0;
                int selEnd = i == el ? ec : _buffer[i].Length;

                if (!_wordWrap && !HasFoldLayout)
                {
                    double y = i * _font.LineHeight - _offset.Y;
                    double x1 = _gutterWidth + GutterPadding + selStart * _font.CharWidth - _offset.X;
                    double x2 = _gutterWidth + GutterPadding + selEnd * _font.CharWidth - _offset.X;
                    if (i > sl && i < el) x2 = Math.Max(x2, ActualWidth);
                    if (i == sl && i != el) x2 = Math.Max(x2, ActualWidth);
                    dc.DrawRectangle(ThemeManager.SelectionBrush, null,
                        new Rect(Math.Max(x1, _gutterWidth + GutterPadding), y,
                                 Math.Max(0, x2 - Math.Max(x1, _gutterWidth + GutterPadding)),
                                 _font.LineHeight));
                }
                else
                {
                    RenderWrappedSelection(dc, i, selStart, selEnd, sl, sc, el);
                }
            }
        }

        if (_find.MatchCount > 0)
        {
            int lo = 0, hi = _find.Matches.Count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) / 2;
                if (_find.Matches[mid].Line < firstLine) lo = mid + 1; else hi = mid - 1;
            }
            for (int m = lo; m < _find.Matches.Count; m++)
            {
                var (mLine, mCol, mLen) = _find.Matches[m];
                if (mLine > lastLine) break;
                if (IsLineHidden(mLine)) continue;
                var brush = m == _find.CurrentIndex
                    ? ThemeManager.FindMatchCurrentBrush
                    : ThemeManager.FindMatchBrush;

                if (!_wordWrap && !HasFoldLayout)
                {
                    double pxStart = mCol * _font.CharWidth;
                    double pxEnd = (mCol + mLen) * _font.CharWidth;
                    double mx = _gutterWidth + GutterPadding + pxStart - _offset.X;
                    double my = mLine * _font.LineHeight - _offset.Y;
                    double textLeft = _gutterWidth + GutterPadding;
                    double clippedX = Math.Max(mx, textLeft);
                    double clippedW = Math.Max(0, mx + (pxEnd - pxStart) - clippedX);
                    if (clippedW > 0)
                        dc.DrawRectangle(brush, null,
                            new Rect(clippedX, my, clippedW, _font.LineHeight));
                }
                else
                {
                    int col = mCol;
                    int remaining = mLen;
                    int vCount = VisualLineCount(mLine);
                    while (remaining > 0)
                    {
                        int visLine = LogicalToVisualLine(mLine, col);
                        int wrapIndex = visLine - _wrap.CumulOffset(mLine);
                        int wrapStart = WrapColStart(mLine, wrapIndex);
                        int colInWrap = col - wrapStart;
                        int wrapEnd = wrapIndex + 1 < vCount ? WrapColStart(mLine, wrapIndex + 1) : _buffer[mLine].Length;
                        int charsOnThisLine = Math.Min(remaining, wrapEnd - col);
                        double indentPx = WrapIndentPx(mLine, wrapIndex);
                        double mx = _gutterWidth + GutterPadding + indentPx + colInWrap * _font.CharWidth;
                        double my = visLine * _font.LineHeight - _offset.Y;
                        dc.DrawRectangle(brush, null,
                            new Rect(mx, my, charsOnThisLine * _font.CharWidth, _font.LineHeight));
                        col += charsOnThisLine;
                        remaining -= charsOnThisLine;
                    }
                }
            }
        }

        if (IsKeyboardFocused && !_selection.HasSelection)
        {
            if (_bracketMatchDirty)
            {
                _bracketMatchCache = _caretLine < _buffer.Count && _buffer[_caretLine].Length > LongLineThreshold
                    ? null
                    : BracketMatcher.FindMatch(_buffer, _caretLine, _caretCol, LiteralSkip);
                _bracketMatchDirty = false;
            }
            if (_bracketMatchCache is var (bl, bc, ml, mc))
            {
                double bracketTextLeft = _gutterWidth + GutterPadding;
                if (bl >= firstLine && bl <= lastLine && !IsLineHidden(bl))
                {
                    var (bx, by) = GetPixelForPosition(bl, bc);
                    double cbx = Math.Max(bx, bracketTextLeft);
                    double cbw = Math.Max(0, bx + _font.CharWidth - cbx);
                    if (cbw > 0)
                        dc.DrawRectangle(ThemeManager.MatchingBracketBrush,
                            ThemeManager.MatchingBracketPen,
                            new Rect(cbx, by, cbw, _font.LineHeight));
                }
                if (ml >= firstLine && ml <= lastLine && !IsLineHidden(ml))
                {
                    var (mx, my) = GetPixelForPosition(ml, mc);
                    double cmx = Math.Max(mx, bracketTextLeft);
                    double cmw = Math.Max(0, mx + _font.CharWidth - cmx);
                    if (cmw > 0)
                        dc.DrawRectangle(ThemeManager.MatchingBracketBrush,
                            ThemeManager.MatchingBracketPen,
                            new Rect(cmx, my, cmw, _font.LineHeight));
                }
            }
        }

        // For long lines, the rendered region is clamped to the viewport.
        // Re-render when scroll moves beyond the rendered buffer zone.
        bool longLineScrolled = !double.IsNaN(_renderedScrollX)
            && (Math.Abs(_offset.X - _renderedScrollX) > _viewport.Width * 0.25
                || Math.Abs(_offset.Y - _renderedScrollY) > _viewport.Height * 0.25);

        if (_textVisualDirty
            || firstLine < _renderedFirstLine
            || lastLine > _renderedLastLine
            || longLineScrolled)
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

        // Set transform/clip AFTER RenderTextVisual so _textXBias is up to date
        _textTransform.X = -(_offset.X - _textXBias);
        _textTransform.Y = -_offset.Y;
        _gutterTransform.Y = -_offset.Y;

        _textClipGeom.Rect = new Rect(
            _gutterWidth + _offset.X - _textXBias, _offset.Y,
            Math.Max(0, ActualWidth - _gutterWidth), ActualHeight);
        UpdateCaretVisual();
    }

    private void RenderWrappedSelection(DrawingContext dc, int line, int selStart, int selEnd, int sl, int sc, int el)
    {
        int vCount = VisualLineCount(line);
        int baseCumul = _wrap.CumulOffset(line);
        int visFirst = Math.Max(0, (int)(_offset.Y / _font.LineHeight) - RenderBufferLines - baseCumul);
        int visLast = Math.Min(vCount - 1,
            (int)((_offset.Y + _viewport.Height) / _font.LineHeight) + RenderBufferLines - baseCumul);
        for (int w = Math.Max(0, visFirst); w <= visLast; w++)
        {
            int wStart = WrapColStart(line, w);
            int wEnd = w + 1 < vCount ? WrapColStart(line, w + 1) : _buffer[line].Length;
            int sA = Math.Max(selStart, wStart);
            int sB = Math.Min(selEnd, wEnd);
            if (sA >= sB && !(line != el && w == vCount - 1 && selEnd >= wEnd)) continue;

            double indentPx = WrapIndentPx(line, w);
            double y = (_wrap.CumulOffset(line) + w) * _font.LineHeight - _offset.Y;
            double x1 = _gutterWidth + GutterPadding + indentPx + (sA - wStart) * _font.CharWidth;
            double x2 = sB > sA
                ? _gutterWidth + GutterPadding + indentPx + (sB - wStart) * _font.CharWidth
                : x1;

            bool extendToEdge = (line > sl || sA > selStart || w > 0) && (line < el || sB < selEnd || w < vCount - 1);
            if (line != el && w == vCount - 1) extendToEdge = true;
            if (line == sl && line != el && w >= (LogicalToVisualLine(line, sc) - _wrap.CumulOffset(line))) extendToEdge = true;
            if (extendToEdge && line != el) x2 = Math.Max(x2, ActualWidth);
            if (line == sl && line != el && sB >= wEnd) x2 = Math.Max(x2, ActualWidth);

            dc.DrawRectangle(ThemeManager.SelectionBrush, null,
                new Rect(Math.Max(x1, _gutterWidth + GutterPadding), y,
                         Math.Max(0, x2 - Math.Max(x1, _gutterWidth + GutterPadding)),
                         _font.LineHeight));
        }
    }

    private void RenderTextVisual(int firstLine, int lastLine)
    {
        int drawFirst = Math.Max(0, firstLine - RenderBufferLines);
        int drawLast = Math.Min(_buffer.Count - 1, lastLine + RenderBufferLines);

        using var dc = _textVisual.RenderOpen();

        if (drawLast < drawFirst) return;

        // Compute X bias before rendering. When long lines are visible, bias
        // shifts content-space X origin near the viewport to avoid float32
        // precision loss in WPF's transform pipeline at very large pixel offsets.
        bool hasLongLine = false;
        for (int i = drawFirst; i <= drawLast; i++)
            if (_buffer[i].Length > LongLineThreshold) { hasLongLine = true; break; }
        _textXBias = hasLongLine ? _offset.X : 0;

        if (_indentGuides)
            RenderIndentGuides(dc, drawFirst, drawLast);

        EnsureLineStates(drawLast);
        for (int i = drawFirst; i <= drawLast; i++)
        {
            if (IsLineHidden(i)) continue;
            var line = _buffer[i];
            if (line.Length == 0) continue;
            double x = _gutterWidth + GutterPadding;

            var inState = i < _lineStates.Count ? _lineStates[i] : SyntaxManager.DefaultState;
            if (!_tokenCache.TryGetValue(i, out var cached)
                || cached.content != line || cached.inState != inState)
            {
                // Skip expensive tokenization for extremely long lines — render as plain text
                var tokens = line.Length > LongLineThreshold
                    ? []
                    : SyntaxManager.Tokenize(line, _grammar, inState, out _);
                _tokenCache[i] = (line, inState, tokens);
                cached = _tokenCache[i];
            }

            if (!_wordWrap || VisualLineCount(i) <= 1)
            {
                double y = _wordWrap || HasFoldLayout ? _wrap.CumulOffset(i) * _font.LineHeight : i * _font.LineHeight;
                int segStart = 0;
                int segEnd = line.Length;
                if (line.Length > LongLineThreshold)
                {
                    // Clamp to visible horizontal range to avoid rendering millions of off-screen chars
                    segStart = Math.Max(0, (int)(_offset.X / _font.CharWidth) - 2);
                    segEnd = Math.Min(line.Length,
                        (int)((_offset.X + _viewport.Width) / _font.CharWidth) + 2);
                }
                // Subtract _textXBias from X to keep content-space coords small
                RenderLineTokens(dc, line, x + segStart * _font.CharWidth - _textXBias,
                    y, segStart, segEnd, cached.tokens);
            }
            else
            {
                int vCount = VisualLineCount(i);
                int baseCumul = _wrap.CumulOffset(i);
                // Clamp wrap segment loop to viewport-visible range (critical for very long lines)
                int visFirst = Math.Max(0, (int)(_offset.Y / _font.LineHeight) - RenderBufferLines - baseCumul);
                int visLast = Math.Min(vCount - 1,
                    (int)((_offset.Y + _viewport.Height) / _font.LineHeight) + RenderBufferLines - baseCumul);
                for (int w = Math.Max(0, visFirst); w <= visLast; w++)
                {
                    int segStart = WrapColStart(i, w);
                    int segEnd = w + 1 < vCount ? WrapColStart(i, w + 1) : line.Length;
                    double y = (baseCumul + w) * _font.LineHeight;
                    double wx = x + WrapIndentPx(i, w);
                    RenderLineTokens(dc, line, wx, y, segStart, segEnd, cached.tokens);
                }
            }
        }

        _renderedFirstLine = drawFirst;
        _renderedLastLine = drawLast;
        // Track scroll position when long-line clamping is active
        if (hasLongLine)
        {
            _renderedScrollX = _offset.X;
            _renderedScrollY = _offset.Y;
        }
        else
        {
            _renderedScrollX = double.NaN;
        }

        if (_tokenCache.Count > (drawLast - drawFirst + 1) * 3)
        {
            int margin = drawLast - drawFirst + 1;
            int pruneBelow = drawFirst - margin;
            int pruneAbove = drawLast + margin;
            _pruneKeys.Clear();
            foreach (var key in _tokenCache.Keys)
                if (key < pruneBelow || key > pruneAbove)
                    _pruneKeys.Add(key);
            foreach (var key in _pruneKeys)
                _tokenCache.Remove(key);
        }
    }

    private void RenderLineTokens(DrawingContext dc, string line, double x, double y,
        int segStart, int segEnd, List<SyntaxToken> tokens)
    {
        if (tokens.Count == 0)
        {
            _font.DrawGlyphRun(dc, line, segStart, segEnd - segStart, x, y, ThemeManager.EditorFg);
            return;
        }

        int pos = segStart;
        foreach (var token in tokens)
        {
            int tEnd = token.Start + token.Length;
            if (tEnd <= segStart) continue;
            if (token.Start >= segEnd) break;
            int drawStart = Math.Max(token.Start, segStart);
            int drawEnd = Math.Min(tEnd, segEnd);

            if (drawStart > pos)
                _font.DrawGlyphRun(dc, line, pos, drawStart - pos,
                    x + (pos - segStart) * _font.CharWidth, y, ThemeManager.EditorFg);
            var brush = ThemeManager.GetScopeBrush(token.Scope);
            _font.DrawGlyphRun(dc, line, drawStart, drawEnd - drawStart,
                x + (drawStart - segStart) * _font.CharWidth, y, brush);
            pos = drawEnd;
        }
        if (pos < segEnd)
            _font.DrawGlyphRun(dc, line, pos, segEnd - pos,
                x + (pos - segStart) * _font.CharWidth, y, ThemeManager.EditorFg);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Code folding
    // ──────────────────────────────────────────────────────────────────

    public void ToggleFold(int line)
    {
        if (line < 0 || line >= _buffer.Count || !IsStructuralBlockOpen(line)) return;
        if (!_foldedLines.Remove(line))
            _foldedLines.Add(line);
        RebuildFoldState();
    }

    private void RebuildFoldState()
    {
        // Validate: remove folds for lines that are no longer block openers
        _foldedLines.RemoveWhere(f => f < 0 || f >= _buffer.Count || !IsStructuralBlockOpen(f));

        if (_foldedLines.Count == 0)
        {
            _hiddenLines = null;
        }
        else
        {
            _hiddenLines = new BitArray(_buffer.Count);
            foreach (var opener in _foldedLines)
            {
                int? closer = FindStructuralBlock(opener);
                if (closer == null) { _foldedLines.Remove(opener); continue; }
                for (int i = opener + 1; i < closer.Value; i++)
                    _hiddenLines[i] = true;
            }
        }

        // If caret is inside a folded region, move it to the fold opener
        if (_hiddenLines != null && _caretLine < _hiddenLines.Length && _hiddenLines[_caretLine])
        {
            for (int i = _caretLine - 1; i >= 0; i--)
            {
                if (!_hiddenLines[i]) { _caretLine = i; _caretCol = _buffer[i].Length; break; }
            }
        }

        _tokenCacheDirty = true;
        _bracketMatchDirty = true;
        UpdateExtent();
        InvalidateText();
    }

    public void GoToLine(int line)
    {
        _selection.Clear();
        _caretLine = Math.Clamp(line, 0, _buffer.Count - 1);
        _caretCol = 0;
        ResetPreferredCol();
        // Centre the target line in the viewport
        double caretTop = GetVisualY(_caretLine, _caretCol);
        double target = caretTop - (_viewport.Height - _font.LineHeight) / 2;
        SetVerticalOffset(Math.Max(0, target));
        ResetCaret();
        _textVisualDirty = true;
        InvalidateVisual();
    }

    /// <summary>Fold the block at or enclosing the caret.</summary>
    public void FoldAtCaret()
    {
        // If caret is on a block opener, fold it
        if (IsStructuralBlockOpen(_caretLine) && !_foldedLines.Contains(_caretLine))
        {
            ToggleFold(_caretLine);
            return;
        }
        // Otherwise find the nearest enclosing block opener above
        int? enclosing = BracketMatcher.FindEnclosingOpenBrace(_buffer, _caretLine, 0, LiteralSkip);
        if (enclosing != null && !_foldedLines.Contains(enclosing.Value))
            ToggleFold(enclosing.Value);
    }

    /// <summary>Unfold the block at or enclosing the caret.</summary>
    public void UnfoldAtCaret()
    {
        // If caret is on a folded opener, unfold it
        if (_foldedLines.Contains(_caretLine))
        {
            ToggleFold(_caretLine);
            return;
        }
        // Otherwise find the nearest enclosing folded opener above
        int? enclosing = BracketMatcher.FindEnclosingOpenBrace(_buffer, _caretLine, 0, LiteralSkip);
        if (enclosing != null && _foldedLines.Contains(enclosing.Value))
            ToggleFold(enclosing.Value);
    }

    /// <summary>Shift fold line indices after buffer edits that insert or remove lines.</summary>
    private void ShiftFolds(int editLine, int linesDelta)
    {
        if (linesDelta == 0 || _foldedLines.Count == 0) return;
        var shifted = new HashSet<int>();
        foreach (var f in _foldedLines)
            shifted.Add(f >= editLine ? f + linesDelta : f);
        _foldedLines.Clear();
        foreach (var f in shifted)
            _foldedLines.Add(f);
        RebuildFoldState();
    }

    /// <summary>Find the next visible line at or after the given line.</summary>
    private int NextVisibleLine(int line)
    {
        if (_hiddenLines == null) return line;
        while (line < _buffer.Count && line < _hiddenLines.Length && _hiddenLines[line])
            line++;
        return Math.Min(line, _buffer.Count - 1);
    }

    /// <summary>Find the previous visible line at or before the given line.</summary>
    private int PrevVisibleLine(int line)
    {
        if (_hiddenLines == null) return line;
        while (line > 0 && line < _hiddenLines.Length && _hiddenLines[line])
            line--;
        return Math.Max(line, 0);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Indent guides & block detection
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Measures the number of leading whitespace columns in a line,
    /// accounting for tab stops.
    /// </summary>
    internal static int MeasureIndentColumns(string line, int tabSize)
    {
        int cols = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] == ' ') cols++;
            else if (line[i] == '\t') cols += tabSize - cols % tabSize;
            else break;
        }
        return cols;
    }

    /// <summary>
    /// Returns true if the line opens a structural block — i.e. has an unmatched '{'
    /// whose matching '}' is the first non-whitespace character on its line.
    /// </summary>
    private bool IsStructuralBlockOpen(int line)
        => FindStructuralBlock(line) != null;

    /// <summary>
    /// Finds the closing line for a structural block opened on the given line.
    /// Returns null if the line has no unmatched '{', or if the matching '}'
    /// is not the first non-whitespace character on its line (filters inline
    /// constructs like "eval { ... })").
    /// </summary>
    private int? FindStructuralBlock(int line)
    {
        if (_buffer[line].Length > LongLineThreshold) return null;
        var closer = BracketMatcher.FindBlockCloser(_buffer, line, LiteralSkip);
        if (closer == null) return null;
        // The '}' must be the first non-whitespace character on its line
        // to qualify as a structural block closer.
        string closerText = _buffer[closer.Value.line];
        for (int i = 0; i < closerText.Length; i++)
        {
            if (closerText[i] == ' ' || closerText[i] == '\t') continue;
            if (i != closer.Value.col) return null;
            break;
        }
        return closer.Value.line;
    }

    private void RenderIndentGuides(DrawingContext dc, int drawFirst, int drawLast)
    {
        if (_buffer.Count == 0) return;
        // Skip indent guides when any visible line is extremely long —
        // bracket scanning would be O(line_length) per character.
        for (int i = drawFirst; i <= drawLast; i++)
            if (_buffer[i].Length > LongLineThreshold) return;

        double baseX = _gutterWidth + GutterPadding;
        var pen = ThemeManager.IndentGuidePen;

        // For each line in the draw range that opens a block, find its closer and draw a guide.
        for (int i = drawFirst; i <= drawLast; i++)
        {
            if (!IsStructuralBlockOpen(i)) continue;

            int? closeLine = FindStructuralBlock(i);
            if (closeLine == null) continue;

            int indentCol = MeasureIndentColumns(_buffer[closeLine.Value], TabSize);
            if (indentCol == 0) continue;

            int guideFirst = i + 1;
            int guideLast = closeLine.Value - 1;
            if (guideLast < guideFirst) continue;

            double x = baseX + indentCol * _font.CharWidth;
            double yTop, yBot;
            if (!_wordWrap && !HasFoldLayout)
            {
                yTop = guideFirst * _font.LineHeight;
                yBot = (guideLast + 1) * _font.LineHeight;
            }
            else
            {
                yTop = _wrap.CumulOffset(guideFirst) * _font.LineHeight;
                yBot = (_wrap.CumulOffset(guideLast) + VisualLineCount(guideLast)) * _font.LineHeight;
            }

            if (yTop < yBot)
                dc.DrawLine(pen, new Point(x, yTop), new Point(x, yBot));
        }

        // Find guides that start above the draw range but pass through it.
        {
            int searchLine = drawFirst;
            int searchCol = 0;
            while (true)
            {
                int? openerLine = BracketMatcher.FindEnclosingOpenBrace(_buffer, searchLine, searchCol, LiteralSkip);
                if (openerLine == null) break;
                int i = openerLine.Value;

                int? closeLineN = FindStructuralBlock(i);
                if (closeLineN == null) { searchLine = i; searchCol = 0; continue; }
                int closeLine = closeLineN.Value;

                // Set up next search from above this opener
                searchLine = i;
                searchCol = 0;

                if (closeLine < drawFirst) continue;

                int indentCol = MeasureIndentColumns(_buffer[closeLine], TabSize);
                if (indentCol == 0) continue;

                int guideFirst = i + 1;
                int guideLast = closeLine - 1;
                if (guideLast < guideFirst) continue;

                double x = baseX + indentCol * _font.CharWidth;
                double yTop, yBot;
                if (!_wordWrap && !HasFoldLayout)
                {
                    yTop = guideFirst * _font.LineHeight;
                    yBot = (guideLast + 1) * _font.LineHeight;
                }
                else
                {
                    yTop = _wrap.CumulOffset(guideFirst) * _font.LineHeight;
                    yBot = (_wrap.CumulOffset(guideLast) + VisualLineCount(guideLast)) * _font.LineHeight;
                }

                dc.DrawLine(pen, new Point(x, yTop), new Point(x, yBot));
            }
        }
    }

    private void RenderGutterVisual(int firstLine, int lastLine)
    {
        int drawFirst = Math.Max(0, firstLine - RenderBufferLines);
        int drawLast = Math.Min(_buffer.Count - 1, lastLine + RenderBufferLines);

        using var dc = _gutterVisual.RenderOpen();

        if (drawLast < drawFirst) return;

        double bgTop, bgBottom;
        if (_wordWrap || HasFoldLayout)
        {
            bgTop = _wrap.CumulOffset(drawFirst) * _font.LineHeight;
            int lastVisual = _wrap.CumulOffset(drawLast) + VisualLineCount(drawLast);
            bgBottom = lastVisual * _font.LineHeight;
        }
        else
        {
            bgTop = drawFirst * _font.LineHeight;
            bgBottom = (drawLast + 1) * _font.LineHeight;
        }

        dc.DrawRectangle(ThemeManager.EditorBg, null,
            new Rect(0, bgTop, _gutterWidth, bgBottom - bgTop));
        dc.DrawLine(_gutterSepPen,
            new Point(_gutterWidth, bgTop), new Point(_gutterWidth, bgBottom));

        double foldCenterX = _gutterWidth - FoldGutterWidth / 2 - 2;
        for (int i = drawFirst; i <= drawLast; i++)
        {
            if (IsLineHidden(i)) continue;
            double y = _wordWrap || HasFoldLayout
                ? _wrap.CumulOffset(i) * _font.LineHeight
                : i * _font.LineHeight;
            var brush = i == _caretLine
                ? ThemeManager.ActiveLineNumberFg : ThemeManager.GutterFg;
            int lineNum = i + 1;
            if (!_lineNumStrings.TryGetValue(lineNum, out var numStr))
            {
                numStr = lineNum.ToString();
                _lineNumStrings[lineNum] = numStr;
            }
            double numWidth = numStr.Length * _font.CharWidth;
            _font.DrawGlyphRun(dc, numStr, 0, numStr.Length,
                _gutterWidth - FoldGutterWidth - numWidth - GutterPadding, y, brush);

            // Draw fold markers for block openers
            if (IsStructuralBlockOpen(i))
            {
                bool isFolded = _foldedLines.Contains(i);
                bool isHovered = i == _hoverFoldLine;
                double cy = y + _font.LineHeight / 2;
                double sz = Math.Min(8, _font.LineHeight * 0.45);
                double btnSize = _font.LineHeight * 0.85;
                double btnX = foldCenterX - btnSize / 2;
                double btnY = y + (_font.LineHeight - btnSize) / 2;

                // Hover background (matches scroll bar arrow button style)
                if (isHovered)
                {
                    dc.DrawRoundedRectangle(ThemeManager.FoldHoverBrush, null,
                        new Rect(btnX, btnY, btnSize, btnSize), 3, 3);
                }

                var fg = isHovered ? ThemeManager.EditorFg : ThemeManager.GutterFg;
                if (isFolded)
                {
                    // ▸ right-pointing triangle
                    var tri = new StreamGeometry();
                    using (var ctx = tri.Open())
                    {
                        ctx.BeginFigure(new Point(foldCenterX - sz * 0.4, cy - sz * 0.55), true, true);
                        ctx.LineTo(new Point(foldCenterX + sz * 0.5, cy), true, false);
                        ctx.LineTo(new Point(foldCenterX - sz * 0.4, cy + sz * 0.55), true, false);
                    }
                    tri.Freeze();
                    dc.DrawGeometry(fg, null, tri);
                }
                else
                {
                    // ▾ down-pointing triangle
                    var tri = new StreamGeometry();
                    using (var ctx = tri.Open())
                    {
                        ctx.BeginFigure(new Point(foldCenterX - sz * 0.55, cy - sz * 0.35), true, true);
                        ctx.LineTo(new Point(foldCenterX + sz * 0.55, cy - sz * 0.35), true, false);
                        ctx.LineTo(new Point(foldCenterX, cy + sz * 0.45), true, false);
                    }
                    tri.Freeze();
                    dc.DrawGeometry(fg, null, tri);
                }
            }
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

        var pos = e.GetPosition(this);

        // Check for click on fold gutter marker
        if (pos.X >= _gutterWidth - FoldGutterWidth && pos.X < _gutterWidth)
        {
            var (foldLine, _) = HitTest(pos);
            if (IsStructuralBlockOpen(foldLine))
            {
                ToggleFold(foldLine);
                e.Handled = true;
                return;
            }
        }

        CaptureMouse();
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
        var pos = e.GetPosition(this);

        // Update fold gutter hover state and cursor
        if (!_isDragging)
        {
            int newHover = -1;
            if (pos.X >= _gutterWidth - FoldGutterWidth && pos.X < _gutterWidth)
            {
                var (hl, _) = HitTest(pos);
                if (hl >= 0 && hl < _buffer.Count && IsStructuralBlockOpen(hl))
                    newHover = hl;
            }
            if (newHover != _hoverFoldLine)
            {
                _hoverFoldLine = newHover;
                Cursor = _hoverFoldLine >= 0 ? Cursors.Hand : Cursors.IBeam;
                _gutterVisualDirty = true;
                InvalidateVisual();
            }
            else if (pos.X < _gutterWidth)
            {
                Cursor = _hoverFoldLine >= 0 ? Cursors.Hand : Cursors.Arrow;
            }
            else
            {
                Cursor = Cursors.IBeam;
            }
        }

        if (!_isDragging) return;
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

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hoverFoldLine >= 0)
        {
            _hoverFoldLine = -1;
            Cursor = Cursors.IBeam;
            _gutterVisualDirty = true;
            InvalidateVisual();
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        Focus();
        var menu = ContextMenuHelper.Create();

        bool hasSel = _selection.HasSelection;
        bool hasClip;
        try { hasClip = Clipboard.ContainsText(); }
        catch (System.Runtime.InteropServices.ExternalException) { hasClip = false; }

        if (hasSel)
        {
            var cut = ContextMenuHelper.Item("Cut", "\uE8C6", HandleCut);
            cut.InputGestureText = "Ctrl+X";
            menu.Items.Add(cut);

            var copy = ContextMenuHelper.Item("Copy", "\uE8C8", HandleCopy);
            copy.InputGestureText = "Ctrl+C";
            menu.Items.Add(copy);
        }

        if (hasClip)
        {
            var paste = ContextMenuHelper.Item("Paste", "\uE77F", HandlePaste);
            paste.InputGestureText = "Ctrl+V";
            menu.Items.Add(paste);
        }

        if (menu.Items.Count > 0)
            menu.Items.Add(new System.Windows.Controls.Separator());

        var selectAll = ContextMenuHelper.Item("Select All", "\uE8B3", HandleSelectAll);
        selectAll.InputGestureText = "Ctrl+A";
        menu.Items.Add(selectAll);

        ContextMenu = menu;
        menu.IsOpen = true;
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
        if (e.Text.Length == 1 && (BracketMatcher.ClosingBrackets.Contains(ch) || BracketMatcher.AutoCloseQuotes.Contains(ch)) && !_selection.HasSelection)
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
        var (sl, el) = GetEditRange();
        var scope = BeginEdit(sl, el);
        DeleteSelectionIfPresent();

        var currentLine = _buffer[_caretLine];
        bool insideString = IsCaretInsideString(currentLine, _caretCol);

        if (!insideString && e.Text.Length == 1 && BracketMatcher.Pairs.TryGetValue(ch, out char closer))
        {
            _buffer.InsertAt(_caretLine, _caretCol, $"{ch}{closer}");
            _caretCol++;
        }
        else if (!insideString && e.Text.Length == 1 && BracketMatcher.AutoCloseQuotes.Contains(ch))
        {
            _buffer.InsertAt(_caretLine, _caretCol, $"{ch}{ch}");
            _caretCol++;
        }
        else
        {
            _buffer.InsertAt(_caretLine, _caretCol, e.Text);
            _caretCol += e.Text.Length;
        }

        FinishEdit(scope);
        e.Handled = true;
    }

    // ──────────────────────────────────────────────────────────────────
    //  Keyboard — dispatch to handler methods
    // ──────────────────────────────────────────────────────────────────
    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        // Alt+key arrives as Key.System — check SystemKey for the real key
        if (e.Key == Key.System && alt && !ctrl)
        {
            switch (e.SystemKey)
            {
                case Key.Up:
                    HandleMoveLine(true);
                    e.Handled = true;
                    return;
                case Key.Down:
                    HandleMoveLine(false);
                    e.Handled = true;
                    return;
            }
        }

        switch (e.Key)
        {
            case Key.Return when ctrl && shift:
                HandleInsertBlankLine(above: true);
                e.Handled = true;
                break;

            case Key.Return when ctrl:
                HandleInsertBlankLine(above: false);
                e.Handled = true;
                break;

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

            case Key.D when ctrl:
                HandleDuplicateLine();
                e.Handled = true;
                break;

            case Key.K when ctrl && shift:
                HandleDeleteLine();
                e.Handled = true;
                break;

            case Key.L when ctrl:
                HandleSelectLine();
                e.Handled = true;
                break;

        }
    }

    // ── Key handlers ─────────────────────────────────────────────────

    private void HandleReturn()
    {
        ResetPreferredCol();
        var (sl, el) = GetEditRange();
        var scope = BeginEdit(sl, el);
        DeleteSelectionIfPresent();

        var currentLine = _buffer[_caretLine];
        var indent = currentLine[..(currentLine.Length - currentLine.TrimStart().Length)];
        var rest = _buffer.TruncateAt(_caretLine, _caretCol);

        bool betweenBrackets = _caretCol > 0 && rest.Length > 0
            && BracketMatcher.Pairs.TryGetValue(_buffer[_caretLine][^1], out char expectedCloser)
            && rest[0] == expectedCloser;

        bool afterOpen = _caretCol > 0
            && BracketMatcher.Pairs.ContainsKey(_buffer[_caretLine][_caretCol - 1]);

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

        _tokenCacheDirty = true;
        FinishEdit(scope);
    }

    private void HandleBackspace()
    {
        ResetPreferredCol();
        var (sl, el) = GetEditRange();
        if (!_selection.HasSelection && _caretCol == 0 && _caretLine > 0)
            sl = _caretLine - 1;
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
                bool isPair = (BracketMatcher.Pairs.TryGetValue(before, out char expected) && after == expected)
                              || (BracketMatcher.AutoCloseQuotes.Contains(before) && after == before);
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
                if (_caretCol <= leadingSpaces && line.AsSpan(0, _caretCol).IndexOfAnyExcept(' ') < 0)
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
        FinishEdit(scope);
    }

    private void HandleDelete()
    {
        ResetPreferredCol();
        var (sl, el) = GetEditRange();
        if (!_selection.HasSelection && _caretCol >= _buffer[_caretLine].Length && _caretLine < _buffer.Count - 1)
            el = _caretLine + 1;
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
        FinishEdit(scope);
    }

    private void HandleTab(bool shift)
    {
        ResetPreferredCol();
        var (sl, el) = GetEditRange();

        if (_selection.HasSelection && sl != el)
        {
            int caretLineBefore = _caretLine, caretColBefore = _caretCol;
            int lineCount = el - sl + 1;
            var spacesPerLine = new int[lineCount];

            for (int i = sl; i <= el; i++)
            {
                if (shift)
                {
                    int remove = 0;
                    while (remove < TabSize && remove < _buffer[i].Length && _buffer[i][remove] == ' ')
                        remove++;
                    spacesPerLine[i - sl] = remove;
                    if (remove > 0)
                    {
                        _buffer.DeleteAt(i, 0, remove);
                        if (i == _caretLine) _caretCol = Math.Max(0, _caretCol - remove);
                        if (i == _selection.AnchorLine) _selection.AnchorCol = Math.Max(0, _selection.AnchorCol - remove);
                    }
                }
                else
                {
                    spacesPerLine[i - sl] = TabSize;
                    _buffer.InsertAt(i, 0, new string(' ', TabSize));
                    if (i == _caretLine) _caretCol += TabSize;
                    if (i == _selection.AnchorLine) _selection.AnchorCol += TabSize;
                }
            }

            bool evicted = _undoManager.Push(new UndoManager.IndentEntry(
                sl, lineCount, spacesPerLine, !shift,
                caretLineBefore, caretColBefore, _caretLine, _caretCol));
            MarkEditDirty(evicted, sl);

            _selection.HasSelection = true;
            UpdateExtent();
            EnsureCaretVisible();
            ResetCaret();
            return;
        }

        if (shift) return;

        var scope = BeginEdit(sl, el);
        DeleteSelectionIfPresent();
        int spacesToInsert = TabSize - (_caretCol % TabSize);
        _buffer.InsertAt(_caretLine, _caretCol, new string(' ', spacesToInsert));
        _caretCol += spacesToInsert;
        FinishEdit(scope);
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
                    _caretLine = PrevVisibleLine(_caretLine - 1);
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
                    _caretLine = NextVisibleLine(_caretLine + 1);
                    _caretCol = 0;
                }
                if (!shift) _selection.Clear();
                break;

            case Key.Up:
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (_caretLine > 0)
                {
                    if (_preferredCol < 0) _preferredCol = _caretCol;
                    _caretLine = PrevVisibleLine(_caretLine - 1);
                    _caretCol = Math.Min(_preferredCol, _buffer[_caretLine].Length);
                }
                if (!shift) _selection.Clear();
                break;

            case Key.Down:
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (_caretLine < _buffer.Count - 1)
                {
                    if (_preferredCol < 0) _preferredCol = _caretCol;
                    _caretLine = NextVisibleLine(_caretLine + 1);
                    _caretCol = Math.Min(_preferredCol, _buffer[_caretLine].Length);
                }
                if (!shift) _selection.Clear();
                break;

            case Key.Home:
                ResetPreferredCol();
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (ctrl)
                {
                    _caretLine = 0;
                    _caretCol = 0;
                }
                else
                {
                    var text = _buffer[_caretLine];
                    int indent = text.Length - text.TrimStart().Length;
                    _caretCol = _caretCol == indent ? 0 : indent;
                }
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
                int visibleLines = Math.Max(1, (int)(_viewport.Height / _font.LineHeight) - 1);
                if (shift) _selection.Start(_caretLine, _caretCol);
                if (key == Key.PageUp)
                    _caretLine = PrevVisibleLine(Math.Max(0, _caretLine - visibleLines));
                else
                    _caretLine = NextVisibleLine(Math.Min(_buffer.Count - 1, _caretLine + visibleLines));
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
        if (!_selection.HasSelection) return;
        ResetPreferredCol();
        var (sl, _, el, _) = _selection.GetOrdered(_caretLine, _caretCol);
        var text = _selection.GetSelectedText(_buffer, _caretLine, _caretCol);
        try
        {
            Clipboard.SetText(text);
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return; // Don't delete if clipboard write failed
        }
        var scope = BeginEdit(sl, el);
        (_caretLine, _caretCol) = _selection.DeleteSelection(_buffer, _caretLine, _caretCol);
        FinishEdit(scope);
    }

    private void HandlePaste()
    {
        try { if (!Clipboard.ContainsText()) return; }
        catch (System.Runtime.InteropServices.ExternalException) { return; }

        // Read clipboard BEFORE modifying the buffer so a clipboard failure
        // doesn't leave the selection deleted with no undo entry.
        string text;
        try { text = Clipboard.GetText(); }
        catch (System.Runtime.InteropServices.ExternalException) { return; }

        ResetPreferredCol();
        var (sl, el) = GetEditRange();
        var scope = BeginEdit(sl, el);
        DeleteSelectionIfPresent();

        // Fast path: no line breaks — skip Split which scans the entire string
        if (!text.AsSpan().ContainsAny('\r', '\n'))
        {
            var expanded = TextBuffer.ExpandTabs(text, TabSize);
            _buffer.InsertAt(_caretLine, _caretCol, expanded);
            _caretCol += expanded.Length;
        }
        else
        {
            var pasteLines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
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
                _buffer.InsertAt(_caretLine, _caretCol, pasteLines[0]);
                for (int i = 1; i < pasteLines.Length; i++)
                {
                    _caretLine++;
                    _buffer.InsertLine(_caretLine, pasteLines[i]);
                }
                _caretCol = _buffer[_caretLine].Length;
                _buffer.InsertAt(_caretLine, _caretCol, after);
                _tokenCacheDirty = true;
            }
        }
        FinishEdit(scope);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Line manipulation shortcuts
    // ──────────────────────────────────────────────────────────────────

    private void HandleDuplicateLine()
    {
        ResetPreferredCol();
        if (_selection.HasSelection)
        {
            var (sl, _, el, _) = _selection.GetOrdered(_caretLine, _caretCol);
            int count = el - sl + 1;
            var scope = BeginEdit(sl, el + count);
            var lines = _buffer.GetLines(sl, count);
            for (int i = 0; i < count; i++)
                _buffer.InsertLine(el + 1 + i, lines[i]);
            _caretLine += count;
            _selection.AnchorLine += count;
            _tokenCacheDirty = true;
            FinishEdit(scope);
        }
        else
        {
            var scope = BeginEdit(_caretLine, _caretLine + 1);
            _buffer.InsertLine(_caretLine + 1, _buffer[_caretLine]);
            _caretLine++;
            _tokenCacheDirty = true;
            FinishEdit(scope);
        }
    }

    private void HandleDeleteLine()
    {
        ResetPreferredCol();
        if (_buffer.Count == 1)
        {
            var scope = BeginEdit(0, 0);
            _buffer.NotifyLineChanging(0);
            _buffer[0] = "";
            _caretLine = 0;
            _caretCol = 0;
            FinishEdit(scope);
            return;
        }

        int sl, el;
        if (_selection.HasSelection)
        {
            (sl, _, el, _) = _selection.GetOrdered(_caretLine, _caretCol);
        }
        else
        {
            sl = _caretLine;
            el = _caretLine;
        }

        int count = el - sl + 1;
        bool deletingAll = count >= _buffer.Count;
        var editScope = BeginEdit(sl, Math.Min(el + 1, _buffer.Count - 1));
        if (deletingAll)
        {
            _buffer.RemoveRange(1, _buffer.Count - 1);
            _buffer.NotifyLineChanging(0);
            _buffer[0] = "";
            _caretLine = 0;
            _caretCol = 0;
        }
        else
        {
            _buffer.RemoveRange(sl, count);
            _caretLine = Math.Min(sl, _buffer.Count - 1);
            _caretCol = Math.Min(_caretCol, _buffer[_caretLine].Length);
        }
        _selection.Clear();
        _tokenCacheDirty = true;
        FinishEdit(editScope);
    }

    private void HandleMoveLine(bool up)
    {
        ResetPreferredCol();
        int sl, el;
        if (_selection.HasSelection)
        {
            (sl, _, el, _) = _selection.GetOrdered(_caretLine, _caretCol);
        }
        else
        {
            sl = _caretLine;
            el = _caretLine;
        }

        if (up && sl == 0) return;

        if (!up && el >= _buffer.Count - 1)
        {
            // At the bottom edge, append an empty line then use the normal swap logic
            var scope2 = BeginEdit(sl, el);
            _buffer.InsertLine(el + 1, "");
            // Swap: move the new empty line from el+1 to sl
            _buffer.RemoveRange(el + 1, 1);
            _buffer.InsertLine(sl, "");
            _caretLine++;
            if (_selection.HasSelection)
                _selection.AnchorLine++;
            _tokenCacheDirty = true;
            FinishEdit(scope2);
            return;
        }

        var scope = BeginEdit(up ? sl - 1 : sl, up ? el : el + 1);
        if (up)
        {
            var line = _buffer[sl - 1];
            _buffer.RemoveRange(sl - 1, 1);
            _buffer.InsertLine(el, line);
            _caretLine--;
            if (_selection.HasSelection)
                _selection.AnchorLine--;
        }
        else
        {
            var line = _buffer[el + 1];
            _buffer.RemoveRange(el + 1, 1);
            _buffer.InsertLine(sl, line);
            _caretLine++;
            if (_selection.HasSelection)
                _selection.AnchorLine++;
        }
        _tokenCacheDirty = true;
        FinishEdit(scope);
    }

    private void HandleInsertBlankLine(bool above)
    {
        ResetPreferredCol();
        var scope = BeginEdit(_caretLine, _caretLine);
        int insertAt = above ? _caretLine : _caretLine + 1;
        _buffer.InsertLine(insertAt, "");
        _caretLine = insertAt;
        _caretCol = 0;
        _selection.Clear();
        _tokenCacheDirty = true;
        FinishEdit(scope);
    }

    private void HandleSelectLine()
    {
        _selection.AnchorLine = _caretLine;
        _selection.AnchorCol = 0;
        _selection.HasSelection = true;
        if (_caretLine < _buffer.Count - 1)
        {
            _caretLine++;
            _caretCol = 0;
        }
        else
        {
            _caretCol = _buffer[_caretLine].Length;
        }
        ResetPreferredCol();
        EnsureCaretVisible();
        ResetCaret();
        _textVisualDirty = true;
        InvalidateVisual();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Mouse wheel
    // ──────────────────────────────────────────────────────────────────
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        SetVerticalOffset(_offset.Y - e.Delta / MouseWheelDeltaUnit * _font.LineHeight * ScrollWheelLines);
        e.Handled = true;
    }

    // ──────────────────────────────────────────────────────────────────
    //  IScrollInfo
    // ──────────────────────────────────────────────────────────────────
    private const double MinThumbPixels = 30;
    private double ThumbPadding(double extent, double viewport)
    {
        if (extent <= viewport || viewport <= MinThumbPixels) return 0;
        double targetRatio = MinThumbPixels / viewport;
        if (viewport / extent >= targetRatio) return 0;
        return (targetRatio * extent - viewport) / (1 - targetRatio);
    }
    public double ExtentWidth => _extent.Width + ThumbPadding(_extent.Width, _viewport.Width);
    public double ExtentHeight => _extent.Height + ThumbPadding(_extent.Height, _viewport.Height);
    public double ViewportWidth => _viewport.Width + ThumbPadding(_extent.Width, _viewport.Width);
    public double ViewportHeight => _viewport.Height + ThumbPadding(_extent.Height, _viewport.Height);
    public double HorizontalOffset => _offset.X;
    public double VerticalOffset => _offset.Y;

    public void SetHorizontalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Width - _viewport.Width));
        offset = Math.Round(offset * _font.Dpi) / _font.Dpi;
        if (Math.Abs(offset - _offset.X) < 0.01) return;
        _offset.X = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        offset = Math.Round(offset * _font.Dpi) / _font.Dpi;
        if (Math.Abs(offset - _offset.Y) < 0.01) return;
        _offset.Y = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;

    public void LineUp() => SetVerticalOffset(_offset.Y - _font.LineHeight);
    public void LineDown() => SetVerticalOffset(_offset.Y + _font.LineHeight);
    public void LineLeft() => SetHorizontalOffset(_offset.X - _font.CharWidth * TabSize);
    public void LineRight() => SetHorizontalOffset(_offset.X + _font.CharWidth * TabSize);
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
        bool viewportChanged = Math.Abs(finalSize.Width - _lastArrangeSize.Width) > 0.01
                            || Math.Abs(finalSize.Height - _lastArrangeSize.Height) > 0.01;
        _lastArrangeSize = finalSize;
        _viewport = finalSize;
        if (viewportChanged)
        {
            _textVisualDirty = true;
            _gutterVisualDirty = true;
            SetHorizontalOffset(_offset.X);
            SetVerticalOffset(_offset.Y);
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
        ResetAfterContentLoad();
    }

    /// <summary>
    /// Apply pre-parsed content (from <see cref="TextBuffer.PrepareContent"/>).
    /// Use this after offloading the heavy parsing to a background thread.
    /// </summary>
    public void SetPreparedContent(TextBuffer.PreparedContent prepared)
    {
        _buffer.SetPreparedContent(prepared);
        ResetAfterContentLoad();
    }

    private void ResetAfterContentLoad()
    {
        _caretLine = 0;
        _caretCol = 0;
        _selection.Clear();
        _undoManager.Clear();
        _cleanUndoDepth = 0;
        _foldedLines.Clear();
        _hiddenLines = null;
        _find.Clear();
        _tokenCacheDirty = true;
        _lineNumStrings.Clear();
        InvalidateLineStates();
        UpdateExtent();
        SetVerticalOffset(0);
        SetHorizontalOffset(0);
        InvalidateText();
        PrecomputeLineStates();
    }

    public void SetCaretPosition(int line, int col)
    {
        if (_buffer.Count == 0) return;
        _caretLine = Math.Clamp(line, 0, _buffer.Count - 1);
        _caretCol = Math.Clamp(col, 0, _buffer[_caretLine].Length);
        _selection.Clear();
    }

    /// <summary>
    /// Replaces the buffer content while preserving scroll position and caret.
    /// Used when reloading a file that changed on disk.
    /// </summary>
    public void ReloadContent(string text)
    {
        var savedVOffset = _offset.Y;
        var savedHOffset = _offset.X;
        var savedLine = _caretLine;
        var savedCol = _caretCol;

        _buffer.SetContent(text, TabSize);
        _selection.Clear();
        _undoManager.Clear();
        _cleanUndoDepth = 0;
        _find.Clear();
        _tokenCacheDirty = true;
        InvalidateLineStates();
        UpdateExtent();

        // Clamp caret to new buffer bounds
        _caretLine = Math.Min(savedLine, _buffer.Count - 1);
        _caretCol = Math.Min(savedCol, _buffer[_caretLine].Length);

        SetVerticalOffset(savedVOffset);
        SetHorizontalOffset(savedHOffset);
        InvalidateText();
        PrecomputeLineStates();
    }

    /// <summary>
    /// Appends new text to the end of the buffer without resetting scroll, caret, or undo.
    /// Used for incremental reload of append-only files (e.g. log files).
    /// </summary>
    public void AppendContent(string text)
    {
        int dirtyFrom = _buffer.AppendContent(text, TabSize);

        // Invalidate syntax and token cache only from the appended region
        InvalidateLineStatesFrom(dirtyFrom);
        for (int i = dirtyFrom; i < _buffer.Count; i++)
            _tokenCache.Remove(i);

        UpdateExtent();
        InvalidateText();
        PrecomputeLineStates();
    }

    public string GetContent() => _buffer.GetContent();

    /// <summary>
    /// Release undo history, buffer, and caches to free memory when closing a tab.
    /// Returns true if the released data was large enough to warrant a GC.
    /// </summary>
    public bool ReleaseResources()
    {
        bool large = _buffer.Count > 10_000;
        _undoManager.Clear();
        _buffer.Clear();
        _foldedLines.Clear();
        _hiddenLines = null;
        // TrimExcess releases the backing arrays that Clear() leaves allocated
        _tokenCache.Clear();
        _tokenCache.TrimExcess();
        _lineStates.Clear();
        _lineStates.TrimExcess();
        _find.Clear(trimExcess: true);
        _pruneKeys.Clear();
        _pruneKeys.TrimExcess();
        _lineNumStrings.Clear();
        _lineNumStrings.TrimExcess();
        return large;
    }

    public void InvalidateSyntax()
    {
        InvalidateLineStates();
        _tokenCacheDirty = true;
        InvalidateText();
        PrecomputeLineStates();
    }

    public void MarkClean()
    {
        _cleanUndoDepth = _undoManager.UndoCount;
        _buffer.IsDirty = false;
    }

    public void MarkDirty()
    {
        _cleanUndoDepth = -1;
        _buffer.IsDirty = true;
        DirtyChanged?.Invoke(this, EventArgs.Empty);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Find support (delegates to FindManager)
    // ──────────────────────────────────────────────────────────────────

    public int FindMatchCount => _find.MatchCount;
    public int CurrentMatchIndex => _find.CurrentIndex;

    public string GetSelectedText()
    {
        return _selection.GetSelectedText(_buffer, _caretLine, _caretCol);
    }

    public (int startLine, int startCol, int endLine, int endCol)? GetSelectionBounds()
    {
        if (!_selection.HasSelection) return null;
        var (sl, sc, el, ec) = _selection.GetOrdered(_caretLine, _caretCol);
        return (sl, sc, el, ec);
    }

    public void SetFindMatches(string query, bool matchCase, bool useRegex = false, bool wholeWord = false,
        (int, int, int, int)? selectionBounds = null, bool preserveSelection = false)
    {
        _find.Search(_buffer, query, matchCase, _caretLine, _caretCol, useRegex, wholeWord, selectionBounds);
        if (_find.MatchCount > 0)
            NavigateToCurrentMatch(preserveSelection);
        InvalidateVisual();
    }

    public void ClearFindMatches()
    {
        _find.Clear();
        InvalidateVisual();
    }

    public void FindNext()
    {
        if (_find.MatchCount == 0) return;
        _find.MoveNext();
        NavigateToCurrentMatch();
        InvalidateVisual();
    }

    public void FindPrevious()
    {
        if (_find.MatchCount == 0) return;
        _find.MovePrevious();
        NavigateToCurrentMatch();
        InvalidateVisual();
    }

    public void ReplaceCurrent(string replacement)
    {
        var match = _find.GetCurrentMatch();
        if (match == null) return;
        var (line, col, len) = match.Value;
        var scope = BeginEdit(line, line);
        _buffer.ReplaceAt(line, col, len, replacement);
        EndEdit(scope);
        _find.Search(_buffer, _find.LastQuery, _find.LastMatchCase, _caretLine, _caretCol, _find.LastUseRegex, _find.LastWholeWord);
        _buffer.InvalidateMaxLineLength();
        _gutterVisualDirty = true;
        InvalidateVisual();
    }

    public void ReplaceAll(string query, string replacement, bool matchCase, bool useRegex = false, bool wholeWord = false,
        (int, int, int, int)? selectionBounds = null)
    {
        var range = _find.GetMatchLineRange();
        if (range == null) return;
        var (firstLine, lastLine) = range.Value;
        var scope = BeginEdit(firstLine, lastLine);
        var matches = _find.Matches;
        for (int i = matches.Count - 1; i >= 0; i--)
        {
            var (line, col, len) = matches[i];
            _buffer.ReplaceAt(line, col, len, replacement);
        }
        EndEdit(scope);
        ClampCaret();
        _buffer.InvalidateMaxLineLength();
        _tokenCacheDirty = true;
        InvalidateLineStates();
        _gutterVisualDirty = true;
        InvalidateVisual();
    }

    private void NavigateToCurrentMatch(bool preserveSelection = false)
    {
        var match = _find.GetCurrentMatch();
        if (match == null) return;
        var (line, col, _) = match.Value;

        if (!preserveSelection)
        {
            _caretLine = line;
            _caretCol = col;
            _selection.Clear();
        }

        CentreLineInViewport(line);
        ResetCaret();
    }

    private void CentreLineInViewport(int line)
    {
        double targetY = GetVisualY(line) - (_viewport.Height - _font.LineHeight) / 2;
        SetVerticalOffset(targetY);

        if (_wordWrap)
            return;

        double caretX = _gutterWidth + GutterPadding + _caretCol * _font.CharWidth;
        double textAreaWidth = _viewport.Width - _gutterWidth - GutterPadding;
        double targetX = caretX - _gutterWidth - GutterPadding - textAreaWidth / 2;
        SetHorizontalOffset(targetX);
    }
}
