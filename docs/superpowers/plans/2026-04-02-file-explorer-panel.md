# File Explorer Panel Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a resizable, togglable file explorer panel to the left (or right) of the editor that displays a lazy-loading folder tree view.

**Architecture:** A new `FileExplorerPanel` UserControl with a styled WPF TreeView and `FileTreeItem` view model. MainWindow's `EditorHost` area is replaced with a 3-column Grid (explorer + splitter + editor). Theme integration follows existing DynamicResource pattern. Settings, command palette, and keyboard shortcut wire everything together.

**Tech Stack:** WPF (.NET 10), MVVM-lite (ObservableCollection, INotifyPropertyChanged), existing theming system

---

### Task 1: Add ExplorerSettings to AppSettings

**Files:**
- Modify: `TextEdit/AppSettings.cs:30-36` (EditorSettings class) and `TextEdit/AppSettings.cs:109-113` (static options)

- [ ] **Step 1: Add ExplorerSettings class**

Add after the `FindSettings` class (line 28):

```csharp
public class ExplorerSettings
{
    public string PanelSide { get; set; } = "Left";
    public double PanelWidth { get; set; } = 250;
    public bool PanelVisible { get; set; } = false;
    public string? OpenFolderPath { get; set; }
}
```

- [ ] **Step 2: Add Explorer property to EditorSettings**

In the `EditorSettings` class, add after the `Find` property:

```csharp
public ExplorerSettings Explorer { get; set; } = new();
```

- [ ] **Step 3: Add PanelSideOptions array**

In the `AppSettings` class, add after `LineHeightOptions`:

```csharp
public static readonly string[] PanelSideOptions = ["Left", "Right"];
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build TextEdit.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 5: Commit**

```bash
git add TextEdit/AppSettings.cs
git commit -m "feat: add ExplorerSettings to AppSettings"
```

---

### Task 2: Add Explorer theme colors to ColorTheme and theme JSONs

**Files:**
- Modify: `TextEdit/Theme/ColorTheme.cs:23-47` (ChromeColors class)
- Modify: `TextEdit/Theme/ThemeManager.cs:123-149` (UpdateAppResources)
- Modify: `TextEdit/App.xaml:7-29` (default resources)
- Modify: `TextEdit/Resources/Themes/default-dark.json`
- Modify: `TextEdit/Resources/Themes/default-light.json`
- Modify: `TextEdit/Resources/Themes/gruvbox-dark.json`

- [ ] **Step 1: Add explorer properties to ChromeColors**

Add after the `tabBorder` property (line 46):

```csharp
[JsonPropertyName("explorerBackground")] public string ExplorerBackground { get; set; } = "#F0F0F0";
[JsonPropertyName("explorerHeaderBackground")] public string ExplorerHeaderBackground { get; set; } = "#E8E8E8";
[JsonPropertyName("explorerHeaderForeground")] public string ExplorerHeaderForeground { get; set; } = "#888888";
[JsonPropertyName("explorerItemHover")] public string ExplorerItemHover { get; set; } = "#E0E0E0";
[JsonPropertyName("explorerItemSelected")] public string ExplorerItemSelected { get; set; } = "#D0D0D0";
```

- [ ] **Step 2: Add explorer resource mappings in ThemeManager.UpdateAppResources**

Add after the `ThemeTabBorder` line (line 148):

```csharp
res["ThemeExplorerBg"] = ColorTheme.ParseBrush(c.ExplorerBackground);
res["ThemeExplorerHeaderBg"] = ColorTheme.ParseBrush(c.ExplorerHeaderBackground);
res["ThemeExplorerHeaderFg"] = ColorTheme.ParseBrush(c.ExplorerHeaderForeground);
res["ThemeExplorerItemHover"] = ColorTheme.ParseBrush(c.ExplorerItemHover);
res["ThemeExplorerItemSelected"] = ColorTheme.ParseBrush(c.ExplorerItemSelected);
```

- [ ] **Step 3: Add default resource entries in App.xaml**

Add after the `ThemeTabBorder` brush (line 29):

```xml
<SolidColorBrush x:Key="ThemeExplorerBg" Color="#F0F0F0"/>
<SolidColorBrush x:Key="ThemeExplorerHeaderBg" Color="#E8E8E8"/>
<SolidColorBrush x:Key="ThemeExplorerHeaderFg" Color="#888888"/>
<SolidColorBrush x:Key="ThemeExplorerItemHover" Color="#E0E0E0"/>
<SolidColorBrush x:Key="ThemeExplorerItemSelected" Color="#D0D0D0"/>
```

- [ ] **Step 4: Add explorer colors to default-dark.json**

Add inside the `"chrome"` object after `"tabBorder"`:

```json
"explorerBackground": "#000000",
"explorerHeaderBackground": "#0A0A0A",
"explorerHeaderForeground": "#555555",
"explorerItemHover": "#1A1A1A",
"explorerItemSelected": "#222222"
```

- [ ] **Step 5: Add explorer colors to default-light.json**

Add inside the `"chrome"` object after `"tabBorder"`:

```json
"explorerBackground": "#FFFFFF",
"explorerHeaderBackground": "#F5F5F5",
"explorerHeaderForeground": "#888888",
"explorerItemHover": "#F0F0F0",
"explorerItemSelected": "#E0E0E0"
```

- [ ] **Step 6: Add explorer colors to gruvbox-dark.json**

Add inside the `"chrome"` object after `"tabBorder"`:

```json
"explorerBackground": "#282828",
"explorerHeaderBackground": "#1D2021",
"explorerHeaderForeground": "#665C54",
"explorerItemHover": "#3C3836",
"explorerItemSelected": "#504945"
```

- [ ] **Step 7: Build and verify**

Run: `dotnet build TextEdit.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 8: Commit**

