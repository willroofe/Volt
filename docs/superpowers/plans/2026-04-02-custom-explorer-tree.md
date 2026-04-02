# Custom Explorer Tree Control Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace the WPF TreeView in FileExplorerPanel with a custom `FrameworkElement` that renders the file tree via `DrawingContext` with `IScrollInfo`, giving full control over rendering, scrolling, and hit-testing.

**Architecture:** A new `ExplorerTreeControl` class mirrors EditorControl's pattern — implements `IScrollInfo`, renders rows via `DrawingContext` using `FormattedText`, maintains a flattened list of visible tree nodes. The existing `FileExplorerPanel` UserControl stays as the XAML shell (header + new control). `FileTreeItem` data model is unchanged.

**Tech Stack:** WPF (.NET 10), DrawingContext rendering, IScrollInfo, FormattedText, Segoe MDL2 Assets icon font

---

### Task 1: Create ExplorerTreeControl with flat list, IScrollInfo, and basic rendering

**Files:**
- Create: `Volt/UI/ExplorerTreeControl.cs`

This task creates the control with all rendering and scrolling — everything except mouse interaction. It should compile and display rows, but won't respond to clicks yet.

- [ ] **Step 1: Create `ExplorerTreeControl.cs` with the full skeleton**

Create `Volt/UI/ExplorerTreeControl.cs` with:
- `FlatRow` record struct
- `IScrollInfo` implementation
- Flat list building from `FileTreeItem` tree
- `OnRender` that draws background, hover/selection highlights (rounded corners), and row content (indent, chevron, icon, name)
- `SetRootItems`, `RefreshFlatList`, `SelectedItem` public API
- Theme integration via app resources + `ThemeChanged` subscription
- `MeasureOverride` / `ArrangeOverride` for layout

