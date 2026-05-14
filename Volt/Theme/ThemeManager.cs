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
    private readonly Dictionary<string, Brush> _syntaxBrushes = new(StringComparer.OrdinalIgnoreCase);

    private static readonly string[] ObsoleteBundledThemeFiles =
    [
        "atom-one-dark-vivid.json",
        "atom-one-dark.json",
        "atom-one-light-vivid.json",
        "atom-one-light.json",
        "c-family-2017-dark.json",
        "c-family-2017-light.json",
        "c-family-dark.json",
        "c-family-light.json",
        "c-cpp-2017-dark.json",
        "c-cpp-2017-light.json",
        "c-cpp-dark.json",
        "c-cpp-light.json",
        "default-dark.json",
        "default-light.json",
        "dracula.json",
        "forge-dark-colorblind.json",
        "forge-dark-default.json",
        "forge-dark-dimmed.json",
        "forge-dark-high-contrast.json",
        "forge-dark-legacy.json",
        "forge-light-colorblind.json",
        "forge-light-default.json",
        "forge-light-high-contrast.json",
        "forge-light-legacy.json",
        "github-dark-colorblind.json",
        "github-dark-default.json",
        "github-dark-dimmed.json",
        "github-dark-high-contrast.json",
        "github-dark-legacy.json",
        "github-light-colorblind.json",
        "github-light-default.json",
        "github-light-high-contrast.json",
        "github-light-legacy.json",
        "gruvbox-dark.json",
        "nord.json",
        "particle-one-dark-vivid.json",
        "particle-one-dark.json",
        "particle-one-light-vivid.json",
        "particle-one-light.json",
        "polar.json",
        "powershell-ise.json",
        "shell-studio.json",
        "vampire-dark.json",
    ];

    private readonly string _themesDir;

    public string CurrentThemeName => _colorTheme.Name;

    // Editor colors (read directly by EditorControl.OnRender)
    public Brush EditorBg { get; private set; } = Brushes.White;
    public Brush EditorFg { get; private set; } = Brushes.Black;
    public Brush GutterFg { get; private set; } = Brushes.Gray;
    public Brush CaretBrush { get; private set; } = Brushes.Black;
    public Brush SelectionBrush { get; private set; } = Brushes.LightBlue;
    public Brush CurrentLineBrush { get; private set; } = Brushes.Transparent;
    public Brush ActiveLineNumberFg { get; private set; } = Brushes.DarkGray;
    public Brush FindMatchBrush { get; private set; } = Brushes.Yellow;
    public Brush FindMatchCurrentBrush { get; private set; } = Brushes.Orange;
    public Brush MatchingBracketBrush { get; private set; } = Brushes.LightGray;
    public Brush MatchingBracketBorderBrush { get; private set; } = Brushes.DodgerBlue;
    public Brush DiagnosticErrorBrush { get; private set; } = Brushes.Red;
    public TerminalColors TerminalColors => _colorTheme.Terminal;

    private bool _initialized;

    public ThemeManager()
    {
        _themesDir = AppPaths.ThemesDir;
        UpdateEditorColors();
    }

    internal ThemeManager(ColorTheme colorTheme)
        : this(colorTheme, AppPaths.ThemesDir)
    {
    }

    internal ThemeManager(ColorTheme colorTheme, string themesDir)
    {
        _colorTheme = colorTheme;
        _themesDir = themesDir;
        UpdateEditorColors();
    }

    public void Initialize()
    {
        if (_initialized) return;
        _initialized = true;
        EnsureDefaultThemes();
        // Theme is applied later by App.OnStartup via Apply(settings.ColorTheme)
        // to avoid a redundant double-apply when the user's theme matches "Dark".
    }

    public List<string> GetAvailableThemes()
    {
        return LoadThemeCache().Select(t => t.Name).ToList();
    }

    public void Apply(string themeName)
    {
        var themes = LoadThemeCache();
        var theme = themes.FirstOrDefault(t => t.Name.Equals(themeName, StringComparison.OrdinalIgnoreCase));
        theme ??= themes.FirstOrDefault(t => t.Name.Equals("Volt Dark", StringComparison.OrdinalIgnoreCase));
        theme ??= new ColorTheme { Name = "Volt Dark" };
        _colorTheme = theme;

        UpdateEditorColors();
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
        FindMatchBrush = ColorTheme.ParseBrush(e.FindMatch);
        FindMatchCurrentBrush = ColorTheme.ParseBrush(e.FindMatchCurrent);
        MatchingBracketBrush = ColorTheme.ParseBrush(e.MatchingBracket);
        MatchingBracketBorderBrush = ColorTheme.ParseBrush(e.MatchingBracketBorder);

        _syntaxBrushes.Clear();
        foreach (var (scope, hex) in _colorTheme.Scopes)
            _syntaxBrushes[scope] = ColorTheme.ParseBrush(hex);

        DiagnosticErrorBrush = GetDiagnosticBrush();
    }

    public Brush GetSyntaxBrush(LanguageTokenKind kind, string? scope)
    {
        if (TryGetSyntaxBrush(scope, out Brush brush))
            return brush;

        return kind switch
        {
            LanguageTokenKind.PropertyName => GetSyntaxBrushFallback("property", "hashkey", "variable"),
            LanguageTokenKind.String => GetSyntaxBrushFallback("string"),
            LanguageTokenKind.Number => GetSyntaxBrushFallback("number"),
            LanguageTokenKind.Boolean or LanguageTokenKind.Null => GetSyntaxBrushFallback("keyword"),
            LanguageTokenKind.Punctuation => GetSyntaxBrushFallback("operator"),
            LanguageTokenKind.Invalid => GetSyntaxBrushFallback("invalid", "regex", "keyword"),
            _ => EditorFg,
        };
    }

    private Brush GetSyntaxBrushFallback(string first, string? second = null, string? third = null)
    {
        if (TryGetSyntaxBrush(first, out Brush brush))
            return brush;
        if (TryGetSyntaxBrush(second, out brush))
            return brush;
        if (TryGetSyntaxBrush(third, out brush))
            return brush;

        return EditorFg;
    }

    private bool TryGetSyntaxBrush(string? scope, out Brush brush)
    {
        if (!string.IsNullOrWhiteSpace(scope)
            && _syntaxBrushes.TryGetValue(scope, out Brush? found))
        {
            brush = found;
            return true;
        }

        brush = EditorFg;
        return false;
    }

    private Brush GetDiagnosticBrush()
    {
        return TryGetSyntaxBrush("invalid", out Brush brush)
            ? brush
            : ColorTheme.ParseBrush("#E05252");
    }

    private void UpdateAppResources()
    {
        var res = Application.Current.Resources;
        var c = _colorTheme.Chrome;
        ReadOnlySpan<(string Key, string Hex)> mapping =
        [
            (ThemeResourceKeys.ChromeBrush, c.TitleBar),
            (ThemeResourceKeys.BorderBrush, c.Border),
            (ThemeResourceKeys.ContentBg, c.ContentBackground),
            (ThemeResourceKeys.TextFg, c.TextForeground),
            (ThemeResourceKeys.TextFgStrong, c.TextForegroundStrong),
            (ThemeResourceKeys.TextFgMuted, c.TextForegroundMuted),
            (ThemeResourceKeys.ButtonFg, c.ButtonForeground),
            (ThemeResourceKeys.ButtonHover, c.ButtonHover),
            (ThemeResourceKeys.MenuPopupBg, c.MenuPopupBackground),
            (ThemeResourceKeys.MenuPopupBorder, c.MenuPopupBorder),
            (ThemeResourceKeys.MenuItemHover, c.MenuItemHover),
            (ThemeResourceKeys.NavBg, c.NavBackground),
            (ThemeResourceKeys.NavActive, c.NavActive),
            (ThemeResourceKeys.NavHover, c.NavHover),
            (ThemeResourceKeys.ScrollBg, c.ScrollBackground),
            (ThemeResourceKeys.ScrollThumb, c.ScrollThumb),
            (ThemeResourceKeys.ScrollThumbHover, c.ScrollThumbHover),
            (ThemeResourceKeys.TabBarBg, c.TabBarBackground),
            (ThemeResourceKeys.TabActive, c.TabActive),
            (ThemeResourceKeys.TabActiveSecondary, string.IsNullOrEmpty(c.TabActiveSecondary) ? c.TabInactive : c.TabActiveSecondary),
            (ThemeResourceKeys.TabInactive, c.TabInactive),
            (ThemeResourceKeys.TabHover, c.TabHover),
            (ThemeResourceKeys.TabBorder, c.TabBorder),
            (ThemeResourceKeys.ExplorerBg, c.ExplorerBackground),
            (ThemeResourceKeys.ExplorerHeaderBg, c.ExplorerHeaderBackground),
            (ThemeResourceKeys.ExplorerHeaderFg, c.ExplorerHeaderForeground),
            (ThemeResourceKeys.ExplorerItemHover, c.ExplorerItemHover),
            (ThemeResourceKeys.ExplorerItemSelected, c.ExplorerItemSelected),
            (ThemeResourceKeys.ExplorerDropTarget, c.ExplorerDropTarget),
            (ThemeResourceKeys.InputSelection, c.InputSelection),
            (ThemeResourceKeys.IconPrimary, string.IsNullOrEmpty(c.IconPrimary) ? c.TextForeground : c.IconPrimary),
            (ThemeResourceKeys.IconSecondary, string.IsNullOrEmpty(c.IconSecondary) ? c.TextForegroundMuted : c.IconSecondary),
            (ThemeResourceKeys.IconAccent, string.IsNullOrEmpty(c.IconAccent) ? c.TextForegroundStrong : c.IconAccent),
        ];
        foreach (var (key, hex) in mapping)
            res[key] = ColorTheme.ParseBrush(hex);
    }

    private void EnsureDefaultThemes()
    {
        DeleteObsoleteBundledThemes();

        try
        {
            EmbeddedResourceHelper.ExtractAll("Volt.Resources.Themes.", _themesDir);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void DeleteObsoleteBundledThemes()
    {
        foreach (string fileName in ObsoleteBundledThemeFiles)
        {
            try
            {
                File.Delete(Path.Combine(_themesDir, fileName));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
