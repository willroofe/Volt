using System.IO;
using System.Text.Json;

namespace TextEdit;

public class ApplicationSettings
{
    public string ColorTheme { get; set; } = "Dark";
}

public class FontSettings
{
    public string? Family { get; set; }
    public double Size { get; set; } = 14;
    public string Weight { get; set; } = "Normal";
    public double LineHeight { get; set; } = 1.0;
}

public class CaretSettings
{
    public bool BlockCaret { get; set; }
    public int BlinkMs { get; set; } = 500;
}

public class FindSettings
{
    public string BarPosition { get; set; } = "Bottom";
}

public class EditorSettings
{
    public int TabSize { get; set; } = 4;
    public FontSettings Font { get; set; } = new();
    public CaretSettings Caret { get; set; } = new();
    public FindSettings Find { get; set; } = new();
}

public class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "TextEdit", "settings.json");

    public ApplicationSettings Application { get; set; } = new();
    public EditorSettings Editor { get; set; } = new();
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    public static readonly string[] FindBarPositionOptions = ["Top", "Bottom"];
    public static readonly double[] FontSizeOptions = [8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 36];
    public static readonly int[] TabSizeOptions = [2, 4, 8];
    public static readonly string[] FontWeightOptions = ["Thin", "ExtraLight", "Light", "Normal", "Medium", "SemiBold", "Bold", "ExtraBold", "Black"];
    public static readonly double[] LineHeightOptions = [1.0, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.8, 2.0];

    public void Save()
    {
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
        FileHelper.AtomicWriteText(SettingsPath, json, System.Text.Encoding.UTF8);
    }

    public static AppSettings Load()
    {
        if (!File.Exists(SettingsPath))
            return new AppSettings();

        try
        {
            var json = File.ReadAllText(SettingsPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            // Migrate from old flat format if needed
            if (root.TryGetProperty("TabSize", out _))
                return MigrateOldFormat(root);

            return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load settings: {ex.Message}");
            return new AppSettings();
        }
    }

    private static AppSettings MigrateOldFormat(JsonElement root)
    {
        var s = new AppSettings();

        if (root.TryGetProperty("ColorTheme", out var ct))
            s.Application.ColorTheme = ct.GetString() ?? "Dark";

        if (root.TryGetProperty("TabSize", out var ts))
            s.Editor.TabSize = ts.GetInt32();
        if (root.TryGetProperty("FontFamily", out var ff))
            s.Editor.Font.Family = ff.GetString();
        if (root.TryGetProperty("FontSize", out var fs))
            s.Editor.Font.Size = fs.GetDouble();
        if (root.TryGetProperty("FontWeight", out var fw))
            s.Editor.Font.Weight = fw.GetString() ?? "Normal";
        if (root.TryGetProperty("BlockCaret", out var bc))
            s.Editor.Caret.BlockCaret = bc.GetBoolean();
        if (root.TryGetProperty("CaretBlinkMs", out var cb))
            s.Editor.Caret.BlinkMs = cb.GetInt32();
        if (root.TryGetProperty("FindBarPosition", out var fp))
            s.Editor.Find.BarPosition = fp.GetString() ?? "Bottom";

        if (root.TryGetProperty("WindowLeft", out var wl))
            s.WindowLeft = wl.GetDouble();
        if (root.TryGetProperty("WindowTop", out var wt))
            s.WindowTop = wt.GetDouble();
        if (root.TryGetProperty("WindowWidth", out var ww))
            s.WindowWidth = ww.GetDouble();
        if (root.TryGetProperty("WindowHeight", out var wh))
            s.WindowHeight = wh.GetDouble();
        if (root.TryGetProperty("WindowMaximized", out var wm))
            s.WindowMaximized = wm.GetBoolean();

        // Side effect: saves in new format so migration only happens once.
        // Callers of Load() should be aware this may write to disk.
        s.Save();
        return s;
    }
}
