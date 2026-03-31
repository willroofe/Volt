using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace TextEdit;

public partial class MainWindow : Window
{
    private string? _filePath;
    private Encoding _fileEncoding = new UTF8Encoding(false);
    private AppSettings _settings;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_BORDER_COLOR = 34;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        ApplySettings();
        RestoreWindowPosition();
        Editor.DirtyChanged += (_, _) => UpdateTitle();
        Editor.CaretMoved += (_, _) => UpdateCaretPos();
        CmdPalette.Closed += (_, _) => Keyboard.Focus(Editor);
        FindBarControl.SetEditor(Editor);
        FindBarControl.Closed += (_, _) => Keyboard.Focus(Editor);
        StateChanged += OnStateChanged;
        Closing += OnWindowClosing;
        ThemeManager.ThemeChanged += (_, _) => ApplyDwmTheme();
        SourceInitialized += (_, _) => ApplyDwmTheme();
    }

    private void ApplyDwmTheme()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        var hwnd = source.Handle;

        // Determine if theme is dark by checking luminance of the editor background
        var bg = ThemeManager.EditorBg as SolidColorBrush;
        if (bg == null) return;
        var c = bg.Color;
        bool isDark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;

        int darkMode = isDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

        // Set caption and border color to match the title bar theme
        var chromeBrush = Application.Current.Resources["ThemeChromeBrush"] as SolidColorBrush;
        if (chromeBrush != null)
        {
            var cc = chromeBrush.Color;
            int colorRef = cc.R | (cc.G << 8) | (cc.B << 16);
            DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref colorRef, sizeof(int));
            DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int));
        }
    }

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
        {
            // When maximized, Windows extends the window beyond screen edges
            // by the resize border thickness. Compensate with padding.
            var thickness = SystemParameters.WindowResizeBorderThickness;
            BorderThickness = new Thickness(
                thickness.Left + 1, thickness.Top + 1,
                thickness.Right + 1, thickness.Bottom + 1);
        }
        else
        {
            BorderThickness = new Thickness(0);
        }

        MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void ApplySettings()
    {
        Editor.TabSize = _settings.TabSize;
        Editor.BlockCaret = _settings.BlockCaret;
        Editor.CaretBlinkMs = _settings.CaretBlinkMs;
        if (_settings.FontFamily != null) Editor.FontFamilyName = _settings.FontFamily;
        Editor.EditorFontSize = _settings.FontSize;
        Editor.EditorFontWeight = _settings.FontWeight;
    }

    private void RestoreWindowPosition()
    {
        if (_settings.WindowWidth.HasValue && _settings.WindowHeight.HasValue)
        {
            double left = _settings.WindowLeft!.Value;
            double top = _settings.WindowTop!.Value;
            double width = _settings.WindowWidth.Value;
            double height = _settings.WindowHeight.Value;

            // Validate that at least 100x100 of the window is visible on the virtual screen
            double vsLeft = SystemParameters.VirtualScreenLeft;
            double vsTop = SystemParameters.VirtualScreenTop;
            double vsRight = vsLeft + SystemParameters.VirtualScreenWidth;
            double vsBottom = vsTop + SystemParameters.VirtualScreenHeight;

            bool visible = left + 100 <= vsRight &&
                           top + 100 <= vsBottom &&
                           left + width >= vsLeft + 100 &&
                           top + height >= vsTop + 100;

            if (visible)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
                Width = width;
                Height = height;
            }
            // else: fall through to default CenterScreen placement
        }

        if (_settings.WindowMaximized)
            Loaded += (_, _) => WindowState = WindowState.Maximized;
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (WindowState == WindowState.Normal)
        {
            _settings.WindowLeft = Left;
            _settings.WindowTop = Top;
            _settings.WindowWidth = Width;
            _settings.WindowHeight = Height;
        }

        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        _settings.Save();
    }

    private void UpdateCaretPos()
    {
        CaretPosText.Text = $"Ln {Editor.CaretLine + 1}, Col {Editor.CaretCol + 1}";
    }

    private string GetEncodingLabel()
    {
        if (_fileEncoding is UTF8Encoding utf8)
            return utf8.GetPreamble().Length > 0 ? "UTF-8 BOM" : "UTF-8";
        if (_fileEncoding is UnicodeEncoding)
            return "UTF-16";
        return _fileEncoding.EncodingName;
    }

    private void UpdateFileType()
    {
        var ext = _filePath != null ? Path.GetExtension(_filePath).ToLowerInvariant() : "";
        SyntaxManager.SetLanguageByExtension(ext);
        Editor.InvalidateSyntax();
        var fileType = ext switch
        {
            ".txt" => "Plain Text",
            ".cs" => "C# Source",
            ".pl" or ".cgi" => "Perl Script",
            ".py" => "Python Script",
            ".js" => "JavaScript",
            ".ts" => "TypeScript",
            ".json" => "JSON",
            ".xml" => "XML Document",
            ".xaml" => "XAML Document",
            ".html" or ".htm" => "HTML Document",
            ".css" => "CSS Stylesheet",
            ".md" => "Markdown",
            ".yml" or ".yaml" => "YAML",
            ".sql" => "SQL",
            ".sh" or ".bash" => "Shell Script",
            ".bat" or ".cmd" => "Batch File",
            ".ps1" => "PowerShell Script",
            ".cpp" or ".cc" or ".cxx" => "C++ Source",
            ".c" => "C Source",
            ".h" => "C/C++ Header",
            ".java" => "Java Source",
            ".rb" => "Ruby Script",
            ".go" => "Go Source",
            ".rs" => "Rust Source",
            ".ini" or ".cfg" => "Configuration File",
            ".log" => "Log File",
            _ => "Plain Text"
        };
        FileTypeText.Text = $"{fileType} ({GetEncodingLabel()}, {Editor.LineEnding})";
    }

    private void UpdateTitle()
    {
        var name = _filePath != null ? Path.GetFileName(_filePath) : "Untitled";
        var title = (Editor.IsDirty ? "\u2022 " : "") + $"TextEdit \u2014 {name}";
        Title = title;
        TitleText.Text = title;
    }

    private bool PromptSaveIfDirty()
    {
        if (!Editor.IsDirty) return true;
        var name = _filePath != null ? Path.GetFileName(_filePath) : "Untitled";
        var result = MessageBox.Show(
            $"Do you want to save changes to {name}?",
            "TextEdit", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) OnSave(this, new RoutedEventArgs());
        return !Editor.IsDirty || result == MessageBoxResult.No;
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        if (!PromptSaveIfDirty()) return;
        _filePath = null;
        _fileEncoding = new UTF8Encoding(false);
        Editor.SetContent("");
        UpdateTitle();
        UpdateFileType();
        FindBarControl.RefreshSearch();
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        if (!PromptSaveIfDirty()) return;
        var dlg = new OpenFileDialog
        {
            Filter = "All Files (*.*)|*.*|Text Files (*.txt)|*.txt|Perl Files (*.pl)|*.pl"
        };
        if (dlg.ShowDialog() != true) return;
        _filePath = dlg.FileName;
        _fileEncoding = DetectEncoding(_filePath);
        Editor.SetContent(File.ReadAllText(_filePath, _fileEncoding));
        UpdateTitle();
        UpdateFileType();
        FindBarControl.RefreshSearch();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_filePath == null)
        {
            OnSaveAs(sender, e);
            return;
        }
        AtomicWriteText(_filePath, Editor.GetContent(), _fileEncoding);
        Editor.MarkClean();
        UpdateTitle();
    }

    private static readonly string[] SaveFilters =
        ["Text Files (*.txt)|*.txt", "Perl Files (*.pl)|*.pl", "All Files (*.*)|*.*"];

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        int filterIndex = 1; // default to .txt
        if (_filePath != null)
        {
            var ext = Path.GetExtension(_filePath).ToLowerInvariant();
            filterIndex = ext switch
            {
                ".pl" => 2,
                ".txt" => 1,
                _ => 3 // All Files
            };
        }
        var dlg = new SaveFileDialog
        {
            Filter = string.Join("|", SaveFilters),
            FilterIndex = filterIndex,
            FileName = _filePath != null ? Path.GetFileName(_filePath) : ""
        };
        if (dlg.ShowDialog() != true) return;
        _filePath = dlg.FileName;
        AtomicWriteText(_filePath, Editor.GetContent(), _fileEncoding);
        Editor.MarkClean();
        UpdateTitle();
        UpdateFileType();
    }

    private static void AtomicWriteText(string path, string content, Encoding encoding)
    {
        var dir = Path.GetDirectoryName(path)!;
        var tempPath = Path.Combine(dir, Path.GetRandomFileName());
        File.WriteAllText(tempPath, content, encoding);
        File.Move(tempPath, path, overwrite: true);
    }

    private static Encoding DetectEncoding(string path)
    {
        using var stream = File.OpenRead(path);
        var bom = new byte[4];
        int read = stream.Read(bom, 0, 4);

        if (read >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF)
            return new UTF8Encoding(true);
        if (read >= 2 && bom[0] == 0xFF && bom[1] == 0xFE)
            return new UnicodeEncoding(false, true); // UTF-16 LE
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
            return new UnicodeEncoding(true, true); // UTF-16 BE
        if (read >= 4 && bom[0] == 0 && bom[1] == 0 && bom[2] == 0xFE && bom[3] == 0xFF)
            return new UTF32Encoding(true, true); // UTF-32 BE

        return new UTF8Encoding(false); // default: UTF-8 without BOM
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(Editor.TabSize, _settings.BlockCaret, _settings.CaretBlinkMs,
            Editor.FontFamilyName, Editor.EditorFontSize, Editor.EditorFontWeight, _settings.ColorTheme) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _settings.TabSize = dlg.TabSize;
            _settings.BlockCaret = dlg.BlockCaret;
            _settings.CaretBlinkMs = dlg.CaretBlinkMs;
            _settings.FontFamily = dlg.SelectedFontFamily;
            _settings.FontSize = dlg.SelectedFontSize;
            _settings.FontWeight = dlg.SelectedFontWeight;
            _settings.ColorTheme = dlg.ColorThemeName;
            _settings.Save();
            ApplySettings();
            ThemeManager.Apply(dlg.ColorThemeName);
        }
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            // Close any open menu, then allow drag
            Keyboard.ClearFocus();
            DragMove();
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && !shift && e.Key == Key.N) { OnNew(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.O) { OnOpen(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.F) { FindBarControl.Open(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.P) { OpenCommandPalette(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.S) { OnSaveAs(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.S) { OnSave(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { StepFontSize(1); e.Handled = true; }
        else if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { StepFontSize(-1); e.Handled = true; }
        else base.OnKeyDown(e);
    }

    private void StepFontSize(int direction)
    {
        var sizes = AppSettings.FontSizeOptions;
        int idx = Array.IndexOf(sizes, Editor.EditorFontSize);
        if (idx < 0) idx = Array.IndexOf(sizes, 14); // fallback to default
        int next = idx + direction;
        if (next < 0 || next >= sizes.Length) return;
        Editor.EditorFontSize = sizes[next];
        _settings.FontSize = sizes[next];
        _settings.Save();
    }

    private void OpenCommandPalette()
    {
        var commands = new List<PaletteCommand>
        {
            new("Change Theme", GetOptions: () =>
            {
                var original = _settings.ColorTheme;
                return ThemeManager.GetAvailableThemes().Select(name => new PaletteOption(
                    name,
                    ApplyPreview: () => ThemeManager.Apply(name),
                    Commit: () => { _settings.ColorTheme = name; _settings.Save(); },
                    Revert: () => ThemeManager.Apply(original)
                )).ToList();
            }),

            new("Change Font Size", GetOptions: () =>
            {
                var original = Editor.EditorFontSize;
                return AppSettings.FontSizeOptions.Select(size => new PaletteOption(
                    size.ToString(),
                    ApplyPreview: () => Editor.EditorFontSize = size,
                    Commit: () => { _settings.FontSize = size; _settings.Save(); },
                    Revert: () => Editor.EditorFontSize = original
                )).ToList();
            }),

            new("Change Font Family", GetOptions: () =>
            {
                var original = Editor.FontFamilyName;
                return EditorControl.GetMonospaceFonts().Select(name => new PaletteOption(
                    name,
                    ApplyPreview: () => Editor.FontFamilyName = name,
                    Commit: () => { _settings.FontFamily = name; _settings.Save(); },
                    Revert: () => Editor.FontFamilyName = original
                )).ToList();
            }),

            new("Change Font Weight", GetOptions: () =>
            {
                var original = Editor.EditorFontWeight;
                return AppSettings.FontWeightOptions.Select(w => new PaletteOption(
                    w,
                    ApplyPreview: () => Editor.EditorFontWeight = w,
                    Commit: () => { _settings.FontWeight = w; _settings.Save(); },
                    Revert: () => Editor.EditorFontWeight = original
                )).ToList();
            }),

            new("Change Tab Size", GetOptions: () =>
            {
                var original = Editor.TabSize;
                return AppSettings.TabSizeOptions.Select(size => new PaletteOption(
                    size.ToString(),
                    ApplyPreview: () => { Editor.TabSize = size; Editor.InvalidateVisual(); },
                    Commit: () => { _settings.TabSize = size; _settings.Save(); },
                    Revert: () => { Editor.TabSize = original; Editor.InvalidateVisual(); }
                )).ToList();
            }),

            new("Toggle Block Caret", Toggle: () =>
            {
                _settings.BlockCaret = !_settings.BlockCaret;
                Editor.BlockCaret = _settings.BlockCaret;
                _settings.Save();
                Editor.InvalidateVisual();
            }),
        };

        CmdPalette.SetCommands(commands);
        CmdPalette.Open();
    }
}
