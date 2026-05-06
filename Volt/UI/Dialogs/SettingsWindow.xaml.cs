using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Volt;

public record SettingsSnapshot(
    int TabSize, bool BlockCaret, int CaretBlinkMs,
    string FontFamily, double FontSize, string FontWeight,
    double LineHeight, string ColorTheme, string FindBarPosition,
    bool FindSeedWithSelection, bool FixedWidthTabs,
    bool WordWrap, bool WordWrapAtWords, bool WordWrapIndent,
    bool IndentGuides, string CommandPalettePosition,
    string ExplorerFileIcons, bool ExplorerRevealActiveFile,
    Dictionary<VoltCommand, KeyCombo> KeyBindings,
    string? TerminalShellPath, string? TerminalShellArgs, int TerminalScrollbackLines);

public partial class SettingsWindow : Window
{
    public int TabSize { get; private set; }
    public bool BlockCaret { get; private set; }
    public int CaretBlinkMs { get; private set; }
    public string SelectedFontFamily { get; private set; }
    public double SelectedFontSize { get; private set; }
    public string SelectedFontWeight { get; private set; }
    public string ColorThemeName { get; private set; }
    public double SelectedLineHeight { get; private set; }
    public string FindBarPosition { get; private set; }
    public bool FindSeedWithSelection { get; private set; }
    public bool FixedWidthTabs { get; private set; }
    public bool WordWrap { get; private set; }
    public bool WordWrapAtWords { get; private set; }
    public bool WordWrapIndent { get; private set; }
    public bool IndentGuides { get; private set; }
    public string CommandPalettePosition { get; private set; }
    public string ExplorerFileIcons { get; private set; }
    public bool ExplorerRevealActiveFile { get; private set; }
    public Dictionary<VoltCommand, KeyCombo> KeyBindings => new(_pendingBindings);
    public string? TerminalShellPath { get; private set; }
    public string? TerminalShellArgs { get; private set; }
    public int TerminalScrollbackLines { get; private set; }

    private enum SettingsSection { Theme, CommandPalette, Keybinds, Font, Caret, Tabs, Find, Indentation, WordWrap, Explorer, Terminal }

    private sealed record SettingsSectionInfo(
        SettingsSection Section,
        Button NavButton,
        FrameworkElement Container,
        string[] Terms,
        bool IsKeybinds = false);

    private sealed record SettingSearchEntry(
        SettingsSection Section,
        FrameworkElement Row,
        string[] Terms);

    public event EventHandler? Applied;

    private readonly ThemeManager _themeManager;
    private readonly List<string> _themeNames;
    private readonly List<string> _fontNames;

    // Keybind editing state
    private readonly Dictionary<VoltCommand, KeyCombo> _pendingBindings = new();
    private readonly Dictionary<VoltCommand, TextBlock> _keybindDisplays = new();
    private readonly Dictionary<VoltCommand, Border> _keybindBorders = new();
    private readonly Dictionary<VoltCommand, Button> _keybindResetButtons = new();
    private readonly Dictionary<VoltCommand, TextBlock> _keybindConflictLabels = new();
    private VoltCommand? _capturingCommand;
    private IReadOnlyList<SettingsSectionInfo>? _sectionInfos;
    private IReadOnlyList<SettingSearchEntry>? _settingSearchEntries;
    private SettingsSection? _activeSection = SettingsSection.Theme;
    private bool _isProgrammaticScroll;

