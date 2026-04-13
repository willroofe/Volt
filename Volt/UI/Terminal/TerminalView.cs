using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Volt;

public sealed class TerminalView : FrameworkElement, IScrollInfo
{
    private readonly DrawingVisual _textVisual = new();
    private readonly DrawingVisual _cursorVisual = new();
    private readonly FontManager _font = new();
    private TerminalGrid? _grid;
    private double _verticalOffset;
    /// <summary>When true, new output keeps the viewport pinned to the bottom (live view).</summary>
    private bool _stickToBottom = true;
    private Size _viewport;
    private bool _blockCaret;
    private int _caretBlinkMs = 500;
    private bool _caretVisible = true;
    private readonly DispatcherTimer _blinkTimer;
    private static readonly FontWeightConverter _fontWeightConverter = new();
    private const double BarCaretWidth = 1;
    private const double OutputPaddingLeft = 6; // gap from left edge to first glyph column

    /// <summary>Last font/caret inputs applied to <see cref="_font"/> — avoids redundant Apply + PTY resize when settings are saved but editor appearance did not change.</summary>
    private bool _fontCaretSnapshotValid;
    private string _snapFontFamily = "";
    private double _snapFontSize;
    private FontWeight _snapFontWeight;
    private double _snapLineHeightMul;
    private bool _snapBlockCaret;
    private int _snapBlinkMs;
    private double _snapDpi;
    /// <summary>
    /// <see cref="FontManager"/> raises <see cref="FontManager.FontChanged"/> from both <c>Apply</c> and
    /// <c>LineHeightMultiplier</c> in one <see cref="ApplyFontAndCaret"/> call. Deferring
    /// <see cref="TryResizeGridToViewport"/> until both finish avoids two grid/PTY resizes (duplicate prompts).
    /// </summary>
    private bool _batchFontCaretApply;
    /// <summary>Coalesces <see cref="ArrangeOverride"/>, font metrics, and DPI into one grid/PTY resize so layout flutter does not fire multiple SIGWINCH redraws (duplicate screen content).</summary>
    private DispatcherTimer? _viewportResizeTimer;
    private double _lastCommittedCellWidth;
    private double _lastCommittedCellHeight;
    private bool _haveCommittedCellSize;

    // Mouse selection (logical document line = 0 oldest scrollback; col 0..Cols-1)
    private int _selAnchorLine;
    private int _selAnchorCol;
    private int _selExtentLine;
    private int _selExtentCol;
    private bool _selectGestureActive;
    private bool _pointerMovedWhileSelecting;
    private bool _hasSelection;
    private int? _findHighlightLine;
    private int _findHighlightCol;
    private int _findHighlightLength;
    private static readonly Brush FallbackSelectionBrush = CreateFallbackSelectionBrush();

    private static Brush CreateFallbackSelectionBrush()
    {
        var b = new SolidColorBrush(Color.FromArgb(0x60, 0x33, 0x99, 0xFF));
        b.Freeze();
        return b;
    }

    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? _textVisual : _cursorVisual;

    public TerminalView()
    {
        ClipToBounds = true;
        AddVisualChild(_textVisual);
        AddVisualChild(_cursorVisual);
        Focusable = true;
        FocusVisualStyle = null;
        CanVerticallyScroll = true;

        _blinkTimer = new DispatcherTimer();
        _blinkTimer.Tick += (_, _) =>
        {
            _caretVisible = !_caretVisible;
            InvalidateVisual();
        };

        _font.FontChanged += OnFontMetricsChanged;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;

        if (Application.Current is App app)
            app.ThemeManager.ThemeChanged += OnThemeChanged;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => SyncFromActiveEditor();

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _blinkTimer.Stop();
        _viewportResizeTimer?.Stop();
        _font.FontChanged -= OnFontMetricsChanged;
        if (Application.Current is App app)
            app.ThemeManager.ThemeChanged -= OnThemeChanged;
    }

