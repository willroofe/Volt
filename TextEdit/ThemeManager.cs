using System.IO;
using System.Windows;
using System.Windows.Media;

namespace TextEdit;

public static class ThemeManager
{
    public static event EventHandler? ThemeChanged;

    private static ColorTheme _colorTheme = new();

    private static readonly string ThemesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TextEdit", "Themes");

    public static string CurrentThemeName => _colorTheme.Name;

    // Editor colors (read directly by EditorControl.OnRender)
    public static Brush EditorBg { get; private set; } = Brushes.White;
    public static Brush EditorFg { get; private set; } = Brushes.Black;
    public static Brush GutterFg { get; private set; } = Brushes.Gray;
    public static Brush CaretBrush { get; private set; } = Brushes.Black;
    public static Brush SelectionBrush { get; private set; } = null!;
    public static Brush CurrentLineBrush { get; private set; } = null!;
    public static Brush ActiveLineNumberFg { get; private set; } = Brushes.DarkGray;
    public static Brush MatchingBracketBrush { get; private set; } = null!;
    public static Pen MatchingBracketPen { get; private set; } = null!;

    // Syntax highlight scope → brush
    private static readonly Dictionary<string, Brush> _scopeBrushes = new();

    static ThemeManager()
    {
        EnsureDefaultThemes();
        // Apply Default Dark as the initial theme
        Apply("Default Dark");
    }

    public static Brush GetScopeBrush(string scope)
    {
        return _scopeBrushes.TryGetValue(scope, out var brush) ? brush : EditorFg;
    }

    public static List<string> GetAvailableThemes()
    {
        var names = new List<string>();
        if (!Directory.Exists(ThemesDir)) return names;
        foreach (var file in Directory.GetFiles(ThemesDir, "*.json"))
        {
            var theme = ColorTheme.LoadFromFile(file);
            if (theme != null) names.Add(theme.Name);
        }
        return names;
    }

    public static void Apply(string themeName)
    {
        var theme = FindTheme(themeName);
        theme ??= FindTheme("Default Dark");
        theme ??= new ColorTheme { Name = "Default Dark" };
        _colorTheme = theme;

        UpdateEditorColors();
        UpdateScopeBrushes();
        UpdateAppResources();
        ThemeChanged?.Invoke(null, EventArgs.Empty);
    }

