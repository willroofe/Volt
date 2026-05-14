using System.IO;
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

    [Fact]
    public void MatchingBracketPalette_UsesThemeColorArray()
    {
        var theme = new ColorTheme
        {
            Editor = new EditorColors
            {
                MatchingBracketColors = ["#112233", "#445566"]
            }
        };
        var manager = new ThemeManager(theme);

        Assert.Equal(2, manager.MatchingBracketPalette.Length);
        AssertColor("#112233", manager.MatchingBracketPalette[0]);
        AssertColor("#445566", manager.MatchingBracketPalette[1]);
    }

    [Fact]
    public void MatchingBracketPalette_FallsBackWhenThemeArrayMissingEmptyOrInvalid()
    {
        var missing = new ThemeManager(new ColorTheme());
        var empty = new ThemeManager(new ColorTheme { Editor = new EditorColors { MatchingBracketColors = [] } });
        var invalid = new ThemeManager(new ColorTheme { Editor = new EditorColors { MatchingBracketColors = ["nope"] } });

        AssertDefaultPalette(missing.MatchingBracketPalette);
        AssertDefaultPalette(empty.MatchingBracketPalette);
        AssertDefaultPalette(invalid.MatchingBracketPalette);
    }

    [Fact]
    public void Initialize_RemovesObsoleteBundledThemeFiles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "Volt.Tests.Themes", Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            string obsoleteTheme = Path.Combine(dir, "dracula.json");
            string defaultObsoleteTheme = Path.Combine(dir, "default-dark.json");
            string legacyObsoleteTheme = Path.Combine(dir, "vampire-dark.json");
            string customTheme = Path.Combine(dir, "custom.json");
            File.WriteAllText(obsoleteTheme, "{}");
            File.WriteAllText(defaultObsoleteTheme, "{}");
            File.WriteAllText(legacyObsoleteTheme, "{}");
            File.WriteAllText(customTheme, "{}");

            var manager = new ThemeManager(new ColorTheme(), dir);

            manager.Initialize();

            Assert.False(File.Exists(obsoleteTheme));
            Assert.False(File.Exists(defaultObsoleteTheme));
            Assert.False(File.Exists(legacyObsoleteTheme));
            Assert.True(File.Exists(customTheme));
            Assert.True(File.Exists(Path.Combine(dir, "volt-dark.json")));
            Assert.True(File.Exists(Path.Combine(dir, "volt-light.json")));
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    private static void AssertBrushColor(string expectedHex, Brush brush)
    {
        var solid = Assert.IsType<SolidColorBrush>(brush);
        AssertColor(expectedHex, solid.Color);
    }

    private static void AssertColor(string expectedHex, Color actual)
    {
        var expected = (Color)ColorConverter.ConvertFromString(expectedHex)!;
        Assert.Equal(expected, actual);
    }

    private static void AssertDefaultPalette(IReadOnlyList<Color> actual)
    {
        Assert.Equal(ThemeManager.DefaultMatchingBracketPalette.Count, actual.Count);
        for (int i = 0; i < actual.Count; i++)
            Assert.Equal(ThemeManager.DefaultMatchingBracketPalette[i], actual[i]);
    }
}
