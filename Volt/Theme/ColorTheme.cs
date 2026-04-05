using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace Volt;

public class EditorColors
{
    [JsonPropertyName("background")] public string Background { get; set; } = "#FFFFFF";
    [JsonPropertyName("foreground")] public string Foreground { get; set; } = "#000000";
    [JsonPropertyName("gutterForeground")] public string GutterForeground { get; set; } = "#808080";
    [JsonPropertyName("activeLineNumber")] public string ActiveLineNumber { get; set; } = "#A9A9A9";
    [JsonPropertyName("caret")] public string Caret { get; set; } = "#000000";
    [JsonPropertyName("selection")] public string Selection { get; set; } = "#60339900";
    [JsonPropertyName("currentLine")] public string CurrentLine { get; set; } = "#F0F0F0";
    [JsonPropertyName("matchingBracket")] public string MatchingBracket { get; set; } = "#DBDBDB";
    [JsonPropertyName("matchingBracketBorder")] public string MatchingBracketBorder { get; set; } = "#999999";
    [JsonPropertyName("findMatch")] public string FindMatch { get; set; } = "#60FFFF00";
    [JsonPropertyName("findMatchCurrent")] public string FindMatchCurrent { get; set; } = "#80FF8C00";
    [JsonPropertyName("indentGuide")] public string IndentGuide { get; set; } = "#30808080";
}

public class ChromeColors
{
    [JsonPropertyName("titleBar")] public string TitleBar { get; set; } = "#E8E8E8";
    [JsonPropertyName("border")] public string Border { get; set; } = "#D0D0D0";
    [JsonPropertyName("contentBackground")] public string ContentBackground { get; set; } = "#FFFFFF";
    [JsonPropertyName("textForeground")] public string TextForeground { get; set; } = "#111111";
    [JsonPropertyName("textForegroundStrong")] public string TextForegroundStrong { get; set; } = "#222222";
    [JsonPropertyName("textForegroundMuted")] public string TextForegroundMuted { get; set; } = "#888888";
    [JsonPropertyName("buttonForeground")] public string ButtonForeground { get; set; } = "#333333";
    [JsonPropertyName("buttonHover")] public string ButtonHover { get; set; } = "#D0D0D0";
    [JsonPropertyName("menuPopupBackground")] public string MenuPopupBackground { get; set; } = "#FFFFFF";
    [JsonPropertyName("menuPopupBorder")] public string MenuPopupBorder { get; set; } = "#D0D0D0";
    [JsonPropertyName("menuItemHover")] public string MenuItemHover { get; set; } = "#E0E0E0";
    [JsonPropertyName("navBackground")] public string NavBackground { get; set; } = "#F0F0F0";
    [JsonPropertyName("navActive")] public string NavActive { get; set; } = "#D8D8D8";
    [JsonPropertyName("navHover")] public string NavHover { get; set; } = "#E0E0E0";
    [JsonPropertyName("scrollBackground")] public string ScrollBackground { get; set; } = "#E0E0E0";
    [JsonPropertyName("scrollThumb")] public string ScrollThumb { get; set; } = "#C0C0C0";
    [JsonPropertyName("scrollThumbHover")] public string ScrollThumbHover { get; set; } = "#A0A0A0";
    [JsonPropertyName("tabBarBackground")] public string TabBarBackground { get; set; } = "#E0E0E0";
    [JsonPropertyName("tabActive")] public string TabActive { get; set; } = "#FFFFFF";
    [JsonPropertyName("tabInactive")] public string TabInactive { get; set; } = "#D8D8D8";
    [JsonPropertyName("tabHover")] public string TabHover { get; set; } = "#EBEBEB";
    [JsonPropertyName("tabBorder")] public string TabBorder { get; set; } = "#D0D0D0";
    [JsonPropertyName("explorerBackground")] public string ExplorerBackground { get; set; } = "#F0F0F0";
    [JsonPropertyName("explorerHeaderBackground")] public string ExplorerHeaderBackground { get; set; } = "#E8E8E8";
    [JsonPropertyName("explorerHeaderForeground")] public string ExplorerHeaderForeground { get; set; } = "#888888";
    [JsonPropertyName("explorerItemHover")] public string ExplorerItemHover { get; set; } = "#E0E0E0";
    [JsonPropertyName("explorerItemSelected")] public string ExplorerItemSelected { get; set; } = "#D0D0D0";
    [JsonPropertyName("explorerDropTarget")] public string ExplorerDropTarget { get; set; } = "#B8D4E8";
    [JsonPropertyName("inputSelection")] public string InputSelection { get; set; } = "#80339900";
}

public class ColorTheme
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("scopes")]
    public Dictionary<string, string> Scopes { get; set; } = new();

    [JsonPropertyName("editor")]
    public EditorColors Editor { get; set; } = new();

    [JsonPropertyName("chrome")]
    public ChromeColors Chrome { get; set; } = new();

    [JsonIgnore]
    private readonly Dictionary<string, SolidColorBrush> _brushCache = new();

    public static SolidColorBrush ParseBrush(string? hex)
    {
        if (string.IsNullOrEmpty(hex))
        {
            var fallback = new SolidColorBrush(Colors.Magenta);
            fallback.Freeze();
            return fallback;
        }

        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch (FormatException)
        {
            var fallback = new SolidColorBrush(Colors.Magenta);
            fallback.Freeze();
            return fallback;
        }
    }

    public static Pen ParsePen(string hex, double thickness)
    {
        var pen = new Pen(ParseBrush(hex), thickness);
        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// Returns a cached frozen brush for the given scope, or null if the scope is undefined.
    /// Brushes are parsed once and reused for the lifetime of this theme instance.
    /// </summary>
    public SolidColorBrush? GetScopeBrush(string scope)
    {
        if (_brushCache.TryGetValue(scope, out var cached)) return cached;
        if (!Scopes.TryGetValue(scope, out var hex)) return null;
        var brush = ParseBrush(hex);
        _brushCache[scope] = brush;
        return brush;
    }

    public static ColorTheme? LoadFromFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ColorTheme>(json);
        }
        catch (Exception) { return null; }
    }
}