```csharp
using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Volt;

public class ExplorerTreeControl : FrameworkElement, IScrollInfo
{
    private const double RowHeight = 24;
    private const double IndentWidth = 20;
    private const double ArrowZoneWidth = 20;
    private const double IconZoneWidth = 16;
    private const double IconGap = 4;
    private const double HighlightMargin = 2;
    private const double HighlightRadius = 4;

    // Segoe MDL2 Assets glyphs
    private const string ChevronRight = "\uE76C";
    private const string ChevronDown = "\uE70D";
    private const string FolderIcon = "\uED41";
    private const string FileIcon = "\uE8A5";

    private static readonly Typeface NormalTypeface = new("Segoe UI");
    private static readonly Typeface SemiBoldTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface ItalicTypeface = new(new FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface IconTypeface = new("Segoe MDL2 Assets");

    private readonly List<FlatRow> _flatRows = [];
    private ObservableCollection<FileTreeItem>? _rootItems;
    private int _hoverRowIndex = -1;
    private int _selectedRowIndex = -1;

    // IScrollInfo state
    private double _verticalOffset;
    private Size _viewport;
    private Size _extent;

    public event Action<string>? FileOpenRequested;
    public event Action<FileTreeItem>? SelectionChanged;
    public event Action<FileTreeItem?>? ItemRightClicked;

    public FileTreeItem? SelectedItem =>
        _selectedRowIndex >= 0 && _selectedRowIndex < _flatRows.Count
            ? _flatRows[_selectedRowIndex].Item
            : null;

    public ExplorerTreeControl()
    {
        ClipToBounds = true;
        Focusable = true;

        Loaded += (_, _) =>
        {
            var tm = ((App)Application.Current).ThemeManager;
            tm.ThemeChanged += OnThemeChanged;
        };
        Unloaded += (_, _) =>
        {
            var tm = ((App)Application.Current).ThemeManager;
            tm.ThemeChanged -= OnThemeChanged;
        };
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    // --- Public API ---

    public void SetRootItems(ObservableCollection<FileTreeItem>? items)
    {
        _rootItems = items;
        _selectedRowIndex = -1;
        _hoverRowIndex = -1;
        _verticalOffset = 0;
        RebuildFlatList();
    }

    public void RefreshFlatList()
    {
        // Preserve selection by item reference
        var selectedItem = SelectedItem;
        RebuildFlatList();
        if (selectedItem != null)
        {
            _selectedRowIndex = _flatRows.FindIndex(r => r.Item == selectedItem);
        }
    }

    // --- Flat list ---

    private void RebuildFlatList()
    {
        _flatRows.Clear();
        if (_rootItems != null)
        {
            foreach (var item in _rootItems)
                Flatten(item, 0);
        }
        UpdateExtent();
        InvalidateVisual();
    }

    private void Flatten(FileTreeItem item, int depth)
    {
        _flatRows.Add(new FlatRow(item, depth));
        if (item.IsExpanded)
        {
            foreach (var child in item.Children)
                Flatten(child, depth + 1);
        }
    }

    // --- IScrollInfo ---

    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _verticalOffset;

    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));
        if (Math.Abs(offset - _verticalOffset) < 0.01) return;
        _verticalOffset = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
    public void LineUp() => SetVerticalOffset(_verticalOffset - RowHeight);
    public void LineDown() => SetVerticalOffset(_verticalOffset + RowHeight);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageUp() => SetVerticalOffset(_verticalOffset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_verticalOffset + _viewport.Height);
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelUp() => SetVerticalOffset(_verticalOffset - RowHeight * 3);
    public void MouseWheelDown() => SetVerticalOffset(_verticalOffset + RowHeight * 3);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    private void UpdateExtent()
    {
        var newExtent = new Size(_viewport.Width, _flatRows.Count * RowHeight);
        if (Math.Abs(newExtent.Height - _extent.Height) > 0.5)
        {
            _extent = newExtent;
            // Clamp offset if extent shrank
            if (_verticalOffset > Math.Max(0, _extent.Height - _viewport.Height))
            {
                _verticalOffset = Math.Max(0, _extent.Height - _viewport.Height);
            }
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    // --- Layout ---

    protected override Size MeasureOverride(Size availableSize)
    {
        _viewport = new Size(
            double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);
        _extent = new Size(_viewport.Width, _flatRows.Count * RowHeight);
        ScrollOwner?.InvalidateScrollInfo();
        return _viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewport = finalSize;
        _extent = new Size(_viewport.Width, _flatRows.Count * RowHeight);
        // Clamp offset on resize
        var maxOffset = Math.Max(0, _extent.Height - _viewport.Height);
        if (_verticalOffset > maxOffset)
        {
            _verticalOffset = maxOffset;
        }
        ScrollOwner?.InvalidateScrollInfo();
        return finalSize;
    }

    // --- Rendering ---

    protected override void OnRender(DrawingContext dc)
    {
        var bg = GetBrush("ThemeExplorerBg");
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_flatRows.Count == 0) return;

        int firstVisible = Math.Max(0, (int)(_verticalOffset / RowHeight));
        int lastVisible = Math.Min(_flatRows.Count - 1,
            firstVisible + (int)Math.Ceiling(_viewport.Height / RowHeight));

        var hoverBrush = GetBrush("ThemeExplorerItemHover");
        var selectedBrush = GetBrush("ThemeExplorerItemSelected");
        var textBrush = GetBrush("ThemeTextFg");
        var mutedBrush = GetBrush("ThemeTextFgMuted");
        var headerFgBrush = GetBrush("ThemeExplorerHeaderFg");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int i = firstVisible; i <= lastVisible; i++)
        {
            var row = _flatRows[i];
            double y = i * RowHeight - _verticalOffset;
            double indent = row.Depth * IndentWidth;

            // Hover / selection highlight (rounded corners, with margin)
            if (i == _selectedRowIndex)
            {
                dc.DrawRoundedRectangle(selectedBrush, null,
                    new Rect(HighlightMargin, y, ActualWidth - HighlightMargin * 2, RowHeight),
                    HighlightRadius, HighlightRadius);
            }
            else if (i == _hoverRowIndex)
            {
                dc.DrawRoundedRectangle(hoverBrush, null,
                    new Rect(HighlightMargin, y, ActualWidth - HighlightMargin * 2, RowHeight),
                    HighlightRadius, HighlightRadius);
            }

            double x = indent;
            bool hasChildren = row.Item.IsDirectory ||
                               row.Item.Kind == FileTreeItemKind.VirtualFolder ||
                               row.Item.Kind == FileTreeItemKind.ProjectRoot;

            // Arrow chevron
            if (hasChildren)
            {
                string chevron = row.Item.IsExpanded ? ChevronDown : ChevronRight;
                var arrowText = new FormattedText(chevron, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, IconTypeface, 8, mutedBrush, dpi);
                double arrowX = x + (ArrowZoneWidth - arrowText.Width) / 2;
                double arrowY = y + (RowHeight - arrowText.Height) / 2;
                dc.DrawText(arrowText, new Point(arrowX, arrowY));
            }
            x += ArrowZoneWidth;

            // Icon (only for directories and files, not project root or virtual folders)
            if (row.Item.Kind == FileTreeItemKind.Directory || row.Item.Kind == FileTreeItemKind.File)
            {
                string icon = row.Item.Kind == FileTreeItemKind.Directory ? FolderIcon : FileIcon;
                var iconBrush = row.Item.Kind == FileTreeItemKind.Directory ? textBrush : mutedBrush;
                var iconText = new FormattedText(icon, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, IconTypeface, 12, iconBrush, dpi);
                double iconX = x + (IconZoneWidth - iconText.Width) / 2;
                double iconY = y + (RowHeight - iconText.Height) / 2;
                dc.DrawText(iconText, new Point(iconX, iconY));
                x += IconZoneWidth + IconGap;
            }

            // Name text
            Brush nameBrush;
            Typeface nameTypeface;
            switch (row.Item.Kind)
            {
                case FileTreeItemKind.ProjectRoot:
                    nameBrush = headerFgBrush;
                    nameTypeface = SemiBoldTypeface;
                    break;
                case FileTreeItemKind.VirtualFolder:
                    nameBrush = headerFgBrush;
                    nameTypeface = ItalicTypeface;
                    break;
                default:
                    nameBrush = textBrush;
                    nameTypeface = NormalTypeface;
                    break;
            }

            double maxTextWidth = Math.Max(0, ActualWidth - x - 8);
            var nameText = new FormattedText(row.Item.Name, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, nameTypeface, 13, nameBrush, dpi)
            {
                MaxTextWidth = maxTextWidth > 0 ? maxTextWidth : 1,
                Trimming = TextTrimming.CharacterEllipsis,
                MaxLineCount = 1
            };
            double nameY = y + (RowHeight - nameText.Height) / 2;
            dc.DrawText(nameText, new Point(x, nameY));
        }
    }

    private static Brush GetBrush(string key) =>
        (Brush)Application.Current.Resources[key];

    // --- Mouse interaction (Task 2) ---
    // Will be added in Task 2

    readonly record struct FlatRow(FileTreeItem Item, int Depth);
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build Volt.sln`
Expected: Build succeeds. The control isn't used in any XAML yet, so no runtime behavior to verify.

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/ExplorerTreeControl.cs
git commit -m "feat: add ExplorerTreeControl with rendering and IScrollInfo"
```

---

### Task 2: Add mouse interaction to ExplorerTreeControl

**Files:**
- Modify: `Volt/UI/ExplorerTreeControl.cs`

Adds hover tracking, click-to-select, click-arrow-to-expand/collapse, double-click-to-open, right-click, and tooltip for truncated names.

- [ ] **Step 1: Add the mouse event handler methods**

Add these methods to `ExplorerTreeControl`, replacing the `// --- Mouse interaction (Task 2) ---` comment:

