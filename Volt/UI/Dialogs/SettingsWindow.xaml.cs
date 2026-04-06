using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Volt;

public record SettingsSnapshot(
    int TabSize, bool BlockCaret, int CaretBlinkMs,
    string FontFamily, double FontSize, string FontWeight,
    double LineHeight, string ColorTheme, string FindBarPosition,
    bool FindSeedWithSelection, bool FixedWidthTabs,
    bool WordWrap, bool WordWrapAtWords, bool WordWrapIndent,
    bool IndentGuides, string CommandPalettePosition,
    Dictionary<VoltCommand, KeyCombo> KeyBindings);

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
    public Dictionary<VoltCommand, KeyCombo> KeyBindings => new(_pendingBindings);

    private enum SettingsSection { Theme, CommandPalette, Keybinds, Font, Caret, Tabs, Find, Explorer, WordWrap, Indentation }

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

        foreach (var (cmd, combo) in snapshot.KeyBindings)
            _pendingBindings[cmd] = combo;

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

    private (Button nav, FrameworkElement scroller)[] _navSections = null!;

    private (Button nav, FrameworkElement scroller)[] NavSections => _navSections ??=
    [
        (NavTheme, ThemeScroller), (NavCommandPalette, CommandPaletteScroller),
        (NavKeybinds, KeybindsScroller), (NavFont, FontScroller),
        (NavCaret, CaretScroller), (NavTabs, TabsScroller),
        (NavFind, FindScroller), (NavExplorer, ExplorerScroller),
        (NavWordWrap, WordWrapScroller), (NavIndentation, IndentationScroller),
    ];

    private void SelectNav(SettingsSection section)
    {
        var active = (Style)FindResource("NavButtonActive");
        var inactive = (Style)FindResource("NavButton");
        var sections = NavSections;

        for (int i = 0; i < sections.Length; i++)
        {
            bool isActive = i == (int)section;
            sections[i].nav.Style = isActive ? active : inactive;
            sections[i].scroller.Visibility = isActive ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void OnNavTheme(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Theme);
    private void OnNavCommandPalette(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.CommandPalette);
    private void OnNavKeybinds(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Keybinds);
    private void OnNavFont(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Font);
    private void OnNavCaret(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Caret);
    private void OnNavTabs(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Tabs);
    private void OnNavFind(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Find);
    private void OnNavExplorer(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Explorer);
    private void OnNavWordWrap(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.WordWrap);
    private void OnNavIndentation(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Indentation);

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
