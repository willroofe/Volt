using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;

namespace TextEdit;

public enum FileTreeItemKind
{
    File,
    Directory,
    VirtualFolder,
    ProjectRoot
}

public class FileTreeItem : INotifyPropertyChanged
{
    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public FileTreeItemKind Kind { get; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            if (value && _hasPlaceholder)
                LoadChildren();
        }
    }

    public ObservableCollection<FileTreeItem> Children { get; } = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _hasPlaceholder;

    private static readonly HashSet<string> IgnoredDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", "bin", "obj", ".git", "__pycache__", ".vs", ".idea"
    };

    public FileTreeItem(string fullPath, bool isDirectory)
    {
        FullPath = fullPath;
        Name = Path.GetFileName(fullPath);
        IsDirectory = isDirectory;
        Kind = isDirectory ? FileTreeItemKind.Directory : FileTreeItemKind.File;

        if (isDirectory)
        {
            // Add dummy placeholder so the expand arrow appears
            Children.Add(CreatePlaceholder());
            _hasPlaceholder = true;
        }
    }

    private FileTreeItem(string fullPath, string name, bool isDirectory, FileTreeItemKind kind)
    {
        FullPath = fullPath;
        Name = name;
        IsDirectory = isDirectory;
        Kind = kind;
    }

    private static FileTreeItem CreatePlaceholder()
    {
        return new FileTreeItem("", "", false, FileTreeItemKind.File);
    }

    private void LoadChildren()
    {
        _hasPlaceholder = false;
        Children.Clear();

        try
        {
            var dirs = Directory.GetDirectories(FullPath)
                .Where(d => !IsIgnored(d))
                .OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase);

            var files = Directory.GetFiles(FullPath)
                .Where(f => !IsHidden(f))
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase);

            foreach (var dir in dirs)
                Children.Add(new FileTreeItem(dir, true));
            foreach (var file in files)
                Children.Add(new FileTreeItem(file, false));
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private static bool IsIgnored(string dirPath)
    {
        var name = Path.GetFileName(dirPath);
        return name.StartsWith('.') || IgnoredDirectories.Contains(name);
    }

    private static bool IsHidden(string filePath)
    {
        return Path.GetFileName(filePath).StartsWith('.');
    }

    public static ObservableCollection<FileTreeItem> LoadRoot(string folderPath)
    {
        var root = new FileTreeItem(folderPath, true);
        root.IsExpanded = true;
        return root.Children;
    }

    public static FileTreeItem CreateRootItem(string folderPath)
    {
        return new FileTreeItem(folderPath, true);
    }

    public static FileTreeItem CreateProjectRoot(string projectName)
    {
        return new FileTreeItem("", projectName, false, FileTreeItemKind.ProjectRoot);
    }

    public static FileTreeItem CreateVirtualFolder(string name)
    {
        return new FileTreeItem("", name, false, FileTreeItemKind.VirtualFolder);
    }
}