    public SettingsWindow(ThemeManager themeManager, SettingsSnapshot snapshot)
    {
        _themeManager = themeManager;
        InitializeComponent();
        TabSize = snapshot.TabSize;
        BlockCaret = snapshot.BlockCaret;
        CaretBlinkMs = snapshot.CaretBlinkMs;
        SelectedFontFamily = snapshot.FontFamily;
        SelectedFontSize = snapshot.FontSize;
        SelectedFontWeight = snapshot.FontWeight;
        SelectedLineHeight = snapshot.LineHeight;
        ColorThemeName = snapshot.ColorTheme;
        FindBarPosition = snapshot.FindBarPosition;
        CommandPalettePosition = snapshot.CommandPalettePosition;
        ExplorerFileIcons = AppSettings.NormalizeExplorerFileIcons(snapshot.ExplorerFileIcons);
        ExplorerRevealActiveFile = snapshot.ExplorerRevealActiveFile;

        foreach (var (cmd, combo) in snapshot.KeyBindings)
            _pendingBindings[cmd] = combo;

        TerminalShellPath = snapshot.TerminalShellPath;
        TerminalShellArgs = snapshot.TerminalShellArgs;
        TerminalScrollbackLines = snapshot.TerminalScrollbackLines;
        var shellPref = TerminalPanel.ClassifyShellPath(snapshot.TerminalShellPath);
        TerminalShellBox.SelectedIndex = (int)shellPref;
        TerminalShellArgsBox.Text = snapshot.TerminalShellArgs ?? "";
        TerminalScrollbackBox.Text = snapshot.TerminalScrollbackLines.ToString();

        int index = Array.IndexOf(AppSettings.TabSizeOptions, snapshot.TabSize);
        TabSizeBox.SelectedIndex = index >= 0 ? index : 1;
        CaretStyleBox.SelectedIndex = snapshot.BlockCaret ? 1 : 0;
        CaretBlinkSlider.Value = snapshot.CaretBlinkMs;
        FindBarPosBox.SelectedIndex = snapshot.FindBarPosition == "Top" ? 0 : 1;
        CmdPalettePosBox.SelectedIndex = snapshot.CommandPalettePosition == "Top" ? 0 : 1;
        FindSeedWithSelection = snapshot.FindSeedWithSelection;
        FindSeedSelBox.SelectedIndex = snapshot.FindSeedWithSelection ? 0 : 1;
        FixedWidthTabs = snapshot.FixedWidthTabs;
        FixedWidthTabsBox.SelectedIndex = snapshot.FixedWidthTabs ? 0 : 1;
        WordWrap = snapshot.WordWrap;
        WordWrapBox.SelectedIndex = snapshot.WordWrap ? 0 : 1;
        WordWrapAtWords = snapshot.WordWrapAtWords;
        WordWrapAtWordsBox.SelectedIndex = snapshot.WordWrapAtWords ? 0 : 1;
        WordWrapIndent = snapshot.WordWrapIndent;
        WordWrapIndentBox.SelectedIndex = snapshot.WordWrapIndent ? 0 : 1;
        IndentGuides = snapshot.IndentGuides;
        IndentGuidesBox.SelectedIndex = snapshot.IndentGuides ? 0 : 1;
        foreach (var option in AppSettings.ExplorerFileIconOptions)
            ExplorerFileIconsBox.Items.Add(option);
        int fileIconsIndex = Array.IndexOf(AppSettings.ExplorerFileIconOptions, ExplorerFileIcons);
        ExplorerFileIconsBox.SelectedIndex = fileIconsIndex >= 0 ? fileIconsIndex : 0;
        ExplorerRevealActiveFileBox.SelectedIndex = snapshot.ExplorerRevealActiveFile ? 0 : 1;

        // Populate font family dropdown
        _fontNames = FontManager.GetMonospaceFonts();
        foreach (var name in _fontNames)
            FontFamilyBox.Items.Add(name);
        int fi = _fontNames.IndexOf(snapshot.FontFamily);
        FontFamilyBox.SelectedIndex = fi >= 0 ? fi : 0;

        // Populate font size dropdown
        foreach (var size in AppSettings.FontSizeOptions)
            FontSizeBox.Items.Add(size.ToString());
        int si = Array.IndexOf(AppSettings.FontSizeOptions, snapshot.FontSize);
        FontSizeBox.SelectedIndex = si >= 0 ? si : Array.IndexOf(AppSettings.FontSizeOptions, 14);

        // Populate font weight dropdown
        foreach (var w in AppSettings.FontWeightOptions)
            FontWeightBox.Items.Add(w);
        int wi = Array.IndexOf(AppSettings.FontWeightOptions, snapshot.FontWeight);
        FontWeightBox.SelectedIndex = wi >= 0 ? wi : Array.IndexOf(AppSettings.FontWeightOptions, "Normal");

        // Populate line height dropdown
        foreach (var lh in AppSettings.LineHeightOptions)
            LineHeightBox.Items.Add(lh.ToString("0.0") + "x");
        int li = Array.IndexOf(AppSettings.LineHeightOptions, snapshot.LineHeight);
        LineHeightBox.SelectedIndex = li >= 0 ? li : 0;

        // Populate color theme dropdown
        _themeNames = _themeManager.GetAvailableThemes();
        foreach (var name in _themeNames)
            ColorThemeBox.Items.Add(name);
        int ti = _themeNames.IndexOf(snapshot.ColorTheme);
        ColorThemeBox.SelectedIndex = ti >= 0 ? ti : 0;

        BuildKeybindRows();
        SetActiveNav(SettingsSection.Theme);
        Loaded += (_, _) =>
        {
            ApplySettingsSearch();
            FocusSettingsSearch();
        };
    }

