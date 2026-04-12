using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Volt;

public partial class EditorPaneChrome : UserControl
{
    public EditorPaneChrome()
    {
        InitializeComponent();
    }

    /// <summary>Hit band for cross-leaf tab drag (tab bar + splitter-adjacent chrome).</summary>
    public FrameworkElement TabDragHitBand => OuterDock;

    /// <summary>Half-pane tint during tab-drag split drop (same approach as <see cref="PanelShell"/> dock overlays).</summary>
    public void SetEditorSplitDropHighlight(bool show, EditorSplitOrientation orientation, bool activeInSecondPane)
    {
        if (!show)
        {
            EditorSplitDropHighlight.Visibility = Visibility.Collapsed;
            EditorSplitDropHighlight.Background = null;
            return;
        }

        double w = EditorHostPanel.ActualWidth;
        double h = EditorHostPanel.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            EditorSplitDropHighlight.Visibility = Visibility.Collapsed;
            return;
        }

        var fg = (Brush)Application.Current.Resources[ThemeResourceKeys.TextFg];
        var brush = fg.Clone();
        brush.Opacity = 0.2;
        if (brush.CanFreeze)
            brush.Freeze();
        EditorSplitDropHighlight.Background = brush;

        if (orientation == EditorSplitOrientation.Vertical)
        {
            EditorSplitDropHighlight.HorizontalAlignment =
                activeInSecondPane ? HorizontalAlignment.Right : HorizontalAlignment.Left;
            EditorSplitDropHighlight.VerticalAlignment = VerticalAlignment.Stretch;
            EditorSplitDropHighlight.Width = w * 0.5;
            EditorSplitDropHighlight.Height = double.NaN;
        }
        else
        {
            EditorSplitDropHighlight.VerticalAlignment =
                activeInSecondPane ? VerticalAlignment.Bottom : VerticalAlignment.Top;
            EditorSplitDropHighlight.HorizontalAlignment = HorizontalAlignment.Stretch;
            EditorSplitDropHighlight.Height = h * 0.5;
            EditorSplitDropHighlight.Width = double.NaN;
        }

        EditorSplitDropHighlight.Visibility = Visibility.Visible;
    }
}