```csharp
    // --- Mouse interaction ---

    private int HitTestRow(MouseEventArgs e)
    {
        double y = e.GetPosition(this).Y;
        int row = (int)((y + _verticalOffset) / RowHeight);
        return row >= 0 && row < _flatRows.Count ? row : -1;
    }

    private bool IsInArrowZone(MouseEventArgs e, int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _flatRows.Count) return false;
        var row = _flatRows[rowIndex];
        double x = e.GetPosition(this).X;
        double indent = row.Depth * IndentWidth;
        return x >= indent && x < indent + ArrowZoneWidth;
    }

    private bool HasChildren(FileTreeItem item) =>
        item.IsDirectory ||
        item.Kind == FileTreeItemKind.VirtualFolder ||
        item.Kind == FileTreeItemKind.ProjectRoot;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        int row = HitTestRow(e);
        if (row < 0) return;

        var item = _flatRows[row].Item;

        // Click in arrow zone toggles expand/collapse
        if (HasChildren(item) && IsInArrowZone(e, row))
        {
            item.IsExpanded = !item.IsExpanded;
            RefreshFlatList();
            e.Handled = true;
            return;
        }

        // Select the row
        if (_selectedRowIndex != row)
        {
            _selectedRowIndex = row;
            InvalidateVisual();
            SelectionChanged?.Invoke(item);
        }
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    protected override void OnMouseDoubleClick(MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        int row = HitTestRow(e);
        if (row < 0) return;

        var item = _flatRows[row].Item;

        // Double-click on a directory/expandable node toggles it
        if (HasChildren(item))
        {
            item.IsExpanded = !item.IsExpanded;
            RefreshFlatList();
            e.Handled = true;
            return;
        }

        // Double-click on a file opens it
        if (item.Kind == FileTreeItemKind.File && !string.IsNullOrEmpty(item.FullPath))
        {
            FileOpenRequested?.Invoke(item.FullPath);
            e.Handled = true;
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        int row = HitTestRow(e);

        if (row >= 0)
        {
            var item = _flatRows[row].Item;
            if (_selectedRowIndex != row)
            {
                _selectedRowIndex = row;
                InvalidateVisual();
                SelectionChanged?.Invoke(item);
            }
            ItemRightClicked?.Invoke(item);
        }
        else
        {
            // Right-clicked empty area — fire with null so panel can show project root menu
            _selectedRowIndex = -1;
            InvalidateVisual();
            ItemRightClicked?.Invoke(null);
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int row = HitTestRow(e);
        if (row != _hoverRowIndex)
        {
            _hoverRowIndex = row;
            InvalidateVisual();
        }

        // Tooltip for truncated names
        if (row >= 0 && row < _flatRows.Count)
        {
            var item = _flatRows[row].Item;
            double indent = _flatRows[row].Depth * IndentWidth + ArrowZoneWidth;
            if (item.Kind == FileTreeItemKind.Directory || item.Kind == FileTreeItemKind.File)
                indent += IconZoneWidth + IconGap;
            double maxTextWidth = Math.Max(0, ActualWidth - indent - 8);
            var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
            var typeface = item.Kind switch
            {
                FileTreeItemKind.ProjectRoot => SemiBoldTypeface,
                FileTreeItemKind.VirtualFolder => ItalicTypeface,
                _ => NormalTypeface
            };
            var ft = new FormattedText(item.Name, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, typeface, 13, Brushes.Black, dpi);
            ToolTip = ft.Width > maxTextWidth
                ? (item.Kind == FileTreeItemKind.File ? item.FullPath : item.Name)
                : null;
        }
        else
        {
            ToolTip = null;
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hoverRowIndex != -1)
        {
            _hoverRowIndex = -1;
            ToolTip = null;
            InvalidateVisual();
        }
    }
```

