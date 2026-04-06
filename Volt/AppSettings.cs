using System.IO;
using System.Text.Json;
using System.Windows.Threading;

namespace Volt;

public enum RecentItemKind { File, Folder, Workspace }

public class RecentItem
{
    public string Path { get; set; } = "";
    public RecentItemKind Kind { get; set; }
}

public class ApplicationSettings
{
    public string ColorTheme { get; set; } = "Volt Dark";
    public string CommandPalettePosition { get; set; } = "Top";
    public List<RecentItem> RecentItems { get; set; } = [];
    public List<RecentItem> RecentHistory { get; set; } = [];
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
    public bool SeedWithSelection { get; set; } = true;
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
    public bool WordWrapAtWords { get; set; } = true;
    public bool WordWrapIndent { get; set; } = true;
    public bool FixedWidthTabs { get; set; }
    public bool IndentGuides { get; set; } = true;
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
    public List<SessionTab> Tabs { get; set; } = [];
    public int ActiveTabIndex { get; set; }

    public static string TabContentPath(int index) => Path.Combine(AppPaths.SessionDir, $"tab-{index}.txt");

    public static string FolderSessionDir(string folderPath)
    {
        // Use a hash of the folder path to create a unique subdirectory
        var hash = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(folderPath.ToLowerInvariant())))[..16];
        return Path.Combine(AppPaths.SessionDir, "folders", hash);
    }

    public static string FolderTabContentPath(string folderPath, int index)
        => Path.Combine(FolderSessionDir(folderPath), $"tab-{index}.txt");

    public void SaveTabContent(int index, string content)
    {
        Directory.CreateDirectory(AppPaths.SessionDir);
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
        catch (Exception) { return null; }
    }

    public static string? LoadFolderTabContent(string folderPath, int index)
    {
        try
        {
            var path = FolderTabContentPath(folderPath, index);
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (Exception) { return null; }
    }

    public static void ClearSessionDir() => SafeDeleteFiles(AppPaths.SessionDir);
    public static void ClearFolderSessionDir(string folderPath) => SafeDeleteFiles(FolderSessionDir(folderPath));

    private static void SafeDeleteFiles(string dir)
    {
        try
        {
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

public class KeyBindingSettings
{
    public Dictionary<string, string> CustomBindings { get; set; } = new();
}

public class AppSettings
{
    private static readonly string SettingsPath = AppPaths.SettingsPath;
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };
    private DispatcherTimer? _saveTimer;

    public int SettingsVersion { get; set; } = 1;
    public ApplicationSettings Application { get; set; } = new();
    public EditorSettings Editor { get; set; } = new();
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
    public string? LastOpenWorkspacePath { get; set; }
    public List<string>? UnsavedWorkspaceFolders { get; set; }
    public WorkspaceSession? UnsavedWorkspaceSession { get; set; }
    public KeyBindingSettings KeyBindings { get; set; } = new();
    public SessionSettings Session { get; set; } = new();
    public Dictionary<string, SessionSettings> FolderSessions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, List<string>> FolderExpandedPaths { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    private const int MaxRecentMenuItems = 10;
    private const int MaxRecentHistory = 200;

    public void AddRecentItem(string path, RecentItemKind kind)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        AddToList(Application.RecentItems, fullPath, kind, MaxRecentMenuItems);
        AddToList(Application.RecentHistory, fullPath, kind, MaxRecentHistory);
    }

    private static void AddToList(List<RecentItem> list, string fullPath, RecentItemKind kind, int max)
    {
        list.RemoveAll(r =>
            string.Equals(r.Path, fullPath, StringComparison.OrdinalIgnoreCase) && r.Kind == kind);
        list.Insert(0, new RecentItem { Path = fullPath, Kind = kind });
        if (list.Count > max)
            list.RemoveRange(max, list.Count - max);
    }

    public static readonly string[] FindBarPositionOptions = ["Top", "Bottom"];
    public static readonly string[] CommandPalettePositionOptions = ["Top", "Center"];
    public static readonly double[] FontSizeOptions = [8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 36];
    public static readonly int[] TabSizeOptions = [2, 4, 8];
    public static readonly string[] FontWeightOptions = ["Thin", "ExtraLight", "Light", "Normal", "Medium", "SemiBold", "Bold", "ExtraBold", "Black"];
    public static readonly double[] LineHeightOptions = [1.0, 1.1, 1.2, 1.3, 1.4, 1.5, 1.6, 1.8, 2.0];

    public void Save()
    {
        _saveTimer?.Stop();
        var dir = Path.GetDirectoryName(SettingsPath)!;
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(this, SerializerOptions);
        FileHelper.AtomicWriteText(SettingsPath, json, System.Text.Encoding.UTF8);
    }

    /// <summary>
    /// Debounced save — batches rapid changes into a single disk write after 1 second of inactivity.
    /// Use for high-frequency events (panel resize, layout changes). For one-shot actions, use Save().
    /// </summary>
    public void ScheduleSave()
    {
        if (_saveTimer == null)
        {
            _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _saveTimer.Tick += (_, _) =>
            {
                _saveTimer.Stop();
                Save();
            };
        }
        _saveTimer.Stop();
        _saveTimer.Start();
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
        catch (Exception)
        {
            // Preserve the corrupted file for debugging
            try
            {
                File.Copy(SettingsPath, SettingsPath + ".bak", overwrite: true);
            }
            catch { /* best effort */ }
            return new AppSettings();
        }
    }

    private static AppSettings MigrateOldFormat(JsonElement root)
    {
        var settings = new AppSettings();

        if (root.TryGetProperty("ColorTheme", out var ct))
            settings.Application.ColorTheme = ct.GetString() ?? "Volt Dark";

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
        catch (Exception) { }
        return settings;
    }
}
