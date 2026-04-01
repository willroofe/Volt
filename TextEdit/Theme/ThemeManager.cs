using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Media;

namespace TextEdit;

public class ThemeManager
{
    public event EventHandler? ThemeChanged;

    private ColorTheme _colorTheme = new();
    private List<ColorTheme>? _themeCache;

    private readonly string ThemesDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TextEdit", "Themes");

    public string CurrentThemeName => _colorTheme.Name;

    // Editor colors (read directly by EditorControl.OnRender)
    public Brush EditorBg { get; private set; } = Brushes.White;
    public Brush EditorFg { get; private set; } = Brushes.Black;
    public Brush GutterFg { get; private set; } = Brushes.Gray;
    public Brush CaretBrush { get; private set; } = Brushes.Black;
    public Brush SelectionBrush { get; private set; } = null!;
    public Brush CurrentLineBrush { get; private set; } = null!;
    public Brush ActiveLineNumberFg { get; private set; } = Brushes.DarkGray;
    public Brush MatchingBracketBrush { get; private set; } = null!;
    public Pen MatchingBracketPen { get; private set; } = null!;
    public Brush FindMatchBrush { get; private set; } = null!;
    public Brush FindMatchCurrentBrush { get; private set; } = null!;

    // Syntax highlight scope → brush
    private readonly Dictionary<string, Brush> _scopeBrushes = new();

    private bool _initialized;

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EnsureDefaultThemes();
        Apply("Dark");
    }

    public Brush GetScopeBrush(string scope)
    {
        return _scopeBrushes.TryGetValue(scope, out var brush) ? brush : EditorFg;
    }

    public List<string> GetAvailableThemes()
    {
        return LoadThemeCache().Select(t => t.Name).ToList();
    }

    public void Apply(string themeName)
    {
        var themes = LoadThemeCache();
        var theme = themes.FirstOrDefault(t => t.Name.Equals(themeName, StringComparison.OrdinalIgnoreCase));
        theme ??= themes.FirstOrDefault(t => t.Name.Equals("Dark", StringComparison.OrdinalIgnoreCase));
        theme ??= new ColorTheme { Name = "Dark" };
        _colorTheme = theme;

        UpdateEditorColors();
        UpdateScopeBrushes();
        UpdateAppResources();
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ReloadThemes()
    {
        _themeCache = null;
    }

    private List<ColorTheme> LoadThemeCache()
    {
        if (_themeCache != null) return _themeCache;
        _themeCache = [];
        if (!Directory.Exists(ThemesDir)) return _themeCache;
        foreach (var file in Directory.GetFiles(ThemesDir, "*.json"))
        {
            try
            {
                var theme = ColorTheme.LoadFromFile(file);
                if (theme != null) _themeCache.Add(theme);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load theme '{file}': {ex.Message}");
            }
        }
        return _themeCache;
    }

    private void UpdateEditorColors()
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
        FindMatchBrush = ColorTheme.ParseBrush(e.FindMatch);
        FindMatchCurrentBrush = ColorTheme.ParseBrush(e.FindMatchCurrent);
    }

    private void UpdateScopeBrushes()
    {
        _scopeBrushes.Clear();
        foreach (var (scope, _) in _colorTheme.Scopes)
        {
            var brush = _colorTheme.GetScopeBrush(scope);
            if (brush != null) _scopeBrushes[scope] = brush;
        }
    }

    private void UpdateAppResources()
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
        res["ThemeTabBarBg"] = ColorTheme.ParseBrush(c.TabBarBackground);
        res["ThemeTabActive"] = ColorTheme.ParseBrush(c.TabActive);
        res["ThemeTabInactive"] = ColorTheme.ParseBrush(c.TabInactive);
        res["ThemeTabHover"] = ColorTheme.ParseBrush(c.TabHover);
        res["ThemeTabBorder"] = ColorTheme.ParseBrush(c.TabBorder);
    }

    private void EnsureDefaultThemes()
    {
        try
        {
            Directory.CreateDirectory(ThemesDir);
            WriteEmbeddedResource("TextEdit.Resources.Themes.default-dark.json", Path.Combine(ThemesDir, "default-dark.json"));
            WriteEmbeddedResource("TextEdit.Resources.Themes.default-light.json", Path.Combine(ThemesDir, "default-light.json"));
            WriteEmbeddedResource("TextEdit.Resources.Themes.gruvbox-dark.json", Path.Combine(ThemesDir, "gruvbox-dark.json"));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void WriteEmbeddedResource(string resourceName, string targetPath)
    {
        // Always overwrite built-in themes so embedded fixes take effect
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream == null) return;
        using var reader = new StreamReader(stream);
        File.WriteAllText(targetPath, reader.ReadToEnd());
    }
}