    private void OnFontMetricsChanged()
    {
        InvalidateVisual();
        // Line height affects IScrollInfo.ExtentHeight even when row/col count is unchanged — must refresh
        // ScrollViewer or the scrollbar stays wrong and scrolling breaks after font changes.
        ScrollOwner?.InvalidateScrollInfo();
        if (!_batchFontCaretApply)
            ScheduleTryResizeGridToViewport();
    }

    /// <summary>Match the active editor (live palette / zoom); fall back to <see cref="AppSettings.Editor"/> if none.</summary>
    public void SyncFromActiveEditor()
    {
        if (Application.Current?.MainWindow is MainWindow mw && mw.Editor is { } ed)
            SyncFromEditor(ed);
        else
            SyncFromAppSettings();
    }

    /// <summary>Apply font and caret from a specific editor instance (same metrics as that control).</summary>
    public void SyncFromEditor(EditorControl editor)
    {
        ApplyFontAndCaret(
            editor.FontFamilyName,
            editor.EditorFontSize,
            editor.EditorFontWeight,
            editor.LineHeightMultiplier,
            editor.BlockCaret,
            editor.CaretBlinkMs);
    }

    /// <summary>Apply <see cref="AppSettings.Editor"/> font, line height, and caret style.</summary>
    public void SyncFromAppSettings()
    {
        if (Application.Current is not App app) return;
        var ed = app.Settings.Editor;
        string family = ed.Font.Family ?? FontManager.DefaultFontFamily();
        ApplyFontAndCaret(family, ed.Font.Size, ed.Font.Weight, ed.Font.LineHeight, ed.Caret.BlockCaret, ed.Caret.BlinkMs);
    }

    private void ApplyFontAndCaret(string fontFamily, double fontSize, string fontWeight, double lineHeight, bool blockCaret, int blinkMs)
    {
        FontWeight fw = FontWeights.Normal;
        try
        {
            fw = (FontWeight)_fontWeightConverter.ConvertFromString(fontWeight)!;
        }
        catch
        {
            fw = FontWeights.Normal;
        }

        double dpi = VisualTreeHelper.GetDpi(new DrawingVisual()).PixelsPerDip;
        try
        {
            dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        }
        catch
        {
            // keep DrawingVisual fallback
        }

        if (_fontCaretSnapshotValid
            && string.Equals(_snapFontFamily, fontFamily, StringComparison.Ordinal)
            && Math.Abs(_snapFontSize - fontSize) < 0.001
            && _snapFontWeight == fw
            && Math.Abs(_snapLineHeightMul - lineHeight) < 0.001
            && _snapBlockCaret == blockCaret
            && _snapBlinkMs == blinkMs
            && Math.Abs(_snapDpi - dpi) < 0.0001)
        {
            return;
        }

        _batchFontCaretApply = true;
        try
        {
            _font.Apply(fontFamily, fontSize, fw, dpi);
            _font.LineHeightMultiplier = lineHeight;
            _blockCaret = blockCaret;
            ApplyCaretBlinkInterval(blinkMs);
            InvalidateVisual();
            ScrollOwner?.InvalidateScrollInfo();

            _fontCaretSnapshotValid = true;
            _snapFontFamily = fontFamily;
            _snapFontSize = fontSize;
            _snapFontWeight = fw;
            _snapLineHeightMul = lineHeight;
            _snapBlockCaret = blockCaret;
            _snapBlinkMs = blinkMs;
            _snapDpi = dpi;
        }
        finally
        {
            _batchFontCaretApply = false;
            ScheduleTryResizeGridToViewport();
        }
    }

    private void ApplyCaretBlinkInterval(int ms)
    {
        _caretBlinkMs = ms;
        _blinkTimer.Stop();
        if (ms > 0)
        {
            _blinkTimer.Interval = TimeSpan.FromMilliseconds(ms);
            if (IsKeyboardFocused)
                _blinkTimer.Start();
        }
        else
        {
            _caretVisible = true;
            InvalidateVisual();
        }
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        _font.Dpi = newDpi.PixelsPerDip;
        _fontCaretSnapshotValid = false;
        InvalidateVisual();
        ScrollOwner?.InvalidateScrollInfo();
        ScheduleTryResizeGridToViewport();
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _caretVisible = true;
        if (_caretBlinkMs > 0)
            _blinkTimer.Start();
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _blinkTimer.Stop();
        _caretVisible = false;
        InvalidateVisual();
    }

