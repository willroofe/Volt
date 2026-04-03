using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Volt;

public class PanelContainer : DockPanel
{
    private readonly IPanel _panel;
    private readonly TextBlock _titleText;
    private Point? _dragStartPoint;

    private const double DragDeadZone = 5;

    /// <summary>Raised when the user drags the header past the dead zone. Parameter is the panel ID.</summary>
    public event Action<string>? DragStarted;

    public PanelContainer(IPanel panel)
    {
        _panel = panel;

        // Header bar
        var header = new Border
        {
            Height = 33,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Cursor = Cursors.SizeAll
        };

        _titleText = new TextBlock
        {
            Text = panel.Title,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12, 0, 12, 0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
        };

        // Bind colors dynamically so theme changes take effect
        _titleText.SetResourceReference(TextBlock.ForegroundProperty, "ThemeExplorerHeaderFg");
        header.SetResourceReference(Border.BackgroundProperty, "ThemeExplorerHeaderBg");
        header.SetResourceReference(Border.BorderBrushProperty, "ThemeTabBorder");

        header.Child = _titleText;
        header.MouseLeftButtonDown += OnHeaderMouseDown;
        header.MouseMove += OnHeaderMouseMove;
        header.MouseLeftButtonUp += OnHeaderMouseUp;

        DockPanel.SetDock(header, Dock.Top);
        Children.Add(header);
        Children.Add(panel.Content);

        panel.TitleChanged += () => _titleText.Text = panel.Title;
    }

    public string PanelId => _panel.PanelId;

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(this);
        ((UIElement)sender).CaptureMouse();
    }

    private void OnHeaderMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStartPoint == null) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            CancelTracking(sender);
            return;
        }

        var pos = e.GetPosition(this);
        var delta = pos - _dragStartPoint.Value;
        if (Math.Abs(delta.X) > DragDeadZone || Math.Abs(delta.Y) > DragDeadZone)
        {
            CancelTracking(sender);
            DragStarted?.Invoke(_panel.PanelId);
        }
    }

    private void OnHeaderMouseUp(object sender, MouseButtonEventArgs e)
    {
        CancelTracking(sender);
    }

    private void CancelTracking(object sender)
    {
        _dragStartPoint = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }
}