    // ── Keybind UI ──────────────────────────────────────────────────────

    private void BuildKeybindRows()
    {
        foreach (VoltCommand cmd in Enum.GetValues<VoltCommand>())
        {
            if (!_pendingBindings.ContainsKey(cmd)) continue;

            var row = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(50) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // Command name
            var nameLabel = new TextBlock
            {
                Text = KeyBindingManager.GetDisplayName(cmd),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            };
            nameLabel.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.TextFg);
            Grid.SetColumn(nameLabel, 0);
            row.Children.Add(nameLabel);

            // Binding display (clickable)
            var bindingText = new TextBlock
            {
                Text = FormatBinding(_pendingBindings[cmd]),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            bindingText.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.TextFg);

            var bindingBorder = new Border
            {
                CornerRadius = new CornerRadius(3),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(8, 4, 8, 4),
                Cursor = Cursors.Hand,
                Child = bindingText,
            };
            bindingBorder.SetResourceReference(Border.BorderBrushProperty, ThemeResourceKeys.MenuPopupBorder);
            bindingBorder.SetResourceReference(Border.BackgroundProperty, ThemeResourceKeys.ContentBg);

            var capturedCmd = cmd;
            bindingBorder.MouseLeftButtonDown += (_, _) => StartCapture(capturedCmd);
            Grid.SetColumn(bindingBorder, 1);
            row.Children.Add(bindingBorder);

            _keybindDisplays[cmd] = bindingText;
            _keybindBorders[cmd] = bindingBorder;

            // Reset button
            var resetBtn = new Button
            {
                Content = "Reset",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                Padding = new Thickness(4, 2, 4, 2),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(6, 0, 0, 0),
                Visibility = IsDefault(cmd) ? Visibility.Hidden : Visibility.Visible,
            };
            resetBtn.SetResourceReference(Control.ForegroundProperty, ThemeResourceKeys.TextFgMuted);
            resetBtn.SetResourceReference(Control.BackgroundProperty, ThemeResourceKeys.ContentBg);
            resetBtn.SetResourceReference(Control.BorderBrushProperty, ThemeResourceKeys.MenuPopupBorder);
            resetBtn.Click += (_, _) => ResetKeybind(capturedCmd);
            Grid.SetColumn(resetBtn, 2);
            row.Children.Add(resetBtn);
            _keybindResetButtons[cmd] = resetBtn;

            // Conflict label
            var conflictLabel = new TextBlock
            {
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xA8, 0x30)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                Visibility = Visibility.Collapsed,
            };
            Grid.SetColumn(conflictLabel, 3);
            row.Children.Add(conflictLabel);
            _keybindConflictLabels[cmd] = conflictLabel;

