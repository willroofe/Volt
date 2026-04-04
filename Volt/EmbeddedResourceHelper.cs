using System.IO;
using System.Reflection;

namespace Volt;

/// <summary>
/// Extracts embedded resources to a target directory on disk.
/// Used by ThemeManager and SyntaxManager to deploy built-in themes/grammars.
/// </summary>
internal static class EmbeddedResourceHelper
{
    /// <summary>
    /// Extracts all embedded resources matching <paramref name="resourcePrefix"/>
    /// (and ending with <paramref name="suffix"/>) to <paramref name="targetDir"/>.
    /// Existing files are always overwritten so that embedded fixes take effect.
    /// </summary>
    public static void ExtractAll(string resourcePrefix, string targetDir, string suffix = ".json")
    {
        Directory.CreateDirectory(targetDir);
        var asm = Assembly.GetExecutingAssembly();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(resourcePrefix) || !name.EndsWith(suffix))
                continue;
            var fileName = name[resourcePrefix.Length..];
            var destPath = Path.Combine(targetDir, fileName);
            using var stream = asm.GetManifestResourceStream(name);
            if (stream == null) continue;
            using var reader = new StreamReader(stream);
            // File.WriteAllText is acceptable here (non-atomic) because these files
            // are re-extracted from embedded resources on every app startup, so a
            // partial write due to a crash is self-healing on next launch.
            File.WriteAllText(destPath, reader.ReadToEnd());
        }
    }
}
