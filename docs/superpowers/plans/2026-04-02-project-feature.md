# Project Feature Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add project support — collections of multiple folders with virtual folder organization, saved as `.vproj` files.

**Architecture:** A `Project` data model and `ProjectManager` class layer on top of the existing `FileExplorerPanel`. When a project is open, the explorer switches to project mode showing a tree with virtual folders grouping real filesystem folders. Session state (open tabs, caret/scroll positions) is stored in the `.vproj` file, separate from the existing `AppSettings.Session`.

**Tech Stack:** C# / WPF / .NET 10, System.Text.Json for serialization

---

## File Structure

| Action | File | Responsibility |
|--------|------|----------------|
| Create | `Volt/UI/Project.cs` | Data model: `Project`, `ProjectFolder`, `ProjectSession`, `ProjectSessionTab` |
| Create | `Volt/UI/ProjectManager.cs` | Project lifecycle: new/open/save/close, folder management, virtual folder management |
| Modify | `Volt/UI/FileTreeItem.cs` | Add `ItemKind` enum, constructors for VirtualFolder/ProjectRoot kinds |
| Modify | `Volt/UI/FileExplorerPanel.xaml` | Context menus, `HierarchicalDataTemplate` changes for item kind icons |
| Modify | `Volt/UI/FileExplorerPanel.xaml.cs` | Project mode: `OpenProject()`, `CloseProject()`, context menu handlers |
| Modify | `Volt/UI/MainWindow.xaml` | File menu: New/Open/Save/Close Project items |
| Modify | `Volt/UI/MainWindow.xaml.cs` | Project menu handlers, session integration, mode exclusivity |
| Modify | `Volt/AppSettings.cs` | Add `LastOpenProjectPath` to settings |
| Modify | `Volt/UI/CommandPaletteCommands.cs` | Add project commands |

---

### Task 1: Project Data Model

**Files:**
- Create: `Volt/UI/Project.cs`

- [ ] **Step 1: Create the Project data model file**

```csharp
using System.Text.Json.Serialization;

namespace Volt;

public class Project
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "Untitled Project";

    [JsonIgnore]
    public string? FilePath { get; set; }

    [JsonPropertyName("virtualFolders")]
    public List<string> VirtualFolders { get; set; } = [];

    [JsonPropertyName("folders")]
    public List<ProjectFolder> Folders { get; set; } = [];

    [JsonPropertyName("session")]
    public ProjectSession Session { get; set; } = new();
}

public class ProjectFolder
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = "";

    [JsonPropertyName("virtualParent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? VirtualParent { get; set; }
}

public class ProjectSession
{
    [JsonPropertyName("tabs")]
    public List<ProjectSessionTab> Tabs { get; set; } = [];

    [JsonPropertyName("activeTabIndex")]
    public int ActiveTabIndex { get; set; }
}

public class ProjectSessionTab
{
    [JsonPropertyName("filePath")]
    public string? FilePath { get; set; }

    [JsonPropertyName("isDirty")]
    public bool IsDirty { get; set; }

    [JsonPropertyName("caretLine")]
    public int CaretLine { get; set; }

    [JsonPropertyName("caretCol")]
    public int CaretCol { get; set; }

    [JsonPropertyName("scrollX")]
    public double ScrollX { get; set; }

    [JsonPropertyName("scrollY")]
    public double ScrollY { get; set; }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Volt.sln`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/Project.cs
git commit -m "feat: add Project data model for .vproj files"
```

---

### Task 2: ProjectManager

**Files:**
- Create: `Volt/UI/ProjectManager.cs`

- [ ] **Step 1: Create the ProjectManager class**

```csharp
using System.IO;
using System.Text;
using System.Text.Json;

namespace Volt;

