using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows.Threading;

namespace Volt;

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

    /// <summary>
    /// Raised when this item's children have been refreshed due to a filesystem change.
    /// Bubbles up from any descendant. The sender is the item whose children changed.
    /// </summary>
    public event Action<FileTreeItem>? TreeChanged;

    private bool _hasPlaceholder;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _refreshTimer;

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

    private async void LoadChildren()
    {
        try
        {
            var (dirs, files) = await Task.Run(() =>
            {
                var d = Directory.GetDirectories(FullPath)
                    .Where(dp => !IsIgnored(dp))
                    .OrderBy(dp => Path.GetFileName(dp), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var f = Directory.GetFiles(FullPath)
                    .Where(fp => !IsHidden(fp))
                    .OrderBy(fp => Path.GetFileName(fp), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return (d, f);
            });

            _hasPlaceholder = false;
            Children.Clear();

            foreach (var dir in dirs)
            {
                var child = new FileTreeItem(dir, true);
                child.TreeChanged += OnChildTreeChanged;
                Children.Add(child);
            }
            foreach (var file in files)
                Children.Add(new FileTreeItem(file, false));
        }
        catch (UnauthorizedAccessException) { _hasPlaceholder = false; Children.Clear(); }
        catch (IOException) { _hasPlaceholder = false; Children.Clear(); }

        StartWatching();
        TreeChanged?.Invoke(this);
    }

    private void StartWatching()
    {
        StopWatching();
        if (string.IsNullOrEmpty(FullPath) || !Directory.Exists(FullPath)) return;

        try
        {
            _watcher = new FileSystemWatcher(FullPath)
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                IncludeSubdirectories = false,
                EnableRaisingEvents = true
            };
            _watcher.Created += OnFileSystemChanged;
            _watcher.Deleted += OnFileSystemChanged;
            _watcher.Renamed += OnFileSystemChanged;
        }
        catch (Exception)
        {
            // Directory may have been deleted or become inaccessible
            StopWatching();
        }
    }

    public void StopWatching()
    {
        if (_watcher != null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Created -= OnFileSystemChanged;
            _watcher.Deleted -= OnFileSystemChanged;
            _watcher.Renamed -= OnFileSystemChanged;
            _watcher.Dispose();
            _watcher = null;
        }
        _refreshTimer?.Stop();
    }

    /// <summary>
    /// Recursively stops all file system watchers on this item and its descendants.
    /// Call when removing the tree (closing folder/project).
    /// </summary>
    public void StopWatchingRecursive()
    {
        StopWatching();
        foreach (var child in Children)
            child.StopWatchingRecursive();
    }

    private void OnFileSystemChanged(object sender, FileSystemEventArgs e)
    {
        // Debounce: filesystem events often fire in bursts.
        // FileSystemWatcher fires on a thread pool thread, so marshal to UI.
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _refreshTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _refreshTimer.Stop();
            _refreshTimer.Tick -= OnRefreshTimerTick;
            _refreshTimer.Tick += OnRefreshTimerTick;
            _refreshTimer.Start();
        });
    }

    private void OnRefreshTimerTick(object? sender, EventArgs e)
    {
        _refreshTimer?.Stop();
        RefreshChildren();
    }

    private async void RefreshChildren()
    {
        if (!_isExpanded || _hasPlaceholder) return;

        // Stop watchers on old children before replacing them
        foreach (var child in Children)
            child.StopWatchingRecursive();

        // Capture which subdirectories were expanded
        var expandedDirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var child in Children)
        {
            if (child.IsDirectory && child.IsExpanded && !string.IsNullOrEmpty(child.FullPath))
                expandedDirs.Add(child.FullPath);
        }

        Children.Clear();

        try
        {
            var (dirs, files) = await Task.Run(() =>
            {
                var d = Directory.GetDirectories(FullPath)
                    .Where(dp => !IsIgnored(dp))
                    .OrderBy(dp => Path.GetFileName(dp), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                var f = Directory.GetFiles(FullPath)
                    .Where(fp => !IsHidden(fp))
                    .OrderBy(fp => Path.GetFileName(fp), StringComparer.OrdinalIgnoreCase)
                    .ToArray();
                return (d, f);
            });

            foreach (var dir in dirs)
            {
                var child = new FileTreeItem(dir, true);
                child.TreeChanged += OnChildTreeChanged;
                if (expandedDirs.Contains(dir))
                    child.IsExpanded = true;
                Children.Add(child);
            }
            foreach (var file in files)
                Children.Add(new FileTreeItem(file, false));
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }

        TreeChanged?.Invoke(this);
    }

    private void OnChildTreeChanged(FileTreeItem child)
    {
        TreeChanged?.Invoke(child);
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
