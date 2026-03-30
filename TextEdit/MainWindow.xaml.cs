using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace TextEdit;

public partial class MainWindow : Window
{
    private string? _filePath;
    private AppSettings _settings;

    private static readonly double[] PaletteFontSizes = [8, 9, 10, 11, 12, 13, 14, 16, 18, 20, 24, 28, 32, 36];
    private static readonly int[] PaletteTabSizes = [2, 4, 8];
    private static readonly string[] PaletteFontWeights = ["Thin", "ExtraLight", "Light", "Normal", "Medium", "SemiBold", "Bold", "ExtraBold", "Black"];

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        ApplySettings();
        RestoreWindowPosition();
        Editor.DirtyChanged += (_, _) => UpdateTitle();
        Editor.CaretMoved += (_, _) => UpdateCaretPos();
        StateChanged += OnStateChanged;
        Closing += OnWindowClosing;
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
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = _settings.WindowLeft!.Value;
            Top = _settings.WindowTop!.Value;
            Width = _settings.WindowWidth.Value;
            Height = _settings.WindowHeight.Value;
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

    private void UpdateFileType()
    {
        var ext = _filePath != null ? Path.GetExtension(_filePath).ToLowerInvariant() : "";
        SyntaxManager.SetLanguageByExtension(ext);
        Editor.InvalidateVisual();
        FileTypeText.Text = ext switch
        {
            ".txt" => "Plain Text",
            ".cs" => "C# Source",
            ".pl" => "Perl Script",
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
            _ => _filePath != null ? "Plain Text" : "Plain Text"
        };
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
        Editor.SetContent("");
        UpdateTitle();
        UpdateFileType();
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
        Editor.SetContent(File.ReadAllText(_filePath));
        UpdateTitle();
        UpdateFileType();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_filePath == null)
        {
            OnSaveAs(sender, e);
            return;
        }
        File.WriteAllText(_filePath, Editor.GetContent());
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
        File.WriteAllText(_filePath, Editor.GetContent());
        Editor.MarkClean();
        UpdateTitle();
        UpdateFileType();
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

        if (ctrl && e.Key == Key.N) { OnNew(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && e.Key == Key.O) { OnOpen(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.P) { OpenCommandPalette(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.S) { OnSaveAs(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && e.Key == Key.S) { OnSave(this, new RoutedEventArgs()); e.Handled = true; }
        else base.OnKeyDown(e);
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
                return PaletteFontSizes.Select(size => new PaletteOption(
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
                return PaletteFontWeights.Select(w => new PaletteOption(
                    w,
                    ApplyPreview: () => Editor.EditorFontWeight = w,
                    Commit: () => { _settings.FontWeight = w; _settings.Save(); },
                    Revert: () => Editor.EditorFontWeight = original
                )).ToList();
            }),

            new("Change Tab Size", GetOptions: () =>
            {
                var original = Editor.TabSize;
                return PaletteTabSizes.Select(size => new PaletteOption(
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
