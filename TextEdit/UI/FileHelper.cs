using System.IO;
using System.Text;

namespace TextEdit;

/// <summary>
/// File I/O utilities: atomic writes, encoding detection, and file-type display names.
/// </summary>
internal static class FileHelper
{
    public static readonly string[] SaveFilters =
        ["Text Files (*.txt)|*.txt", "Perl Files (*.pl)|*.pl", "All Files (*.*)|*.*"];

    private static readonly Dictionary<string, string> FileTypeNames = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "Plain Text",
        [".cs"] = "C# Source",
        [".pl"] = "Perl Script", [".cgi"] = "Perl Script",
        [".py"] = "Python Script",
        [".js"] = "JavaScript",
        [".ts"] = "TypeScript",
        [".json"] = "JSON",
        [".xml"] = "XML Document",
        [".xaml"] = "XAML Document",
        [".html"] = "HTML Document", [".htm"] = "HTML Document",
        [".css"] = "CSS Stylesheet",
        [".md"] = "Markdown",
        [".yml"] = "YAML", [".yaml"] = "YAML",
        [".sql"] = "SQL",
        [".sh"] = "Shell Script", [".bash"] = "Shell Script",
        [".bat"] = "Batch File", [".cmd"] = "Batch File",
        [".ps1"] = "PowerShell Script",
        [".cpp"] = "C++ Source", [".cc"] = "C++ Source", [".cxx"] = "C++ Source",
        [".c"] = "C Source",
        [".h"] = "C/C++ Header",
        [".java"] = "Java Source",
        [".rb"] = "Ruby Script",
        [".go"] = "Go Source",
        [".rs"] = "Rust Source",
        [".ini"] = "Configuration File", [".cfg"] = "Configuration File",
        [".log"] = "Log File",
    };

    public static string GetFileTypeName(string extension)
        => FileTypeNames.GetValueOrDefault(extension, "Plain Text");

    public static void AtomicWriteText(string path, string content, Encoding encoding)
    {
        var dir = Path.GetDirectoryName(path)!;
        var tempPath = Path.Combine(dir, Path.GetRandomFileName());
        try
        {
            File.WriteAllText(tempPath, content, encoding);
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tempPath, path, overwrite: true);
                    return;
                }
                catch (UnauthorizedAccessException) when (attempt < 3)
                {
                    Thread.Sleep(50);
                }
                catch (IOException) when (attempt < 3)
                {
                    Thread.Sleep(50);
                }
            }
        }
        catch
        {
            try { File.Delete(tempPath); } catch { }
            throw;
        }
    }

    /// <summary>
    /// Reads all text from a file using FileShare.ReadWrite | FileShare.Delete so that
    /// files locked by other processes (e.g. log files being written by a service) can
    /// still be opened.
    /// </summary>
    public static string ReadAllText(string path, Encoding encoding)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, encoding);
        return reader.ReadToEnd();
    }

    public static Encoding DetectEncoding(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var bom = new byte[4];
        int read = stream.Read(bom, 0, 4);

        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            return new UTF8Encoding(true);
        // Check 4-byte BOMs before 2-byte to avoid misidentifying UTF-32 LE as UTF-16 LE
        if (read >= 4 && bom[0] == 0xFF && bom[1] == 0xFE && bom[2] == 0 && bom[3] == 0)
            return new UTF32Encoding(false, true);
        if (read >= 4 && bom[0] == 0 && bom[1] == 0 && bom[2] == 0xFE && bom[3] == 0xFF)
            return new UTF32Encoding(true, true);
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
            return new UnicodeEncoding(false, true);
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
            return new UnicodeEncoding(true, true);

        return new UTF8Encoding(false);
    }
}
