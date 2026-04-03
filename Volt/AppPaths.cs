using System.IO;

namespace Volt;

/// <summary>
/// Centralized application directory paths under %AppData%/Volt/.
/// </summary>
internal static class AppPaths
{
    private static readonly string AppDataDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Volt");

    public static string ThemesDir => Path.Combine(AppDataDir, "Themes");
    public static string GrammarsDir => Path.Combine(AppDataDir, "Grammars");
    public static string SessionDir => Path.Combine(AppDataDir, "Session");
    public static string SettingsPath => Path.Combine(AppDataDir, "settings.json");
}
