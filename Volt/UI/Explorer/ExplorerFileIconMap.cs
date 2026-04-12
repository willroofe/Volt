using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Windows.Media;

namespace Volt;

/// <summary>
/// Fine-grained extension (and special file names) → Codicon glyph + optional ARGB tint for explorer file rows.
/// Null tint means the icon uses the same brush as the file name (theme text colour).
/// </summary>
internal static class ExplorerFileIconMap
{
    public readonly record struct FileIconSpec(string Glyph, uint? TintArgb);

    private static readonly ConcurrentDictionary<uint, SolidColorBrush> TintBrushCache = new();

    private static readonly Dictionary<string, FileIconSpec> Map = CreateMap();

    private static readonly FileIconSpec DefaultFile = new(Codicons.File, null);

    private static SolidColorBrush GetOrCreateTintBrush(uint argb)
    {
        return TintBrushCache.GetOrAdd(argb, static a =>
        {
            var b = new SolidColorBrush(Color.FromArgb(
                (byte)((a >> 24) & 0xFF),
                (byte)((a >> 16) & 0xFF),
                (byte)((a >> 8) & 0xFF),
                (byte)(a & 0xFF)));
            b.Freeze();
            return b;
        });
    }

    public static Brush TintBrush(uint tintArgb) => GetOrCreateTintBrush(tintArgb);

    public static FileIconSpec Resolve(string? fullPath, string displayName)
    {
        var path = fullPath ?? displayName;
        var fileName = Path.GetFileName(path);
        if (fileName.Length == 0) return DefaultFile;

        var special = MatchSpecialFileName(fileName);
        if (special != null) return special.Value;

        var ext = Path.GetExtension(path);
        if (ext.Length == 0) return DefaultFile;
        ext = ext.TrimStart('.').ToLowerInvariant();

        return Map.TryGetValue(ext, out var mapped) ? mapped : DefaultFile;
    }

    private static FileIconSpec? MatchSpecialFileName(string fileName)
    {
        var lower = fileName.ToLowerInvariant();

        if (lower.EndsWith(".min.js", StringComparison.Ordinal))
            return new FileIconSpec(Codicons.FileCode, 0xFF78909C);
        if (lower.EndsWith(".min.css", StringComparison.Ordinal))
            return new FileIconSpec(Codicons.FileCode, 0xFF78909C);

        if (lower is "dockerfile" or "containerfile" || lower.StartsWith("dockerfile.", StringComparison.Ordinal))
            return new FileIconSpec(Codicons.FileCode, 0xFF2496ED);

        if (lower.StartsWith("docker-compose", StringComparison.Ordinal) &&
            (lower.EndsWith(".yml", StringComparison.Ordinal) || lower.EndsWith(".yaml", StringComparison.Ordinal)))
            return new FileIconSpec(Codicons.Json, 0xFF2496ED);

        if (lower is "makefile" or "gnumakefile" or "bsdmakefile")
            return new FileIconSpec(Codicons.FileCode, 0xFF6D4C41);

        if (lower is "cmakelists.txt" or "cmakecache.txt")
            return new FileIconSpec(Codicons.FileCode, 0xFF064F8C);

        if (lower is "rakefile" or "gemfile" or "podfile")
            return new FileIconSpec(Codicons.Ruby, 0xFFCC342D);

        if (lower == "jenkinsfile")
            return new FileIconSpec(Codicons.FileCode, 0xFFD24939);

        return null;
    }

