using System.Windows;
using System.Windows.Controls;

namespace Volt;

public interface IPanel
{
    string PanelId { get; }
    string Title { get; }
    string? IconGlyph { get; }
    UIElement Content { get; }
    event Action? TitleChanged;

    /// <summary>Optional entries for the panel tab strip context menu (before Close).</summary>
    void AppendTabContextMenuItems(ContextMenu menu) { }
}

public enum PanelPlacement
{
    Left,
    Right,
    Top,
    Bottom
}