```bash
git add TextEdit/Theme/ColorTheme.cs TextEdit/Theme/ThemeManager.cs TextEdit/App.xaml TextEdit/Resources/Themes/default-dark.json TextEdit/Resources/Themes/default-light.json TextEdit/Resources/Themes/gruvbox-dark.json
git commit -m "feat: add explorer theme colors to all themes"
```

---

### Task 3: Create FileTreeItem view model

**Files:**
- Create: `TextEdit/UI/FileTreeItem.cs`

- [ ] **Step 1: Create the FileTreeItem class**

Create `TextEdit/UI/FileTreeItem.cs`:

```csharp
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;

namespace TextEdit;

public class FileTreeItem : INotifyPropertyChanged
{
    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }

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

        if (isDirectory)
        {
            // Add dummy placeholder so the expand arrow appears
            Children.Add(new FileTreeItem("", false) { _hasPlaceholder = false });
            _hasPlaceholder = true;
        }
    }

    /// <summary>
    /// Creates a dummy placeholder item (used internally to show the expand arrow).
    /// </summary>
    private FileTreeItem()
    {
        FullPath = "";
        Name = "";
        IsDirectory = false;
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
}
```

- [ ] **Step 2: Build and verify**

Run: `dotnet build TextEdit.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 3: Commit**

```bash
git add TextEdit/UI/FileTreeItem.cs
git commit -m "feat: add FileTreeItem view model with lazy loading"
```

---

### Task 4: Create FileExplorerPanel UserControl

**Files:**
- Create: `TextEdit/UI/FileExplorerPanel.xaml`
- Create: `TextEdit/UI/FileExplorerPanel.xaml.cs`

- [ ] **Step 1: Create FileExplorerPanel.xaml**

Create `TextEdit/UI/FileExplorerPanel.xaml`:

```xml
<UserControl x:Class="TextEdit.FileExplorerPanel"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:local="clr-namespace:TextEdit">
    <UserControl.Resources>
        <!-- TreeViewItem style for themed hover/selection -->
        <Style TargetType="TreeViewItem">
            <Setter Property="IsExpanded" Value="{Binding IsExpanded, Mode=TwoWay}"/>
            <Setter Property="FontFamily" Value="Segoe UI"/>
            <Setter Property="FontSize" Value="13"/>
            <Setter Property="Foreground" Value="{DynamicResource ThemeTextFg}"/>
            <Setter Property="Padding" Value="2,1"/>
            <Setter Property="Template">
                <Setter.Value>
                    <ControlTemplate TargetType="TreeViewItem">
                        <StackPanel>
                            <Border x:Name="Bd" Background="Transparent"
                                    Padding="{TemplateBinding Padding}"
                                    SnapsToDevicePixels="True">
                                <Grid Margin="{Binding RelativeSource={RelativeSource TemplatedParent}, Converter={x:Static local:TreeIndentConverter.Instance}}">
                                    <Grid.ColumnDefinitions>
                                        <ColumnDefinition Width="16"/>
                                        <ColumnDefinition Width="*"/>
                                    </Grid.ColumnDefinitions>
                                    <TextBlock x:Name="Arrow" Grid.Column="0"
                                               Text="&#xE76C;" FontFamily="Segoe MDL2 Assets"
                                               FontSize="8"
                                               Foreground="{DynamicResource ThemeTextFgMuted}"
                                               VerticalAlignment="Center"
                                               HorizontalAlignment="Center"
                                               Visibility="Collapsed"/>
                                    <ContentPresenter Grid.Column="1"
                                                      ContentSource="Header"
                                                      VerticalAlignment="Center"/>
                                </Grid>
                            </Border>
                            <ItemsPresenter x:Name="ItemsHost" Visibility="Collapsed"/>
                        </StackPanel>
                        <ControlTemplate.Triggers>
                            <Trigger Property="HasItems" Value="True">
                                <Setter TargetName="Arrow" Property="Visibility" Value="Visible"/>
                            </Trigger>
                            <Trigger Property="IsExpanded" Value="True">
                                <Setter TargetName="ItemsHost" Property="Visibility" Value="Visible"/>
                                <Setter TargetName="Arrow" Property="Text" Value="&#xE70D;"/>
                            </Trigger>
                            <Trigger Property="IsMouseOver" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource ThemeExplorerItemHover}"/>
                            </Trigger>
                            <Trigger Property="IsSelected" Value="True">
                                <Setter TargetName="Bd" Property="Background" Value="{DynamicResource ThemeExplorerItemSelected}"/>
                            </Trigger>
                        </ControlTemplate.Triggers>
                    </ControlTemplate>
                </Setter.Value>
            </Setter>
        </Style>
    </UserControl.Resources>
    <DockPanel Background="{DynamicResource ThemeExplorerBg}">
        <!-- Header -->
        <Border DockPanel.Dock="Top" Height="28"
                Background="{DynamicResource ThemeExplorerHeaderBg}">
            <TextBlock x:Name="HeaderText" Text="Explorer"
                       VerticalAlignment="Center" Margin="12,0"
                       FontFamily="Segoe UI" FontSize="11" FontWeight="SemiBold"
                       Foreground="{DynamicResource ThemeExplorerHeaderFg}"/>
        </Border>
        <Border DockPanel.Dock="Top" Height="1" Background="{DynamicResource ThemeBorderBrush}"/>

        <!-- Tree view -->
        <TreeView x:Name="FolderTree"
                  Background="Transparent" BorderThickness="0"
                  VirtualizingStackPanel.IsVirtualizing="True"
                  VirtualizingStackPanel.VirtualizationMode="Recycling"
                  ScrollViewer.HorizontalScrollBarVisibility="Auto"
                  ScrollViewer.VerticalScrollBarVisibility="Auto">
            <TreeView.ItemTemplate>
                <HierarchicalDataTemplate DataType="{x:Type local:FileTreeItem}"
                                          ItemsSource="{Binding Children}">
                    <TextBlock Text="{Binding Name}" Margin="2,0"/>
                </HierarchicalDataTemplate>
            </TreeView.ItemTemplate>
        </TreeView>
    </DockPanel>