- [ ] **Step 2: Remove the `hasChildren` local variable from `OnRender` and use the new `HasChildren` method**

In `OnRender`, find this block:

```csharp
            bool hasChildren = row.Item.IsDirectory ||
                               row.Item.Kind == FileTreeItemKind.VirtualFolder ||
                               row.Item.Kind == FileTreeItemKind.ProjectRoot;

            // Arrow chevron
            if (hasChildren)
```

Replace with:

```csharp
            // Arrow chevron
            if (HasChildren(row.Item))
```

- [ ] **Step 3: Build to verify it compiles**

Run: `dotnet build Volt.sln`
Expected: Build succeeds.

- [ ] **Step 4: Commit**

```bash
git add Volt/UI/ExplorerTreeControl.cs
git commit -m "feat: add mouse interaction to ExplorerTreeControl"
```

---

### Task 3: Replace TreeView with ExplorerTreeControl in FileExplorerPanel XAML

**Files:**
- Modify: `Volt/UI/FileExplorerPanel.xaml`

Replace the entire TreeView block (including the TreeViewItem style in UserControl.Resources and the TreeView element) with the new control inside a ScrollViewer.

- [ ] **Step 1: Replace the XAML content**

Replace the entire contents of `Volt/UI/FileExplorerPanel.xaml` with:

