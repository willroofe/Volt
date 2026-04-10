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

        // Paint the background — colors may come from the theme's terminal section
        var bgColor = AnsiPalette.DefaultBg();
        var bg = new SolidColorBrush(bgColor);
        bg.Freeze();
        dc.DrawRectangle(bg, null, new Rect(RenderSize));

        // Task 30 implements the grid rendering; this skeleton draws just the background.
        if (_grid == null) return;
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
