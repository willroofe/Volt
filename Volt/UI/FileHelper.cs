using System.Diagnostics;
using System.IO;
using System.Text;

namespace Volt;

/// <summary>
/// File I/O utilities: atomic writes, encoding detection, and file-type display names.
/// </summary>
internal static class FileHelper
{
    public static readonly string[] SaveFilters =
    [
        "Text Files (*.txt)|*.txt",
        "Perl Files (*.pl)|*.pl",
        "JSON Files (*.json)|*.json",
        "Markdown Files (*.md)|*.md",
        "All Files (*.*)|*.*"
    ];

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
        [".volt-workspace"] = "Volt Workspace Definition",
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
            FileShare.ReadWrite | FileShare.Delete, bufferSize: 1 << 20);
        // Read raw bytes in one shot to avoid StreamReader's StringBuilder growth
        int length = (int)stream.Length;
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        // Skip BOM if present
        var preamble = encoding.Preamble;
        if (preamble.Length > 0 && bytes.AsSpan().StartsWith(preamble))
            return encoding.GetString(bytes, preamble.Length, length - preamble.Length);
        return encoding.GetString(bytes);
    }

    /// <summary>
    /// Reads text from a file starting at the given byte offset.
    /// Returns the new text and the new file length.
    /// </summary>
    public static (string text, long newSize) ReadTail(string path, Encoding encoding, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var newSize = stream.Length;
        if (offset >= newSize) return ("", newSize);
        stream.Seek(offset, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, encoding);
        return (reader.ReadToEnd(), newSize);
    }

    private const int VerifySnippetSize = 256;

    /// <summary>
    /// Reads the last <see cref="VerifySnippetSize"/> bytes of a file, used to later
    /// verify that the beginning of the file hasn't changed (append-only detection).
    /// </summary>
    public static byte[] ReadTailVerifyBytes(string path, long fileSize)
    {
        int len = (int)Math.Min(VerifySnippetSize, fileSize);
        return ReadBytesAt(path, fileSize - len, len);
    }

    /// <summary>
    /// Checks whether the bytes at the same position in the (possibly grown) file
    /// still match the previously captured tail snippet — i.e. the old content hasn't
    /// been modified, only appended to.
    /// </summary>
    public static bool VerifyAppendOnly(string path, long previousSize, byte[] previousTailBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length < previousSize) return false;

        int len = previousTailBytes.Length;
        var buf = ReadBytesAt(stream, previousSize - len, len);
        return buf.AsSpan().SequenceEqual(previousTailBytes);
    }

    private static byte[] ReadBytesAt(string path, long offset, int count)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        return ReadBytesAt(stream, offset, count);
    }

    private static byte[] ReadBytesAt(FileStream stream, long offset, int count)
    {
        stream.Seek(offset, SeekOrigin.Begin);
        var buf = new byte[count];
        int totalRead = 0;
        while (totalRead < count)
        {
            int n = stream.Read(buf, totalRead, count - totalRead);
            if (n == 0) break;
            totalRead += n;
        }
        return buf.AsSpan(0, totalRead).ToArray();
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

    /// <summary>Opens Windows File Explorer at the folder, or with the file selected.</summary>
    public static void RevealInFileExplorer(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        try
        {
            // Use /select for both files and folders so Explorer shows the parent with the item highlighted,
            // instead of navigating inside a folder.
            if (File.Exists(path) || Directory.Exists(path))
            {
                var full = Path.GetFullPath(path);
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = "/select,\"" + full + "\"",
                    UseShellExecute = true
                });
            }
        }
        catch
        {
            // Ignore launch failures (missing path, policy, etc.)
        }
    }
}