    private static ColorTheme? FindTheme(string name)
    {
        if (!Directory.Exists(ThemesDir)) return null;
        foreach (var file in Directory.GetFiles(ThemesDir, "*.json"))
        {
            var theme = ColorTheme.LoadFromFile(file);
            if (theme != null && theme.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                return theme;
        }
        return null;
    }

    private static void UpdateEditorColors()
    {
        var e = _colorTheme.Editor;
        EditorBg = ColorTheme.ParseBrush(e.Background);
        EditorFg = ColorTheme.ParseBrush(e.Foreground);
        GutterFg = ColorTheme.ParseBrush(e.GutterForeground);
        CaretBrush = ColorTheme.ParseBrush(e.Caret);
        SelectionBrush = ColorTheme.ParseBrush(e.Selection);
        CurrentLineBrush = ColorTheme.ParseBrush(e.CurrentLine);
        ActiveLineNumberFg = ColorTheme.ParseBrush(e.ActiveLineNumber);
        MatchingBracketBrush = ColorTheme.ParseBrush(e.MatchingBracket);
        MatchingBracketPen = ColorTheme.ParsePen(e.MatchingBracketBorder, 1);
    }

    private static void UpdateScopeBrushes()
    {
        _scopeBrushes.Clear();
        foreach (var (scope, _) in _colorTheme.Scopes)
        {
            var brush = _colorTheme.GetScopeBrush(scope);
            if (brush != null) _scopeBrushes[scope] = brush;
        }
    }

    private static void UpdateAppResources()
    {
        var res = Application.Current.Resources;
        var c = _colorTheme.Chrome;
        res["ThemeChromeBrush"] = ColorTheme.ParseBrush(c.TitleBar);
        res["ThemeBorderBrush"] = ColorTheme.ParseBrush(c.Border);
        res["ThemeContentBg"] = ColorTheme.ParseBrush(c.ContentBackground);
        res["ThemeTextFg"] = ColorTheme.ParseBrush(c.TextForeground);
        res["ThemeTextFgStrong"] = ColorTheme.ParseBrush(c.TextForegroundStrong);
        res["ThemeTextFgMuted"] = ColorTheme.ParseBrush(c.TextForegroundMuted);
        res["ThemeButtonFg"] = ColorTheme.ParseBrush(c.ButtonForeground);
        res["ThemeButtonHover"] = ColorTheme.ParseBrush(c.ButtonHover);
        res["ThemeMenuPopupBg"] = ColorTheme.ParseBrush(c.MenuPopupBackground);
        res["ThemeMenuPopupBorder"] = ColorTheme.ParseBrush(c.MenuPopupBorder);
        res["ThemeMenuItemHover"] = ColorTheme.ParseBrush(c.MenuItemHover);
        res["ThemeNavBg"] = ColorTheme.ParseBrush(c.NavBackground);
        res["ThemeNavActive"] = ColorTheme.ParseBrush(c.NavActive);
        res["ThemeNavHover"] = ColorTheme.ParseBrush(c.NavHover);
        res["ThemeScrollBg"] = ColorTheme.ParseBrush(c.ScrollBackground);
        res["ThemeScrollThumb"] = ColorTheme.ParseBrush(c.ScrollThumb);
        res["ThemeScrollThumbHover"] = ColorTheme.ParseBrush(c.ScrollThumbHover);
    }

    private static void EnsureDefaultThemes()
    {
        Directory.CreateDirectory(ThemesDir);

        var darkPath = Path.Combine(ThemesDir, "default-dark.json");
        if (!File.Exists(darkPath))
            File.WriteAllText(darkPath, DefaultDarkTheme);

        var lightPath = Path.Combine(ThemesDir, "default-light.json");
        if (!File.Exists(lightPath))
            File.WriteAllText(lightPath, DefaultLightTheme);

        var gruvboxPath = Path.Combine(ThemesDir, "gruvbox-dark.json");
        if (!File.Exists(gruvboxPath))
            File.WriteAllText(gruvboxPath, GruvboxDarkTheme);
    }

    private static readonly string DefaultDarkTheme = """
        {
          "name": "Default Dark",
          "editor": {
            "background": "#1E1E1E",
            "foreground": "#D4D4D4",
            "gutterForeground": "#6E7681",
            "activeLineNumber": "#C6C6C6",
            "caret": "#D4D4D4",
            "selection": "#80264F78",
            "currentLine": "#2A2A2A",
            "matchingBracket": "#3A3A3A",
            "matchingBracketBorder": "#888888"
          },
          "chrome": {
            "titleBar": "#2D2D2D",
            "border": "#3F3F3F",
            "contentBackground": "#1E1E1E",
            "textForeground": "#CCCCCC",
            "textForegroundStrong": "#EEEEEE",
            "textForegroundMuted": "#666666",
            "buttonForeground": "#CCCCCC",
            "buttonHover": "#404040",
            "menuPopupBackground": "#2D2D2D",
            "menuPopupBorder": "#4A4A4A",
            "menuItemHover": "#3A3A3A",
            "navBackground": "#252525",
            "navActive": "#383838",
            "navHover": "#333333",
            "scrollBackground": "#2A2A2A",
            "scrollThumb": "#4A4A4A",
            "scrollThumbHover": "#5A5A5A"
          },
          "scopes": {
            "comment": "#6A9955",
            "string": "#CE9178",
            "keyword": "#569CD6",
            "variable": "#9CDCFE",
            "number": "#B5CEA8",
            "operator": "#D4D4D4",
            "regex": "#D16969",
            "type": "#4EC9B0",
            "function": "#DCDCAA",
            "hashkey": "#92C5F7",
            "escape": "#D7BA7D"
          }
        }
        """;

    private static readonly string DefaultLightTheme = """
        {
          "name": "Default Light",
          "editor": {
            "background": "#FFFFFF",
            "foreground": "#000000",
            "gutterForeground": "#808080",
            "activeLineNumber": "#A9A9A9",
            "caret": "#000000",
            "selection": "#60339900",
            "currentLine": "#F0F0F0",
            "matchingBracket": "#DBDBDB",
            "matchingBracketBorder": "#999999"
          },
          "chrome": {
            "titleBar": "#E8E8E8",
            "border": "#D0D0D0",
            "contentBackground": "#FFFFFF",
            "textForeground": "#111111",
            "textForegroundStrong": "#222222",
            "textForegroundMuted": "#888888",
            "buttonForeground": "#333333",
            "buttonHover": "#D0D0D0",
            "menuPopupBackground": "#FFFFFF",
            "menuPopupBorder": "#D0D0D0",
            "menuItemHover": "#E0E0E0",
            "navBackground": "#F0F0F0",
            "navActive": "#D8D8D8",
            "navHover": "#E0E0E0",
            "scrollBackground": "#E0E0E0",
            "scrollThumb": "#C0C0C0",
            "scrollThumbHover": "#A0A0A0"
          },
          "scopes": {
            "comment": "#008000",
            "string": "#A31515",
            "keyword": "#0000FF",
            "variable": "#001080",
            "number": "#098658",
            "operator": "#000000",
            "regex": "#811F3F",
            "type": "#267F99",
            "function": "#795E26",
            "hashkey": "#2B6CB0",
            "escape": "#A1260D"
          }
        }
        """;

    private static readonly string GruvboxDarkTheme = """
        {
          "name": "Gruvbox Dark",
          "editor": {
            "background": "#282828",
            "foreground": "#EBDBB2",
            "gutterForeground": "#665C54",
            "activeLineNumber": "#A89984",
            "caret": "#EBDBB2",
            "selection": "#60458588",
            "currentLine": "#32302F",
            "matchingBracket": "#3C3836",
            "matchingBracketBorder": "#928374"
          },
          "chrome": {
            "titleBar": "#1D2021",
            "border": "#3C3836",
            "contentBackground": "#282828",
            "textForeground": "#EBDBB2",
            "textForegroundStrong": "#FBF1C7",
            "textForegroundMuted": "#665C54",
            "buttonForeground": "#EBDBB2",
            "buttonHover": "#3C3836",
            "menuPopupBackground": "#1D2021",
            "menuPopupBorder": "#504945",
            "menuItemHover": "#3C3836",
            "navBackground": "#1D2021",
            "navActive": "#3C3836",
            "navHover": "#32302F",
            "scrollBackground": "#32302F",
            "scrollThumb": "#504945",
            "scrollThumbHover": "#665C54"
          },
          "scopes": {
            "comment": "#928374",
            "string": "#B8BB26",
            "keyword": "#FB4934",
            "variable": "#83A598",
            "number": "#D3869B",
            "operator": "#FE8019",
            "regex": "#FABD2F",
            "type": "#8EC07C",
            "function": "#FABD2F",
            "hashkey": "#D3869B",
            "escape": "#FE8019"
          }
        }
        """;
}