</UserControl>
```

- [ ] **Step 2: Create FileExplorerPanel.xaml.cs**

Create `TextEdit/UI/FileExplorerPanel.xaml.cs`:

```csharp
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;

namespace TextEdit;

public partial class FileExplorerPanel : UserControl
{
    public event Action<string>? FileOpenRequested;

    private string? _openFolderPath;

    public FileExplorerPanel()
    {
        InitializeComponent();
        FolderTree.MouseDoubleClick += OnTreeDoubleClick;
    }

    public void OpenFolder(string path)
    {
        if (!Directory.Exists(path)) return;
        _openFolderPath = path;
        HeaderText.Text = Path.GetFileName(path);
        var rootItem = FileTreeItem.CreateRootItem(path);
        rootItem.IsExpanded = true;
        FolderTree.ItemsSource = new[] { rootItem };
    }

    public void CloseFolder()
    {
        _openFolderPath = null;
        HeaderText.Text = "Explorer";
        FolderTree.ItemsSource = null;
    }

    public string? OpenFolderPath => _openFolderPath;

    private void OnTreeDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FolderTree.SelectedItem is FileTreeItem item && !item.IsDirectory)
        {
            FileOpenRequested?.Invoke(item.FullPath);
            e.Handled = true;
        }
    }
}