            KeybindList.Items.Add(row);
        }
    }

    private void StartCapture(VoltCommand cmd)
    {
        // Cancel any previous capture
        if (_capturingCommand is { } prev)
            EndCapture(prev, _pendingBindings[prev]);

        _capturingCommand = cmd;
        _keybindDisplays[cmd].Text = "Press keys...";
        _keybindDisplays[cmd].FontStyle = FontStyles.Italic;
        _keybindDisplays[cmd].SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.TextFgMuted);
        _keybindBorders[cmd].SetResourceReference(Border.BorderBrushProperty, ThemeResourceKeys.TextFg);
    }

    private void EndCapture(VoltCommand cmd, KeyCombo combo)
    {
        _capturingCommand = null;
        _pendingBindings[cmd] = combo;
        _keybindDisplays[cmd].Text = FormatBinding(combo);
        _keybindDisplays[cmd].FontStyle = FontStyles.Normal;
        _keybindDisplays[cmd].SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.TextFg);
        _keybindBorders[cmd].SetResourceReference(Border.BorderBrushProperty, ThemeResourceKeys.MenuPopupBorder);
        _keybindResetButtons[cmd].Visibility = IsDefault(cmd) ? Visibility.Hidden : Visibility.Visible;
        UpdateConflicts();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_capturingCommand is not { } cmd)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && e.Key == Key.F)
            {
                FocusSettingsSearch();
                e.Handled = true;
                return;
            }

            if (SettingsSearchInput.IsKeyboardFocusWithin && e.Key == Key.Escape)
            {
                if (!string.IsNullOrEmpty(SettingsSearchInput.Text))
                    SettingsSearchInput.Clear();
                else
                    Keyboard.ClearFocus();

                e.Handled = true;
                return;
            }

            base.OnPreviewKeyDown(e);
            return;
        }

        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        // Escape cancels capture
        if (key == Key.Escape)
        {
            EndCapture(cmd, _pendingBindings[cmd]);
            return;
        }

        // Delete/Backspace clears binding
        if (key is Key.Delete or Key.Back)
        {
            EndCapture(cmd, KeyCombo.None);
            return;
        }

        // Ignore modifier-only presses
        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin)
            return;

        var combo = new KeyCombo(key, Keyboard.Modifiers);
        EndCapture(cmd, combo);
    }

    private void ResetKeybind(VoltCommand cmd)
    {
        if (_capturingCommand == cmd)
            _capturingCommand = null;

        if (KeyBindingManager.Defaults.TryGetValue(cmd, out var def))
            _pendingBindings[cmd] = def;

        _keybindDisplays[cmd].Text = FormatBinding(_pendingBindings[cmd]);
        _keybindDisplays[cmd].FontStyle = FontStyles.Normal;
        _keybindDisplays[cmd].SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.TextFg);
        _keybindBorders[cmd].SetResourceReference(Border.BorderBrushProperty, ThemeResourceKeys.MenuPopupBorder);
        _keybindResetButtons[cmd].Visibility = Visibility.Hidden;
        UpdateConflicts();
    }

    private void OnResetAllKeybinds(object sender, RoutedEventArgs e)
    {
        _capturingCommand = null;
        foreach (var cmd in _pendingBindings.Keys)
        {
            if (KeyBindingManager.Defaults.TryGetValue(cmd, out var def))
                _pendingBindings[cmd] = def;
            _keybindDisplays[cmd].Text = FormatBinding(_pendingBindings[cmd]);
            _keybindDisplays[cmd].FontStyle = FontStyles.Normal;
            _keybindDisplays[cmd].SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.TextFg);
            _keybindBorders[cmd].SetResourceReference(Border.BorderBrushProperty, ThemeResourceKeys.MenuPopupBorder);
            _keybindResetButtons[cmd].Visibility = Visibility.Hidden;
        }
        UpdateConflicts();
    }

    private void UpdateConflicts()
    {
        // Clear all conflict labels
        foreach (var label in _keybindConflictLabels.Values)
            label.Visibility = Visibility.Collapsed;

        // Find duplicates
        var seen = new Dictionary<KeyCombo, VoltCommand>();
        foreach (var (cmd, combo) in _pendingBindings)
        {
            if (combo.IsNone) continue;
            if (seen.TryGetValue(combo, out var other))
            {
                var msg = $"Conflicts with {KeyBindingManager.GetDisplayName(other)}";
                _keybindConflictLabels[cmd].Text = msg;
                _keybindConflictLabels[cmd].Visibility = Visibility.Visible;

                var otherMsg = $"Conflicts with {KeyBindingManager.GetDisplayName(cmd)}";
                _keybindConflictLabels[other].Text = otherMsg;
                _keybindConflictLabels[other].Visibility = Visibility.Visible;
            }
            else
            {
                seen[combo] = cmd;
            }
        }
    }

    private bool IsDefault(VoltCommand cmd)
        => KeyBindingManager.Defaults.TryGetValue(cmd, out var def) && _pendingBindings[cmd] == def;

    private static string FormatBinding(KeyCombo combo)
        => combo.IsNone ? "(none)" : combo.ToString();

    // ── Navigation ──────────────────────────────────────────────────────

    private IReadOnlyList<SettingsSectionInfo> SectionInfos => _sectionInfos ??=
    [
        new(SettingsSection.Theme, NavTheme, ThemeSection, ["theme", "appearance"]),
        new(SettingsSection.CommandPalette, NavCommandPalette, CommandPaletteSection, ["command palette", "palette", "commands"]),
        new(SettingsSection.Keybinds, NavKeybinds, KeybindsSection, ["keybinds", "key bindings", "keybindings", "bindings", "shortcuts", "hotkeys", "keyboard"], IsKeybinds: true),
        new(SettingsSection.Font, NavFont, FontSection, ["font", "typeface", "text"]),
        new(SettingsSection.Caret, NavCaret, CaretSection, ["caret", "cursor"]),
        new(SettingsSection.Tabs, NavTabs, TabsSection, ["tabs"]),
        new(SettingsSection.Find, NavFind, FindSection, ["find", "search"]),
        new(SettingsSection.Indentation, NavIndentation, IndentationSection, ["indentation", "indent"]),
        new(SettingsSection.WordWrap, NavWordWrap, WordWrapSection, ["word wrap", "wrapping", "wrapped lines", "line wrapping"]),
        new(SettingsSection.Explorer, NavExplorer, ExplorerSection, ["explorer", "files", "folders", "panel"]),
        new(SettingsSection.Terminal, NavTerminal, TerminalSection, ["terminal", "console"]),
    ];

    private IReadOnlyList<SettingSearchEntry> SettingSearchEntries => _settingSearchEntries ??=
    [
        new(SettingsSection.Theme, ThemeColorThemeRow, ["theme", "colour theme", "color theme", "appearance"]),
        new(SettingsSection.CommandPalette, CommandPalettePositionRow, ["command palette", "palette", "position", "top", "center"]),
        new(SettingsSection.Font, FontFamilyRow, ["font", "font family", "typeface", "family"]),
        new(SettingsSection.Font, FontSizeRow, ["font", "font size", "text size", "size"]),
        new(SettingsSection.Font, FontWeightRow, ["font", "font weight", "weight", "bold"]),
        new(SettingsSection.Font, LineHeightRow, ["font", "line height", "line spacing", "spacing"]),
        new(SettingsSection.Caret, CaretStyleRow, ["caret", "caret style", "cursor", "bar", "block"]),
        new(SettingsSection.Caret, CaretBlinkRow, ["caret", "caret blink", "blink", "cursor blink"]),
        new(SettingsSection.Tabs, TabsFixedWidthRow, ["tabs", "fixed width tabs", "fixed tabs"]),
        new(SettingsSection.Find, FindBarPositionRow, ["find", "find bar position", "search position", "top", "bottom"]),
        new(SettingsSection.Find, FindSeedSelectionRow, ["find", "selection", "add selection to find", "seed selection"]),
        new(SettingsSection.Indentation, IndentationTabSizeRow, ["indentation", "tab size", "indent size"]),
        new(SettingsSection.Indentation, IndentationGuidesRow, ["indentation", "indent guides", "guide lines"]),
        new(SettingsSection.WordWrap, WordWrapEnabledRow, ["word wrap", "wrap", "line wrapping"]),
        new(SettingsSection.WordWrap, WordWrapAtWordsRow, ["word wrap", "word boundaries", "break at word boundaries"]),
        new(SettingsSection.WordWrap, WordWrapIndentRow, ["word wrap", "indent wrapped lines", "wrapped line indent"]),
        new(SettingsSection.Explorer, ExplorerFileIconsRow, ["explorer", "file icons", "file type icons", "icons", "types", "full", "basic"]),
        new(SettingsSection.Explorer, ExplorerRevealActiveFileRow, ["explorer", "reveal active file", "active file", "follow active file", "select active file"]),
        new(SettingsSection.Terminal, TerminalShellRow, ["terminal", "shell", "powershell", "command prompt"]),
        new(SettingsSection.Terminal, TerminalShellArgsRow, ["terminal", "shell arguments", "arguments", "args"]),
        new(SettingsSection.Terminal, TerminalScrollbackRow, ["terminal", "scrollback", "scrollback lines", "history"]),
    ];

    private void SelectNav(SettingsSection section) => ScrollToSection(section);

    private void ScrollToSection(SettingsSection section)
    {
        var info = SectionInfos.FirstOrDefault(s => s.Section == section);
        if (info == null || info.Container.Visibility != Visibility.Visible)
            return;

        SetActiveNav(section);

        if (!IsLoaded)
            return;

        try
        {
            SettingsContent.UpdateLayout();
            var position = info.Container.TransformToAncestor(SettingsContent).Transform(new Point(0, 0));
            _isProgrammaticScroll = true;
            SettingsScroller.ScrollToVerticalOffset(Math.Max(0, position.Y - 4));
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                _isProgrammaticScroll = false;
                UpdateActiveSectionFromScroll();
            });
        }
        catch (InvalidOperationException)
        {
            _isProgrammaticScroll = false;
        }
    }

    private void SetActiveNav(SettingsSection? section)
    {
        var active = (Style)FindResource("NavButtonActive");
        var inactive = (Style)FindResource("NavButton");

        foreach (var info in SectionInfos)
        {
            bool isActive = section == info.Section && info.NavButton.Visibility == Visibility.Visible;
            info.NavButton.Style = isActive ? active : inactive;
        }

        _activeSection = section;
    }

    private void OnSettingsScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_isProgrammaticScroll)
            return;

        UpdateActiveSectionFromScroll();
    }

    private void UpdateActiveSectionFromScroll()
    {
        if (!IsLoaded)
            return;

        var visibleSections = SectionInfos
            .Where(info => info.Container.Visibility == Visibility.Visible)
            .ToList();

        if (visibleSections.Count == 0)
        {
            SetActiveNav(null);
            return;
        }

        if (SettingsScroller.VerticalOffset >= SettingsScroller.ScrollableHeight - 1)
        {
            SetActiveNav(visibleSections[^1].Section);
            return;
        }

        SettingsSection? active = null;
        double offset = SettingsScroller.VerticalOffset + 24;

        foreach (var info in visibleSections)
        {
            try
            {
                var position = info.Container.TransformToAncestor(SettingsContent).Transform(new Point(0, 0));
                if (position.Y <= offset)
                    active = info.Section;
                else
                    break;
            }
            catch (InvalidOperationException)
            {
                break;
            }
        }

        active ??= visibleSections[0].Section;
        if (active != _activeSection)
            SetActiveNav(active);
    }

    private void OnNavTheme(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Theme);
    private void OnNavCommandPalette(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.CommandPalette);
    private void OnNavKeybinds(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Keybinds);
    private void OnNavFont(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Font);
    private void OnNavCaret(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Caret);
    private void OnNavTabs(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Tabs);
    private void OnNavFind(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Find);
    private void OnNavExplorer(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Explorer);
    private void OnNavTerminal(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Terminal);
    private void OnNavWordWrap(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.WordWrap);
    private void OnNavIndentation(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Indentation);

    // ── Search ──────────────────────────────────────────────────────────

    private void OnSettingsSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        ClearSettingsSearchBtn.Visibility = string.IsNullOrEmpty(SettingsSearchInput.Text)
            ? Visibility.Hidden : Visibility.Visible;
        ApplySettingsSearch();
    }

    private void OnSettingsSearchFocusChanged(object sender, RoutedEventArgs e)
    {
        SettingsSearchBorder.SetResourceReference(Border.BorderBrushProperty,
            SettingsSearchInput.IsKeyboardFocused ? ThemeResourceKeys.TextFgMuted : ThemeResourceKeys.MenuPopupBorder);
    }

    private void OnClearSettingsSearchClick(object sender, RoutedEventArgs e)
    {
        SettingsSearchInput.Clear();
        FocusSettingsSearch();
    }

    private void FocusSettingsSearch()
    {
        Dispatcher.BeginInvoke(DispatcherPriority.Input,
            () =>
            {
                Keyboard.Focus(SettingsSearchInput);
                SettingsSearchInput.SelectAll();
            });
    }

    private void ApplySettingsSearch()
    {
        string query = SettingsSearchInput.Text?.Trim() ?? "";
        bool hasQuery = query.Length > 0;
        var sectionMatches = new Dictionary<SettingsSection, bool>();

        foreach (var section in SectionInfos)
            sectionMatches[section.Section] = !hasQuery || TermsMatch(section.Terms, query);

        var rowMatchesBySection = new HashSet<SettingsSection>();
        foreach (var entry in SettingSearchEntries)
        {
            bool rowVisible = !hasQuery
                || sectionMatches[entry.Section]
                || TermsMatch(entry.Terms, query);

            entry.Row.Visibility = rowVisible ? Visibility.Visible : Visibility.Collapsed;
            if (rowVisible && hasQuery)
                rowMatchesBySection.Add(entry.Section);
        }

        SettingsSection? firstVisible = null;
        foreach (var section in SectionInfos)
        {
            bool visible = !hasQuery
                || sectionMatches[section.Section]
                || (!section.IsKeybinds && rowMatchesBySection.Contains(section.Section));

            section.Container.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            section.NavButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            firstVisible ??= visible ? section.Section : null;
        }

        UpdateNavGroupVisibility();
        NoSettingsResults.Visibility = firstVisible == null ? Visibility.Visible : Visibility.Collapsed;

        if (firstVisible == null)
        {
            SetActiveNav(null);
            return;
        }

        SettingsContent.UpdateLayout();
        ScrollToSection(firstVisible.Value);
    }

    private void UpdateNavGroupVisibility()
    {
        SetHeaderVisibility(NavApplicationHeader, SettingsSection.Theme, SettingsSection.CommandPalette, SettingsSection.Keybinds);
        SetHeaderVisibility(NavEditorHeader, SettingsSection.Font, SettingsSection.Caret, SettingsSection.Tabs,
            SettingsSection.Find, SettingsSection.Indentation, SettingsSection.WordWrap);
        SetHeaderVisibility(NavPanelsHeader, SettingsSection.Explorer, SettingsSection.Terminal);
    }

    private void SetHeaderVisibility(TextBlock header, params SettingsSection[] sections)
    {
        bool anyVisible = SectionInfos.Any(info =>
            sections.Contains(info.Section) && info.NavButton.Visibility == Visibility.Visible);
        header.Visibility = anyVisible ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool TermsMatch(IEnumerable<string> terms, string query)
    {
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
            return true;

        string haystack = string.Join(' ', terms);
        return tokens.All(token => haystack.Contains(token, StringComparison.OrdinalIgnoreCase));
    }

    // ── Read / Apply ────────────────────────────────────────────────────

    private void ReadCurrentValues()
    {
        TabSize = AppSettings.TabSizeOptions[Math.Max(0, TabSizeBox.SelectedIndex)];
        BlockCaret = CaretStyleBox.SelectedIndex == 1;
        CaretBlinkMs = (int)CaretBlinkSlider.Value;
        SelectedFontFamily = _fontNames[Math.Max(0, FontFamilyBox.SelectedIndex)];
        SelectedFontSize = AppSettings.FontSizeOptions[Math.Max(0, FontSizeBox.SelectedIndex)];
        SelectedFontWeight = AppSettings.FontWeightOptions[Math.Max(0, FontWeightBox.SelectedIndex)];
        SelectedLineHeight = AppSettings.LineHeightOptions[Math.Max(0, LineHeightBox.SelectedIndex)];
        ColorThemeName = _themeNames[Math.Max(0, ColorThemeBox.SelectedIndex)];
        FindBarPosition = FindBarPosBox.SelectedIndex == 0 ? "Top" : "Bottom";
        CommandPalettePosition = CmdPalettePosBox.SelectedIndex == 0 ? "Top" : "Center";
        FindSeedWithSelection = FindSeedSelBox.SelectedIndex == 0;
        FixedWidthTabs = FixedWidthTabsBox.SelectedIndex == 0;
        WordWrap = WordWrapBox.SelectedIndex == 0;
        WordWrapAtWords = WordWrapAtWordsBox.SelectedIndex == 0;
        WordWrapIndent = WordWrapIndentBox.SelectedIndex == 0;
        IndentGuides = IndentGuidesBox.SelectedIndex == 0;
        ExplorerFileIcons = AppSettings.ExplorerFileIconOptions[Math.Max(0, ExplorerFileIconsBox.SelectedIndex)];
        ExplorerRevealActiveFile = ExplorerRevealActiveFileBox.SelectedIndex == 0;

        var shellChoice = (TerminalPanel.TerminalShellPreference)Math.Clamp(TerminalShellBox.SelectedIndex, 0, 1);
        TerminalShellPath = TerminalPanel.ResolveShellPath(shellChoice);
        TerminalShellArgs = string.IsNullOrWhiteSpace(TerminalShellArgsBox.Text)
            ? null
            : TerminalShellArgsBox.Text.Trim();
        if (!int.TryParse(TerminalScrollbackBox.Text.Trim(), out int sb) || sb < 100)
            sb = 10_000;
        TerminalScrollbackLines = Math.Clamp(sb, 100, 1_000_000);
    }

    private void OnApply(object sender, RoutedEventArgs e)
    {
        ReadCurrentValues();
        Applied?.Invoke(this, EventArgs.Empty);
    }

    private void OnOK(object sender, RoutedEventArgs e)
    {
        ReadCurrentValues();
        DialogResult = true;
    }

    private void OnCaretBlinkSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (CaretBlinkLabel == null) return;
        int ms = (int)e.NewValue;
        CaretBlinkLabel.Text = ms == 0 ? "Off" : $"{ms}ms";
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
