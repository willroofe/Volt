using System.Windows;

namespace Volt;

public interface IPanel
{
    string PanelId { get; }
    string Title { get; }
    string? IconGlyph { get; }
    UIElement Content { get; }
    event Action? TitleChanged;
}

public enum PanelPlacement
{
    Left,
    Right,
    Top,
    Bottom
}