```xml
<UserControl x:Class="Volt.FileExplorerPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:Volt">
    <DockPanel Background="{DynamicResource ThemeExplorerBg}">
        <!-- Header -->
        <Border DockPanel.Dock="Top"
                Background="{DynamicResource ThemeExplorerHeaderBg}"
                BorderBrush="{DynamicResource ThemeTabBorder}" BorderThickness="0,0,0,1">
            <DockPanel Height="33">
                <TextBlock x:Name="HeaderText" Text="Explorer"
                           VerticalAlignment="Center" Margin="12,0"
                           FontFamily="Segoe UI" FontSize="11" FontWeight="SemiBold"
                           Foreground="{DynamicResource ThemeExplorerHeaderFg}"/>
            </DockPanel>
        </Border>
        <!-- Custom tree control -->
        <ScrollViewer x:Name="TreeScrollViewer"
                      HorizontalScrollBarVisibility="Disabled"
                      VerticalScrollBarVisibility="Auto"
                      CanContentScroll="True"
                      Focusable="False"
                      Template="{StaticResource ThemedScrollViewer}">
            <local:ExplorerTreeControl x:Name="ExplorerTree"/>
        </ScrollViewer>
    </DockPanel>
</UserControl>
```

This removes:
- The `UserControl.Resources` block with the entire `TreeViewItem` style (lines 5-58 of the old XAML)
- The `TreeView` element with its Resources, ItemTemplate, and HierarchicalDataTemplate (lines 72-105 of the old XAML)

And replaces them with a `ScrollViewer` wrapping the new `ExplorerTreeControl`.

- [ ] **Step 2: Build to verify XAML compiles**

Run: `dotnet build Volt.sln`
Expected: Build fails — the code-behind still references `FolderTree` (the old TreeView name). This is expected and will be fixed in Task 4.

- [ ] **Step 3: Commit**

```bash
git add Volt/UI/FileExplorerPanel.xaml
git commit -m "feat: replace TreeView with ExplorerTreeControl in XAML"
```

---

### Task 4: Update FileExplorerPanel code-behind to use ExplorerTreeControl

**Files:**
- Modify: `Volt/UI/FileExplorerPanel.xaml.cs`

Rewrite the code-behind to use the new control's API. The context menu logic stays mostly the same, but TreeView-specific workarounds are removed.

- [ ] **Step 1: Replace the entire code-behind**

Replace the entire contents of `Volt/UI/FileExplorerPanel.xaml.cs` with:

```csharp
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Volt;

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
        ExplorerTree.FileOpenRequested += path => FileOpenRequested?.Invoke(path);
        ExplorerTree.ItemRightClicked += OnItemRightClicked;
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
        var root = new FileTreeItem(path, true);
        root.IsExpanded = true;
        ExplorerTree.SetRootItems(new ObservableCollection<FileTreeItem> { root });
    }

    public void CloseFolder()
    {
        _openFolderPath = null;
        HeaderText.Text = "Explorer";
        ExplorerTree.SetRootItems(null);
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
        ExplorerTree.SetRootItems(null);
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
        CollectExpandedPaths(_flatItems(), expandedPaths);

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
        ExplorerTree.SetRootItems(new ObservableCollection<FileTreeItem> { projectRoot });
    }

    /// <summary>
    /// Returns the current root items for expand-state collection, or empty if none set.
    /// </summary>
    private IEnumerable<FileTreeItem> _flatItems()
    {
        // Walk the tree from whatever root items are currently displayed
        // This is used only for collecting expanded paths before a rebuild
        var items = new List<FileTreeItem>();
        CollectAllItems(ExplorerTree.SelectedItem, items);
        // We can't easily get root items back from the control, so we collect
        // from the project manager's data instead — but the expand states live
        // on the FileTreeItem objects which are about to be discarded.
        // Instead, we store root items:
        return _currentRootItems ?? [];
    }

    private ObservableCollection<FileTreeItem>? _currentRootItems;

    // Override SetRootItems to track root items for expand state collection
    private void SetTreeRootItems(ObservableCollection<FileTreeItem>? items)
    {
        _currentRootItems = items;
        ExplorerTree.SetRootItems(items);
    }

    private static void CollectAllItems(FileTreeItem? item, List<FileTreeItem> result)
    {
        if (item == null) return;
        result.Add(item);
        foreach (var child in item.Children)
            CollectAllItems(child, result);
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

    private void OnItemRightClicked(FileTreeItem? item)
    {
        if (_projectManager?.CurrentProject == null) return;

        var menu = new ContextMenu();
        var project = _projectManager.CurrentProject;

        if (item == null)
        {
            // Right-clicked empty area — show project root actions
            menu.Items.Add(CreateMenuItem("Add Folder...", () => AddFolderRequested?.Invoke(null)));
            menu.Items.Add(CreateMenuItem("New Virtual Folder", () => NewVirtualFolderRequested?.Invoke()));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Close Project", () => CloseProjectRequested?.Invoke()));
            ExplorerTree.ContextMenu = menu;
            menu.IsOpen = true;
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

        ExplorerTree.ContextMenu = menu;
        menu.IsOpen = true;
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
}
```

