using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace Volt;

public class ThemeManager
{
    public event EventHandler? ThemeChanged;

    private ColorTheme _colorTheme = new();
    private List<ColorTheme>? _themeCache;

    private readonly string _themesDir = AppPaths.ThemesDir;

    public string CurrentThemeName => _colorTheme.Name;

    // Editor colors (read directly by EditorControl.OnRender)
    public Brush EditorBg { get; private set; } = Brushes.White;
    public Brush EditorFg { get; private set; } = Brushes.Black;
    public Brush GutterFg { get; private set; } = Brushes.Gray;
    public Brush CaretBrush { get; private set; } = Brushes.Black;
    public Brush SelectionBrush { get; private set; } = Brushes.LightBlue;
    public Brush CurrentLineBrush { get; private set; } = Brushes.Transparent;
    public Brush ActiveLineNumberFg { get; private set; } = Brushes.DarkGray;
    public Brush MatchingBracketBrush { get; private set; } = Brushes.Transparent;
    public Pen MatchingBracketPen { get; private set; } = new Pen(Brushes.Gray, 1);
    public Brush FindMatchBrush { get; private set; } = Brushes.Yellow;
    public Brush FindMatchCurrentBrush { get; private set; } = Brushes.Orange;

    // Syntax highlight scope → brush
    private readonly Dictionary<string, Brush> _scopeBrushes = new();

    private bool _initialized;

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EnsureDefaultThemes();
        // Theme is applied later by App.OnStartup via Apply(settings.ColorTheme)
        // to avoid a redundant double-apply when the user's theme matches "Dark".
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
        if (!Directory.Exists(_themesDir)) return _themeCache;
        foreach (var file in Directory.GetFiles(_themesDir, "*.json"))
        {
            try
            {
                var theme = ColorTheme.LoadFromFile(file);
                if (theme != null) _themeCache.Add(theme);
            }
            catch (Exception) { }
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
        res[ThemeResourceKeys.ChromeBrush] = ColorTheme.ParseBrush(c.TitleBar);
        res[ThemeResourceKeys.BorderBrush] = ColorTheme.ParseBrush(c.Border);
        res[ThemeResourceKeys.ContentBg] = ColorTheme.ParseBrush(c.ContentBackground);
        res[ThemeResourceKeys.TextFg] = ColorTheme.ParseBrush(c.TextForeground);
        res[ThemeResourceKeys.TextFgStrong] = ColorTheme.ParseBrush(c.TextForegroundStrong);
        res[ThemeResourceKeys.TextFgMuted] = ColorTheme.ParseBrush(c.TextForegroundMuted);
        res[ThemeResourceKeys.ButtonFg] = ColorTheme.ParseBrush(c.ButtonForeground);
        res[ThemeResourceKeys.ButtonHover] = ColorTheme.ParseBrush(c.ButtonHover);
        res[ThemeResourceKeys.MenuPopupBg] = ColorTheme.ParseBrush(c.MenuPopupBackground);
        res[ThemeResourceKeys.MenuPopupBorder] = ColorTheme.ParseBrush(c.MenuPopupBorder);
        res[ThemeResourceKeys.MenuItemHover] = ColorTheme.ParseBrush(c.MenuItemHover);
        res[ThemeResourceKeys.NavBg] = ColorTheme.ParseBrush(c.NavBackground);
        res[ThemeResourceKeys.NavActive] = ColorTheme.ParseBrush(c.NavActive);
        res[ThemeResourceKeys.NavHover] = ColorTheme.ParseBrush(c.NavHover);
        res[ThemeResourceKeys.ScrollBg] = ColorTheme.ParseBrush(c.ScrollBackground);
        res[ThemeResourceKeys.ScrollThumb] = ColorTheme.ParseBrush(c.ScrollThumb);
        res[ThemeResourceKeys.ScrollThumbHover] = ColorTheme.ParseBrush(c.ScrollThumbHover);
        res[ThemeResourceKeys.TabBarBg] = ColorTheme.ParseBrush(c.TabBarBackground);
        res[ThemeResourceKeys.TabActive] = ColorTheme.ParseBrush(c.TabActive);
        res[ThemeResourceKeys.TabInactive] = ColorTheme.ParseBrush(c.TabInactive);
        res[ThemeResourceKeys.TabHover] = ColorTheme.ParseBrush(c.TabHover);
        res[ThemeResourceKeys.TabBorder] = ColorTheme.ParseBrush(c.TabBorder);
        res[ThemeResourceKeys.ExplorerBg] = ColorTheme.ParseBrush(c.ExplorerBackground);
        res[ThemeResourceKeys.ExplorerHeaderBg] = ColorTheme.ParseBrush(c.ExplorerHeaderBackground);
        res[ThemeResourceKeys.ExplorerHeaderFg] = ColorTheme.ParseBrush(c.ExplorerHeaderForeground);
        res[ThemeResourceKeys.ExplorerItemHover] = ColorTheme.ParseBrush(c.ExplorerItemHover);
        res[ThemeResourceKeys.ExplorerItemSelected] = ColorTheme.ParseBrush(c.ExplorerItemSelected);
        res[ThemeResourceKeys.ExplorerDropTarget] = ColorTheme.ParseBrush(c.ExplorerDropTarget);
        res[ThemeResourceKeys.InputSelection] = ColorTheme.ParseBrush(c.InputSelection);
    }

    private void EnsureDefaultThemes()
    {
        try
        {
            EmbeddedResourceHelper.ExtractAll("Volt.Resources.Themes.", _themesDir);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
