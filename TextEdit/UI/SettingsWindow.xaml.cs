using System.Windows;

namespace TextEdit;

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

    public event EventHandler? Applied;

    private readonly ThemeManager _themeManager;
    private readonly List<string> _themeNames;
    private readonly List<string> _fontNames;

    public SettingsWindow(ThemeManager themeManager, int currentTabSize, bool blockCaret, int caretBlinkMs,
        string currentFontFamily, double currentFontSize, string currentFontWeight, double currentLineHeight,
        string currentColorTheme, string findBarPosition)
    {
        _themeManager = themeManager;
        InitializeComponent();
        TabSize = currentTabSize;
        BlockCaret = blockCaret;
        CaretBlinkMs = caretBlinkMs;
        SelectedFontFamily = currentFontFamily;
        SelectedFontSize = currentFontSize;
        SelectedFontWeight = currentFontWeight;
        SelectedLineHeight = currentLineHeight;
        ColorThemeName = currentColorTheme;
        FindBarPosition = findBarPosition;

        int index = Array.IndexOf(AppSettings.TabSizeOptions, currentTabSize);
        TabSizeBox.SelectedIndex = index >= 0 ? index : 1;
        CaretStyleBox.SelectedIndex = blockCaret ? 1 : 0;
        CaretBlinkSlider.Value = caretBlinkMs;
        FindBarPosBox.SelectedIndex = findBarPosition == "Top" ? 0 : 1;

        // Populate font family dropdown
        _fontNames = FontManager.GetMonospaceFonts();
        foreach (var name in _fontNames)
            FontFamilyBox.Items.Add(name);
        int fi = _fontNames.IndexOf(currentFontFamily);
        FontFamilyBox.SelectedIndex = fi >= 0 ? fi : 0;

        // Populate font size dropdown
        foreach (var size in AppSettings.FontSizeOptions)
            FontSizeBox.Items.Add(size.ToString());
        int si = Array.IndexOf(AppSettings.FontSizeOptions, currentFontSize);
        FontSizeBox.SelectedIndex = si >= 0 ? si : Array.IndexOf(AppSettings.FontSizeOptions, 14);

        // Populate font weight dropdown
        foreach (var w in AppSettings.FontWeightOptions)
            FontWeightBox.Items.Add(w);
        int wi = Array.IndexOf(AppSettings.FontWeightOptions, currentFontWeight);
        FontWeightBox.SelectedIndex = wi >= 0 ? wi : Array.IndexOf(AppSettings.FontWeightOptions, "Normal");

        // Populate line height dropdown
        foreach (var lh in AppSettings.LineHeightOptions)
            LineHeightBox.Items.Add(lh.ToString("0.0") + "x");
        int li = Array.IndexOf(AppSettings.LineHeightOptions, currentLineHeight);
        LineHeightBox.SelectedIndex = li >= 0 ? li : 0;

        // Populate color theme dropdown
        _themeNames = _themeManager.GetAvailableThemes();
        foreach (var name in _themeNames)
            ColorThemeBox.Items.Add(name);
        int ti = _themeNames.IndexOf(currentColorTheme);
        ColorThemeBox.SelectedIndex = ti >= 0 ? ti : 0;
    }

    private void SelectNav(string section)
    {
        NavTheme.Style = (Style)FindResource(section == "Theme" ? "NavButtonActive" : "NavButton");
        NavFont.Style = (Style)FindResource(section == "Font" ? "NavButtonActive" : "NavButton");
        NavCaret.Style = (Style)FindResource(section == "Caret" ? "NavButtonActive" : "NavButton");
        NavFind.Style = (Style)FindResource(section == "Find" ? "NavButtonActive" : "NavButton");

        ThemeScroller.Visibility = section == "Theme" ? Visibility.Visible : Visibility.Collapsed;
        FontScroller.Visibility = section == "Font" ? Visibility.Visible : Visibility.Collapsed;
        CaretScroller.Visibility = section == "Caret" ? Visibility.Visible : Visibility.Collapsed;
        FindScroller.Visibility = section == "Find" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavTheme(object sender, RoutedEventArgs e) => SelectNav("Theme");
    private void OnNavFont(object sender, RoutedEventArgs e) => SelectNav("Font");
    private void OnNavCaret(object sender, RoutedEventArgs e) => SelectNav("Caret");
    private void OnNavFind(object sender, RoutedEventArgs e) => SelectNav("Find");

    private void ReadCurrentValues()
    {
        TabSize = AppSettings.TabSizeOptions[TabSizeBox.SelectedIndex];
        BlockCaret = CaretStyleBox.SelectedIndex == 1;
        CaretBlinkMs = (int)CaretBlinkSlider.Value;
        SelectedFontFamily = _fontNames[FontFamilyBox.SelectedIndex];
        SelectedFontSize = AppSettings.FontSizeOptions[FontSizeBox.SelectedIndex];
        SelectedFontWeight = AppSettings.FontWeightOptions[FontWeightBox.SelectedIndex];
        SelectedLineHeight = AppSettings.LineHeightOptions[LineHeightBox.SelectedIndex];
        ColorThemeName = _themeNames[ColorThemeBox.SelectedIndex];
        FindBarPosition = FindBarPosBox.SelectedIndex == 0 ? "Top" : "Bottom";
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
