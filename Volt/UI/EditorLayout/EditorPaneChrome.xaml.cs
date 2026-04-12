using System.Windows;
using System.Windows.Controls;

namespace Volt;

public partial class EditorPaneChrome : UserControl
{
    public EditorPaneChrome()
    {
        InitializeComponent();
    }

    /// <summary>Hit band for cross-leaf tab drag (tab bar + splitter-adjacent chrome).</summary>
    public FrameworkElement TabDragHitBand => OuterDock;
}
