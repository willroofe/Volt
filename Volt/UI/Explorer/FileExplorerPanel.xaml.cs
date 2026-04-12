using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DataFormats = System.Windows.DataFormats;
using System.Windows.Threading;
using Microsoft.VisualBasic.FileIO;

namespace Volt;

enum FileOperationKind { CreateFile, CreateFolder, Rename, Move, Delete, Duplicate }
record FileOperation(FileOperationKind Kind, string Path, string? NewPath);

public partial class FileExplorerPanel : UserControl, IPanel
{
    public event Action<string>? FileOpenRequested;
    public event Action? AddFolderRequested;
    public event Action<string>? RemoveFolderRequested;
    public event Action? CloseWorkspaceRequested;
    public event Action? CloseFolderRequested;
    public event Action<string, string>? FileRenamed;
    public event Action<string>? FileDeleted;

    private string? _openFolderPath;
    private WorkspaceManager? _workspaceManager;
    private FileTreeItem? _folderRoot; // kept alive for watcher cleanup
    private ObservableCollection<FileTreeItem>? _currentRootItems;
    private HashSet<string>? _pendingExpandPaths;

    // Debounce flat list refreshes so multiple watcher events (e.g. source + target
    // directories during a move) coalesce into a single visual update.
    private DispatcherTimer? _flatListRefreshTimer;

    // File operation undo/redo
    private readonly Stack<FileOperation> _undoStack = new();
    private readonly Stack<FileOperation> _redoStack = new();
    private static readonly string DeleteStagingDir = Path.Combine(Path.GetTempPath(), "Volt-deleted");

    public string PanelId => "file-explorer";
    public string Title => _title;
    public string? IconGlyph => Codicons.FolderOpened;
    public new UIElement Content => this;
    public event Action? TitleChanged;

    public void AppendTabContextMenuItems(ContextMenu menu)
    {
        var path = ExplorerTree.SelectedItem?.FullPath;
        if (string.IsNullOrEmpty(path))
        {
            path = _openFolderPath;
            if (string.IsNullOrEmpty(path) && _workspaceManager?.CurrentWorkspace?.Folders is { Count: > 0 } folders)
            {
                foreach (var f in folders)
                {
                    if (Directory.Exists(f))
                    {
                        path = f;
                        break;
                    }
                }
            }
        }

        if (string.IsNullOrEmpty(path) || (!File.Exists(path) && !Directory.Exists(path)))
            return;

        menu.Items.Add(ContextMenuHelper.Item("Reveal in File Explorer", Codicons.FolderOpened,
            () => FileHelper.RevealInFileExplorer(path)));
    }

    private string _title = "Explorer";

    private void SetTitle(string title)
    {
        _title = title;
        TitleChanged?.Invoke();
    }

    private DispatcherTimer? _searchDebounce;

    public FileExplorerPanel()
    {
        InitializeComponent();
        ExplorerTree.FileOpenRequested += path => FileOpenRequested?.Invoke(path);
        ExplorerTree.ItemRightClicked += OnItemRightClicked;
        ExplorerTree.FileMoveRequested += OnFileMoveRequested;
        ExplorerTree.RenameRequested += OnRenameRequested;
        ExplorerTree.DeleteRequested += OnDeleteRequested;
        ExplorerTree.UndoRequested += Undo;
        ExplorerTree.RedoRequested += Redo;
        ExplorerTree.PasteRequested += TryPasteFromClipboard;
        ExplorerTree.NavigateAboveFirst += () => FocusSearch();
        PreviewKeyDown += OnPanelPreviewKeyDown;
        CleanOrphanedStagingDir();
    }

    /// <summary>
    /// Removes leftover staging directory from a previous crash.
    /// Safe to call on startup before any undo state exists.
    /// </summary>
    private static void CleanOrphanedStagingDir()
    {
        try
        {
            if (Directory.Exists(DeleteStagingDir))
                Directory.Delete(DeleteStagingDir, recursive: true);
        }
        catch (Exception) { }
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchInput.Text;
        ClearSearchBtn.Visibility = string.IsNullOrEmpty(query)
            ? Visibility.Hidden : Visibility.Visible;

        _searchDebounce ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounce.Stop();
        _searchDebounce.Tick -= OnSearchDebounce;
        _searchDebounce.Tick += OnSearchDebounce;
        _searchDebounce.Start();
    }

