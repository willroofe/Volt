using System.Windows;

namespace Volt;

public record SettingsSnapshot(
    int TabSize, bool BlockCaret, int CaretBlinkMs,
    string FontFamily, double FontSize, string FontWeight,
    double LineHeight, string ColorTheme, string FindBarPosition,
    bool FindSeedWithSelection, bool FixedWidthTabs,
    bool WordWrap, bool WordWrapAtWords);

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

    private enum SettingsSection { Theme, Font, Caret, Tabs, Find, Explorer, WordWrap }

    public event EventHandler? Applied;

    private readonly ThemeManager _themeManager;
    private readonly List<string> _themeNames;
    private readonly List<string> _fontNames;

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

        int index = Array.IndexOf(AppSettings.TabSizeOptions, snapshot.TabSize);
        TabSizeBox.SelectedIndex = index >= 0 ? index : 1;
        CaretStyleBox.SelectedIndex = snapshot.BlockCaret ? 1 : 0;
        CaretBlinkSlider.Value = snapshot.CaretBlinkMs;
        FindBarPosBox.SelectedIndex = snapshot.FindBarPosition == "Top" ? 0 : 1;
        FindSeedWithSelection = snapshot.FindSeedWithSelection;
        FindSeedSelBox.SelectedIndex = snapshot.FindSeedWithSelection ? 0 : 1;
        FixedWidthTabs = snapshot.FixedWidthTabs;
        FixedWidthTabsBox.SelectedIndex = snapshot.FixedWidthTabs ? 0 : 1;
        WordWrap = snapshot.WordWrap;
        WordWrapBox.SelectedIndex = snapshot.WordWrap ? 0 : 1;
        WordWrapAtWords = snapshot.WordWrapAtWords;
        WordWrapAtWordsBox.SelectedIndex = snapshot.WordWrapAtWords ? 0 : 1;

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
    }

    private void SelectNav(SettingsSection section)
    {
        NavTheme.Style = (Style)FindResource(section == SettingsSection.Theme ? "NavButtonActive" : "NavButton");
        NavFont.Style = (Style)FindResource(section == SettingsSection.Font ? "NavButtonActive" : "NavButton");
        NavCaret.Style = (Style)FindResource(section == SettingsSection.Caret ? "NavButtonActive" : "NavButton");
        NavTabs.Style = (Style)FindResource(section == SettingsSection.Tabs ? "NavButtonActive" : "NavButton");
        NavFind.Style = (Style)FindResource(section == SettingsSection.Find ? "NavButtonActive" : "NavButton");
        NavExplorer.Style = (Style)FindResource(section == SettingsSection.Explorer ? "NavButtonActive" : "NavButton");
        NavWordWrap.Style = (Style)FindResource(section == SettingsSection.WordWrap ? "NavButtonActive" : "NavButton");

        ThemeScroller.Visibility = section == SettingsSection.Theme ? Visibility.Visible : Visibility.Collapsed;
        FontScroller.Visibility = section == SettingsSection.Font ? Visibility.Visible : Visibility.Collapsed;
        CaretScroller.Visibility = section == SettingsSection.Caret ? Visibility.Visible : Visibility.Collapsed;
        TabsScroller.Visibility = section == SettingsSection.Tabs ? Visibility.Visible : Visibility.Collapsed;
        FindScroller.Visibility = section == SettingsSection.Find ? Visibility.Visible : Visibility.Collapsed;
        ExplorerScroller.Visibility = section == SettingsSection.Explorer ? Visibility.Visible : Visibility.Collapsed;
        WordWrapScroller.Visibility = section == SettingsSection.WordWrap ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavTheme(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Theme);
    private void OnNavFont(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Font);
    private void OnNavCaret(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Caret);
    private void OnNavTabs(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Tabs);
    private void OnNavFind(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Find);
    private void OnNavExplorer(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.Explorer);
    private void OnNavWordWrap(object sender, RoutedEventArgs e) => SelectNav(SettingsSection.WordWrap);

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
        FindSeedWithSelection = FindSeedSelBox.SelectedIndex == 0;
        FixedWidthTabs = FixedWidthTabsBox.SelectedIndex == 0;
        WordWrap = WordWrapBox.SelectedIndex == 0;
        WordWrapAtWords = WordWrapAtWordsBox.SelectedIndex == 0;
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