/// <summary>
/// Converts TreeViewItem nesting depth to a left margin for indentation.
/// </summary>
public class TreeIndentConverter : IValueConverter
{
    public static readonly TreeIndentConverter Instance = new();
    private const double IndentSize = 16;

    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        if (value is TreeViewItem item)
        {
            int depth = GetDepth(item);
            return new Thickness(depth * IndentSize, 0, 0, 0);
        }
        return new Thickness(0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotSupportedException();

    private static int GetDepth(TreeViewItem item)
    {
        int depth = 0;
        var parent = item.Parent as ItemsControl;
        while (parent is TreeViewItem tvi)
        {
            depth++;
            parent = tvi.Parent as ItemsControl ?? ItemsControl.ItemsControlFromItemContainer(tvi);
        }
        return depth;
    }
}
```

- [ ] **Step 3: Build and verify**

Run: `dotnet build TextEdit.sln`
Expected: Build succeeds. There may be a warning about the TreeIndentConverter binding — it's OK if the converter doesn't resolve via the simple `Converter` approach; we'll fix any binding issues after visual testing.

- [ ] **Step 4: Commit**

```bash
git add TextEdit/UI/FileExplorerPanel.xaml TextEdit/UI/FileExplorerPanel.xaml.cs
git commit -m "feat: create FileExplorerPanel UserControl with themed TreeView"
```

---

### Task 5: Integrate panel into MainWindow layout

**Files:**
- Modify: `TextEdit/UI/MainWindow.xaml:286-287` (EditorHost area)
- Modify: `TextEdit/UI/MainWindow.xaml.cs` (constructor, toggle, session, open folder)

- [ ] **Step 1: Replace EditorHost with 3-column Grid in MainWindow.xaml**

Replace the single `EditorHost` Border (line 286-287):

```xml
<Border x:Name="EditorHost"
        WindowChrome.IsHitTestVisibleInChrome="True"/>
```

With a 3-column Grid:

```xml
<Grid x:Name="MainContentGrid" WindowChrome.IsHitTestVisibleInChrome="True">
    <Grid.ColumnDefinitions>
        <ColumnDefinition x:Name="ExplorerColumn" Width="0"/>
        <ColumnDefinition x:Name="SplitterColumn" Width="0"/>
        <ColumnDefinition Width="*"/>
    </Grid.ColumnDefinitions>

