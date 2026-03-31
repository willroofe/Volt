using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace TextEdit;

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

    public static SolidColorBrush ParseBrush(string hex)
    {
        try
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
        catch
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

    public SolidColorBrush? GetScopeBrush(string scope)
    {
        if (!Scopes.TryGetValue(scope, out var hex)) return null;
        try { return ParseBrush(hex); }
        catch { return null; }
    }

    public static ColorTheme? LoadFromFile(string path)
    {
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ColorTheme>(json);
        }
        catch
        {
            return null;
        }
    }
}
