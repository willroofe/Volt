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

    protected override int VisualChildrenCount => 2;
    protected override Visual GetVisualChild(int index) => index == 0 ? _textVisual : _cursorVisual;

    public TerminalView()
    {
        AddVisualChild(_textVisual);
        AddVisualChild(_cursorVisual);
        Focusable = true;
        FocusVisualStyle = null;

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
        Dispatcher.BeginInvoke(new Action(InvalidateVisual), System.Windows.Threading.DispatcherPriority.Render);
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);

        var defaultBgColor = AnsiPalette.DefaultBg();
        var bg = new SolidColorBrush(defaultBgColor);
        bg.Freeze();
        dc.DrawRectangle(bg, null, new Rect(RenderSize));

        if (_grid == null) return;

        var defaultFgColor = AnsiPalette.DefaultFg();
        var defaultFg = new SolidColorBrush(defaultFgColor);
        defaultFg.Freeze();

        double y = 0;
        double cellWidth = _font.CharWidth;
        double cellHeight = _font.LineHeight;

        for (int row = 0; row < _grid.Rows; row++)
        {
            RenderRow(dc, row, y, cellWidth, cellHeight, defaultFg);
            y += cellHeight;
        }

        DrawCursor(dc, cellWidth, cellHeight);
    }

    private void RenderRow(DrawingContext dc, int row, double y, double cellWidth, double cellHeight, Brush defaultFg)
    {
        int col = 0;
        while (col < _grid!.Cols)
        {
            // Snapshot the attributes of the first cell in this run
            ref readonly var first = ref _grid.CellAt(row, col);
            int runStart = col;
            int fg = first.FgIndex;
            int bg = first.BgIndex;
            var attr = first.Attr;
            col++;

            // Extend the run while adjacent cells share (fg, bg, attr)
            while (col < _grid.Cols)
            {
                ref readonly var c = ref _grid.CellAt(row, col);
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
                char g = _grid.CellAt(row, runStart + i).Glyph;
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
        var rect = new Rect(c * cellWidth, r * cellHeight, cellWidth, cellHeight);
        var br = new SolidColorBrush(AnsiPalette.DefaultFg());
        br.Freeze();
        // 40% alpha for an inverse-looking cursor
        br.Opacity = 0.5;
        dc.DrawRectangle(br, null, rect);
    }

    public event Action<byte[]>? InputBytes; // raised when bytes should go to pty

    private readonly HashSet<(Key key, ModifierKeys mods)> _allowlist = new();

    /// <summary>
    /// Register a Volt-global shortcut that should bubble past the terminal
    /// instead of being forwarded as VT input bytes to the shell.
    /// Call from MainWindow during startup.
    /// </summary>
    public void AddAllowlistedShortcut(Key key, ModifierKeys mods) => _allowlist.Add((key, mods));

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

    // --- IScrollInfo stubs (fleshed out in Task 32) ---
    public bool CanVerticallyScroll { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public double ExtentWidth => ActualWidth;
    public double ExtentHeight => _grid == null ? 0 : (_grid.Rows + _grid.ScrollbackCount) * _font.LineHeight;
    public double ViewportWidth => ActualWidth;
    public double ViewportHeight => ActualHeight;
    public double HorizontalOffset => 0;
    public double VerticalOffset { get; private set; }
    public ScrollViewer? ScrollOwner { get; set; }
    public void LineUp() { }
    public void LineDown() { }
    public void LineLeft() { }
    public void LineRight() { }
    public void PageUp() { }
    public void PageDown() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelUp() { }
    public void MouseWheelDown() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }
    public void SetVerticalOffset(double offset) { VerticalOffset = offset; InvalidateVisual(); }
    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
}