    <local:FileExplorerPanel x:Name="ExplorerPanel" Grid.Column="0"/>

    <GridSplitter x:Name="ExplorerSplitter" Grid.Column="1"
                  Width="3" HorizontalAlignment="Stretch"
                  Background="{DynamicResource ThemeBorderBrush}"
                  Visibility="Collapsed"/>

    <Border x:Name="EditorHost" Grid.Column="2"/>
</Grid>
```

- [ ] **Step 2: Add explorer toggle and folder methods to MainWindow.xaml.cs**

Add the following methods. Place them after `ApplySettings()` (around line 522):

```csharp
private void ToggleExplorer()
{
    bool show = ExplorerColumn.Width.Value == 0;
    SetExplorerVisible(show);
    _settings.Editor.Explorer.PanelVisible = show;
    _settings.Save();
}

private void SetExplorerVisible(bool visible)
{
    if (visible)
    {
        double width = Math.Max(_settings.Editor.Explorer.PanelWidth, 100);
        bool rightSide = _settings.Editor.Explorer.PanelSide == "Right";

        if (rightSide)
        {
            // Explorer on right: editor col 0, splitter col 1, explorer col 2
            Grid.SetColumn(EditorHost, 0);
            Grid.SetColumn(ExplorerSplitter, 1);
            Grid.SetColumn(ExplorerPanel, 2);
        }
        else
        {
            // Explorer on left (default): explorer col 0, splitter col 1, editor col 2
            Grid.SetColumn(ExplorerPanel, 0);
            Grid.SetColumn(ExplorerSplitter, 1);
            Grid.SetColumn(EditorHost, 2);
        }

        ExplorerColumn.Width = new GridLength(width);
        SplitterColumn.Width = new GridLength(3);
        ExplorerSplitter.Visibility = Visibility.Visible;
    }
    else
    {
        ExplorerColumn.Width = new GridLength(0);
        SplitterColumn.Width = new GridLength(0);
        ExplorerSplitter.Visibility = Visibility.Collapsed;
    }
}

private void OpenFolderInExplorer()
{
    var dlg = new System.Windows.Forms.FolderBrowserDialog();
    if (_settings.Editor.Explorer.OpenFolderPath is string prev && Directory.Exists(prev))
        dlg.SelectedPath = prev;

    if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;

    ExplorerPanel.OpenFolder(dlg.SelectedPath);
    _settings.Editor.Explorer.OpenFolderPath = dlg.SelectedPath;
    SetExplorerVisible(true);
    _settings.Editor.Explorer.PanelVisible = true;
    _settings.Save();
}

private void CloseFolderInExplorer()
{
    ExplorerPanel.CloseFolder();
    SetExplorerVisible(false);
    _settings.Editor.Explorer.OpenFolderPath = null;
    _settings.Editor.Explorer.PanelVisible = false;
    _settings.Save();
}
```

- [ ] **Step 3: Wire up explorer events in the constructor**

Add after the `ThemeManager.ThemeChanged` handler (around line 60):

```csharp
ExplorerPanel.FileOpenRequested += OnExplorerFileOpen;
ExplorerSplitter.DragCompleted += (_, _) =>
{
    _settings.Editor.Explorer.PanelWidth = ExplorerColumn.ActualWidth;
    _settings.Save();
};
```

- [ ] **Step 4: Add the OnExplorerFileOpen handler**

Place near the other On* handlers:

```csharp
private void OnExplorerFileOpen(string path)
{
    var fullPath = Path.GetFullPath(path);

    // Switch to existing tab if already open
    var existing = _tabs.FirstOrDefault(t =>
        t.FilePath != null && string.Equals(Path.GetFullPath(t.FilePath), fullPath, StringComparison.OrdinalIgnoreCase));
    if (existing != null)
    {
        ActivateTab(existing);
        return;
    }

    // Reuse current tab if untitled and clean
    TabInfo tab;
    if (_activeTab != null && _activeTab.FilePath == null && !_activeTab.Editor.IsDirty)
        tab = _activeTab;
    else
        tab = CreateTab();

    tab.FilePath = path;
    tab.FileEncoding = FileHelper.DetectEncoding(path);
    tab.Editor.SetContent(FileHelper.ReadAllText(path, tab.FileEncoding));
    tab.LastKnownFileSize = new FileInfo(path).Length;
    tab.TailVerifyBytes = FileHelper.ReadTailVerifyBytes(path, tab.LastKnownFileSize);
    tab.StartWatching();
    UpdateTabHeader(tab);
    ActivateTab(tab);
    FindBarControl.RefreshSearch();
}
```

- [ ] **Step 5: Add Ctrl+B keyboard shortcut**

In `OnKeyDown` (line 1083-1099), add before the `else base.OnKeyDown(e)` line:

```csharp
else if (ctrl && !shift && e.Key == Key.B) { ToggleExplorer(); e.Handled = true; }
```

- [ ] **Step 6: Restore explorer state in constructor**

Add after `RestoreWindowPosition()` (line 52), before `CmdPalette.Closed`:

```csharp
// Restore explorer state
if (_settings.Editor.Explorer.PanelVisible)
{
    if (_settings.Editor.Explorer.OpenFolderPath is string folderPath && Directory.Exists(folderPath))
        ExplorerPanel.OpenFolder(folderPath);
    SetExplorerVisible(true);
}
```

- [ ] **Step 7: Build and verify**

Run: `dotnet build TextEdit.sln`
Expected: Build succeeds. Note: `System.Windows.Forms.FolderBrowserDialog` requires a reference to `System.Windows.Forms`. If the build fails, add `<UseWindowsForms>true</UseWindowsForms>` to the `.csproj` `<PropertyGroup>`.

- [ ] **Step 8: Commit**

```bash
git add TextEdit/UI/MainWindow.xaml TextEdit/UI/MainWindow.xaml.cs TextEdit/TextEdit.csproj
git commit -m "feat: integrate file explorer panel into MainWindow layout"
```

---

### Task 6: Add command palette entries

**Files:**
- Modify: `TextEdit/UI/CommandPaletteCommands.cs`
- Modify: `TextEdit/UI/MainWindow.xaml.cs` (OpenCommandPalette method)

- [ ] **Step 1: Update CommandPaletteCommands.Build signature**

The `Build` method needs access to explorer toggle/open/close actions. Add parameters. Change the method signature to:

```csharp
public static List<PaletteCommand> Build(
    List<TabInfo> tabs,
    AppSettings settings,
    ThemeManager themeManager,
    EditorControl activeEditor,
    FindBar findBar,
    Action saveSettings,
    Action toggleExplorer,
    Action openFolder,
    Action closeFolder)
```

- [ ] **Step 2: Add explorer commands to the returned list**

Add after the "Find Bar Position" entry (before the closing `];`):

```csharp
new("Toggle File Explorer", Toggle: toggleExplorer),

