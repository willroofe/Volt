using System;
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
