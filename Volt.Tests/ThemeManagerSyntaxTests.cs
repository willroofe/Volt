using System.Windows.Media;
using Volt;
using Xunit;

namespace Volt.Tests;

public class ThemeManagerSyntaxTests
{
    [Fact]
    public void GetSyntaxBrush_UsesTokenScopeColor()
    {
        var theme = new ColorTheme
        {
            Editor = new EditorColors { Foreground = "#111111" },
            Scopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["string"] = "#00FF00"
            }
        };
        var manager = new ThemeManager(theme);

        Brush brush = manager.GetSyntaxBrush(LanguageTokenKind.String, "string");

        AssertBrushColor("#00FF00", brush);
    }

    [Fact]
    public void GetSyntaxBrush_PropertyNameFallsBackToHashKeyScope()
    {
        var theme = new ColorTheme
        {
            Editor = new EditorColors { Foreground = "#111111" },
            Scopes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["hashkey"] = "#FF8800"
            }
        };
        var manager = new ThemeManager(theme);

        Brush brush = manager.GetSyntaxBrush(LanguageTokenKind.PropertyName, "property");

        AssertBrushColor("#FF8800", brush);
    }

    [Fact]
    public void GetSyntaxBrush_FallsBackToEditorForeground()
    {
        var theme = new ColorTheme
        {
            Editor = new EditorColors { Foreground = "#123456" }
        };
        var manager = new ThemeManager(theme);

        Brush brush = manager.GetSyntaxBrush(LanguageTokenKind.Punctuation, "unknown");

        Assert.Same(manager.EditorFg, brush);
    }

    [Fact]
    public void MatchingBracketBrushes_UseEditorThemeColors()
    {
        var theme = new ColorTheme
        {
            Editor = new EditorColors
            {
                MatchingBracket = "#112233",
                MatchingBracketBorder = "#445566"
            }
        };
        var manager = new ThemeManager(theme);

        AssertBrushColor("#112233", manager.MatchingBracketBrush);
        AssertBrushColor("#445566", manager.MatchingBracketBorderBrush);
    }

    private static void AssertBrushColor(string expectedHex, Brush brush)
    {
        var solid = Assert.IsType<SolidColorBrush>(brush);
        var expected = (Color)ColorConverter.ConvertFromString(expectedHex)!;
        Assert.Equal(expected, solid.Color);
    }
}