new("Explorer: Open Folder...", Toggle: openFolder),

new("Explorer: Close Folder", Toggle: closeFolder),

new("Explorer: Panel Side", CurrentValue: () => settings.Editor.Explorer.PanelSide, GetOptions: () =>
{
    var original = settings.Editor.Explorer.PanelSide;
    return AppSettings.PanelSideOptions.Select(side => new PaletteOption(
        side,
        ApplyPreview: () => { settings.Editor.Explorer.PanelSide = side; },
        Commit: () => { settings.Editor.Explorer.PanelSide = side; saveSettings(); },
        Revert: () => { settings.Editor.Explorer.PanelSide = original; }
    )).ToList();
}),
```

- [ ] **Step 3: Update the OpenCommandPalette call in MainWindow**

Change the call in `OpenCommandPalette()` (line 1118):

```csharp
var commands = CommandPaletteCommands.Build(
    _tabs, _settings, ThemeManager, Editor, FindBarControl, () => _settings.Save(),
    ToggleExplorer, OpenFolderInExplorer, CloseFolderInExplorer);
```

- [ ] **Step 4: Build and verify**

Run: `dotnet build TextEdit.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 5: Commit**

```bash
git add TextEdit/UI/CommandPaletteCommands.cs TextEdit/UI/MainWindow.xaml.cs
git commit -m "feat: add explorer commands to command palette"
```