    private void OnSearchDebounce(object? sender, EventArgs e)
    {
        _searchDebounce?.Stop();
        ExplorerTree.FilterText = SearchInput.Text?.Trim() ?? "";
    }

    private void OnSearchFocusChanged(object sender, RoutedEventArgs e)
    {
        SearchBorder.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty,
            SearchInput.IsKeyboardFocused ? ThemeResourceKeys.TextFgMuted : ThemeResourceKeys.MenuPopupBorder);
    }

    public bool IsSearchFocused => SearchInput.IsKeyboardFocused;

    public void FocusSearch()
    {
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
            () => { Keyboard.Focus(SearchInput); SearchInput.SelectAll(); });
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e)
    {
        SearchInput.Clear();
        SearchInput.Focus();
    }

    private void OnPanelPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (SearchInput.IsFocused)
        {
            if (e.Key == Key.Escape)
            {
                if (!string.IsNullOrEmpty(SearchInput.Text))
                    SearchInput.Clear();
                else
                    ExplorerTree.Focus();
                e.Handled = true;
            }
            else if (e.Key == Key.Down || e.Key == Key.Tab)
            {
                ExplorerTree.SelectFirstAndFocus();
                e.Handled = true;
            }
        }
    }

    public void SetWorkspaceManager(WorkspaceManager manager)
    {
        _workspaceManager = manager;
    }

    public async void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        StopAllWatchers();
        _openFolderPath = path;
        SetTitle("Explorer");
        var root = new FileTreeItem(path, true);
        _folderRoot = root;

        // Pre-load children so the tree appears fully populated (no placeholder flash)
        await root.ExpandAsync();

        // Subscribe after loading so the initial expand doesn't trigger a premature refresh
        root.TreeChanged += OnTreeChanged;
        _currentRootItems = new ObservableCollection<FileTreeItem> { root };
        ExplorerTree.SetRootItems(_currentRootItems);
        SearchBorder.Visibility = Visibility.Visible;

        // Process any pending expand paths that were set before the tree was ready
        if (_pendingExpandPaths is { Count: > 0 })
        {
            ExpandMatchingChildren(_currentRootItems);
            TryExpandPendingInTree(_currentRootItems);
            ExplorerTree.RefreshFlatList();
        }
    }

    public void CloseFolder()
    {
        StopAllWatchers();
        _openFolderPath = null;
        _pendingExpandPaths = null;
        SetTitle("Explorer");
        _currentRootItems = null;
        ExplorerTree.SetRootItems(null);
        SearchInput.Clear();
        SearchBorder.Visibility = Visibility.Collapsed;
    }

    public void OpenWorkspace(Workspace workspace)
    {
        _openFolderPath = null;
        RebuildWorkspaceTree(workspace);
        SearchBorder.Visibility = Visibility.Visible;
    }

    public void CloseWorkspace()
    {
        StopAllWatchers();
        _pendingExpandPaths = null;
        SetTitle("Explorer");
        _currentRootItems = null;
        ExplorerTree.SetRootItems(null);
        SearchInput.Clear();
        SearchBorder.Visibility = Visibility.Collapsed;
    }

    public void SelectFile(string? path) => ExplorerTree.SelectByPath(path);

    public void RefreshWorkspaceTree()
    {
        if (_workspaceManager?.CurrentWorkspace is Workspace workspace)
            RebuildWorkspaceTree(workspace);
    }

    public string? OpenFolderPath => _openFolderPath;

    public List<string> GetExpandedPaths()
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_currentRootItems != null)
            CollectExpandedPaths(_currentRootItems, paths);
        return [.. paths];
    }

    public void RestoreExpandedPaths(IEnumerable<string> paths)
    {
        _pendingExpandPaths = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
        if (_pendingExpandPaths.Count == 0)
        {
            _pendingExpandPaths = null;
            return;
        }
        if (_currentRootItems != null)
        {
            ExpandMatchingChildren(_currentRootItems);
            TryExpandPendingInTree(_currentRootItems);
            ExplorerTree.RefreshFlatList();
        }
    }

    private void TryExpandPendingPaths(FileTreeItem changedItem)
    {
        if (_pendingExpandPaths == null || _pendingExpandPaths.Count == 0) return;
        ExpandMatchingChildren(changedItem.Children);
        if (_pendingExpandPaths is { Count: 0 })
            _pendingExpandPaths = null;
    }

    private void TryExpandPendingInTree(IEnumerable<FileTreeItem> items)
    {
        if (_pendingExpandPaths == null || _pendingExpandPaths.Count == 0) return;
        foreach (var item in items)
        {
            if (_pendingExpandPaths == null || _pendingExpandPaths.Count == 0) return;
            if (item.IsExpanded)
            {
                ExpandMatchingChildren(item.Children);
                TryExpandPendingInTree(item.Children);
            }
        }
        if (_pendingExpandPaths is { Count: 0 })
            _pendingExpandPaths = null;
    }

    private void ExpandMatchingChildren(IEnumerable<FileTreeItem> children)
    {
        foreach (var child in children)
        {
            if (!child.IsDirectory) continue;

            if (!string.IsNullOrEmpty(child.FullPath) && _pendingExpandPaths!.Remove(child.FullPath))
            {
                child.IsExpanded = true;
            }
        }
    }

    private async void RebuildWorkspaceTree(Workspace workspace)
    {
        StopAllWatchers();

        var expandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (_currentRootItems != null)
            CollectExpandedPaths(_currentRootItems, expandedPaths);

        SetTitle("Explorer");

        var items = new ObservableCollection<FileTreeItem>();

        foreach (var folderPath in workspace.Folders)
        {
            if (!Directory.Exists(folderPath)) continue;

            var dirItem = FileTreeItem.CreateRootItem(folderPath);
            if (expandedPaths.Contains(folderPath))
                await dirItem.ExpandAsync();
            items.Add(dirItem);
        }

        // Subscribe after loading so initial expands don't trigger premature refreshes
        foreach (var item in items)
            item.TreeChanged += OnTreeChanged;

        _currentRootItems = items;
        ExplorerTree.SetRootItems(items);
    }

    private static void CollectExpandedPaths(IEnumerable<FileTreeItem> items, HashSet<string> paths)
    {
        foreach (var item in items)
        {
            if (!item.IsExpanded) continue;

            if (!string.IsNullOrEmpty(item.FullPath))
                paths.Add(item.FullPath);

            CollectExpandedPaths(item.Children, paths);
        }
    }

    private void OnItemRightClicked(FileTreeItem? item)
    {
        var menu = ContextMenuHelper.Create();

        if (item == null)
        {
            // Right-clicked empty area
            var targetDir = GetRootDirectory();
            if (targetDir != null)
            {
                menu.Items.Add(ContextMenuHelper.Item("New File", () => DoNewFile(targetDir)));
                menu.Items.Add(ContextMenuHelper.Item("New Folder", () => DoNewFolder(targetDir)));
                if (Directory.Exists(targetDir))
                {
                    menu.Items.Add(ContextMenuHelper.Separator());
                    menu.Items.Add(ContextMenuHelper.Item("Reveal in File Explorer", Codicons.FolderOpened,
                        () => FileHelper.RevealInFileExplorer(targetDir)));
                }
                if (ClipboardHasFileDropList())
                {
                    menu.Items.Add(ContextMenuHelper.Separator());
                    menu.Items.Add(ContextMenuHelper.Item("Paste", Codicons.Clippy, () => TryPasteFromClipboard()));
                }
            }
            if (_workspaceManager?.CurrentWorkspace != null)
            {
                if (menu.Items.Count > 0) menu.Items.Add(ContextMenuHelper.Separator());
                menu.Items.Add(ContextMenuHelper.Item("Add Folder to Workspace", Codicons.Add, () => AddFolderRequested?.Invoke()));
                menu.Items.Add(ContextMenuHelper.Item("Close Workspace", Codicons.Close, () => CloseWorkspaceRequested?.Invoke()));
            }
            else if (_openFolderPath != null)
            {
                if (menu.Items.Count > 0) menu.Items.Add(ContextMenuHelper.Separator());
                menu.Items.Add(ContextMenuHelper.Item("Close Folder", Codicons.Close, () => CloseFolderRequested?.Invoke()));
            }
        }
        else if (item.IsDirectory)
        {
            menu.Items.Add(ContextMenuHelper.Item("New File", () => DoNewFile(item.FullPath)));
            menu.Items.Add(ContextMenuHelper.Item("New Folder", () => DoNewFolder(item.FullPath)));
            menu.Items.Add(ContextMenuHelper.Separator());
            menu.Items.Add(ContextMenuHelper.Item("Reveal in File Explorer", Codicons.FolderOpened,
                () => FileHelper.RevealInFileExplorer(item.FullPath)));
            menu.Items.Add(ContextMenuHelper.Separator());
            menu.Items.Add(ContextMenuHelper.Item("Copy", Codicons.Copy, () => CopyPathsToClipboard(item.FullPath)));
            if (ClipboardHasFileDropList())
                menu.Items.Add(ContextMenuHelper.Item("Paste", Codicons.Clippy, () => TryPasteFromClipboard()));
            if (!IsRootFolder(item))
            {
                menu.Items.Add(ContextMenuHelper.Separator());
                menu.Items.Add(ContextMenuHelper.Item("Rename", Codicons.Rename, () => DoRename(item)));
                menu.Items.Add(ContextMenuHelper.Item("Delete", Codicons.Trash, () => DoDelete(item)));
            }
            else if (IsTopLevelWorkspaceFolder(item))
            {
                menu.Items.Add(ContextMenuHelper.Separator());
                menu.Items.Add(ContextMenuHelper.Item("Remove from Workspace", () => RemoveFolderRequested?.Invoke(item.FullPath)));
            }
        }
        else
        {
            // File item
            menu.Items.Add(ContextMenuHelper.Item("Reveal in File Explorer", Codicons.FolderOpened,
                () => FileHelper.RevealInFileExplorer(item.FullPath)));
            menu.Items.Add(ContextMenuHelper.Separator());
            menu.Items.Add(ContextMenuHelper.Item("Copy", Codicons.Copy, () => CopyPathsToClipboard(item.FullPath)));
            if (ClipboardHasFileDropList())
                menu.Items.Add(ContextMenuHelper.Item("Paste", Codicons.Clippy, () => TryPasteFromClipboard()));
            menu.Items.Add(ContextMenuHelper.Separator());
            menu.Items.Add(ContextMenuHelper.Item("Duplicate", () => DoDuplicate(item)));
            menu.Items.Add(ContextMenuHelper.Item("Rename", Codicons.Rename, () => DoRename(item)));
            menu.Items.Add(ContextMenuHelper.Item("Delete", Codicons.Trash, () => DoDelete(item)));
        }

        if (menu.Items.Count > 0)
        {
            ExplorerTree.ContextMenu = menu;
            menu.IsOpen = true;
        }
        else
        {
            ExplorerTree.ContextMenu = null;
        }
    }

    private string? GetRootDirectory()
    {
        if (_openFolderPath != null) return _openFolderPath;
        if (_workspaceManager?.CurrentWorkspace?.Folders is { Count: > 0 } folders)
            return folders[0];
        return null;
    }

    private string? GetPasteTargetDirectory()
    {
        var sel = ExplorerTree.SelectedItem;
        if (sel == null || string.IsNullOrEmpty(sel.FullPath)) return GetRootDirectory();
        if (sel.IsDirectory) return sel.FullPath;
        return Path.GetDirectoryName(sel.FullPath);
    }

    private static bool ClipboardHasFileDropList()
    {
        try
        {
            return Clipboard.GetDataObject()?.GetDataPresent(DataFormats.FileDrop) == true;
        }
        catch
        {
            return false;
        }
    }

    private static void CopyPathsToClipboard(string fullPath)
    {
        try
        {
            Clipboard.SetDataObject(new DataObject(DataFormats.FileDrop, new[] { fullPath }), copy: true);
        }
        catch { /* clipboard busy */ }
    }

    private void TryPasteFromClipboard()
    {
        string[]? paths;
        try
        {
            var data = Clipboard.GetDataObject();
            if (data?.GetDataPresent(DataFormats.FileDrop) != true) return;
            paths = data.GetData(DataFormats.FileDrop) as string[];
        }
        catch
        {
            return;
        }
        if (paths is not { Length: > 0 }) return;

        var targetDir = GetPasteTargetDirectory();
        if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir)) return;

        var targetNorm = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDir));
        var owner = Window.GetWindow(this);

        foreach (var src in paths)
        {
            if (string.IsNullOrWhiteSpace(src)) continue;
            string fullSrc;
            try { fullSrc = Path.GetFullPath(src); }
            catch { continue; }

            if (!File.Exists(fullSrc) && !Directory.Exists(fullSrc)) continue;

            if (Directory.Exists(fullSrc))
            {
                var srcNorm = Path.TrimEndingDirectorySeparator(fullSrc);
                if (string.Equals(targetNorm, srcNorm, StringComparison.OrdinalIgnoreCase)) continue;
                if (targetNorm.StartsWith(srcNorm + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
            }

            try
            {
                if (File.Exists(fullSrc))
                    PasteFileIntoDirectory(fullSrc, targetNorm);
                else
                    PasteDirectoryIntoDirectory(fullSrc, targetNorm);
            }
            catch (Exception ex)
            {
                if (owner != null)
                    ThemedMessageBox.Show(owner, ex.Message, "Paste Failed");
                break;
            }
        }
    }

    private void PasteFileIntoDirectory(string sourceFile, string targetDir)
    {
        var dest = GetNonCollidingFilePathInDirectory(targetDir, sourceFile);
        File.Copy(sourceFile, dest, overwrite: false);
        PushUndo(new FileOperation(FileOperationKind.CreateFile, dest, null));
    }

    private void PasteDirectoryIntoDirectory(string sourceDir, string targetDir)
    {
        var destRoot = GetNonCollidingDirectoryPathInDirectory(targetDir, sourceDir);
        CopyDirectoryRecursive(sourceDir, destRoot);
        PushUndo(new FileOperation(FileOperationKind.CreateFolder, destRoot, null));
    }

    private static void CopyDirectoryRecursive(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var name = Path.GetFileName(file);
            File.Copy(file, Path.Combine(destDir, name), overwrite: false);
        }
        foreach (var sub in Directory.GetDirectories(sourceDir))
        {
            var name = Path.GetFileName(sub);
            CopyDirectoryRecursive(sub, Path.Combine(destDir, name));
        }
    }

    /// <summary>Resolves a destination file path under <paramref name="targetDir"/> that does not exist yet.</summary>
    private static string GetNonCollidingFilePathInDirectory(string targetDir, string sourceFilePath)
    {
        var leaf = Path.GetFileName(sourceFilePath);
        if (string.IsNullOrEmpty(leaf)) leaf = "file";
        var candidate = Path.Combine(targetDir, leaf);
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        var baseName = Path.GetFileNameWithoutExtension(leaf);
        var ext = Path.GetExtension(leaf);
        candidate = Path.Combine(targetDir, $"{baseName} - Copy{ext}");
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(targetDir, $"{baseName} - Copy ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private static string GetNonCollidingDirectoryPathInDirectory(string targetDir, string sourceDirPath)
    {
        var leaf = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceDirPath));
        if (string.IsNullOrEmpty(leaf)) leaf = "Folder";
        var candidate = Path.Combine(targetDir, leaf);
        if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        candidate = Path.Combine(targetDir, $"{leaf} - Copy");
        if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(targetDir, $"{leaf} - Copy ({i})");
            if (!Directory.Exists(candidate) && !File.Exists(candidate)) return candidate;
        }
    }

    private void DoNewFile(string parentDir) => CreateFileSystemItem(parentDir, isDirectory: false);
    private void DoNewFolder(string parentDir) => CreateFileSystemItem(parentDir, isDirectory: true);

    private void CreateFileSystemItem(string parentDir, bool isDirectory)
    {
        var kind = isDirectory ? "Folder" : "File";
        var owner = Window.GetWindow(this);
        if (owner == null) return;
        var name = ThemedInputBox.Show(owner, $"New {kind}", $"{kind} name:");
        if (string.IsNullOrWhiteSpace(name)) return;
        if (name.Trim().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            ThemedMessageBox.Show(owner, $"The {kind.ToLowerInvariant()} name contains invalid characters.", $"New {kind}");
            return;
        }
        var fullPath = Path.Combine(parentDir, name.Trim());
        if (File.Exists(fullPath) || Directory.Exists(fullPath))
        {
            ThemedMessageBox.Show(owner, $"'{name.Trim()}' already exists.", $"New {kind}");
            return;
        }
        try
        {
            if (isDirectory)
            {
                Directory.CreateDirectory(fullPath);
                PushUndo(new FileOperation(FileOperationKind.CreateFolder, fullPath, null));
            }
            else
            {
                File.Create(fullPath).Dispose();
                PushUndo(new FileOperation(FileOperationKind.CreateFile, fullPath, null));
                FileOpenRequested?.Invoke(fullPath);
            }
        }
        catch (Exception ex) { ThemedMessageBox.Show(owner, ex.Message, "Error"); }
    }

    private void DoDuplicate(FileTreeItem item)
    {
        if (item.IsDirectory) return;
        var owner = Window.GetWindow(this);
        if (owner == null) return;
        if (!File.Exists(item.FullPath)) return;
        var destPath = GetDuplicateDestinationPath(item.FullPath);
        try
        {
            File.Copy(item.FullPath, destPath, overwrite: false);
            PushUndo(new FileOperation(FileOperationKind.Duplicate, item.FullPath, destPath));
            FileOpenRequested?.Invoke(destPath);
        }
        catch (Exception ex) { ThemedMessageBox.Show(owner, ex.Message, "Duplicate Failed"); }
    }

    /// <summary>Builds a non-colliding path in the same directory: <c>name - Copy.ext</c>, then <c>name - Copy (2).ext</c>, etc.</summary>
    private static string GetDuplicateDestinationPath(string sourcePath)
    {
        var dir = Path.GetDirectoryName(sourcePath)!;
        var baseName = Path.GetFileNameWithoutExtension(sourcePath);
        var ext = Path.GetExtension(sourcePath);
        var candidate = Path.Combine(dir, $"{baseName} - Copy{ext}");
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        for (var i = 2; ; i++)
        {
            candidate = Path.Combine(dir, $"{baseName} - Copy ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    private void DoRename(FileTreeItem item)
    {
        var owner = Window.GetWindow(this);
        if (owner == null) return;
        var newName = ThemedInputBox.Show(owner, "Rename", "New name:", item.Name);
        if (string.IsNullOrWhiteSpace(newName) || newName.Trim() == item.Name) return;
        var parentDir = Path.GetDirectoryName(item.FullPath)!;
        var newPath = Path.Combine(parentDir, newName.Trim());
        try
        {
            var oldPath = item.FullPath;
            if (item.IsDirectory)
                Directory.Move(oldPath, newPath);
            else
                File.Move(oldPath, newPath);
            PushUndo(new FileOperation(FileOperationKind.Rename, oldPath, newPath));
            FileRenamed?.Invoke(oldPath, newPath);
        }
        catch (Exception ex) { ThemedMessageBox.Show(owner, ex.Message, "Rename Failed"); }
    }

    private void DoDelete(FileTreeItem item)
    {
        var owner = Window.GetWindow(this);
        if (owner == null) return;
        var msg = item.IsDirectory
            ? $"Delete folder '{item.Name}' and all its contents?"
            : $"Delete file '{item.Name}'?";
        var result = ThemedMessageBox.Show(owner, msg, "Confirm Delete", MessageBoxButton.YesNo);
        if (result != MessageBoxResult.Yes) return;
        try
        {
            var stagedPath = StageForDelete(item.FullPath, item.IsDirectory);
            PushUndo(new FileOperation(FileOperationKind.Delete, item.FullPath, stagedPath));
            FileDeleted?.Invoke(item.FullPath);
        }
        catch (Exception ex) { ThemedMessageBox.Show(owner, ex.Message, "Delete Failed"); }
    }

    private static string StageForDelete(string originalPath, bool isDirectory)
    {
        Directory.CreateDirectory(DeleteStagingDir);
        // Use a unique subdirectory to avoid name collisions
        var id = Guid.NewGuid().ToString("N")[..8];
        var name = Path.GetFileName(originalPath);
        var stagedPath = Path.Combine(DeleteStagingDir, id + "_" + name);
        if (isDirectory)
            Directory.Move(originalPath, stagedPath);
        else
            File.Move(originalPath, stagedPath);
        return stagedPath;
    }

    private static void MoveFileOrDirectory(string source, string dest)
    {
        if (Directory.Exists(source))
            Directory.Move(source, dest);
        else if (File.Exists(source))
            File.Move(source, dest);
    }

    private void OnFileMoveRequested(string sourcePath, string destPath)
    {
        try
        {
            MoveFileOrDirectory(sourcePath, destPath);
            PushUndo(new FileOperation(FileOperationKind.Move, sourcePath, destPath));
            FileRenamed?.Invoke(sourcePath, destPath);
        }
        catch (Exception ex)
        {
            var owner = Window.GetWindow(this);
            if (owner != null)
                ThemedMessageBox.Show(owner, ex.Message, "Move Failed");
        }
    }

    private void PushUndo(FileOperation op)
    {
        _undoStack.Push(op);
        FlushStagedDeletes(_redoStack);
        _redoStack.Clear();
    }

    private void Undo()
    {
        if (_undoStack.Count == 0) return;
        var op = _undoStack.Pop();
        try
        {
            switch (op.Kind)
            {
                case FileOperationKind.CreateFile:
                    if (File.Exists(op.Path))
                    {
                        FileSystem.DeleteFile(op.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                        FileDeleted?.Invoke(op.Path);
                    }
                    break;
                case FileOperationKind.CreateFolder:
                    if (Directory.Exists(op.Path))
                    {
                        FileSystem.DeleteDirectory(op.Path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                        FileDeleted?.Invoke(op.Path);
                    }
                    break;
                case FileOperationKind.Duplicate:
                    if (op.NewPath != null && File.Exists(op.NewPath))
                    {
                        FileSystem.DeleteFile(op.NewPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                        FileDeleted?.Invoke(op.NewPath);
                    }
                    break;
                case FileOperationKind.Rename:
                case FileOperationKind.Move:
                    MoveFileOrDirectory(op.NewPath!, op.Path);
                    FileRenamed?.Invoke(op.NewPath!, op.Path);
                    break;
                case FileOperationKind.Delete:
                    MoveFileOrDirectory(op.NewPath!, op.Path);
                    break;
            }
            _redoStack.Push(op);
        }
        catch (Exception ex)
        {
            var owner = Window.GetWindow(this);
            if (owner != null)
                ThemedMessageBox.Show(owner, ex.Message, "Undo Failed");
        }
    }

    private void Redo()
    {
        if (_redoStack.Count == 0) return;
        var op = _redoStack.Pop();
        try
        {
            switch (op.Kind)
            {
                case FileOperationKind.CreateFile:
                    File.Create(op.Path).Dispose();
                    break;
                case FileOperationKind.CreateFolder:
                    Directory.CreateDirectory(op.Path);
                    break;
                case FileOperationKind.Duplicate:
                    if (File.Exists(op.Path) && op.NewPath != null)
                        File.Copy(op.Path, op.NewPath, overwrite: true);
                    break;
                case FileOperationKind.Rename:
                case FileOperationKind.Move:
                    MoveFileOrDirectory(op.Path, op.NewPath!);
                    FileRenamed?.Invoke(op.Path, op.NewPath!);
                    break;
                case FileOperationKind.Delete:
                    // Re-stage to temp
                    var isDir = Directory.Exists(op.Path);
                    var stagedPath = StageForDelete(op.Path, isDir);
                    op = op with { NewPath = stagedPath };
                    FileDeleted?.Invoke(op.Path);
                    break;
            }
            _undoStack.Push(op);
        }
        catch (Exception ex)
        {
            var owner = Window.GetWindow(this);
            if (owner != null)
                ThemedMessageBox.Show(owner, ex.Message, "Redo Failed");
        }
    }

    /// <summary>
    /// Sends any staged delete files in the given stack to the recycle bin.
    /// Called when the redo stack is about to be cleared (new operation performed)
    /// or when the panel is being torn down.
    /// </summary>
    private static void FlushStagedDeletes(Stack<FileOperation> stack)
    {
        foreach (var op in stack)
        {
            if (op.Kind != FileOperationKind.Delete || op.NewPath == null) continue;
            try
            {
                if (Directory.Exists(op.NewPath))
                    FileSystem.DeleteDirectory(op.NewPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
                else if (File.Exists(op.NewPath))
                    FileSystem.DeleteFile(op.NewPath, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
            }
            catch (Exception) { }
        }
    }

    /// <summary>
    /// Flushes all staged deletes from both stacks. Call on app exit.
    /// </summary>
    public void FlushAllStagedDeletes()
    {
        FlushStagedDeletes(_undoStack);
        FlushStagedDeletes(_redoStack);
    }

    private void OnRenameRequested(FileTreeItem item)
    {
        if (!IsRootFolder(item))
            DoRename(item);
    }

    private void OnDeleteRequested(FileTreeItem item)
    {
        if (!IsRootFolder(item))
            DoDelete(item);
    }

    private bool IsRootFolder(FileTreeItem item)
    {
        if (!item.IsDirectory) return false;
        // Single-folder mode root
        if (_openFolderPath != null &&
            string.Equals(_openFolderPath, item.FullPath, StringComparison.OrdinalIgnoreCase))
            return true;
        // Workspace root folder
        if (_workspaceManager?.CurrentWorkspace != null)
            return _workspaceManager.CurrentWorkspace.Folders.Any(f =>
                string.Equals(f, item.FullPath, StringComparison.OrdinalIgnoreCase));
        return false;
    }

    private bool IsTopLevelWorkspaceFolder(FileTreeItem item)
    {
        if (_workspaceManager?.CurrentWorkspace == null) return false;
        return _workspaceManager.CurrentWorkspace.Folders.Any(f =>
            string.Equals(f, item.FullPath, StringComparison.OrdinalIgnoreCase));
    }

    private void OnTreeChanged(FileTreeItem item)
    {
        TryExpandPendingPaths(item);
        // Debounce: multiple watcher-triggered TreeChanged events (e.g. source + target
        // dirs during a move) arrive in a burst — coalesce into one flat list rebuild.
        if (_flatListRefreshTimer == null)
        {
            _flatListRefreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(30) };
            _flatListRefreshTimer.Tick += (_, _) =>
            {
                _flatListRefreshTimer.Stop();
                ExplorerTree.RefreshFlatList();
            };
        }
        _flatListRefreshTimer.Stop();
        _flatListRefreshTimer.Start();
    }

    private void StopAllWatchers()
    {
        _folderRoot?.StopWatchingRecursive();
        _folderRoot = null;
        if (_currentRootItems == null) return;
        foreach (var root in _currentRootItems)
            root.StopWatchingRecursive();
    }
}