Wait — there's a problem with the expand state collection. The old code used `FolderTree.ItemsSource` to get the current items. The new control doesn't expose root items. We need to track them.

Let me fix this. Replace the `_flatItems()` method and the `_currentRootItems` field with a cleaner approach. The `RebuildProjectTree` method should use `_currentRootItems` to collect expand states, and `OpenFolder`/`CloseFolder`/`OpenProject`/`CloseProject` should all go through a helper that tracks root items.

Actually, let me simplify this significantly. Here is the corrected full file:

```csharp
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace Volt;

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
    private ObservableCollection<FileTreeItem>? _currentRootItems;

    public FileExplorerPanel()
    {
        InitializeComponent();
        ExplorerTree.FileOpenRequested += path => FileOpenRequested?.Invoke(path);
        ExplorerTree.ItemRightClicked += OnItemRightClicked;
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
        var root = new FileTreeItem(path, true);
        root.IsExpanded = true;
        var items = new ObservableCollection<FileTreeItem> { root };
        _currentRootItems = items;
        ExplorerTree.SetRootItems(items);
    }

    public void CloseFolder()
    {
        _openFolderPath = null;
        HeaderText.Text = "Explorer";
        _currentRootItems = null;
        ExplorerTree.SetRootItems(null);
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
        _currentRootItems = null;
        ExplorerTree.SetRootItems(null);
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
        if (_currentRootItems != null)
            CollectExpandedPaths(_currentRootItems, expandedPaths);

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
        var items = new ObservableCollection<FileTreeItem> { projectRoot };
        _currentRootItems = items;
        ExplorerTree.SetRootItems(items);
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

    private void OnItemRightClicked(FileTreeItem? item)
    {
        if (_projectManager?.CurrentProject == null) return;

        var menu = new ContextMenu();
        var project = _projectManager.CurrentProject;

        if (item == null)
        {
            menu.Items.Add(CreateMenuItem("Add Folder...", () => AddFolderRequested?.Invoke(null)));
            menu.Items.Add(CreateMenuItem("New Virtual Folder", () => NewVirtualFolderRequested?.Invoke()));
            menu.Items.Add(new Separator());
            menu.Items.Add(CreateMenuItem("Close Project", () => CloseProjectRequested?.Invoke()));
            ExplorerTree.ContextMenu = menu;
            menu.IsOpen = true;
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
                return;
        }

        ExplorerTree.ContextMenu = menu;
        menu.IsOpen = true;
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
}
```

Key changes from the old code-behind:
- `FolderTree` references → `ExplorerTree` references
- `FolderTree.ItemsSource = ...` → `ExplorerTree.SetRootItems(...)`
- `FolderTree.MouseDoubleClick` handler removed — `ExplorerTree.FileOpenRequested` used instead
- `FolderTree.ContextMenuOpening` suppression removed — not needed
- `OnTreeDoubleClick` method removed entirely
- `OnTreeRightClick` replaced by `OnItemRightClicked` — receives `FileTreeItem?` directly, no `FindVisualParent<TreeViewItem>` needed
- `FindVisualParent<T>` helper removed entirely
- `_currentRootItems` field added to track root items for expand state collection
- `OpenFolder` creates a single root `FileTreeItem` and wraps in `ObservableCollection` (the old code used `FileTreeItem.LoadRoot` which returned the expanded children directly — now we pass the root item itself so the control can render it)
- Removed `using System.Windows.Input` and `using System.Windows.Media` (no longer needed)

