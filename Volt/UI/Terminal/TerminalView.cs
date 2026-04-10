using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

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

        // Subscribe to theme changes so rendering updates colors
        if (Application.Current is App app)
        {
            app.ThemeManager.ThemeChanged += OnThemeChanged;
        }
    }

    public TerminalGrid? Grid
    {
        get => _grid;
        set
        {
            if (_grid != null) _grid.Changed -= OnGridChanged;
            _grid = value;
            if (_grid != null) _grid.Changed += OnGridChanged;
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

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _resizeDebounce ??= new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(50),
            System.Windows.Threading.DispatcherPriority.Background,
            OnResizeTick,
            Dispatcher);
        _resizeDebounce.Stop();
        _resizeDebounce.Start();
    }

    private void OnResizeTick(object? sender, EventArgs e)
    {
        _resizeDebounce?.Stop();
        if (_grid == null) return;
        double cellWidth = _font.CharWidth;
        double cellHeight = _font.LineHeight;
        if (cellWidth <= 0 || cellHeight <= 0) return;
        double vw = ViewportWidthPx;
        double vh = ViewportHeightPx;
        if (vw <= 0 || vh <= 0) return;
        int cols = Math.Max(1, (int)(vw / cellWidth));
        int rows = Math.Max(1, (int)(vh / cellHeight));
        if (cols != _grid.Cols || rows != _grid.Rows)
        {
            _grid.Resize(rows, cols);
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
                dc.DrawRectangle(bgBrush, null, new Rect(runStart * cellWidth, y, runLen * cellWidth, cellHeight));
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

            _font.DrawGlyphRun(dc, str, 0, runLen, runStart * cellWidth, y, fgBrush);
        }
    }

    private void DrawCursor(DrawingContext dc, double cellWidth, double cellHeight)
    {
        if (_grid == null || !_grid.CursorVisible) return;
        var (r, c) = _grid.Cursor;
        if (r < 0 || r >= _grid.Rows || c < 0 || c >= _grid.Cols) return;
        int cursorLogicalLine = _grid.ScrollbackCount + r;
        double cursorY = cursorLogicalLine * cellHeight - _verticalOffset;
        if (cursorY + cellHeight < 0 || cursorY > ViewportHeightPx) return;
        var rect = new Rect(c * cellWidth, cursorY, cellWidth, cellHeight);
        // Bake the alpha into the color so the brush can be frozen
        var fg = AnsiPalette.DefaultFg();
        var cursorColor = Color.FromArgb(0x80, fg.R, fg.G, fg.B);
        var br = new SolidColorBrush(cursorColor);
        br.Freeze();
        dc.DrawRectangle(br, null, rect);
    }

    public event Action<byte[]>? InputBytes; // raised when bytes should go to pty
    public event Action<int, int>? SizeRequested; // (rows, cols) fired after grid.Resize

    private readonly HashSet<(Key key, ModifierKeys mods)> _allowlist = new();
    private System.Windows.Threading.DispatcherTimer? _resizeDebounce;

    /// <summary>
    /// Register a Volt-global shortcut that should bubble past the terminal
    /// instead of being forwarded as VT input bytes to the shell.
    /// Call from MainWindow during startup.
    /// </summary>
    public void AddAllowlistedShortcut(Key key, ModifierKeys mods) => _allowlist.Add((key, mods));

    private double ViewportWidthPx => _viewport.Width > 0 ? _viewport.Width : ActualWidth;
    private double ViewportHeightPx => _viewport.Height > 0 ? _viewport.Height : ActualHeight;

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 400 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height;
        _viewport = new Size(w, h);
        ScrollOwner?.InvalidateScrollInfo();
        return _viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewport = finalSize;
        ScrollOwner?.InvalidateScrollInfo();
        return finalSize;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;

        var mods = Keyboard.Modifiers;

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

        // Ctrl+C with no selection → SIGINT (v1: always SIGINT since selection isn't implemented yet)
        if (mods == ModifierKeys.Control && e.Key == Key.C)
        {
            InputBytes?.Invoke(new byte[] { 0x03 });
            e.Handled = true;
            return;
        }

        // Encode via KeyEncoder
        var bytes = KeyEncoder.Encode(e.Key, mods);
        if (bytes != null)
        {
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
        InputBytes?.Invoke(bytes);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
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