---

### Task 7: Add Explorer section to Settings window

**Files:**
- Modify: `TextEdit/UI/SettingsWindow.xaml`
- Modify: `TextEdit/UI/SettingsWindow.xaml.cs`

- [ ] **Step 1: Add Explorer nav button to SettingsWindow.xaml**

In the left nav StackPanel, add after the NavFind button (around line 270):

```xml
<Button x:Name="NavExplorer" Content="Explorer"
        Style="{StaticResource NavButton}"
        Click="OnNavExplorer"/>
```

- [ ] **Step 2: Add Explorer settings section**

Add after the FindScroller (around line 386), before the bottom bar border:

```xml
<!-- Explorer section -->
<ScrollViewer x:Name="ExplorerScroller" Grid.Row="0"
              Template="{StaticResource ThemedScrollViewer}"
              VerticalScrollBarVisibility="Auto"
              HorizontalScrollBarVisibility="Disabled"
              Visibility="Collapsed">
    <StackPanel Margin="24,20">
        <TextBlock Text="Explorer" Style="{StaticResource SectionHeader}"/>

        <TextBlock Text="Panel side" Style="{StaticResource SettingLabel}" Margin="0,0,0,4"/>
        <ComboBox x:Name="PanelSideBox" Width="100" Height="26"
                  HorizontalAlignment="Left" Margin="0,0,0,16"
                  VerticalContentAlignment="Center" FontSize="13">
            <ComboBoxItem Content="Left"/>
            <ComboBoxItem Content="Right"/>
        </ComboBox>
    </StackPanel>
</ScrollViewer>
```

- [ ] **Step 3: Update SettingsSnapshot record and SettingsWindow code-behind**

In `SettingsWindow.xaml.cs`, update the `SettingsSnapshot` record to include `PanelSide`:

```csharp
public record SettingsSnapshot(
    int TabSize, bool BlockCaret, int CaretBlinkMs,
    string FontFamily, double FontSize, string FontWeight,
    double LineHeight, string ColorTheme, string FindBarPosition,
    string PanelSide);
```

Add a new property:

```csharp
public string PanelSide { get; private set; }
```

In the constructor, after setting `FindBarPosition`, add:

```csharp
PanelSide = snapshot.PanelSide;
PanelSideBox.SelectedIndex = snapshot.PanelSide == "Right" ? 1 : 0;
```

- [ ] **Step 4: Update SelectNav to include Explorer**

Update the `SelectNav` method to handle the "Explorer" section:

```csharp
private void SelectNav(string section)
{
    NavTheme.Style = (Style)FindResource(section == "Theme" ? "NavButtonActive" : "NavButton");
    NavFont.Style = (Style)FindResource(section == "Font" ? "NavButtonActive" : "NavButton");
    NavCaret.Style = (Style)FindResource(section == "Caret" ? "NavButtonActive" : "NavButton");
    NavFind.Style = (Style)FindResource(section == "Find" ? "NavButtonActive" : "NavButton");
    NavExplorer.Style = (Style)FindResource(section == "Explorer" ? "NavButtonActive" : "NavButton");

    ThemeScroller.Visibility = section == "Theme" ? Visibility.Visible : Visibility.Collapsed;
    FontScroller.Visibility = section == "Font" ? Visibility.Visible : Visibility.Collapsed;
    CaretScroller.Visibility = section == "Caret" ? Visibility.Visible : Visibility.Collapsed;
    FindScroller.Visibility = section == "Find" ? Visibility.Visible : Visibility.Collapsed;
    ExplorerScroller.Visibility = section == "Explorer" ? Visibility.Visible : Visibility.Collapsed;
}
```