public class ProjectManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public Project? CurrentProject { get; private set; }

    public Project NewProject(string name = "Untitled Project")
    {
        var project = new Project { Name = name };
        CurrentProject = project;
        return project;
    }

    public Project OpenProject(string vprojPath)
    {
        var json = File.ReadAllText(vprojPath, Encoding.UTF8);
        var project = JsonSerializer.Deserialize<Project>(json, JsonOptions)
                      ?? new Project();
        project.FilePath = vprojPath;
        if (string.IsNullOrWhiteSpace(project.Name))
            project.Name = Path.GetFileNameWithoutExtension(vprojPath);
        CurrentProject = project;
        return project;
    }

    public void SaveProject()
    {
        if (CurrentProject?.FilePath == null) return;
        var json = JsonSerializer.Serialize(CurrentProject, JsonOptions);
        FileHelper.AtomicWriteText(CurrentProject.FilePath, json, Encoding.UTF8);
    }

    public void CloseProject()
    {
        CurrentProject = null;
    }

    public void AddFolder(string path)
    {
        if (CurrentProject == null) return;
        if (CurrentProject.Folders.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;
        CurrentProject.Folders.Add(new ProjectFolder { Path = path });
    }

    public void RemoveFolder(string path)
    {
        if (CurrentProject == null) return;
        CurrentProject.Folders.RemoveAll(f =>
            string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    public void CreateVirtualFolder(string name)
    {
        if (CurrentProject == null) return;
        if (CurrentProject.VirtualFolders.Contains(name, StringComparer.OrdinalIgnoreCase))
            return;
        CurrentProject.VirtualFolders.Add(name);
    }

    public void RemoveVirtualFolder(string name)
    {
        if (CurrentProject == null) return;
        CurrentProject.VirtualFolders.Remove(name);
        foreach (var folder in CurrentProject.Folders)
        {
            if (string.Equals(folder.VirtualParent, name, StringComparison.OrdinalIgnoreCase))
                folder.VirtualParent = null;
        }
    }

    public void RenameVirtualFolder(string oldName, string newName)
    {
        if (CurrentProject == null) return;
        var idx = CurrentProject.VirtualFolders.FindIndex(
            v => string.Equals(v, oldName, StringComparison.OrdinalIgnoreCase));
        if (idx < 0) return;
        CurrentProject.VirtualFolders[idx] = newName;
        foreach (var folder in CurrentProject.Folders)
        {
            if (string.Equals(folder.VirtualParent, oldName, StringComparison.OrdinalIgnoreCase))
                folder.VirtualParent = newName;
        }
    }

    public void AssignToVirtualFolder(string folderPath, string? virtualFolderName)
    {
        if (CurrentProject == null) return;
        var folder = CurrentProject.Folders.FirstOrDefault(f =>
            string.Equals(f.Path, folderPath, StringComparison.OrdinalIgnoreCase));
        if (folder != null)
            folder.VirtualParent = virtualFolderName;
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Volt.sln`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/ProjectManager.cs
git commit -m "feat: add ProjectManager for project lifecycle"
```

---

### Task 3: FileTreeItem — Add ItemKind Support

**Files:**
- Modify: `Volt/UI/FileTreeItem.cs`

- [ ] **Step 1: Add the ItemKind enum and new properties**

Add at the top of the file, inside the namespace but before the class:

```csharp
public enum FileTreeItemKind
{
    File,
    Directory,
    VirtualFolder,
    ProjectRoot
}
```

Add a new property to `FileTreeItem` after the `IsDirectory` property (line 11):

```csharp
public FileTreeItemKind Kind { get; }
```

- [ ] **Step 2: Update the main constructor to set Kind**

Replace the existing constructor at lines 38-50:

```csharp
public FileTreeItem(string fullPath, bool isDirectory)
{
    FullPath = fullPath;
    Name = Path.GetFileName(fullPath);
    IsDirectory = isDirectory;
    Kind = isDirectory ? FileTreeItemKind.Directory : FileTreeItemKind.File;

    if (isDirectory)
    {
        Children.Add(new FileTreeItem("", false) { _hasPlaceholder = false });
        _hasPlaceholder = true;
    }
}
```

- [ ] **Step 3: Update the private parameterless constructor**

Replace the private constructor at lines 55-60:

```csharp
private FileTreeItem()
{
    FullPath = "";
    Name = "";
    IsDirectory = false;
    Kind = FileTreeItemKind.File;
}
```

- [ ] **Step 4: Add factory methods for VirtualFolder and ProjectRoot**

Add these static methods after `CreateRootItem` (after line 107):

```csharp
public static FileTreeItem CreateProjectRoot(string projectName)
{
    return new FileTreeItem
    {
        FullPath = "",
        Name = projectName,
        IsDirectory = false,
        Kind = FileTreeItemKind.ProjectRoot
    };
}

public static FileTreeItem CreateVirtualFolder(string name)
{
    return new FileTreeItem
    {
        FullPath = "",
        Name = name,
        IsDirectory = false,
        Kind = FileTreeItemKind.VirtualFolder
    };
}
```

Wait — the private constructor sets properties but they're read-only (`{ get; }`). We need to use `init` setters or make the private constructor work differently. Let me revise.

Actually, looking at the code more carefully: `Name`, `FullPath`, `IsDirectory` are `{ get; }` — no setter. The private parameterless constructor can set them because C# allows setting get-only properties in the constructor. But the factory methods use object initializer syntax which won't work.

Revised approach — add a private constructor that takes all the parameters:

```csharp
private FileTreeItem(string fullPath, string name, bool isDirectory, FileTreeItemKind kind)
{
    FullPath = fullPath;
    Name = name;
    IsDirectory = isDirectory;
    Kind = kind;
}

public static FileTreeItem CreateProjectRoot(string projectName)
{
    return new FileTreeItem("", projectName, false, FileTreeItemKind.ProjectRoot);
}

public static FileTreeItem CreateVirtualFolder(string name)
{
    return new FileTreeItem("", name, false, FileTreeItemKind.VirtualFolder);
}
```

Replace the existing private parameterless constructor with this new private constructor. Update the placeholder creation in the main constructor from `new FileTreeItem("", false) { _hasPlaceholder = false }` to use a dedicated placeholder factory:

```csharp
private static FileTreeItem CreatePlaceholder()
{
    var item = new FileTreeItem("", "", false, FileTreeItemKind.File);
    return item;
}
```

And update the main constructor's placeholder line to:
```csharp
var placeholder = CreatePlaceholder();
Children.Add(placeholder);
_hasPlaceholder = true;
```

- [ ] **Step 5: Build to verify**

Run: `dotnet build Volt.sln`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add Volt/UI/FileTreeItem.cs
git commit -m "feat: add ItemKind to FileTreeItem for project tree support"
```

---

### Task 4: FileExplorerPanel — Project Mode

**Files:**
- Modify: `Volt/UI/FileExplorerPanel.xaml.cs`
- Modify: `Volt/UI/FileExplorerPanel.xaml`

- [ ] **Step 1: Add project mode methods to FileExplorerPanel.xaml.cs**

Add these fields and methods to `FileExplorerPanel`:

```csharp
private ProjectManager? _projectManager;

public void SetProjectManager(ProjectManager manager)
{
    _projectManager = manager;
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

private void RebuildProjectTree(Project project)
{
    var rootChildren = new ObservableCollection<FileTreeItem>();

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
                vfItem.Children.Add(dirItem);
            }
        }
        rootChildren.Add(vfItem);
    }

    // Add unassigned real folders at root level
    var unassigned = project.Folders
        .Where(f => f.VirtualParent == null)
        .ToList();
    foreach (var folder in unassigned)
    {
        if (Directory.Exists(folder.Path))
        {
            var dirItem = FileTreeItem.CreateRootItem(folder.Path);
            rootChildren.Add(dirItem);
        }
    }

    FolderTree.ItemsSource = rootChildren;
}
```

Add the `using System.Collections.ObjectModel;` and `using System.IO;` imports at the top if not already present.

- [ ] **Step 2: Update the XAML to support item kind icons and context menus**

In `FileExplorerPanel.xaml`, replace the `TreeView.ItemTemplate` section (the `HierarchicalDataTemplate`) with:

```xml
<TreeView.Resources>
    <HierarchicalDataTemplate DataType="{x:Type local:FileTreeItem}"
                              ItemsSource="{Binding Children}">
        <TextBlock Text="{Binding Name}" Margin="2,0">
            <TextBlock.Style>
                <Style TargetType="TextBlock">
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding Kind}" Value="VirtualFolder">
                            <Setter Property="FontStyle" Value="Italic"/>
                            <Setter Property="Foreground" Value="{DynamicResource ThemeExplorerHeaderFg}"/>
                        </DataTrigger>
                        <DataTrigger Binding="{Binding Kind}" Value="ProjectRoot">
                            <Setter Property="FontWeight" Value="SemiBold"/>
                            <Setter Property="Foreground" Value="{DynamicResource ThemeExplorerHeaderFg}"/>
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </TextBlock.Style>
        </TextBlock>
    </HierarchicalDataTemplate>
</TreeView.Resources>
```

- [ ] **Step 3: Add context menu support to the TreeView**

Add a right-click handler to the TreeView in `FileExplorerPanel.xaml`:

```xml
<TreeView x:Name="FolderTree"
          Background="Transparent" BorderThickness="0"
          ... existing attributes ...
          MouseRightButtonUp="OnTreeRightClick">
```

Add the handler in `FileExplorerPanel.xaml.cs`:

```csharp
public event Action? AddFolderRequested;
public event Action<string>? RemoveFolderRequested;
public event Action? NewVirtualFolderRequested;
public event Action<string>? RemoveVirtualFolderRequested;
public event Action<string>? RenameVirtualFolderRequested;
public event Action<string, string?>? MoveToVirtualFolderRequested;
public event Action? CloseProjectRequested;

private void OnTreeRightClick(object sender, MouseButtonEventArgs e)
{
    if (_projectManager?.CurrentProject == null) return;

    var item = FolderTree.SelectedItem as FileTreeItem;
    if (item == null) return;

    var menu = new ContextMenu();
    var project = _projectManager.CurrentProject;

    switch (item.Kind)
    {
        case FileTreeItemKind.VirtualFolder:
            menu.Items.Add(CreateMenuItem("Add Folder...", () => AddFolderRequested?.Invoke()));
            menu.Items.Add(CreateMenuItem("Rename", () => RenameVirtualFolderRequested?.Invoke(item.Name)));
            menu.Items.Add(CreateMenuItem("Remove Virtual Folder", () => RemoveVirtualFolderRequested?.Invoke(item.Name)));
            break;

        case FileTreeItemKind.Directory when IsTopLevelProjectFolder(item):
            if (project.VirtualFolders.Count > 0)
            {
                var moveMenu = new MenuItem { Header = "Move to Virtual Folder" };
                // Option to move to root (no virtual parent)
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

    // Always add project-level actions at bottom for virtual folders
    if (item.Kind == FileTreeItemKind.VirtualFolder)
    {
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Close Project", () => CloseProjectRequested?.Invoke()));
    }

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
```

- [ ] **Step 4: Add a right-click handler for the explorer background (empty area)**

Add a context menu to the panel background for project-root-level actions. In `FileExplorerPanel.xaml.cs`, update the constructor:

```csharp
public FileExplorerPanel()
{
    InitializeComponent();
    FolderTree.MouseDoubleClick += OnTreeDoubleClick;
    FolderTree.ContextMenuOpening += OnContextMenuOpening;
}

private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
{
    // Let the right-click handler manage context menus instead
    e.Handled = true;
}
```

Add a separate background right-click on the panel itself (the DockPanel or an empty area). Actually, a simpler approach: when no item is selected on right-click, show the project-root menu. Update `OnTreeRightClick`:

At the top of `OnTreeRightClick`, change the null-item handling:

```csharp
private void OnTreeRightClick(object sender, MouseButtonEventArgs e)
{
    if (_projectManager?.CurrentProject == null) return;

    var item = FolderTree.SelectedItem as FileTreeItem;
    var menu = new ContextMenu();
    var project = _projectManager.CurrentProject;

    if (item == null)
    {
        // Right-clicked empty area — show project root actions
        menu.Items.Add(CreateMenuItem("Add Folder...", () => AddFolderRequested?.Invoke()));
        menu.Items.Add(CreateMenuItem("New Virtual Folder", () => NewVirtualFolderRequested?.Invoke()));
        menu.Items.Add(new Separator());
        menu.Items.Add(CreateMenuItem("Close Project", () => CloseProjectRequested?.Invoke()));
        FolderTree.ContextMenu = menu;
        menu.IsOpen = true;
        e.Handled = true;
        return;
    }

    // ... rest of switch statement as above ...
```

Also add project-root actions when right-clicking a virtual folder or top-level directory — add "Add Folder..." and "New Virtual Folder" to the VirtualFolder case:

The VirtualFolder case already has "Add Folder...". For the directory case, no additional items needed. This is fine as-is.

- [ ] **Step 5: Build to verify**

Run: `dotnet build Volt.sln`
Expected: Build succeeded.

- [ ] **Step 6: Commit**

```bash
git add Volt/UI/FileExplorerPanel.xaml Volt/UI/FileExplorerPanel.xaml.cs
git commit -m "feat: add project mode to FileExplorerPanel with context menus"
```

---

### Task 5: AppSettings — Add LastOpenProjectPath

**Files:**
- Modify: `Volt/AppSettings.cs`

- [ ] **Step 1: Add LastOpenProjectPath to AppSettings**

Add a new property to the `AppSettings` class (after `WindowMaximized`):

```csharp
public string? LastOpenProjectPath { get; set; }
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build Volt.sln`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add Volt/AppSettings.cs
git commit -m "feat: add LastOpenProjectPath to AppSettings"
```

---

### Task 6: MainWindow — File Menu Items

**Files:**
- Modify: `Volt/UI/MainWindow.xaml`

- [ ] **Step 1: Add project menu items to the File menu**

In `MainWindow.xaml`, after the "Open _Folder..." menu item (line 157), add a separator and four project menu items:

```xml
<MenuItem Header="Open _Folder..." InputGestureText="Ctrl+Shift+O" Click="OnOpenFolder"/>
<Separator/>
<MenuItem Header="New Pro_ject" Click="OnNewProject"/>
<MenuItem Header="Open Pr_oject..." Click="OnOpenProject"/>
<MenuItem x:Name="MenuSaveProject" Header="Save Pro_ject" Click="OnSaveProject" IsEnabled="False"/>
<MenuItem x:Name="MenuCloseProject" Header="Close Projec_t" Click="OnCloseProject" IsEnabled="False"/>
<Separator/>
<MenuItem Header="_Save" InputGestureText="Ctrl+S" Click="OnSave">
```

Note: remove the existing `<Separator/>` that was between "Open Folder" and "Save" since we're adding our own separators.

- [ ] **Step 2: Build to verify**

Run: `dotnet build Volt.sln`
Expected: Build may fail due to missing click handlers — that's expected, we'll add them in the next task.

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/MainWindow.xaml
git commit -m "feat: add project menu items to File menu"
```

---

### Task 7: MainWindow — Project Integration

**Files:**
- Modify: `Volt/UI/MainWindow.xaml.cs`

This is the largest task — it wires up the ProjectManager, menu handlers, session integration, and mode exclusivity.

- [ ] **Step 1: Add ProjectManager field and initialization**

Add a field near the top of MainWindow where other fields are declared:

```csharp
private readonly ProjectManager _projectManager = new();
```

In the constructor, after `ExplorerPanel.FileOpenRequested += OnExplorerFileOpen;` (or wherever explorer is initialized), add:

```csharp
ExplorerPanel.SetProjectManager(_projectManager);
ExplorerPanel.AddFolderRequested += OnProjectAddFolder;
ExplorerPanel.RemoveFolderRequested += OnProjectRemoveFolder;
ExplorerPanel.NewVirtualFolderRequested += OnProjectNewVirtualFolder;
ExplorerPanel.RemoveVirtualFolderRequested += OnProjectRemoveVirtualFolder;
ExplorerPanel.RenameVirtualFolderRequested += OnProjectRenameVirtualFolder;
ExplorerPanel.MoveToVirtualFolderRequested += OnProjectMoveToVirtualFolder;
ExplorerPanel.CloseProjectRequested += CloseCurrentProject;
```

- [ ] **Step 2: Add menu click handlers**

```csharp
private void OnNewProject(object sender, RoutedEventArgs e)
{
    var dlg = new Microsoft.Win32.SaveFileDialog
    {
        Filter = "Project Files (*.vproj)|*.vproj",
        DefaultExt = ".vproj",
        FileName = "MyProject.vproj"
    };
    if (dlg.ShowDialog() != true) return;

    // Close existing project or folder
    if (_projectManager.CurrentProject != null)
        CloseCurrentProject();
    else if (ExplorerPanel.OpenFolderPath != null)
        CloseFolderInExplorer();

    CloseAllTabs();

    _projectManager.NewProject(System.IO.Path.GetFileNameWithoutExtension(dlg.FileName));
    _projectManager.CurrentProject!.FilePath = dlg.FileName;
    _projectManager.SaveProject();

    ExplorerPanel.OpenProject(_projectManager.CurrentProject);
    SetExplorerVisible(true);
    UpdateProjectMenuState(true);

    _settings.LastOpenProjectPath = dlg.FileName;
    _settings.Save();
}

private void OnOpenProject(object sender, RoutedEventArgs e)
{
    var dlg = new Microsoft.Win32.OpenFileDialog
    {
        Filter = "Project Files (*.vproj)|*.vproj",
        DefaultExt = ".vproj"
    };
    if (dlg.ShowDialog() != true) return;

    OpenProjectFromPath(dlg.FileName);
}

private void OnSaveProject(object sender, RoutedEventArgs e)
{
    if (_projectManager.CurrentProject == null) return;
    CaptureProjectSession();
    _projectManager.SaveProject();
}

private void OnCloseProject(object sender, RoutedEventArgs e)
{
    CloseCurrentProject();
}
```

- [ ] **Step 3: Add helper methods for project lifecycle**

```csharp
private void OpenProjectFromPath(string vprojPath)
{
    if (!System.IO.File.Exists(vprojPath)) return;

    // Close existing project or folder
    if (_projectManager.CurrentProject != null)
        CloseCurrentProject();
    else if (ExplorerPanel.OpenFolderPath != null)
        CloseFolderInExplorer();

    PromptSaveDirtyTabs();
    CloseAllTabs();

    var project = _projectManager.OpenProject(vprojPath);

    ExplorerPanel.OpenProject(project);
    SetExplorerVisible(true);
    UpdateProjectMenuState(true);

    _settings.LastOpenProjectPath = vprojPath;
    _settings.Editor.Explorer.PanelVisible = true;
    _settings.Save();

    // Restore session from project
    RestoreProjectSession(project);
}

private void CloseCurrentProject()
{
    if (_projectManager.CurrentProject == null) return;

    CaptureProjectSession();
    _projectManager.SaveProject();
    _projectManager.CloseProject();

    ExplorerPanel.CloseProject();
    UpdateProjectMenuState(false);

    _settings.LastOpenProjectPath = null;
    _settings.Save();

    CloseAllTabs();
    CreateTab();
    ActivateTab(_tabs[0]);
}

private void UpdateProjectMenuState(bool projectOpen)
{
    MenuSaveProject.IsEnabled = projectOpen;
    MenuCloseProject.IsEnabled = projectOpen;
}

private void CloseAllTabs()
{
    foreach (var tab in _tabs.ToList())
    {
        tab.StopWatching();
        tab.Editor.ReleaseResources();
    }
    _tabs.Clear();
    TabBar.Children.Clear();
    _activeTab = null;
}

private void PromptSaveDirtyTabs()
{
    foreach (var tab in _tabs.ToList())
    {
        if (tab.Editor.IsDirty)
        {
            ActivateTab(tab);
            PromptSaveTab(tab);
        }
    }
}
```

- [ ] **Step 4: Add project session capture and restore**

```csharp
private void CaptureProjectSession()
{
    if (_projectManager.CurrentProject == null) return;

    var sessionTabs = new List<ProjectSessionTab>();
    int activeIdx = 0;

    foreach (var t in _tabs)
    {
        if (t == _activeTab)
            activeIdx = sessionTabs.Count;

        sessionTabs.Add(new ProjectSessionTab
        {
            FilePath = t.FilePath,
            IsDirty = t.Editor.IsDirty,
            CaretLine = t.Editor.CaretLine,
            CaretCol = t.Editor.CaretCol,
            ScrollX = t.Editor.HorizontalOffset,
            ScrollY = t.Editor.VerticalOffset,
        });
    }

    _projectManager.CurrentProject.Session = new ProjectSession
    {
        Tabs = sessionTabs,
        ActiveTabIndex = activeIdx
    };
}

private void RestoreProjectSession(Project project)
{
    if (project.Session.Tabs.Count == 0)
    {
        var tab = CreateTab();
        ActivateTab(tab);
        return;
    }

    TabInfo? activeTab = null;
    for (int i = 0; i < project.Session.Tabs.Count; i++)
    {
        var st = project.Session.Tabs[i];
        if (st.FilePath != null && !System.IO.File.Exists(st.FilePath))
            continue;

        var tab = CreateTab();

        if (st.FilePath != null)
        {
            tab.FilePath = st.FilePath;
            tab.FileEncoding = FileHelper.DetectEncoding(st.FilePath);
            tab.Editor.SetContent(FileHelper.ReadAllText(st.FilePath, tab.FileEncoding));
            tab.LastKnownFileSize = new System.IO.FileInfo(st.FilePath).Length;
            tab.TailVerifyBytes = FileHelper.ReadTailVerifyBytes(st.FilePath, tab.LastKnownFileSize);
            tab.StartWatching();
        }

        if (i == project.Session.ActiveTabIndex)
            activeTab = tab;

        UpdateTabHeader(tab);
    }

    if (_tabs.Count == 0)
    {
        var tab = CreateTab();
        ActivateTab(tab);
        return;
    }

    ActivateTab(activeTab ?? _tabs[0]);

    // Defer caret/scroll restoration to after layout
    Dispatcher.InvokeAsync(() =>
    {
        for (int i = 0; i < _tabs.Count && i < project.Session.Tabs.Count; i++)
        {
            var st = project.Session.Tabs[i];
            var tab = _tabs[i];
            tab.Editor.CaretLine = st.CaretLine;
            tab.Editor.CaretCol = st.CaretCol;
            tab.ScrollHost?.ScrollToVerticalOffset(st.ScrollY);
            tab.ScrollHost?.ScrollToHorizontalOffset(st.ScrollX);
        }
    }, System.Windows.Threading.DispatcherPriority.Loaded);
}
```

- [ ] **Step 5: Update OnWindowClosing to handle project session**

In `OnWindowClosing` (line 826), add project session save before the existing session save:

```csharp
private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
{
    // Save project session if a project is open
    if (_projectManager.CurrentProject != null)
    {
        try
        {
            CaptureProjectSession();
            _projectManager.SaveProject();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Project save failed: {ex.Message}");
        }
    }

    // Try to persist session; if it fails, fall back to prompting for dirty tabs
    bool sessionSaved = false;
    try
    {
        SaveSession();
        sessionSaved = true;
    }
    catch (Exception ex)
    {
        System.Diagnostics.Debug.WriteLine($"Session save failed: {ex.Message}");
    }

    if (!sessionSaved)
    {
        foreach (var tab in _tabs.ToList())
        {
            if (tab.Editor.IsDirty)
            {
                ActivateTab(tab);
                if (!PromptSaveTab(tab))
                {
                    e.Cancel = true;
                    return;
                }
            }
        }
    }
}
```

- [ ] **Step 6: Update RestoreSession to handle project restore on startup**

At the start of `RestoreSession()`, add project restoration:

```csharp
private void RestoreSession()
{
    // Restore project if one was open
    if (_settings.LastOpenProjectPath is string projPath && System.IO.File.Exists(projPath))
    {
        OpenProjectFromPath(projPath);
        return;
    }

    // ... existing session restore code ...
}
```

- [ ] **Step 7: Update OpenFolderInExplorer to close any open project**

At the start of `OpenFolderInExplorer()`, add:

```csharp
private void OpenFolderInExplorer()
{
    // Close project if one is open (mode exclusivity)
    if (_projectManager.CurrentProject != null)
        CloseCurrentProject();

    // ... existing code ...
}
```

- [ ] **Step 8: Add context menu event handlers for project operations**

```csharp
private void OnProjectAddFolder()
{
    var dlg = new System.Windows.Forms.FolderBrowserDialog();
    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

    _projectManager.AddFolder(dlg.SelectedPath);
    ExplorerPanel.RefreshProjectTree();
    _projectManager.SaveProject();
}

private void OnProjectRemoveFolder(string path)
{
    _projectManager.RemoveFolder(path);
    ExplorerPanel.RefreshProjectTree();
    _projectManager.SaveProject();
}

private void OnProjectNewVirtualFolder()
{
    var name = PromptForInput("New Virtual Folder", "Enter a name:");
    if (string.IsNullOrWhiteSpace(name)) return;

    _projectManager.CreateVirtualFolder(name);
    ExplorerPanel.RefreshProjectTree();
    _projectManager.SaveProject();
}

private void OnProjectRemoveVirtualFolder(string name)
{
    _projectManager.RemoveVirtualFolder(name);
    ExplorerPanel.RefreshProjectTree();
    _projectManager.SaveProject();
}

private void OnProjectRenameVirtualFolder(string oldName)
{
    var newName = PromptForInput("Rename Virtual Folder", "Enter a new name:", oldName);
    if (string.IsNullOrWhiteSpace(newName) || newName == oldName) return;

    _projectManager.RenameVirtualFolder(oldName, newName);
    ExplorerPanel.RefreshProjectTree();
    _projectManager.SaveProject();
}

private void OnProjectMoveToVirtualFolder(string folderPath, string? virtualFolderName)
{
    _projectManager.AssignToVirtualFolder(folderPath, virtualFolderName);
    ExplorerPanel.RefreshProjectTree();
    _projectManager.SaveProject();
}
```

- [ ] **Step 9: Add the PromptForInput helper**

This is a simple input dialog. WPF doesn't have one built-in, so we'll create a minimal one inline using a `Window`:

```csharp
private static string? PromptForInput(string title, string prompt, string defaultValue = "")
{
    var window = new Window
    {
        Title = title,
        Width = 350,
        Height = 150,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        ResizeMode = ResizeMode.NoResize,
        WindowStyle = WindowStyle.ToolWindow
    };

    var panel = new StackPanel { Margin = new Thickness(12) };
    panel.Children.Add(new TextBlock { Text = prompt, Margin = new Thickness(0, 0, 0, 8) });
    var textBox = new TextBox { Text = defaultValue };
    textBox.SelectAll();
    panel.Children.Add(textBox);

    var btnPanel = new StackPanel
    {
        Orientation = Orientation.Horizontal,
        HorizontalAlignment = HorizontalAlignment.Right,
        Margin = new Thickness(0, 12, 0, 0)
    };
    var okBtn = new Button { Content = "OK", Width = 75, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
    var cancelBtn = new Button { Content = "Cancel", Width = 75, IsCancel = true };
    okBtn.Click += (_, _) => { window.DialogResult = true; };
    btnPanel.Children.Add(okBtn);
    btnPanel.Children.Add(cancelBtn);
    panel.Children.Add(btnPanel);

    window.Content = panel;
    window.Owner = Application.Current.MainWindow;

    textBox.Focus();

    return window.ShowDialog() == true ? textBox.Text : null;
}
```

- [ ] **Step 10: Build to verify**

Run: `dotnet build Volt.sln`
Expected: Build succeeded.

- [ ] **Step 11: Commit**

```bash
git add Volt/UI/MainWindow.xaml.cs
git commit -m "feat: wire up project lifecycle in MainWindow"
```

---

### Task 8: Command Palette — Project Commands

**Files:**
- Modify: `Volt/UI/CommandPaletteCommands.cs`

- [ ] **Step 1: Update the Build method signature**

Add project action parameters to the `Build` method. After the existing `closeFolder` parameter, add:

```csharp
Action newProject,
Action openProject,
Action saveProject,
Action closeProject,
```

- [ ] **Step 2: Add project commands to the returned list**

Add these entries after the existing "Explorer: Close Folder" command:

```csharp
new("Project: New Project", Toggle: newProject),

new("Project: Open Project...", Toggle: openProject),

new("Project: Save Project", Toggle: saveProject),

new("Project: Close Project", Toggle: closeProject),
```

- [ ] **Step 3: Update the call site in MainWindow.xaml.cs**

Find the `CommandPaletteCommands.Build(...)` call (around line 1245) and add the new arguments:

```csharp
var commands = CommandPaletteCommands.Build(
    _tabs, _settings, ThemeManager, Editor, FindBarControl, () => _settings.Save(),
    ToggleExplorer, OpenFolderInExplorer, CloseFolderInExplorer,
    () => { if (_settings.Editor.Explorer.PanelVisible) SetExplorerVisible(true); },
    () => OnNewProject(this, new RoutedEventArgs()),
    () => OnOpenProject(this, new RoutedEventArgs()),
    () => OnSaveProject(this, new RoutedEventArgs()),
    () => CloseCurrentProject());
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build Volt.sln`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add Volt/UI/CommandPaletteCommands.cs Volt/UI/MainWindow.xaml.cs
git commit -m "feat: add project commands to command palette"
```

---

### Task 9: Manual Verification

- [ ] **Step 1: Run the application**

Run: `dotnet run --project Volt/Volt.csproj`

- [ ] **Step 2: Test New Project**

1. File > New Project — should prompt for save location
2. Save as `test.vproj` somewhere
3. Explorer should show empty project with name "test"
4. Verify `test.vproj` exists on disk with valid JSON

- [ ] **Step 3: Test Add Folder**

1. Right-click in the explorer > Add Folder...
2. Select a folder with some files
3. Folder should appear in the project tree
4. Expand it — files should lazy-load
5. Double-click a file — it should open in a tab

- [ ] **Step 4: Test Virtual Folders**

1. Right-click in explorer > New Virtual Folder — enter "Source"
2. Virtual folder should appear in italic
3. Add another real folder
4. Right-click a real folder > Move to Virtual Folder > Source
5. Folder should move under the "Source" virtual group
6. Right-click the virtual folder > Rename — enter "Src"
7. Name should update

- [ ] **Step 5: Test session persistence**

1. Open a few files from the project folders
2. Place the caret at specific positions
3. Close the app
4. Reopen — project should restore with same tabs and caret positions

- [ ] **Step 6: Test mode exclusivity**

1. With a project open, File > Open Folder
2. Project should close, single folder should open
3. File > Open Project — folder should close, project should open

- [ ] **Step 7: Test Close Project**

1. File > Close Project
2. Explorer should clear, tabs should reset to one empty tab
3. Reopen — should start fresh (no project restored)
