using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;

namespace TextEdit;

public class TabInfo
{
    public string? FilePath { get; set; }
    public Encoding FileEncoding { get; set; } = new UTF8Encoding(false);
    public EditorControl Editor { get; }
    public ScrollViewer ScrollHost { get; }
    public Border HeaderElement { get; set; } = null!;

    public string DisplayName => FilePath != null ? Path.GetFileName(FilePath) : "Untitled";

    public TabInfo(ThemeManager themeManager, SyntaxManager syntaxManager)
    {
        Editor = new EditorControl
        {
            ThemeManager = themeManager,
            SyntaxManager = syntaxManager
        };
        ScrollHost = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Visible,
            VerticalScrollBarVisibility = ScrollBarVisibility.Visible,
            CanContentScroll = true,
            Content = Editor,
            Template = (ControlTemplate)Application.Current.FindResource("ThemedScrollViewer")
        };
    }
}
