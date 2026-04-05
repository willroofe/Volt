using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.VisualBasic.FileIO;

namespace Volt;

enum FileOperationKind { CreateFile, CreateFolder, Rename, Move, Delete }
record FileOperation(FileOperationKind Kind, string Path, string? NewPath);

public partial class FileExplorerPanel : UserControl, IPanel
{
    public event Action<string>? FileOpenRequested;
    public event Action? AddFolderRequested;
    public event Action<string>? RemoveFolderRequested;
    public event Action? CloseWorkspaceRequested;
    public event Action<string, string>? FileRenamed;
    public event Action<string>? FileDeleted;

    private string? _openFolderPath;
    private WorkspaceManager? _workspaceManager;
    private FileTreeItem? _folderRoot; // kept alive for watcher cleanup
    private ObservableCollection<FileTreeItem>? _currentRootItems;
    private HashSet<string>? _pendingExpandPaths;

    // File operation undo/redo
    private readonly Stack<FileOperation> _undoStack = new();
    private readonly Stack<FileOperation> _redoStack = new();
    private static readonly string DeleteStagingDir = Path.Combine(Path.GetTempPath(), "Volt-deleted");

    public string PanelId => "file-explorer";
    public string Title => _title;
    public string? IconGlyph => "\uE8B7";
    public new UIElement Content => this;
    public event Action? TitleChanged;

    private string _title = "Explorer";

    private void SetTitle(string title)
    {
        _title = title;
        TitleChanged?.Invoke();
    }

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
    }

    public void SetWorkspaceManager(WorkspaceManager manager)
    {
        _workspaceManager = manager;
    }

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        StopAllWatchers();
        _openFolderPath = path;
        SetTitle("Explorer");
        var root = new FileTreeItem(path, true);
        root.TreeChanged += OnTreeChanged;
        root.IsExpanded = true;
        _folderRoot = root;
        _currentRootItems = new ObservableCollection<FileTreeItem> { root };
        ExplorerTree.SetRootItems(_currentRootItems);
    }

    public void CloseFolder()
    {
        StopAllWatchers();
        _openFolderPath = null;
        _pendingExpandPaths = null;
        SetTitle("Explorer");
        _currentRootItems = null;
        ExplorerTree.SetRootItems(null);
    }

    public void OpenWorkspace(Workspace workspace)
    {
        _openFolderPath = null;
        RebuildWorkspaceTree(workspace);
    }

    public void CloseWorkspace()
    {
        StopAllWatchers();
        _pendingExpandPaths = null;
        SetTitle("Explorer");
        _currentRootItems = null;
        ExplorerTree.SetRootItems(null);
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

    private void RebuildWorkspaceTree(Workspace workspace)
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
            dirItem.TreeChanged += OnTreeChanged;
            if (expandedPaths.Contains(folderPath))
                dirItem.IsExpanded = true;
            items.Add(dirItem);
        }

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
            }
            if (_workspaceManager?.CurrentWorkspace != null)
            {
                if (menu.Items.Count > 0) menu.Items.Add(ContextMenuHelper.Separator());
                menu.Items.Add(ContextMenuHelper.Item("Add Folder to Workspace", "\uE710", () => AddFolderRequested?.Invoke()));
                menu.Items.Add(ContextMenuHelper.Separator());
                menu.Items.Add(ContextMenuHelper.Item("Close Workspace", "\uE711", () => CloseWorkspaceRequested?.Invoke()));
            }
        }
        else if (item.IsDirectory)
        {
            menu.Items.Add(ContextMenuHelper.Item("New File", () => DoNewFile(item.FullPath)));
            menu.Items.Add(ContextMenuHelper.Item("New Folder", () => DoNewFolder(item.FullPath)));
            if (!IsRootFolder(item))
            {
                menu.Items.Add(ContextMenuHelper.Separator());
                menu.Items.Add(ContextMenuHelper.Item("Rename", "\uE70F", () => DoRename(item)));
                menu.Items.Add(ContextMenuHelper.Item("Delete", "\uE74D", () => DoDelete(item)));
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
            menu.Items.Add(ContextMenuHelper.Item("Rename", "\uE70F", () => DoRename(item)));
            menu.Items.Add(ContextMenuHelper.Item("Delete", "\uE74D", () => DoDelete(item)));
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

    private void OnFileMoveRequested(string sourcePath, string destPath)
    {
        try
        {
            if (File.Exists(sourcePath))
                File.Move(sourcePath, destPath);
            else if (Directory.Exists(sourcePath))
                Directory.Move(sourcePath, destPath);
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
                case FileOperationKind.Rename:
                case FileOperationKind.Move:
                    if (Directory.Exists(op.NewPath!))
                        Directory.Move(op.NewPath!, op.Path);
                    else if (File.Exists(op.NewPath!))
                        File.Move(op.NewPath!, op.Path);
                    FileRenamed?.Invoke(op.NewPath!, op.Path);
                    break;
                case FileOperationKind.Delete:
                    // Restore from staging directory
                    if (Directory.Exists(op.NewPath!))
                        Directory.Move(op.NewPath!, op.Path);
                    else if (File.Exists(op.NewPath!))
                        File.Move(op.NewPath!, op.Path);
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
                case FileOperationKind.Rename:
                case FileOperationKind.Move:
                    if (Directory.Exists(op.Path))
                        Directory.Move(op.Path, op.NewPath!);
                    else if (File.Exists(op.Path))
                        File.Move(op.Path, op.NewPath!);
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
        ExplorerTree.RefreshFlatList();
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
