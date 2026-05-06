using System.Reflection;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using Xunit;

namespace Volt.Tests;

[CollectionDefinition("WpfApplication", DisableParallelization = true)]
public sealed class WpfApplicationCollection;

[Collection("WpfApplication")]
public class SettingsWindowTests
{
    private static readonly object AppGate = new();

    [StaFact]
    public void ExplorerSettings_DefaultToFullIconsAndRevealOff()
    {
        var settings = new AppSettings();

        Assert.Equal("Full", settings.Editor.Explorer.FileIcons);
        Assert.False(settings.Editor.Explorer.RevealActiveFile);
    }

    [StaFact]
    public void SearchExplorerIcons_FiltersToExplorerIconSetting()
    {
        var window = CreateWindow();
        try
        {
            Field<TextBox>(window, "SettingsSearchInput").Text = "file icons";

            Assert.Equal(Visibility.Visible, Field<FrameworkElement>(window, "ExplorerSection").Visibility);
            Assert.Equal(Visibility.Visible, Field<FrameworkElement>(window, "ExplorerFileIconsRow").Visibility);
            Assert.Equal(Visibility.Collapsed, Field<FrameworkElement>(window, "ExplorerRevealActiveFileRow").Visibility);
            Assert.Equal(Visibility.Collapsed, Field<FrameworkElement>(window, "TerminalSection").Visibility);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void ExplorerSettings_RoundTripFromControls()
    {
        var window = CreateWindow();
        try
        {
            var fileIconsBox = Field<ComboBox>(window, "ExplorerFileIconsBox");
            Assert.Equal(["Full", "Basic", "Off"], fileIconsBox.Items.Cast<string>().ToArray());

            fileIconsBox.SelectedIndex = 2;
            Field<ComboBox>(window, "ExplorerRevealActiveFileBox").SelectedIndex = 0;

            Field<Button>(window, "ApplyButton").RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));

            Assert.Equal("Off", window.ExplorerFileIcons);
            Assert.True(window.ExplorerRevealActiveFile);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void ExplorerSelectByPath_CentersSelectedRowWhenRequested()
    {
        var tree = new ExplorerTreeControl();
        var items = new ObservableCollection<FileTreeItem>(
            Enumerable.Range(0, 30)
                .Select(i => new FileTreeItem($@"C:\project\file{i}.txt", isDirectory: false)));

        tree.SetRootItems(items);
        tree.Measure(new Size(300, 120));
        tree.Arrange(new Rect(0, 0, 300, 120));

        tree.SelectByPath(@"C:\project\file10.txt", center: true);

        Assert.InRange(tree.VerticalOffset, 188, 196);
    }

    [StaFact]
    public void OpeningWindow_FocusesSearchBox()
    {
        var window = CreateWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            DrainDispatcher();
            DrainDispatcher();

            Assert.True(Field<TextBox>(window, "SettingsSearchInput").IsKeyboardFocused);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void SearchFontSize_FiltersRowsAndKeepsFontSectionVisible()
    {
        var window = CreateWindow();
        try
        {
            Field<TextBox>(window, "SettingsSearchInput").Text = "font size";

            Assert.Equal(Visibility.Visible, Field<FrameworkElement>(window, "FontSection").Visibility);
            Assert.Equal(Visibility.Visible, Field<FrameworkElement>(window, "FontSizeRow").Visibility);
            Assert.Equal(Visibility.Collapsed, Field<FrameworkElement>(window, "FontFamilyRow").Visibility);
            Assert.Equal(Visibility.Collapsed, Field<FrameworkElement>(window, "ThemeSection").Visibility);
            Assert.Equal(Visibility.Collapsed, Field<Button>(window, "NavTheme").Visibility);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void SearchShortcuts_ShowsFullKeybindSection()
    {
        var window = CreateWindow();
        try
        {
            var list = Field<ItemsControl>(window, "KeybindList");
            int rowCount = list.Items.Count;

            Field<TextBox>(window, "SettingsSearchInput").Text = "shortcuts";

            Assert.Equal(Visibility.Visible, Field<FrameworkElement>(window, "KeybindsSection").Visibility);
            Assert.Equal(rowCount, list.Items.Count);
            Assert.All(list.Items.Cast<FrameworkElement>(), row =>
                Assert.Equal(Visibility.Visible, row.Visibility));
            Assert.Equal(Visibility.Collapsed, Field<FrameworkElement>(window, "FontSection").Visibility);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void ClearingSearch_RestoresAllSectionsAndNav()
    {
        var window = CreateWindow();
        try
        {
            var search = Field<TextBox>(window, "SettingsSearchInput");
            search.Text = "font size";
            search.Clear();

            Assert.Equal(Visibility.Visible, Field<FrameworkElement>(window, "ThemeSection").Visibility);
            Assert.Equal(Visibility.Visible, Field<FrameworkElement>(window, "TerminalSection").Visibility);
            Assert.Equal(Visibility.Visible, Field<Button>(window, "NavTheme").Visibility);
            Assert.Equal(Visibility.Visible, Field<TextBlock>(window, "NavPanelsHeader").Visibility);
            Assert.Equal(Visibility.Collapsed, Field<FrameworkElement>(window, "NoSettingsResults").Visibility);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void ScrollingToBottom_ActivatesLastVisibleSection()
    {
        var window = CreateWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            DrainDispatcher();

            var scroller = Field<ScrollViewer>(window, "SettingsScroller");
            scroller.ScrollToEnd();
            window.UpdateLayout();
            DrainDispatcher();

            Assert.Same(
                Field<Button>(window, "NavTerminal").Style,
                window.FindResource("NavButtonActive"));
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void NavClick_ScrollsToSectionWithoutChangingValues()
    {
        var window = CreateWindow();
        try
        {
            window.Show();
            window.UpdateLayout();
            DrainDispatcher();

            var scroller = Field<ScrollViewer>(window, "SettingsScroller");
            var tabSizeBox = Field<ComboBox>(window, "TabSizeBox");
            int tabSizeIndex = tabSizeBox.SelectedIndex;

            Field<Button>(window, "NavTerminal").RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            DrainDispatcher();

            Assert.True(scroller.VerticalOffset > 0);
            Assert.Equal(tabSizeIndex, tabSizeBox.SelectedIndex);
            Assert.Equal(4, window.TabSize);
        }
        finally
        {
            window.Close();
        }
    }

    [StaFact]
    public void ResettingSingleKeybind_DoesNotMoveSettingsScroll()
    {
        var keyBindings = new Dictionary<VoltCommand, KeyCombo>(KeyBindingManager.Defaults)
        {
            [VoltCommand.CommandPalette] = KeyCombo.None,
        };
        var window = CreateWindow(keyBindings);
        try
        {
            window.Show();
            window.UpdateLayout();
            DrainDispatcher();

            Field<Button>(window, "NavKeybinds").RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            DrainDispatcher();

            var scroller = Field<ScrollViewer>(window, "SettingsScroller");
            double scrollOffset = scroller.VerticalOffset;
            var resetButtons = Field<Dictionary<VoltCommand, Button>>(window, "_keybindResetButtons");

            resetButtons[VoltCommand.CommandPalette].RaiseEvent(
                new RoutedEventArgs(Button.ClickEvent));
            window.UpdateLayout();
            DrainDispatcher();
            DrainDispatcher();

            Assert.Equal(Visibility.Collapsed, resetButtons[VoltCommand.CommandPalette].Visibility);
            Assert.Equal(scrollOffset, scroller.VerticalOffset, precision: 3);
        }
        finally
        {
            window.Close();
        }
    }

    private static SettingsWindow CreateWindow(Dictionary<VoltCommand, KeyCombo>? keyBindings = null)
    {
        EnsureWpfResources();
        var themeManager = new ThemeManager();
        themeManager.Initialize();
        themeManager.Apply("Volt Dark");
        return new SettingsWindow(themeManager, CreateSnapshot(keyBindings));
    }

    private static SettingsSnapshot CreateSnapshot(Dictionary<VoltCommand, KeyCombo>? keyBindings = null)
    {
        return new SettingsSnapshot(
            TabSize: 4,
            BlockCaret: false,
            CaretBlinkMs: 500,
            FontFamily: "Consolas",
            FontSize: 14,
            FontWeight: "Normal",
            LineHeight: 1.0,
            ColorTheme: "Volt Dark",
            FindBarPosition: "Bottom",
            FindSeedWithSelection: true,
            FixedWidthTabs: false,
            WordWrap: false,
            WordWrapAtWords: true,
            WordWrapIndent: true,
            IndentGuides: true,
            CommandPalettePosition: "Top",
            ExplorerFileIcons: "Full",
            ExplorerRevealActiveFile: false,
            KeyBindings: keyBindings ?? new Dictionary<VoltCommand, KeyCombo>(KeyBindingManager.Defaults),
            TerminalShellPath: null,
            TerminalShellArgs: null,
            TerminalScrollbackLines: 10_000);
    }

    private static T Field<T>(SettingsWindow window, string name) where T : class
    {
        var field = typeof(SettingsWindow).GetField(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(field);
        var value = field.GetValue(window);
        return Assert.IsAssignableFrom<T>(value);
    }

    private static void DrainDispatcher()
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(
            DispatcherPriority.Background,
            () => frame.Continue = false);
        Dispatcher.PushFrame(frame);
    }

    private static void EnsureWpfResources()
    {
        lock (AppGate)
        {
            if (Application.Current == null)
                _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

            var resources = Application.Current!.Resources;
            resources["CloseButton"] = new Style(typeof(Button));
            resources["MatchCaseButton"] = new Style(typeof(Button));
            resources["RoundedTextBox"] = new Style(typeof(TextBox));
            resources["ThemedScrollViewer"] = CreateScrollViewerTemplate();

            resources["ThemeChromeBrush"] = Brushes.White;
            resources["ThemeBorderBrush"] = Brushes.LightGray;
            resources["ThemeContentBg"] = Brushes.White;
            resources["ThemeTextFg"] = Brushes.Black;
            resources["ThemeTextFgStrong"] = Brushes.Black;
            resources["ThemeTextFgMuted"] = Brushes.Gray;
            resources["ThemeButtonFg"] = Brushes.Black;
            resources["ThemeButtonHover"] = Brushes.Gainsboro;
            resources["ThemeMenuPopupBg"] = Brushes.White;
            resources["ThemeMenuPopupBorder"] = Brushes.LightGray;
            resources["ThemeMenuItemHover"] = Brushes.Gainsboro;
            resources["ThemeNavBg"] = Brushes.WhiteSmoke;
            resources["ThemeNavActive"] = Brushes.Gainsboro;
            resources["ThemeNavHover"] = Brushes.Gainsboro;
            resources["ThemeScrollBg"] = Brushes.Gainsboro;
            resources["ThemeScrollThumb"] = Brushes.DarkGray;
            resources["ThemeScrollThumbHover"] = Brushes.Gray;
            resources["ThemeInputSelection"] = Brushes.LightBlue;
        }
    }

    private static ControlTemplate CreateScrollViewerTemplate()
    {
        const string xaml =
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' " +
            "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='ScrollViewer'>" +
            "<Grid>" +
            "<Grid.ColumnDefinitions><ColumnDefinition Width='*'/><ColumnDefinition Width='Auto'/></Grid.ColumnDefinitions>" +
            "<Grid.RowDefinitions><RowDefinition Height='*'/><RowDefinition Height='Auto'/></Grid.RowDefinitions>" +
            "<ScrollContentPresenter x:Name='PART_ScrollContentPresenter' Grid.Column='0' Grid.Row='0' " +
            "Content='{TemplateBinding Content}' ContentTemplate='{TemplateBinding ContentTemplate}' " +
            "CanContentScroll='{TemplateBinding CanContentScroll}'/>" +
            "<ScrollBar x:Name='PART_VerticalScrollBar' Grid.Column='1' Grid.Row='0' Orientation='Vertical' " +
            "Value='{TemplateBinding VerticalOffset}' Maximum='{TemplateBinding ScrollableHeight}' " +
            "ViewportSize='{TemplateBinding ViewportHeight}' Visibility='{TemplateBinding ComputedVerticalScrollBarVisibility}'/>" +
            "<ScrollBar x:Name='PART_HorizontalScrollBar' Grid.Column='0' Grid.Row='1' Orientation='Horizontal' " +
            "Value='{TemplateBinding HorizontalOffset}' Maximum='{TemplateBinding ScrollableWidth}' " +
            "ViewportSize='{TemplateBinding ViewportWidth}' Visibility='{TemplateBinding ComputedHorizontalScrollBarVisibility}'/>" +
            "</Grid>" +
            "</ControlTemplate>";
        return (ControlTemplate)XamlReader.Parse(xaml);
    }
}
