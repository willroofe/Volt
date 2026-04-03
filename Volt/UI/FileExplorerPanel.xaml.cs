using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Volt;

public partial class FileExplorerPanel : UserControl, IPanel
{
    public event Action<string>? FileOpenRequested;
    public event Action? AddFolderRequested;
    public event Action<string>? RemoveFolderRequested;
    public event Action? CloseWorkspaceRequested;

    private string? _openFolderPath;
    private WorkspaceManager? _workspaceManager;
    private FileTreeItem? _folderRoot; // kept alive for watcher cleanup
    private ObservableCollection<FileTreeItem>? _currentRootItems;
    private HashSet<string>? _pendingExpandPaths;

    public string PanelId => "file-explorer";
    public string Title => _title;
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
        if (_workspaceManager?.CurrentWorkspace == null)
        {
            ExplorerTree.ContextMenu = null;
            return;
        }

        var menu = ContextMenuHelper.Create();

        if (item == null)
        {
            menu.Items.Add(ContextMenuHelper.Item("Add Folder to Workspace", () => AddFolderRequested?.Invoke()));
            menu.Items.Add(ContextMenuHelper.Separator());
            menu.Items.Add(ContextMenuHelper.Item("Close Workspace", () => CloseWorkspaceRequested?.Invoke()));
            ExplorerTree.ContextMenu = menu;
            menu.IsOpen = true;
            return;
        }

        if (IsTopLevelWorkspaceFolder(item))
        {
            menu.Items.Add(ContextMenuHelper.Item("Remove from Workspace", () => RemoveFolderRequested?.Invoke(item.FullPath)));
            ExplorerTree.ContextMenu = menu;
            menu.IsOpen = true;
            return;
        }

        ExplorerTree.ContextMenu = null;
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
