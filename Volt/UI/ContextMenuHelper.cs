using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Volt;

/// <summary>
/// Creates themed context menus and menu items that match the app's visual style.
/// </summary>
public static class ContextMenuHelper
{
    private static Style MenuItemStyle =>
        (Style)Application.Current.Resources["ThemedContextMenuItem"];

    /// <summary>Creates a new themed ContextMenu (popup appearance is handled by the global implicit style).</summary>
    public static ContextMenu Create() => new();

    /// <summary>Creates a themed MenuItem with a click handler.</summary>
    public static MenuItem Item(string header, Action onClick)
    {
        var mi = new MenuItem { Header = header, Style = MenuItemStyle };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    /// <summary>Creates a themed MenuItem with a Segoe MDL2 Assets icon and a click handler.</summary>
    public static MenuItem Item(string header, string iconGlyph, Action onClick)
    {
        var mi = Item(header, onClick);
        mi.Icon = new TextBlock
        {
            Text = iconGlyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 12,
            Foreground = (Brush)Application.Current.Resources[ThemeResourceKeys.TextFg],
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        return mi;
    }

    /// <summary>Creates a themed MenuItem that acts as a submenu parent.</summary>
    public static MenuItem Submenu(string header)
    {
        return new MenuItem { Header = header, Style = MenuItemStyle };
    }

    /// <summary>Creates a themed Separator.</summary>
    public static Separator Separator() => new();
}
