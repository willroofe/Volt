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

    private readonly List<string> _themeNames;
    private readonly List<string> _fontNames;

    public SettingsWindow(int currentTabSize, bool blockCaret, int caretBlinkMs,
        string currentFontFamily, double currentFontSize, string currentFontWeight, string currentColorTheme)
    {
        InitializeComponent();
        TabSize = currentTabSize;
        BlockCaret = blockCaret;
        CaretBlinkMs = caretBlinkMs;
        SelectedFontFamily = currentFontFamily;
        SelectedFontSize = currentFontSize;
        SelectedFontWeight = currentFontWeight;
        ColorThemeName = currentColorTheme;

        int index = Array.IndexOf(AppSettings.TabSizeOptions, currentTabSize);
        TabSizeBox.SelectedIndex = index >= 0 ? index : 1;
        CaretStyleBox.SelectedIndex = blockCaret ? 1 : 0;
        CaretBlinkSlider.Value = caretBlinkMs;

        // Populate font family dropdown
        _fontNames = EditorControl.GetMonospaceFonts();
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

        // Populate color theme dropdown
        _themeNames = ThemeManager.GetAvailableThemes();
        foreach (var name in _themeNames)
            ColorThemeBox.Items.Add(name);
        int ti = _themeNames.IndexOf(currentColorTheme);
        ColorThemeBox.SelectedIndex = ti >= 0 ? ti : 0;
    }

    private void SelectNav(string section)
    {
        NavBehaviour.Style = (Style)FindResource(section == "Behaviour" ? "NavButtonActive" : "NavButton");
        NavAppearance.Style = (Style)FindResource(section == "Appearance" ? "NavButtonActive" : "NavButton");

        BehaviourScroller.Visibility = section == "Behaviour" ? Visibility.Visible : Visibility.Collapsed;
        AppearanceScroller.Visibility = section == "Appearance" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNavBehaviour(object sender, RoutedEventArgs e) => SelectNav("Behaviour");
    private void OnNavAppearance(object sender, RoutedEventArgs e) => SelectNav("Appearance");

    private void OnOK(object sender, RoutedEventArgs e)
    {
        TabSize = AppSettings.TabSizeOptions[TabSizeBox.SelectedIndex];
        BlockCaret = CaretStyleBox.SelectedIndex == 1;
        CaretBlinkMs = (int)CaretBlinkSlider.Value;
        SelectedFontFamily = _fontNames[FontFamilyBox.SelectedIndex];
        SelectedFontSize = AppSettings.FontSizeOptions[FontSizeBox.SelectedIndex];
        SelectedFontWeight = AppSettings.FontWeightOptions[FontWeightBox.SelectedIndex];
        ColorThemeName = _themeNames[ColorThemeBox.SelectedIndex];
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