    private static Dictionary<string, FileIconSpec> CreateMap()
    {
        var m = new Dictionary<string, FileIconSpec>(StringComparer.OrdinalIgnoreCase);
        void add(string ext, FileIconSpec spec) => m[ext] = spec;
        void addMany(IEnumerable<string> exts, FileIconSpec spec)
        {
            foreach (var e in exts) add(e, spec);
        }

        addMany(["zip", "7z", "rar", "tar", "gz", "tgz", "bz2", "xz", "lz", "lzma", "cab", "iso", "dmg", "zst"],
            new FileIconSpec(Codicons.FileZip, 0xFF8B6914));

        addMany(["pdf"], new FileIconSpec(Codicons.FilePdf, 0xFFE53935));

        addMany(["png", "jpg", "jpeg", "gif", "webp", "bmp", "tif", "tiff", "ico", "svg", "heic", "avif", "raw", "psd", "ai", "eps"],
            new FileIconSpec(Codicons.FileMedia, 0xFF7E57C2));

        addMany(["mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus", "aiff", "mid", "midi"],
            new FileIconSpec(Codicons.FileMedia, 0xFF5C6BC0));
        addMany(["mp4", "mkv", "webm", "mov", "avi", "wmv", "m4v", "mpeg", "mpg", "3gp", "flv", "swf"],
            new FileIconSpec(Codicons.FileMedia, 0xFF3949AB));

        addMany(["ttf", "otf", "woff", "woff2", "eot"], new FileIconSpec(Codicons.FileBinary, 0xFF546E7A));
        addMany(["bin", "hex", "obj", "o", "a", "lib", "dll", "exe", "so", "dylib", "pdb", "ilk", "class", "jar", "war", "nupkg", "snupkg"],
            new FileIconSpec(Codicons.FileBinary, 0xFF607D8B));

        addMany(["json", "jsonc", "webmanifest"], new FileIconSpec(Codicons.Json, 0xFFF9A825));
        addMany(["yaml", "yml"], new FileIconSpec(Codicons.Json, 0xFFEC407A));
        addMany(["xml", "plist", "xsd", "xsl", "xslt", "rss", "atom", "svgz"], new FileIconSpec(Codicons.FileCode, 0xFFFB8C00));
        addMany(["toml"], new FileIconSpec(Codicons.FileCode, 0xFF8D6E63));
        addMany(["ini", "cfg", "conf", "config", "properties", "env", "editorconfig", "gitattributes", "gitmodules", "npmrc", "nvmrc"],
            new FileIconSpec(Codicons.FileText, 0xFF78909C));
        addMany(["csv", "tsv"], new FileIconSpec(Codicons.Table, 0xFF43A047));

        addMany(["md", "mdx", "markdown", "rst", "adoc", "tex", "bib", "org"], new FileIconSpec(Codicons.Markdown, 0xFF42A5F5));

        addMany(["html", "htm", "xhtml", "shtml"], new FileIconSpec(Codicons.FileCode, 0xFFE64A19));
        addMany(["css", "scss", "sass", "less", "styl"], new FileIconSpec(Codicons.FileCode, 0xFF1565C0));
        addMany(["js", "mjs", "cjs", "jsx"], new FileIconSpec(Codicons.FileCode, 0xFFF9A825));
        addMany(["ts", "tsx", "mts", "cts"], new FileIconSpec(Codicons.FileCode, 0xFF3178C6));
        addMany(["vue", "svelte"], new FileIconSpec(Codicons.FileCode, 0xFF43A047));

        addMany(["cs", "csx"], new FileIconSpec(Codicons.FileCode, 0xFF239120));
        addMany(["fs", "fsi", "fsx"], new FileIconSpec(Codicons.FileCode, 0xFF378BBB));
        addMany(["vb", "vbs"], new FileIconSpec(Codicons.FileCode, 0xFF5C6BC0));
        addMany(["csproj", "vbproj", "fsproj", "props", "targets", "sln", "slnx", "xaml", "axaml", "resx", "settings"],
            new FileIconSpec(Codicons.FileCode, 0xFF6A1B9A));
        addMany(["java", "gradle", "groovy"], new FileIconSpec(Codicons.FileCode, 0xFFE65100));
        addMany(["kt", "kts"], new FileIconSpec(Codicons.FileCode, 0xFF7E57C2));
        addMany(["scala", "sbt"], new FileIconSpec(Codicons.FileCode, 0xFFD84315));
        addMany(["c", "h"], new FileIconSpec(Codicons.FileCode, 0xFF546E7A));
        addMany(["cc", "cxx", "cpp", "hpp", "hh", "hxx", "inl", "idl", "def"], new FileIconSpec(Codicons.FileCode, 0xFF3949AB));
        addMany(["go"], new FileIconSpec(Codicons.FileCode, 0xFF00ACC1));
        addMany(["rs"], new FileIconSpec(Codicons.FileCode, 0xFFD84315));
        addMany(["swift"], new FileIconSpec(Codicons.FileCode, 0xFFFF6D00));
        addMany(["m", "mm"], new FileIconSpec(Codicons.FileCode, 0xFF5C6BC0));

        addMany(["py", "pyi", "pyw", "pyx"], new FileIconSpec(Codicons.Python, 0xFF0277BD));
        addMany(["rb", "erb", "gemspec"], new FileIconSpec(Codicons.Ruby, 0xFFCC342D));
        addMany(["pl", "pm", "pod"], new FileIconSpec(Codicons.FileCode, 0xFF5E35B1));
        add("t", new FileIconSpec(Codicons.FileCode, 0xFF5E35B1));
        addMany(["php", "phtml"], new FileIconSpec(Codicons.FileCode, 0xFF6A1B9A));
        addMany(["lua"], new FileIconSpec(Codicons.FileCode, 0xFF00838F));
        addMany(["r"], new FileIconSpec(Codicons.FileCode, 0xFF1565C0));
        addMany(["jl"], new FileIconSpec(Codicons.FileCode, 0xFF8E24AA));
        addMany(["dart"], new FileIconSpec(Codicons.FileCode, 0xFF00897B));
        addMany(["ex", "exs"], new FileIconSpec(Codicons.FileCode, 0xFF6A1B9A));
        addMany(["erl", "hrl"], new FileIconSpec(Codicons.FileCode, 0xFF5C6BC0));
        addMany(["clj", "cljs", "edn"], new FileIconSpec(Codicons.FileCode, 0xFF43A047));
        addMany(["hs", "lhs"], new FileIconSpec(Codicons.FileCode, 0xFF5C6BC0));
        addMany(["elm"], new FileIconSpec(Codicons.FileCode, 0xFF00897B));

        addMany(["sh", "bash", "zsh", "fish", "ksh", "ps1", "psm1", "psd1", "bat", "cmd", "nu"],
            new FileIconSpec(Codicons.Terminal, 0xFF558B2F));
        addMany(["dockerignore"], new FileIconSpec(Codicons.FileText, 0xFF78909C));

        addMany(["sql", "sqlite", "db", "duckdb"], new FileIconSpec(Codicons.Database, 0xFF00897B));
        addMany(["graphql", "gql"], new FileIconSpec(Codicons.FileCode, 0xFFE91E63));
        addMany(["proto"], new FileIconSpec(Codicons.FileCode, 0xFF5C6BC0));
        addMany(["wat", "wasm"], new FileIconSpec(Codicons.FileBinary, 0xFF6A1B9A));

        addMany(["lock"], new FileIconSpec(Codicons.Package, 0xFF78909C));
        addMany(["tf", "tfvars", "hcl"], new FileIconSpec(Codicons.FileCode, 0xFF5E35B1));

        addMany(["gitignore", "gitkeep", "gitconfig", "git-blame-ignore-revs"], new FileIconSpec(Codicons.Github, 0xFFF4511E));
        addMany(["patch", "diff", "rej"], new FileIconSpec(Codicons.FileText, 0xFF78909C));

        addMany(["log", "txt", "text", "nfo"], new FileIconSpec(Codicons.FileText, null));
        addMany(["map"], new FileIconSpec(Codicons.FileText, 0xFF78909C));

        addMany(["ipynb"], new FileIconSpec(Codicons.Notebook, 0xFFFF6F00));

        addMany(["pem", "crt", "cer", "key", "pub", "asc", "gpg", "pfx", "p12"],
            new FileIconSpec(Codicons.Lock, 0xFF6D4C41));

        addMany(["pbxproj", "xcconfig", "storyboard", "xib"], new FileIconSpec(Codicons.FileCode, 0xFF3949AB));
        addMany(["pr", "pmc"], new FileIconSpec(Codicons.FileCode, 0xFF5E35B1));

        addMany(["rules"], new FileIconSpec(Codicons.FileCode, 0xFF00897B));
        addMany(["cmake"], new FileIconSpec(Codicons.FileCode, 0xFF064F8C));

        return m;
    }
}
