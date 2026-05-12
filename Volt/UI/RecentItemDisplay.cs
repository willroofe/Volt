using System.IO;

namespace Volt;

internal static class RecentItemDisplay
{
    public static string GetMenuLabel(RecentItem recent) => FormatLabel(recent.Path, recent.Kind, includeKind: false);

    public static string GetPaletteLabel(RecentItem recent) => FormatLabel(recent.Path, recent.Kind, includeKind: true);

    public static string GetIconGlyph(RecentItemKind kind) => kind switch
    {
        RecentItemKind.Folder => Codicons.FolderOpened,
        RecentItemKind.Workspace => Codicons.Project,
        _ => Codicons.File
    };

    private static string FormatLabel(string path, RecentItemKind kind, bool includeKind)
    {
        string name = GetDisplayName(path, kind);
        string? directory = GetDisplayDirectory(path);

        if (includeKind && kind != RecentItemKind.File)
            name += " (" + kind + ")";

        return string.IsNullOrEmpty(directory)
            ? name
            : name + " - " + directory;
    }

    private static string GetDisplayName(string path, RecentItemKind kind)
    {
        if (TryGetRootPath(path, out string rootPath))
            return rootPath;

        string displayPath = TrimTrailingSeparators(path);
        return kind == RecentItemKind.Workspace
            ? Path.GetFileNameWithoutExtension(displayPath)
            : Path.GetFileName(displayPath);
    }

    private static string? GetDisplayDirectory(string path)
    {
        if (TryGetRootPath(path, out _))
            return null;

        return Path.GetDirectoryName(TrimTrailingSeparators(path));
    }

    private static bool TryGetRootPath(string path, out string rootPath)
    {
        rootPath = Path.GetPathRoot(path) ?? "";
        if (rootPath.Length == 0)
            return false;

        return string.Equals(
            TrimTrailingSeparators(path),
            TrimTrailingSeparators(rootPath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string TrimTrailingSeparators(string path)
    {
        return path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
