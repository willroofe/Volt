using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TextEdit;

public partial class FileExplorerPanel : UserControl
{
    public event Action<string>? FileOpenRequested;
    public event Action<string?>? AddFolderRequested;
    public event Action<string>? RemoveFolderRequested;
    public event Action? NewVirtualFolderRequested;
    public event Action<string>? RemoveVirtualFolderRequested;
    public event Action<string>? RenameVirtualFolderRequested;
    public event Action<string, string?>? MoveToVirtualFolderRequested;
    public event Action? CloseProjectRequested;

    private string? _openFolderPath;
    private ProjectManager? _projectManager;

    public FileExplorerPanel()
    {
        InitializeComponent();
        FolderTree.MouseDoubleClick += OnTreeDoubleClick;
        FolderTree.ContextMenuOpening += (_, e) => e.Handled = true;
    }

    public void SetProjectManager(ProjectManager manager)
    {
        _projectManager = manager;
    }

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        _openFolderPath = path;
        HeaderText.Text = Path.GetFileName(path);
        FolderTree.ItemsSource = FileTreeItem.LoadRoot(path);
    }

    public void CloseFolder()
    {
        _openFolderPath = null;
        HeaderText.Text = "Explorer";
        FolderTree.ItemsSource = null;
    }

    public void OpenProject(Project project)
    {
        _openFolderPath = null;
        HeaderText.Text = project.Name;
        RebuildProjectTree(project);
    }

    public void CloseProject()
    {
        HeaderText.Text = "Explorer";
        FolderTree.ItemsSource = null;
    }

    public void RefreshProjectTree()
    {
        if (_projectManager?.CurrentProject is Project project)
            RebuildProjectTree(project);
    }

    public string? OpenFolderPath => _openFolderPath;

    private void RebuildProjectTree(Project project)
    {
        // Capture expanded state before rebuilding
        var expandedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (FolderTree.ItemsSource is ObservableCollection<FileTreeItem> oldItems)
            CollectExpandedPaths(oldItems, expandedPaths);

        var projectRoot = FileTreeItem.CreateProjectRoot(project.Name);

        // Add virtual folders with their assigned real folders
        foreach (var vf in project.VirtualFolders)
        {
            var vfItem = FileTreeItem.CreateVirtualFolder(vf);
            var assigned = project.Folders
                .Where(f => string.Equals(f.VirtualParent, vf, StringComparison.OrdinalIgnoreCase))
                .ToList();
            foreach (var folder in assigned)
            {
                if (Directory.Exists(folder.Path))
                {
                    var dirItem = FileTreeItem.CreateRootItem(folder.Path);
                    if (expandedPaths.Contains(folder.Path))
                        dirItem.IsExpanded = true;
                    vfItem.Children.Add(dirItem);
                }
            }
            if (expandedPaths.Contains("vf:" + vf))
                vfItem.IsExpanded = true;
            projectRoot.Children.Add(vfItem);
        }

        // Add unassigned real folders under the project root
        var unassigned = project.Folders
            .Where(f => f.VirtualParent == null)
            .ToList();
        foreach (var folder in unassigned)
        {
            if (Directory.Exists(folder.Path))
            {
                var dirItem = FileTreeItem.CreateRootItem(folder.Path);
                if (expandedPaths.Contains(folder.Path))
                    dirItem.IsExpanded = true;
                projectRoot.Children.Add(dirItem);
            }
        }

        projectRoot.IsExpanded = true;
        FolderTree.ItemsSource = new ObservableCollection<FileTreeItem> { projectRoot };
    }

    private static void CollectExpandedPaths(IEnumerable<FileTreeItem> items, HashSet<string> paths)
    {
        foreach (var item in items)
        {
            if (!item.IsExpanded) continue;

            if (item.Kind == FileTreeItemKind.VirtualFolder)
                paths.Add("vf:" + item.Name);
            else if (!string.IsNullOrEmpty(item.FullPath))
                paths.Add(item.FullPath);

            CollectExpandedPaths(item.Children, paths);
        }
    }

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderTree.SelectedItem is FileTreeItem item && item.Kind == FileTreeItemKind.File
            && !string.IsNullOrEmpty(item.FullPath))
        {
            FileOpenRequested?.Invoke(item.FullPath);
            e.Handled = true;
        }
    }

    private void OnTreeRightClick(object sender, MouseButtonEventArgs e)
    {
        if (_projectManager?.CurrentProject == null) return;

        // Select the item under the cursor so right-click targets the right node
        FileTreeItem? item = null;
        if (e.OriginalSource is DependencyObject source)
        {
            var treeViewItem = FindVisualParent<TreeViewItem>(source);
            if (treeViewItem != null)
            {
                treeViewItem.IsSelected = true;
                item = treeViewItem.DataContext as FileTreeItem;
            }
        }
        var menu = new ContextMenu();
        var project = _projectManager.CurrentProject;

        if (item == null)
        {
            // Right-clicked empty area — show project root actions
            menu.Items.Add(CreateMenuItem("Add Folder...", () => AddFolderRequested?.Invoke(null)));
            menu.Items.Add(CreateMenuItem("New Virtual Folder", () => NewVirtualFolderRequested?.Invoke()));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Close Project", () => CloseProjectRequested?.Invoke()));
            FolderTree.ContextMenu = menu;
            menu.IsOpen = true;
            e.Handled = true;
            return;
        }

        switch (item.Kind)
        {
            case FileTreeItemKind.ProjectRoot:
                menu.Items.Add(CreateMenuItem("Add Folder...", () => AddFolderRequested?.Invoke(null)));
                menu.Items.Add(CreateMenuItem("New Virtual Folder", () => NewVirtualFolderRequested?.Invoke()));
                menu.Items.Add(new Separator());
                menu.Items.Add(CreateMenuItem("Close Project", () => CloseProjectRequested?.Invoke()));
                break;

            case FileTreeItemKind.VirtualFolder:
                var targetVf = item.Name;
                menu.Items.Add(CreateMenuItem("Add Folder...", () => AddFolderRequested?.Invoke(targetVf)));
                menu.Items.Add(CreateMenuItem("Rename", () => RenameVirtualFolderRequested?.Invoke(item.Name)));
                menu.Items.Add(CreateMenuItem("Remove Virtual Folder", () => RemoveVirtualFolderRequested?.Invoke(item.Name)));
                break;

            case FileTreeItemKind.Directory when IsTopLevelProjectFolder(item):
                if (project.VirtualFolders.Count > 0)
                {
                    var moveMenu = new MenuItem { Header = "Move to Virtual Folder" };
                    moveMenu.Items.Add(CreateMenuItem("(Project Root)",
                        () => MoveToVirtualFolderRequested?.Invoke(item.FullPath, null)));
                    moveMenu.Items.Add(new Separator());
                    foreach (var vf in project.VirtualFolders)
                    {
                        var vfName = vf;
                        moveMenu.Items.Add(CreateMenuItem(vfName,
                            () => MoveToVirtualFolderRequested?.Invoke(item.FullPath, vfName)));
                    }
                    menu.Items.Add(moveMenu);
                }
                menu.Items.Add(CreateMenuItem("Remove from Project", () => RemoveFolderRequested?.Invoke(item.FullPath)));
                break;

            default:
                return; // No context menu for regular files/subdirectories
        }

        FolderTree.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
    }

    private bool IsTopLevelProjectFolder(FileTreeItem item)
    {
        if (_projectManager?.CurrentProject == null) return false;
        return _projectManager.CurrentProject.Folders.Any(f =>
            string.Equals(f.Path, item.FullPath, StringComparison.OrdinalIgnoreCase));
    }

    private static MenuItem CreateMenuItem(string header, Action onClick)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => onClick();
        return mi;
    }

    private static T? FindVisualParent<T>(DependencyObject child) where T : DependencyObject
    {
        while (child != null)
        {
            if (child is T parent) return parent;
            child = VisualTreeHelper.GetParent(child);
        }
        return null;
    }
}