- [ ] **Step 2: Update `OpenFolder` to match new behavior**

Note: The old `OpenFolder` used `FileTreeItem.LoadRoot(path)` which returns the *children* of the root (the root itself is expanded and discarded). The new code passes the root `FileTreeItem` itself to `SetRootItems`, so the root folder shows as a node in the tree (matching how projects show a project root node). This is a minor behavior change — the folder name will appear as a top-level expandable node instead of its children being shown at the top level. This is consistent with VS Code's behavior and with how projects already work.

If the old behavior (children at top level, no root node) is preferred, use this instead:

```csharp
public void OpenFolder(string path)
{
    if (!Directory.Exists(path)) return;
    _openFolderPath = path;
    HeaderText.Text = Path.GetFileName(path);
    var items = FileTreeItem.LoadRoot(path);
    _currentRootItems = items;
    ExplorerTree.SetRootItems(items);
}
```

The implementer should use the first version (root node visible) for consistency with project mode.

- [ ] **Step 3: Build**

Run: `dotnet build Volt.sln`
Expected: Build succeeds. The MainWindow code-behind still references `ExplorerPanel.FolderTree` in `OnTreeDoubleClick` wiring — but checking the MainWindow code, it actually subscribes to `ExplorerPanel.FileOpenRequested`, not `FolderTree` directly. So this should compile.

- [ ] **Step 4: Commit**

```bash
git add Volt/UI/FileExplorerPanel.xaml Volt/UI/FileExplorerPanel.xaml.cs
git commit -m "feat: wire ExplorerTreeControl into FileExplorerPanel"
```

---

### Task 5: Update MainWindow references and verify end-to-end

**Files:**
- Modify: `Volt/UI/MainWindow.xaml.cs` (if any references to old TreeView API exist)

- [ ] **Step 1: Search for any remaining `FolderTree` references in MainWindow**

Run: `grep -n "FolderTree" Volt/UI/MainWindow.xaml.cs`

If there are any hits, they need to be updated. The MainWindow should only interact with `FileExplorerPanel`'s public API (events and methods), not the internal tree control. If all references go through `ExplorerPanel.FileOpenRequested`, `ExplorerPanel.OpenFolder()`, etc., no changes are needed.

- [ ] **Step 2: Check that `ExplorerPanel.RefreshProjectTree()` is called after project tree mutations**

In `MainWindow.xaml.cs`, every project mutation handler (add folder, remove folder, virtual folder operations) should call `ExplorerPanel.RefreshProjectTree()` after modifying the project. This was already the case with the old code, but verify it still works since `RefreshProjectTree` now calls `ExplorerTree.SetRootItems` instead of setting `FolderTree.ItemsSource`.

Actually, `RefreshProjectTree` calls `RebuildProjectTree` which calls `ExplorerTree.SetRootItems` — this rebuilds the flat list and triggers a re-render. This is correct.

- [ ] **Step 3: Build and run the application**

Run: `dotnet build Volt.sln`
Expected: Build succeeds with no errors.

Run: `dotnet run --project Volt/Volt.csproj`

Manual test checklist:
1. Open a folder via File > Open Folder — tree displays with folder contents, expand/collapse works
2. Hover over rows — rounded highlight appears
3. Click a row — selection highlight with rounded corners
4. Double-click a file — file opens in a tab
5. Right-click a file — no context menu (expected, no project mode)
6. Open/create a project, add folders — project tree displays with project root node
7. Right-click project root — context menu appears (Add Folder, New Virtual Folder, Close Project)
8. Right-click virtual folder — context menu appears (Add Folder, Rename, Remove)
9. Right-click a top-level project folder — context menu appears (Move to VF, Remove from Project)
10. Expand/collapse nodes — tree updates correctly, scroll adjusts
11. Scroll with mouse wheel — smooth scrolling
12. Long file names — truncated with ellipsis, tooltip shows full name on hover
13. Switch themes — tree re-renders with new colors immediately

- [ ] **Step 4: Commit**

```bash
git add -A
git commit -m "feat: complete custom explorer tree control integration"
```