    // Re-apply caret blink state when Keyboard.Focus landed but GotKeyboardFocus did not run as expected.
    internal void ResyncCaretAfterFocusAttempt()
    {
        if (!IsKeyboardFocused) return;
        _caretVisible = true;
        if (_caretBlinkMs > 0)
            _blinkTimer.Start();
        InvalidateVisual();
    }

    public TerminalGrid? Grid
    {
        get => _grid;
        set
        {
            if (_grid != null) _grid.Changed -= OnGridChanged;
            _grid = value;
            if (_grid != null) _grid.Changed += OnGridChanged;
            _haveCommittedCellSize = false;
            InvalidateVisual();
        }
    }

    private void OnGridChanged()
    {
        // Coalesce via WPF layout — InvalidateVisual queues a redraw at the next frame
        Dispatcher.BeginInvoke(new Action(() =>
        {
            InvalidateVisual();
            ScrollOwner?.InvalidateScrollInfo();
        }), System.Windows.Threading.DispatcherPriority.Render);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    private void ScheduleTryResizeGridToViewport()
    {
        _viewportResizeTimer ??= new DispatcherTimer(
            TimeSpan.FromMilliseconds(25),
            DispatcherPriority.Normal,
            OnViewportResizeTick,
            Dispatcher);
        _viewportResizeTimer.Stop();
        _viewportResizeTimer.Start();
    }

    private void OnViewportResizeTick(object? sender, EventArgs e)
    {
        _viewportResizeTimer?.Stop();
        TryResizeGridToViewport();
    }

    private void TryResizeGridToViewport()
    {
        if (_grid == null) return;
        double cellWidth = _font.CharWidth;
        double cellHeight = _font.LineHeight;
        if (cellWidth <= 0 || cellHeight <= 0) return;
        double vw = ViewportWidthPx;
        double vh = ViewportHeightPx;
        if (vw <= 0 || vh <= 0) return;
        double drawableW = Math.Max(cellWidth, vw - OutputPaddingLeft);
        int cols = Math.Max(1, (int)(drawableW / cellWidth));
        int rows = Math.Max(1, (int)(vh / cellHeight));
        bool cellMetricsChanged = _haveCommittedCellSize
            && (Math.Abs(cellWidth - _lastCommittedCellWidth) > 0.001
                || Math.Abs(cellHeight - _lastCommittedCellHeight) > 0.001);
        if (cols != _grid.Cols || rows != _grid.Rows)
        {
            _grid.Resize(rows, cols);
            if (cellMetricsChanged)
                _grid.ClearMainScreenHome();
            SizeRequested?.Invoke(rows, cols);
        }

        _lastCommittedCellWidth = cellWidth;
        _lastCommittedCellHeight = cellHeight;
        _haveCommittedCellSize = true;

        // Always notify ScrollViewer: ExtentHeight uses LineHeight × line count; font/viewport updates
        // often leave row/col count the same but still change extent (or scroll data was never refreshed).
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var defaultBgColor = AnsiPalette.DefaultBg();
        var bg = new SolidColorBrush(defaultBgColor);
        bg.Freeze();
        dc.DrawRectangle(bg, null, new Rect(RenderSize));

        if (_grid == null) return;

        double cellWidth = _font.CharWidth;
        double cellHeight = _font.LineHeight;
        if (cellHeight <= 0 || cellWidth <= 0) return;

        int totalLines = _grid.ScrollbackCount + _grid.Rows;
        double totalHeight = totalLines * cellHeight;
        double viewportH = ViewportHeightPx;
        double viewportW = ViewportWidthPx;
        double maxOffset = Math.Max(0, totalHeight - viewportH);
        double beforeStick = _verticalOffset;
        if (_stickToBottom)
            _verticalOffset = maxOffset;
        else
            _verticalOffset = Math.Clamp(_verticalOffset, 0, maxOffset);
        if (Math.Abs(beforeStick - _verticalOffset) > 0.01)
            ScrollOwner?.InvalidateScrollInfo();

        var defaultFgColor = AnsiPalette.DefaultFg();
        var defaultFg = new SolidColorBrush(defaultFgColor);
        defaultFg.Freeze();

        int firstLine = (int)Math.Floor(_verticalOffset / cellHeight);
        int lastLine = (int)Math.Ceiling((_verticalOffset + viewportH) / cellHeight);
        lastLine = Math.Min(lastLine, totalLines) - 1;

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, viewportW, viewportH)));
        dc.PushTransform(new TranslateTransform(0, -_verticalOffset));
        for (int logicalLine = firstLine; logicalLine <= lastLine; logicalLine++)
        {
            if (logicalLine < 0 || logicalLine >= totalLines) continue;
            double y = logicalLine * cellHeight;
            int gridRow = LogicalLineToGridRow(_grid, logicalLine);
            RenderGridRow(dc, logicalLine, gridRow, y, cellWidth, cellHeight, defaultFg);
        }
        dc.Pop();
        dc.Pop();

        DrawCursor(dc, cellWidth, cellHeight);
    }

    /// <summary>Maps a vertical slice through scrollback + main (0 = oldest scrollback line).</summary>
    private static int LogicalLineToGridRow(TerminalGrid grid, int logicalLine)
    {
        int sb = grid.ScrollbackCount;
        if (logicalLine < sb)
            return -(sb - logicalLine);
        return logicalLine - sb;
    }

    public void SetFindHighlight(int logicalLine, int col, int length)
    {
        _findHighlightLine = logicalLine;
        _findHighlightCol = Math.Max(0, col);
        _findHighlightLength = Math.Max(1, length);
        InvalidateVisual();
    }

    public void ClearFindHighlight()
    {
        _findHighlightLine = null;
        _findHighlightCol = 0;
        _findHighlightLength = 0;
        InvalidateVisual();
    }

    public void ScrollLogicalLineIntoView(int logicalLine)
    {
        if (_grid == null) return;
        int maxLine = _grid.ScrollbackCount + _grid.Rows - 1;
        if (maxLine < 0) return;
        logicalLine = Math.Clamp(logicalLine, 0, maxLine);
        double cellHeight = _font.LineHeight;
        if (cellHeight <= 0) return;
        double targetTop = logicalLine * cellHeight;
        double anchor = Math.Max(0, (ViewportHeightPx - cellHeight) * 0.5);
        SetVerticalOffset(targetTop - anchor);
    }

    private static void NormalizeSelectionEndpoints(int al, int ac, int bl, int bc, out int sl, out int sc, out int el, out int ec)
    {
        if (al < bl || (al == bl && ac <= bc))
        {
            sl = al;
            sc = ac;
            el = bl;
            ec = bc;
        }
        else
        {
            sl = bl;
            sc = bc;
            el = al;
            ec = ac;
        }
    }

    private bool TryGetDisplayedSelectionNormalized(out int sl, out int sc, out int el, out int ec)
    {
        sl = sc = el = ec = 0;
        if (_grid == null) return false;
        NormalizeSelectionEndpoints(_selAnchorLine, _selAnchorCol, _selExtentLine, _selExtentCol, out sl, out sc, out el, out ec);
        int maxLine = _grid.ScrollbackCount + _grid.Rows - 1;
        if (maxLine < 0) return false;
        sl = Math.Clamp(sl, 0, maxLine);
        el = Math.Clamp(el, 0, maxLine);
        if (sl > el)
            (sl, sc, el, ec) = (el, ec, sl, sc);
        int maxCol = _grid.Cols - 1;
        sc = Math.Clamp(sc, 0, maxCol);
        ec = Math.Clamp(ec, 0, maxCol);
        bool dragging = _selectGestureActive;
        bool previewOk = dragging && (_pointerMovedWhileSelecting || sl != el || sc != ec);
        bool committedOk = !dragging && _hasSelection;
        return previewOk || committedOk;
    }

    private void ClearSelection()
    {
        if (Mouse.Captured == this)
            ReleaseMouseCapture();
        _hasSelection = false;
        _selectGestureActive = false;
        _pointerMovedWhileSelecting = false;
        _selAnchorLine = _selExtentLine = 0;
        _selAnchorCol = _selExtentCol = 0;
        InvalidateVisual();
    }

    private void FinalizeSelectionGesture()
    {
        _selectGestureActive = false;
        NormalizeSelectionEndpoints(_selAnchorLine, _selAnchorCol, _selExtentLine, _selExtentCol, out int sl, out int sc, out int el, out int ec);
        _hasSelection = _pointerMovedWhileSelecting || sl != el || sc != ec;
        _pointerMovedWhileSelecting = false;
        if (!_hasSelection)
        {
            _selAnchorLine = _selExtentLine = 0;
            _selAnchorCol = _selExtentCol = 0;
        }
        InvalidateVisual();
    }

    private (int line, int col) HitTestLogicalCell(Point viewportPoint)
    {
        if (_grid == null) return (0, 0);
        double cellH = _font.LineHeight;
        double cellW = _font.CharWidth;
        if (cellH <= 0 || cellW <= 0) return (0, 0);
        double yDoc = viewportPoint.Y + _verticalOffset;
        int maxLine = _grid.ScrollbackCount + _grid.Rows - 1;
        int line = (int)Math.Floor(yDoc / cellH);
        line = Math.Clamp(line, 0, Math.Max(0, maxLine));
        double relX = viewportPoint.X - OutputPaddingLeft;
        int col = (int)Math.Floor(relX / cellW);
        col = Math.Clamp(col, 0, _grid.Cols - 1);
        return (line, col);
    }

    private void DrawSelectionForRun(DrawingContext dc, int logicalLine, double y, double cellWidth, double cellHeight, int runStart, int runLen)
    {
        if (!TryGetDisplayedSelectionNormalized(out int sl, out int sc, out int el, out int ec))
            return;
        if (logicalLine < sl || logicalLine > el)
            return;
        int lineColStart = logicalLine == sl ? sc : 0;
        int lineColEnd = logicalLine == el ? ec : _grid!.Cols - 1;
        int runEnd = runStart + runLen;
        int segStart = Math.Max(runStart, lineColStart);
        int segEnd = Math.Min(runEnd - 1, lineColEnd);
        if (segStart > segEnd)
            return;
        int segLen = segEnd - segStart + 1;
        Brush hi = (Application.Current as App)?.ThemeManager?.SelectionBrush ?? FallbackSelectionBrush;
        dc.DrawRectangle(hi, null,
            new Rect(OutputPaddingLeft + segStart * cellWidth, y, segLen * cellWidth, cellHeight));
    }

    private void DrawFindHighlightForRun(DrawingContext dc, int logicalLine, double y, double cellWidth, double cellHeight, int runStart, int runLen)
    {
        if (_grid == null || _findHighlightLine != logicalLine || _findHighlightLength <= 0)
            return;
        int maxCol = _grid.Cols - 1;
        int hiStart = Math.Clamp(_findHighlightCol, 0, maxCol);
        int hiEnd = Math.Clamp(_findHighlightCol + _findHighlightLength - 1, 0, maxCol);
        if (hiStart > hiEnd)
            return;
        int runEnd = runStart + runLen - 1;
        int segStart = Math.Max(runStart, hiStart);
        int segEnd = Math.Min(runEnd, hiEnd);
        if (segStart > segEnd)
            return;
        var brush = (Application.Current as App)?.ThemeManager?.FindMatchCurrentBrush;
        if (brush == null)
            return;
        dc.DrawRectangle(brush, null,
            new Rect(OutputPaddingLeft + segStart * cellWidth, y, (segEnd - segStart + 1) * cellWidth, cellHeight));
    }

    private void RenderGridRow(DrawingContext dc, int logicalLine, int gridRow, double y, double cellWidth, double cellHeight, Brush defaultFg)
    {
        int col = 0;
        while (col < _grid!.Cols)
        {
            // Snapshot the attributes of the first cell in this run
            ref readonly var first = ref _grid.CellAt(gridRow, col);
            int runStart = col;
            int fg = first.FgIndex;
            int bg = first.BgIndex;
            var attr = first.Attr;
            col++;

            // Extend the run while adjacent cells share (fg, bg, attr)
            while (col < _grid.Cols)
            {
                ref readonly var c = ref _grid.CellAt(gridRow, col);
                if (c.FgIndex != fg || c.BgIndex != bg || c.Attr != attr) break;
                col++;
            }

            int runLen = col - runStart;

            // Draw background run if non-default
            if (bg != -1)
            {
                var bgColor = bg < -1 ? AnsiPalette.ResolveTrueColor(_grid.GetTrueColor(bg)) : AnsiPalette.ResolveDefault(bg);
                var bgBrush = new SolidColorBrush(bgColor);
                bgBrush.Freeze();
                dc.DrawRectangle(bgBrush, null,
                    new Rect(OutputPaddingLeft + runStart * cellWidth, y, runLen * cellWidth, cellHeight));
            }

            DrawFindHighlightForRun(dc, logicalLine, y, cellWidth, cellHeight, runStart, runLen);
            DrawSelectionForRun(dc, logicalLine, y, cellWidth, cellHeight, runStart, runLen);

            // Resolve foreground brush
            Brush fgBrush;
            if (fg == -1)
            {
                fgBrush = defaultFg;
            }
            else
            {
                var fgColor = fg < -1 ? AnsiPalette.ResolveTrueColor(_grid.GetTrueColor(fg)) : AnsiPalette.ResolveDefault(fg);
                var br = new SolidColorBrush(fgColor);
                br.Freeze();
                fgBrush = br;
            }

            // Build the character run string
            var chars = new char[runLen];
            for (int i = 0; i < runLen; i++)
            {
                char g = _grid.CellAt(gridRow, runStart + i).Glyph;
                chars[i] = g == '\0' ? ' ' : g;
            }
            var str = new string(chars);

            _font.DrawGlyphRun(dc, str, 0, runLen, OutputPaddingLeft + runStart * cellWidth, y, fgBrush);
        }
    }

    private void DrawCursor(DrawingContext dc, double cellWidth, double cellHeight)
    {
        if (_grid == null || !_grid.CursorVisible) return;
        if (!IsKeyboardFocused) return;
        if (_caretBlinkMs > 0 && !_caretVisible) return;

        var (r, c) = _grid.Cursor;
        if (r < 0 || r >= _grid.Rows || c < 0 || c >= _grid.Cols) return;
        int cursorLogicalLine = _grid.ScrollbackCount + r;
        double cursorY = cursorLogicalLine * cellHeight - _verticalOffset;
        if (cursorY + cellHeight < 0 || cursorY > ViewportHeightPx) return;

        double caretX = OutputPaddingLeft + c * cellWidth;
        var theme = (Application.Current as App)?.ThemeManager;
        Brush caretBrush = theme?.CaretBrush ?? new SolidColorBrush(AnsiPalette.DefaultFg());

        if (_blockCaret)
        {
            dc.DrawRectangle(caretBrush, null, new Rect(caretX, cursorY, cellWidth, cellHeight));
            char g = _grid.CellAt(r, c).Glyph;
            char ch = g == '\0' ? ' ' : g;
            Brush hole = theme?.EditorBg ?? new SolidColorBrush(AnsiPalette.DefaultBg());
            _font.DrawGlyphRun(dc, new string(ch, 1), 0, 1, caretX, cursorY, hole);
        }
        else
        {
            dc.DrawRectangle(caretBrush, null, new Rect(caretX, cursorY, BarCaretWidth, cellHeight));
        }
    }

    public event Action<byte[]>? InputBytes; // raised when bytes should go to pty
    public event Action<int, int>? SizeRequested; // (rows, cols) fired after grid.Resize

    private readonly HashSet<(Key key, ModifierKeys mods)> _allowlist = new();

    /// <summary>
    /// Register a Volt-global shortcut that should bubble past the terminal
    /// instead of being forwarded as VT input bytes to the shell.
    /// Call from MainWindow during startup.
    /// </summary>
    public void AddAllowlistedShortcut(Key key, ModifierKeys mods) => _allowlist.Add((key, mods));

    /// <summary>After the user types or pastes, snap scroll to the live area so the shell cursor is visible.</summary>
    private void FollowCursorAfterUserInput()
    {
        _stickToBottom = true;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    private double ViewportWidthPx => ActualWidth > 0 ? ActualWidth : (_viewport.Width > 0 ? _viewport.Width : 0);

    private double ViewportHeightPx => ActualHeight > 0 ? ActualHeight : (_viewport.Height > 0 ? _viewport.Height : 0);

    protected override Size MeasureOverride(Size availableSize)
    {
        // Only treat finite constraints as the viewport — infinity is common before the
        // parent has a size; do not use a fake height here or TryResizeGridToViewport could
        // briefly match the wrong row count before Arrange runs.
        if (!double.IsInfinity(availableSize.Width) && availableSize.Width > 0
            && !double.IsInfinity(availableSize.Height) && availableSize.Height > 0)
        {
            _viewport = new Size(availableSize.Width, availableSize.Height);
        }

        ScrollOwner?.InvalidateScrollInfo();
        double w = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height;
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewport = finalSize;
        ScrollOwner?.InvalidateScrollInfo();
        // Grid + PTY size must follow the arranged viewport (not a debounced RenderSize tick),
        // otherwise we can resize to a stale height between measure and arrange and confuse
        // the shell (duplicate reflow / spurious scroll extent).
        ScheduleTryResizeGridToViewport();
        return finalSize;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var mods = Keyboard.Modifiers;

        if (e.Key == Key.Escape)
        {
            if (_hasSelection || _selectGestureActive)
            {
                ClearSelection();
                e.Handled = true;
                return;
            }
            // No local selection — let Escape reach the VT encoder for the shell
        }

        // Reserved Volt shortcuts — bubble up unhandled so MainWindow handles them
        if (_allowlist.Contains((e.Key, mods))) return;

        // Terminal-managed copy/paste
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.C)
        {
            DoCopy();
            e.Handled = true;
            return;
        }
        if (mods == (ModifierKeys.Control | ModifierKeys.Shift) && e.Key == Key.V)
        {
            DoPaste();
            e.Handled = true;
            return;
        }

        // Ctrl+C copies when text is highlighted; otherwise send SIGINT
        if (mods == ModifierKeys.Control && e.Key == Key.C)
        {
            if (TryGetDisplayedSelectionNormalized(out _, out _, out _, out _))
            {
                DoCopy();
                ClearSelection();
                e.Handled = true;
                return;
            }
            FollowCursorAfterUserInput();
            InputBytes?.Invoke(new byte[] { 0x03 });
            e.Handled = true;
            return;
        }

        // Encode via KeyEncoder
        var bytes = KeyEncoder.Encode(e.Key, mods);
        if (bytes != null)
        {
            FollowCursorAfterUserInput();
            InputBytes?.Invoke(bytes);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (e.Handled || string.IsNullOrEmpty(e.Text)) return;
        // Skip control characters that OnKeyDown already handled
        if (e.Text.Length == 1 && e.Text[0] < 0x20 && e.Text[0] != '\r' && e.Text[0] != '\t') return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(e.Text);
        FollowCursorAfterUserInput();
        InputBytes?.Invoke(bytes);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        Keyboard.Focus(this);
        var hit = HitTestLogicalCell(e.GetPosition(this));
        _hasSelection = false;
        _selAnchorLine = _selExtentLine = hit.line;
        _selAnchorCol = _selExtentCol = hit.col;
        _selectGestureActive = true;
        _pointerMovedWhileSelecting = false;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_selectGestureActive || e.LeftButton != MouseButtonState.Pressed)
            return;
        _pointerMovedWhileSelecting = true;
        var hit = HitTestLogicalCell(e.GetPosition(this));
        _selExtentLine = hit.line;
        _selExtentCol = hit.col;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_selectGestureActive)
            return;
        if (Mouse.Captured == this)
            ReleaseMouseCapture();
        FinalizeSelectionGesture();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        if (_grid == null || !TryGetDisplayedSelectionNormalized(out _, out _, out _, out _))
            return;
        Focus();
        Keyboard.Focus(this);
        DoCopy();
        ClearSelection();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (!_selectGestureActive)
            return;
        FinalizeSelectionGesture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_grid == null) return;
        double cellHeight = _font.LineHeight;
        if (cellHeight <= 0) return;
        double delta = e.Delta > 0 ? -3 : 3;
        double maxOffset = Math.Max(0, ExtentHeight - ViewportHeight);
        SetVerticalOffset(Math.Clamp(_verticalOffset + delta * cellHeight, 0, maxOffset));
        e.Handled = true;
    }

    private void DoCopy()
    {
        if (_grid == null || !TryGetDisplayedSelectionNormalized(out int sl, out int sc, out int el, out int ec))
            return;
        var sb = new StringBuilder();
        for (int line = sl; line <= el; line++)
        {
            if (line > sl)
                sb.Append(Environment.NewLine);
            int gridRow = LogicalLineToGridRow(_grid, line);
            int c0 = line == sl ? sc : 0;
            int c1 = line == el ? ec : _grid.Cols - 1;
            var lineSb = new StringBuilder(c1 - c0 + 1);
            for (int c = c0; c <= c1; c++)
            {
                char g = _grid.CellAt(gridRow, c).Glyph;
                lineSb.Append(g == '\0' ? ' ' : g);
            }
            sb.Append(lineSb.ToString().TrimEnd());
        }
        try
        {
            Clipboard.SetText(sb.ToString());
        }
        catch
        {
            // Clipboard can throw if another app holds it open
        }
    }

    private void DoPaste()
    {
        if (!Clipboard.ContainsText()) return;
        FollowCursorAfterUserInput();
        var text = Clipboard.GetText();
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        // Chunk at 4 KB to avoid blocking the UI thread on absurdly large pastes
        const int chunkSize = 4096;
        for (int i = 0; i < bytes.Length; i += chunkSize)
        {
            int len = Math.Min(chunkSize, bytes.Length - i);
            var slice = new byte[len];
            System.Buffer.BlockCopy(bytes, i, slice, 0, len);
            InputBytes?.Invoke(slice);
        }
    }

    // --- IScrollInfo (ScrollViewer + themed scrollbar) ---
    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => ViewportWidthPx;
    public double ExtentHeight => _grid == null ? 0 : (_grid.Rows + _grid.ScrollbackCount) * _font.LineHeight;
    public double ViewportWidth => ViewportWidthPx;
    public double ViewportHeight => ViewportHeightPx;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _verticalOffset;
    public ScrollViewer? ScrollOwner { get; set; }
    public void LineUp() => SetVerticalOffset(_verticalOffset - _font.LineHeight);
    public void LineDown() => SetVerticalOffset(_verticalOffset + _font.LineHeight);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageUp() => SetVerticalOffset(_verticalOffset - ViewportHeightPx);
    public void PageDown() => SetVerticalOffset(_verticalOffset + ViewportHeightPx);
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelUp() => LineUp();
    public void MouseWheelDown() => LineDown();
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        if (_grid == null)
        {
            _verticalOffset = offset;
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateVisual();
            return;
        }
        double cellHeight = _font.LineHeight;
        if (cellHeight <= 0)
        {
            _verticalOffset = offset;
            ScrollOwner?.InvalidateScrollInfo();
            InvalidateVisual();
            return;
        }
        int totalLines = _grid.ScrollbackCount + _grid.Rows;
        double totalHeight = totalLines * cellHeight;
        double maxOffset = Math.Max(0, totalHeight - ViewportHeightPx);
        _verticalOffset = Math.Clamp(offset, 0, maxOffset);
        _stickToBottom = _verticalOffset >= maxOffset - 0.5;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }
    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
