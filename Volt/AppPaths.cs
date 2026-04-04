using System.IO;

namespace Volt;

/// <summary>
/// Centralized application directory paths under %AppData%/Volt/.
/// </summary>
internal static class AppPaths
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Volt");

    public static readonly string ThemesDir = Path.Combine(AppDataDir, "Themes");
    public static readonly string GrammarsDir = Path.Combine(AppDataDir, "Grammars");
    public static readonly string SessionDir = Path.Combine(AppDataDir, "Session");
    public static readonly string SettingsPath = Path.Combine(AppDataDir, "settings.json");
}
