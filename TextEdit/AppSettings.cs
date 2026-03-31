using System.IO;
using System.Text.Json;

namespace TextEdit;

public class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TextEdit", "settings.json");

    public int TabSize { get; set; } = 4;
    public bool BlockCaret { get; set; }
    public int CaretBlinkMs { get; set; } = 500;
    public string? FontFamily { get; set; }
    public double FontSize { get; set; } = 14;
    public string FontWeight { get; set; } = "Normal";
    public string ColorTheme { get; set; } = "Default Dark";
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    // Shared option arrays used by both SettingsWindow and Command Palette
    public static readonly double[] FontSizeOptions = [8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 36];
    public static readonly int[] TabSizeOptions = [2, 4, 8];
    public static readonly string[] FontWeightOptions = ["Thin", "ExtraLight", "Light", "Normal", "Medium", "SemiBold", "Bold", "ExtraBold", "Black"];

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsPath, json);
    }

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(SettingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }
}