Add the nav click handler:

```csharp
private void OnNavExplorer(object sender, RoutedEventArgs e) => SelectNav("Explorer");
```

- [ ] **Step 5: Update ReadCurrentValues to include PanelSide**

Add to the end of `ReadCurrentValues()`:

```csharp
PanelSide = PanelSideBox.SelectedIndex == 1 ? "Right" : "Left";
```

- [ ] **Step 6: Update OnSettings in MainWindow to pass and apply PanelSide**

In `MainWindow.xaml.cs`, update the `OnSettings` method to include `PanelSide` in the snapshot:

```csharp
var snapshot = new SettingsSnapshot(
    Editor.TabSize, _settings.Editor.Caret.BlockCaret, _settings.Editor.Caret.BlinkMs,
    Editor.FontFamilyName, Editor.EditorFontSize, Editor.EditorFontWeight,
    Editor.LineHeightMultiplier, _settings.Application.ColorTheme, _settings.Editor.Find.BarPosition,
    _settings.Editor.Explorer.PanelSide);
```

In `ApplySettingsFromDialog`, add after `FindBarPosition`:

```csharp
_settings.Editor.Explorer.PanelSide = dlg.PanelSide;
// Refresh explorer layout if visible
if (_settings.Editor.Explorer.PanelVisible)
    SetExplorerVisible(true);
```

- [ ] **Step 7: Build and verify**

Run: `dotnet build TextEdit.sln`
Expected: Build succeeds with no errors.

- [ ] **Step 8: Commit**

```bash
git add TextEdit/UI/SettingsWindow.xaml TextEdit/UI/SettingsWindow.xaml.cs TextEdit/UI/MainWindow.xaml.cs
git commit -m "feat: add Explorer section to Settings window"
```

---

### Task 8: Manual testing and polish

- [ ] **Step 1: Run the application**

Run: `dotnet run --project TextEdit/TextEdit.csproj`

- [ ] **Step 2: Test explorer toggle**

1. Press Ctrl+B — explorer panel should appear on the left (empty, showing "Explorer")
2. Press Ctrl+B again — panel should hide
3. Verify the splitter is hidden when panel is hidden

- [ ] **Step 3: Test opening a folder**

1. Press Ctrl+Shift+P → type "Open Folder" → select a project folder
2. Verify folder name shows in the header
3. Verify directory tree loads with expand arrows on folders
4. Click a folder expand arrow — children should load lazily
5. Verify `.git`, `node_modules`, etc. are hidden

- [ ] **Step 4: Test file opening**

1. Double-click a file in the tree — should open in a new tab
2. Double-click the same file again — should switch to existing tab
3. Double-click another file — should open in a new tab

- [ ] **Step 5: Test panel side switching**

1. Ctrl+Shift+P → "Panel Side" → select "Right"
2. Verify explorer moves to the right side
3. Switch back to "Left"

- [ ] **Step 6: Test persistence**

1. Open a folder, resize the panel via splitter drag
2. Close and reopen the application
3. Verify the panel is visible with the same folder, same width, same side

- [ ] **Step 7: Test themes**

1. Switch between Dark, Light, and Gruvbox Dark themes
2. Verify explorer colors update correctly in each theme

- [ ] **Step 8: Fix any issues found during testing**

Address any visual glitches, layout issues, or bugs discovered.

- [ ] **Step 9: Final commit**

```bash
git add -A
git commit -m "fix: polish file explorer panel after testing"
```
