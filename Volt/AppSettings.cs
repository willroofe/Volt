using System.IO;
using System.Text.Json;

namespace Volt;

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

public class ExplorerSettings
{
    public string PanelSide { get; set; } = "Left";
    public double PanelWidth { get; set; } = 250;
    public bool PanelVisible { get; set; } = false;
    public string? OpenFolderPath { get; set; }
}

public class EditorSettings
{
    public int TabSize { get; set; } = 4;
    public FontSettings Font { get; set; } = new();
    public CaretSettings Caret { get; set; } = new();
    public FindSettings Find { get; set; } = new();
    public ExplorerSettings Explorer { get; set; } = new();
}

public class SessionTab
{
    public string? FilePath { get; set; }
    public bool IsDirty { get; set; }
    public int CaretLine { get; set; }
    public int CaretCol { get; set; }
    public double ScrollVertical { get; set; }
    public double ScrollHorizontal { get; set; }
}

public class SessionSettings
{
    public static readonly string SessionDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Volt", "Session");

    public List<SessionTab> Tabs { get; set; } = [];
    public int ActiveTabIndex { get; set; }

    public static string TabContentPath(int index) => Path.Combine(SessionDir, $"tab-{index}.txt");

    public void SaveTabContent(int index, string content)
    {
        Directory.CreateDirectory(SessionDir);
        FileHelper.AtomicWriteText(TabContentPath(index), content, System.Text.Encoding.UTF8);
    }

    public static string? LoadTabContent(int index)
    {
        try
        {
            var path = TabContentPath(index);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load session tab {index}: {ex.Message}");
            return null;
        }
    }

    public static void ClearSessionDir()
    {
        try
        {
            if (!Directory.Exists(SessionDir)) return;
            foreach (var file in Directory.GetFiles(SessionDir))
            {
                try { File.Delete(file); }
                catch { /* best effort */ }
            }
        }
        catch { /* best effort */ }
    }
}

public class AppSettings
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Volt", "settings.json");

    public ApplicationSettings Application { get; set; } = new();
    public EditorSettings Editor { get; set; } = new();
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public string? LastOpenProjectPath { get; set; }
    public SessionSettings Session { get; set; } = new();

    public static readonly string[] FindBarPositionOptions = ["Top", "Bottom"];
    public static readonly double[] FontSizeOptions = [8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 36];
    public static readonly int[] TabSizeOptions = [2, 4, 8];
    public static readonly string[] FontWeightOptions = ["Thin", "ExtraLight", "Light", "Normal", "Medium", "SemiBold", "Bold", "ExtraBold", "Black"];
    public static readonly double[] LineHeightOptions = [1.0, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.8, 2.0];
    public static readonly string[] PanelSideOptions = ["Left", "Right"];

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
