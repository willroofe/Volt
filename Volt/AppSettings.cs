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
    public string? OpenFolderPath { get; set; }
    public List<string> ExpandedPaths { get; set; } = [];
}

public class EditorSettings
{
    public int TabSize { get; set; } = 4;
    public bool WordWrap { get; set; }
    public FontSettings Font { get; set; } = new();
    public CaretSettings Caret { get; set; } = new();
    public FindSettings Find { get; set; } = new();
    public ExplorerSettings Explorer { get; set; } = new();
    public List<PanelSlotConfig> PanelLayouts { get; set; } = [];
    public List<RegionState> OpenRegions { get; set; } = [];
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
    public static readonly string SessionDir = AppPaths.SessionDir;

    public List<SessionTab> Tabs { get; set; } = [];
    public int ActiveTabIndex { get; set; }

    public static string TabContentPath(int index) => Path.Combine(SessionDir, $"tab-{index}.txt");

    public static string FolderSessionDir(string folderPath)
    {
        // Use a hash of the folder path to create a unique subdirectory
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(folderPath.ToLowerInvariant())))[..16];
        return Path.Combine(SessionDir, "folders", hash);
    }

    public static string FolderTabContentPath(string folderPath, int index)
        => Path.Combine(FolderSessionDir(folderPath), $"tab-{index}.txt");

    public void SaveTabContent(int index, string content)
    {
        Directory.CreateDirectory(SessionDir);
        FileHelper.AtomicWriteText(TabContentPath(index), content, System.Text.Encoding.UTF8);
    }

    public void SaveFolderTabContent(string folderPath, int index, string content)
    {
        var dir = FolderSessionDir(folderPath);
        Directory.CreateDirectory(dir);
        FileHelper.AtomicWriteText(FolderTabContentPath(folderPath, index), content, System.Text.Encoding.UTF8);
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

    public static string? LoadFolderTabContent(string folderPath, int index)
    {
        try
        {
            var path = FolderTabContentPath(folderPath, index);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to load folder session tab {index}: {ex.Message}");
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

    public static void ClearFolderSessionDir(string folderPath)
    {
        try
        {
            var dir = FolderSessionDir(folderPath);
            if (!Directory.Exists(dir)) return;
            foreach (var file in Directory.GetFiles(dir))
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
    private static readonly string SettingsPath = AppPaths.SettingsPath;

    public int SettingsVersion { get; set; } = 1;
    public ApplicationSettings Application { get; set; } = new();
    public EditorSettings Editor { get; set; } = new();
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public string? LastOpenProjectPath { get; set; }
    public SessionSettings Session { get; set; } = new();
    public Dictionary<string, SessionSettings> FolderSessions { get; set; } = new();

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
            // Preserve the corrupted file for debugging
            try
            {
                var backupPath = SettingsPath + ".bak";
                File.Copy(SettingsPath, backupPath, overwrite: true);
                System.Diagnostics.Debug.WriteLine($"Corrupted settings backed up to {backupPath}");
            }
            catch { /* best effort */ }
            return new AppSettings();
        }
    }

    private static AppSettings MigrateOldFormat(JsonElement root)
    {
        var settings = new AppSettings();

        if (root.TryGetProperty("ColorTheme", out var ct))
            settings.Application.ColorTheme = ct.GetString() ?? "Dark";

        if (root.TryGetProperty("TabSize", out var ts))
            settings.Editor.TabSize = ts.GetInt32();
        if (root.TryGetProperty("FontFamily", out var ff))
            settings.Editor.Font.Family = ff.GetString();
        if (root.TryGetProperty("FontSize", out var fs))
            settings.Editor.Font.Size = fs.GetDouble();
        if (root.TryGetProperty("FontWeight", out var fw))
            settings.Editor.Font.Weight = fw.GetString() ?? "Normal";
        if (root.TryGetProperty("BlockCaret", out var bc))
            settings.Editor.Caret.BlockCaret = bc.GetBoolean();
        if (root.TryGetProperty("CaretBlinkMs", out var cb))
            settings.Editor.Caret.BlinkMs = cb.GetInt32();
        if (root.TryGetProperty("FindBarPosition", out var fp))
            settings.Editor.Find.BarPosition = fp.GetString() ?? "Bottom";

        if (root.TryGetProperty("WindowLeft", out var wl))
            settings.WindowLeft = wl.GetDouble();
        if (root.TryGetProperty("WindowTop", out var wt))
            settings.WindowTop = wt.GetDouble();
        if (root.TryGetProperty("WindowWidth", out var ww))
            settings.WindowWidth = ww.GetDouble();
        if (root.TryGetProperty("WindowHeight", out var wh))
            settings.WindowHeight = wh.GetDouble();
        if (root.TryGetProperty("WindowMaximized", out var wm))
            settings.WindowMaximized = wm.GetBoolean();

        // Intentional side effect: saves in new format so migration only happens once.
        // If Save() fails, the migrated settings are still returned in-memory and the
        // old-format file will trigger re-migration on next launch (acceptable).
        try { settings.Save(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to save migrated settings: {ex.Message}");
        }
        return settings;
    }
}
