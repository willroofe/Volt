using System.Windows;

namespace Volt;

public interface IPanel
{
    string PanelId { get; }
    string Title { get; }
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
