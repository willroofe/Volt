using System;
using System.Collections.Generic;
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
    /// <see cref="ScheduleResizeGridToViewport"/> until both finish avoids two grid/PTY resizes (duplicate prompts).
    /// </summary>
    private bool _batchFontCaretApply;
    /// <summary>Coalesces <see cref="ArrangeOverride"/>, font metrics, and DPI into one grid/PTY resize so layout flutter does not fire multiple SIGWINCH redraws (duplicate screen content).</summary>
    private DispatcherTimer? _viewportResizeTimer;
    private double _lastCommittedCellWidth;
    private double _lastCommittedCellHeight;
    private bool _haveCommittedCellSize;
    private int _lastRequestedRows = -1;
    private int _lastRequestedCols = -1;

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
            ScheduleResizeGridToViewport();
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
            ScheduleResizeGridToViewport();
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
        ScheduleResizeGridToViewport();
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
            _lastRequestedRows = -1;
            _lastRequestedCols = -1;
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

    private void ScheduleResizeGridToViewport()
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
        ResizeGridToViewport(notifyPty: true);
    }

    private void ResizeGridToViewport(bool notifyPty)
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
        }

        _lastCommittedCellWidth = cellWidth;
        _lastCommittedCellHeight = cellHeight;
        _haveCommittedCellSize = true;

        // Always notify ScrollViewer: ExtentHeight uses LineHeight × line count; font/viewport updates
        // often leave row/col count the same but still change extent (or scroll data was never refreshed).
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();

        if (notifyPty && (rows != _lastRequestedRows || cols != _lastRequestedCols))
        {
            _lastRequestedRows = rows;
            _lastRequestedCols = cols;
            SizeRequested?.Invoke(rows, cols);
        }
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
            RenderGridRow(dc, gridRow, y, cellWidth, cellHeight, defaultFg);
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

    private void RenderGridRow(DrawingContext dc, int gridRow, double y, double cellWidth, double cellHeight, Brush defaultFg)
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
    /// Register Volt-global shortcuts that should bubble past the terminal
    /// instead of being forwarded as VT input bytes to the shell.
    /// </summary>
    internal void SetAllowlistedShortcuts(IEnumerable<KeyCombo> shortcuts)
    {
        _allowlist.Clear();
        foreach (var shortcut in shortcuts)
        {
            if (!shortcut.IsNone)
                _allowlist.Add((shortcut.Key, shortcut.Modifiers));
        }
    }

    private static Key ShortcutKey(KeyEventArgs e) => e.Key == Key.System ? e.SystemKey : e.Key;

    /// <summary>After the user types or pastes, snap scroll to the live area so the shell cursor is visible.</summary>
    private void FollowCursorAfterUserInput()
    {
        _stickToBottom = true;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    private double ViewportWidthPx => _viewport.Width > 0 ? _viewport.Width : (ActualWidth > 0 ? ActualWidth : 0);

    private double ViewportHeightPx => _viewport.Height > 0 ? _viewport.Height : (ActualHeight > 0 ? ActualHeight : 0);

    protected override Size MeasureOverride(Size availableSize)
    {
        // Only treat finite constraints as the viewport — infinity is common before the
        // parent has a size; do not use a fake height here or ResizeGridToViewport could
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
        ResizeGridToViewport(notifyPty: false);
        // Keep the local buffer in step with live splitter drags; the ConPTY resize stays
        // debounced so the shell is not hammered with transient dimensions.
        ScheduleResizeGridToViewport();
        return finalSize;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var key = ShortcutKey(e);
        var mods = Keyboard.Modifiers;

        // Reserved Volt shortcuts — bubble up unhandled so MainWindow handles them
        if (_allowlist.Contains((key, mods))) return;

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

        // Ctrl+C with no selection → SIGINT (v1: always SIGINT since selection isn't implemented yet)
        if (mods == ModifierKeys.Control && e.Key == Key.C)
        {
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
        // Selection support deferred to post-v1; this stub does nothing for now.
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
