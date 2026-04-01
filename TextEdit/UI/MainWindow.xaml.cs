using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace TextEdit;

public partial class MainWindow : Window
{
    private readonly List<TabInfo> _tabs = [];
    private TabInfo? _activeTab;
    private AppSettings _settings;

    // Tab drag-to-reorder state
    private TabInfo? _dragTab;
    private Point _dragStartPos;
    private bool _isDragging;
    private int _dragTargetIndex = -1;
    private System.Windows.Controls.Primitives.Popup? _dragGhost;

    private EditorControl Editor => _activeTab!.Editor;

    private ThemeManager ThemeManager => App.Current.ThemeManager;
    private SyntaxManager SyntaxManager => App.Current.SyntaxManager;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_CAPTION_COLOR = 35;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const int WM_NCHITTEST = 0x0084;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTTOP = 12;
    private const int HTTOPLEFT = 13;
    private const int HTTOPRIGHT = 14;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();

        // Create the initial tab
        var tab = CreateTab();
        ActivateTab(tab);

        ApplySettings();
        UpdateTabOverflowBrushes();
        RestoreWindowPosition();

        CmdPalette.Closed += (_, _) => Keyboard.Focus(Editor);
        FindBarControl.Closed += (_, _) => Keyboard.Focus(Editor);
        TabScrollViewer.ScrollChanged += (_, _) => UpdateTabOverflowIndicators();
        StateChanged += OnStateChanged;
        Closing += OnWindowClosing;
        ThemeManager.ThemeChanged += (_, _) => { ApplyDwmTheme(); UpdateTabOverflowBrushes(); };
        SourceInitialized += (_, _) =>
        {
            ApplyDwmTheme();
            if (PresentationSource.FromVisual(this) is HwndSource source)
                source.AddHook(WndProc);
        };
    }

    private TabInfo CreateTab(string? filePath = null)
    {
        var tab = new TabInfo(ThemeManager, SyntaxManager) { FilePath = filePath };
        _tabs.Add(tab);

        // Wire up per-tab dirty handler (for tab header updates — lives for the tab's lifetime)
        tab.Editor.DirtyChanged += (_, _) => UpdateTabHeader(tab);

        tab.HeaderElement = CreateTabHeader(tab);
        TabStrip.Children.Add(tab.HeaderElement);
        return tab;
    }

    private void ActivateTab(TabInfo tab)
    {
        // Unhook events from previous active tab
        if (_activeTab != null)
        {
            _activeTab.Editor.DirtyChanged -= OnActiveDirtyChanged;
            _activeTab.Editor.CaretMoved -= OnActiveCaretMoved;
        }

        _activeTab = tab;

        // Swap the editor into the host
        EditorHost.Child = tab.ScrollHost;

        // Hook events for the new active tab
        tab.Editor.DirtyChanged += OnActiveDirtyChanged;
        tab.Editor.CaretMoved += OnActiveCaretMoved;

        // Update FindBar to target the new editor
        FindBarControl.SetEditor(tab.Editor);
        FindBarControl.RefreshSearch();

        // Apply current settings to the editor
        ApplySettingsToEditor(tab.Editor);

        // Update UI
        UpdateTitle();
        UpdateFileType();
        UpdateCaretPos();
        UpdateAllTabHeaders();
        BringTabIntoView(tab);

        Keyboard.Focus(tab.Editor);
    }

    private void BringTabIntoView(TabInfo tab)
    {
        tab.HeaderElement.BringIntoView();
    }

    private void OnTabScrollViewerMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScrollViewer.ScrollToHorizontalOffset(
            TabScrollViewer.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void UpdateTabOverflowIndicators()
    {
        double offset = TabScrollViewer.HorizontalOffset;
        double scrollable = TabScrollViewer.ScrollableWidth;
        TabOverflowLeft.Visibility = offset > 1 ? Visibility.Visible : Visibility.Collapsed;
        TabOverflowRight.Visibility = offset < scrollable - 1 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateTabOverflowBrushes()
    {
        var color = (Application.Current.Resources["ThemeTabBarBg"] as SolidColorBrush)?.Color ?? Colors.Black;
        var transparent = Color.FromArgb(0, color.R, color.G, color.B);

        var leftBrush = new LinearGradientBrush();
        leftBrush.StartPoint = new Point(0, 0);
        leftBrush.EndPoint = new Point(1, 0);
        leftBrush.GradientStops.Add(new GradientStop(color, 0.0));
        leftBrush.GradientStops.Add(new GradientStop(color, 0.6));
        leftBrush.GradientStops.Add(new GradientStop(transparent, 1.0));
        TabOverflowLeft.Background = leftBrush;

        var rightBrush = new LinearGradientBrush();
        rightBrush.StartPoint = new Point(0, 0);
        rightBrush.EndPoint = new Point(1, 0);
        rightBrush.GradientStops.Add(new GradientStop(transparent, 0.0));
        rightBrush.GradientStops.Add(new GradientStop(color, 0.4));
        rightBrush.GradientStops.Add(new GradientStop(color, 1.0));
        TabOverflowRight.Background = rightBrush;
    }

    private void CloseTab(TabInfo tab)
    {
        if (tab.Editor.IsDirty && !PromptSaveTab(tab)) return;

        int idx = _tabs.IndexOf(tab);
        _tabs.Remove(tab);
        TabStrip.Children.Remove(tab.HeaderElement);

        // Unhook events if this was the active tab
        if (tab == _activeTab)
        {
            tab.Editor.DirtyChanged -= OnActiveDirtyChanged;
            tab.Editor.CaretMoved -= OnActiveCaretMoved;
            _activeTab = null;
        }

        if (_tabs.Count == 0)
        {
            // Always keep at least one tab
            var newTab = CreateTab();
            ActivateTab(newTab);
        }
        else if (_activeTab == null)
        {
            int nextIdx = Math.Min(idx, _tabs.Count - 1);
            ActivateTab(_tabs[nextIdx]);
        }
    }

    private Border CreateTabHeader(TabInfo tab)
    {
        var textBlock = new TextBlock
        {
            Text = tab.DisplayName,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 6, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 150
        };
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextFg");

        var closeBtn = new Button
        {
            Content = "\uE8BB",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 8,
            Width = 20,
            Height = 20,
            Margin = new Thickness(0, 0, 4, 0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center
        };
        closeBtn.SetResourceReference(Button.ForegroundProperty, "ThemeTextFgMuted");

        // Close button template with hover effect
        var closeBtnTemplate = new ControlTemplate(typeof(Button));
        var closeBorder = new FrameworkElementFactory(typeof(Border), "Bd");
        closeBorder.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        closeBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        var closeContent = new FrameworkElementFactory(typeof(ContentPresenter));
        closeContent.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        closeContent.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        closeBorder.AppendChild(closeContent);
        closeBtnTemplate.VisualTree = closeBorder;
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
            new DynamicResourceExtension("ThemeButtonHover"), "Bd"));
        closeBtnTemplate.Triggers.Add(hoverTrigger);
        closeBtn.Template = closeBtnTemplate;

        closeBtn.Click += (_, _) => CloseTab(tab);

        var panel = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(closeBtn, Dock.Right);
        panel.Children.Add(closeBtn);
        panel.Children.Add(textBlock);

        var header = new Border
        {
            Child = panel,
            Height = 33,
            MinWidth = 60,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0, 0, 1, 0)
        };
        header.SetResourceReference(Border.BorderBrushProperty, "ThemeTabBorder");

        // Click to activate + drag to reorder
        header.MouseLeftButtonDown += (_, e) =>
        {
            ActivateTab(tab);
            _dragTab = tab;
            _dragStartPos = e.GetPosition(TabStrip);
            _isDragging = false;
            _dragTargetIndex = -1;
            header.CaptureMouse();
            e.Handled = true;
        };

        header.MouseMove += (_, e) =>
        {
            if (_dragTab != tab || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(TabStrip);
            if (!_isDragging)
            {
                if (Math.Abs(pos.X - _dragStartPos.X) < SystemParameters.MinimumHorizontalDragDistance)
                    return;
                _isDragging = true;
                ShowDragGhost(tab);
                header.Opacity = 0.4;
            }
            UpdateDragGhost(e);
            UpdateDropIndicator(pos.X, _tabs.IndexOf(tab));
        };

        header.MouseLeftButtonUp += (_, e) =>
        {
            if (_dragTab == tab)
            {
                header.ReleaseMouseCapture();
                if (_isDragging)
                {
                    header.Opacity = 1.0;
                    HideDragGhost();
                    TabDropIndicator.Visibility = Visibility.Collapsed;
                    if (_dragTargetIndex >= 0)
                        CommitTabReorder(tab, _dragTargetIndex);
                }
                _dragTab = null;
                _isDragging = false;
                _dragTargetIndex = -1;
            }
        };

        // Middle-click to close tab
        header.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                CloseTab(tab);
                e.Handled = true;
            }
        };

        return header;
    }

    private void ShowDragGhost(TabInfo tab)
    {
        var text = new TextBlock
        {
            Text = tab.DisplayName,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextFg");

        var border = new Border
        {
            Child = text,
            Height = 30,
            MinWidth = 60,
            CornerRadius = new CornerRadius(4),
            Opacity = 0.85
        };
        border.SetResourceReference(Border.BackgroundProperty, "ThemeTabActive");
        border.SetResourceReference(Border.BorderBrushProperty, "ThemeTabBorder");
        border.BorderThickness = new Thickness(1);

        _dragGhost = new System.Windows.Controls.Primitives.Popup
        {
            Child = border,
            AllowsTransparency = true,
            Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint,
            IsHitTestVisible = false,
            IsOpen = true
        };
    }

    private void UpdateDragGhost(MouseEventArgs e)
    {
        if (_dragGhost == null) return;
        var screenPos = PointToScreen(e.GetPosition(this));
        // PointToScreen returns physical pixels; Popup offsets use DIPs — convert back
        var source = PresentationSource.FromVisual(this);
        double dpiScale = source?.CompositionTarget?.TransformFromDevice.M11 ?? 1.0;
        _dragGhost.HorizontalOffset = screenPos.X * dpiScale + 12;
        _dragGhost.VerticalOffset = screenPos.Y * dpiScale + 4;
    }

    private void HideDragGhost()
    {
        if (_dragGhost != null)
        {
            _dragGhost.IsOpen = false;
            _dragGhost = null;
        }
    }

    private void UpdateDropIndicator(double mouseX, int dragSourceIdx)
    {
        // Calculate the insertion index and the X position for the indicator line
        double offset = 0;
        int insertIdx = -1;
        double indicatorX = 0;

        for (int i = 0; i < TabStrip.Children.Count; i++)
        {
            if (TabStrip.Children[i] is FrameworkElement el)
            {
                double width = el.ActualWidth;
                double midpoint = offset + width / 2;
                if (mouseX < midpoint)
                {
                    insertIdx = i;
                    indicatorX = offset;
                    break;
                }
                offset += width;
            }
        }

        if (insertIdx < 0)
        {
            // Past the last tab — insert at end
            insertIdx = TabStrip.Children.Count;
            indicatorX = offset;
        }

        // If dropping at the same position or adjacent (no actual move), hide indicator
        if (insertIdx == dragSourceIdx || insertIdx == dragSourceIdx + 1)
        {
            TabDropIndicator.Visibility = Visibility.Collapsed;
            _dragTargetIndex = -1;
            return;
        }

        _dragTargetIndex = insertIdx > dragSourceIdx ? insertIdx - 1 : insertIdx;
        TabDropIndicator.Visibility = Visibility.Visible;
        TabDropIndicator.Margin = new Thickness(indicatorX - 1, 0, 0, 0);
    }

    private void CommitTabReorder(TabInfo tab, int targetIdx)
    {
        int currentIdx = _tabs.IndexOf(tab);
        if (targetIdx == currentIdx) return;

        _tabs.RemoveAt(currentIdx);
        _tabs.Insert(targetIdx, tab);

        TabStrip.Children.Remove(tab.HeaderElement);
        TabStrip.Children.Insert(targetIdx, tab.HeaderElement);
    }

    private void UpdateTabHeader(TabInfo tab)
    {
        if (tab.HeaderElement?.Child is DockPanel panel)
        {
            // TextBlock is the last child (fill)
            foreach (var child in panel.Children)
            {
                if (child is TextBlock tb)
                {
                    var name = tab.DisplayName;
                    tb.Text = tab.Editor.IsDirty ? "\u2022 " + name : name;
                    break;
                }
            }
        }
        // Also update window title if this is the active tab
        if (tab == _activeTab) UpdateTitle();
    }

    private void UpdateAllTabHeaders()
    {
        foreach (var tab in _tabs)
        {
            var isActive = tab == _activeTab;
            if (tab.HeaderElement != null)
            {
                tab.HeaderElement.SetResourceReference(Border.BackgroundProperty,
                    isActive ? "ThemeTabActive" : "ThemeTabInactive");
            }
        }
    }

    private void SwitchTab(int direction)
    {
        if (_tabs.Count <= 1 || _activeTab == null) return;
        int idx = _tabs.IndexOf(_activeTab);
        int next = (idx + direction + _tabs.Count) % _tabs.Count;
        ActivateTab(_tabs[next]);
    }

    private void OnActiveDirtyChanged(object? sender, EventArgs e) => UpdateTitle();
    private void OnActiveCaretMoved(object? sender, EventArgs e) => UpdateCaretPos();

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_NCHITTEST && WindowState != WindowState.Maximized)
        {
            int x = (short)(lParam.ToInt64() & 0xFFFF);
            int y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
            var pt = PointFromScreen(new Point(x, y));
            const int side = 3;
            const int edge = 6;

            bool left = pt.X < side + 1;
            bool right = pt.X >= ActualWidth - side;
            bool top = pt.Y < side + 1;
            bool bottom = pt.Y >= ActualHeight - edge;

            if (top || bottom || left || right)
            {
                handled = true;
                if (top && left) return (IntPtr)HTTOPLEFT;
                if (top && right) return (IntPtr)HTTOPRIGHT;
                if (bottom && left) return (IntPtr)HTBOTTOMLEFT;
                if (bottom && right) return (IntPtr)HTBOTTOMRIGHT;
                if (left) return (IntPtr)HTLEFT;
                if (right) return (IntPtr)HTRIGHT;
                if (top) return (IntPtr)HTTOP;
                return (IntPtr)HTBOTTOM;
            }
        }

        if (msg == WM_MOUSEHWHEEL)
        {
            int delta = (short)(wParam.ToInt64() >> 16);
            var pos = Mouse.GetPosition(TabScrollViewer);
            bool overTabBar = pos.Y >= 0 && pos.Y <= TabScrollViewer.ActualHeight
                           && pos.X >= 0 && pos.X <= TabScrollViewer.ActualWidth;

            if (overTabBar)
            {
                TabScrollViewer.ScrollToHorizontalOffset(
                    TabScrollViewer.HorizontalOffset + delta);
                handled = true;
            }
            else if (_activeTab != null)
            {
                _activeTab.Editor.SetHorizontalOffset(
                    _activeTab.Editor.HorizontalOffset + delta);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    private void ApplyDwmTheme()
    {
        if (PresentationSource.FromVisual(this) is not HwndSource source) return;
        var hwnd = source.Handle;

        var bg = ThemeManager.EditorBg as SolidColorBrush;
        if (bg == null) return;
        var c = bg.Color;
        bool isDark = (0.299 * c.R + 0.587 * c.G + 0.114 * c.B) < 128;

        int darkMode = isDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref darkMode, sizeof(int));

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
        foreach (var tab in _tabs)
            ApplySettingsToEditor(tab.Editor);
        FindBarControl.SetPosition(_settings.Editor.Find.BarPosition);
    }

    private void ApplySettingsToEditor(EditorControl editor)
    {
        editor.TabSize = _settings.Editor.TabSize;
        editor.BlockCaret = _settings.Editor.Caret.BlockCaret;
        editor.CaretBlinkMs = _settings.Editor.Caret.BlinkMs;
        if (_settings.Editor.Font.Family != null) editor.FontFamilyName = _settings.Editor.Font.Family;
        editor.EditorFontSize = _settings.Editor.Font.Size;
        editor.EditorFontWeight = _settings.Editor.Font.Weight;
        editor.LineHeightMultiplier = _settings.Editor.Font.LineHeight;
    }

    private void RestoreWindowPosition()
    {
        if (_settings.WindowWidth.HasValue && _settings.WindowHeight.HasValue)
        {
            double left = _settings.WindowLeft!.Value;
            double top = _settings.WindowTop!.Value;
            double width = _settings.WindowWidth.Value;
            double height = _settings.WindowHeight.Value;

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
        }

        if (_settings.WindowMaximized)
            Loaded += (_, _) => WindowState = WindowState.Maximized;
    }

    private void OnWindowClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Prompt save for each dirty tab
        foreach (var tab in _tabs.ToList())
        {
            if (tab.Editor.IsDirty)
            {
                ActivateTab(tab);
                if (!PromptSaveTab(tab))
                {
                    e.Cancel = true;
                    return;
                }
            }
        }

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
        if (_activeTab == null) return;
        CaretPosText.Text = $"Ln {Editor.CaretLine + 1}, Col {Editor.CaretCol + 1}";
    }

    private string GetEncodingLabel()
    {
        if (_activeTab == null) return "UTF-8";
        var enc = _activeTab.FileEncoding;
        if (enc is UTF8Encoding utf8)
            return utf8.GetPreamble().Length > 0 ? "UTF-8 BOM" : "UTF-8";
        if (enc is UnicodeEncoding)
            return "UTF-16";
        return enc.EncodingName;
    }

    private void UpdateFileType()
    {
        if (_activeTab == null) return;
        var ext = _activeTab.FilePath != null ? Path.GetExtension(_activeTab.FilePath).ToLowerInvariant() : "";
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
        if (_activeTab == null) return;
        var name = _activeTab.FilePath != null ? Path.GetFileName(_activeTab.FilePath) : "Untitled";
        var title = (Editor.IsDirty ? "\u2022 " : "") + name;
        Title = title;
        TitleText.Text = title;
    }

    private bool PromptSaveTab(TabInfo tab)
    {
        if (!tab.Editor.IsDirty) return true;
        var name = tab.FilePath != null ? Path.GetFileName(tab.FilePath) : "Untitled";
        var result = MessageBox.Show(
            $"Do you want to save changes to {name}?",
            "TextEdit", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);
        if (result == MessageBoxResult.Cancel) return false;
        if (result == MessageBoxResult.Yes) SaveTab(tab);
        return !tab.Editor.IsDirty || result == MessageBoxResult.No;
    }

    private void SaveTab(TabInfo tab)
    {
        if (tab.FilePath == null)
        {
            SaveTabAs(tab);
            return;
        }
        AtomicWriteText(tab.FilePath, tab.Editor.GetContent(), tab.FileEncoding);
        tab.Editor.MarkClean();
        UpdateTabHeader(tab);
        if (tab == _activeTab) UpdateTitle();
    }

    private void SaveTabAs(TabInfo tab)
    {
        int filterIndex = 1;
        if (tab.FilePath != null)
        {
            var ext = Path.GetExtension(tab.FilePath).ToLowerInvariant();
            filterIndex = ext switch
            {
                ".pl" => 2,
                ".txt" => 1,
                _ => 3
            };
        }
        var dlg = new SaveFileDialog
        {
            Filter = string.Join("|", SaveFilters),
            FilterIndex = filterIndex,
            FileName = tab.FilePath != null ? Path.GetFileName(tab.FilePath) : ""
        };
        if (dlg.ShowDialog() != true) return;
        tab.FilePath = dlg.FileName;
        AtomicWriteText(tab.FilePath, tab.Editor.GetContent(), tab.FileEncoding);
        tab.Editor.MarkClean();
        UpdateTabHeader(tab);
        if (tab == _activeTab)
        {
            UpdateTitle();
            UpdateFileType();
        }
    }

    private void OnNewTab(object sender, RoutedEventArgs e)
    {
        var tab = CreateTab();
        ActivateTab(tab);
    }

    private void OnNew(object sender, RoutedEventArgs e)
    {
        // Ctrl+N creates a new tab
        var tab = CreateTab();
        ActivateTab(tab);
    }

    private void OnOpen(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "All Files (*.*)|*.*|Text Files (*.txt)|*.txt|Perl Files (*.pl)|*.pl",
            Multiselect = true
        };
        if (dlg.ShowDialog() != true) return;

        TabInfo? lastTab = null;
        foreach (var fileName in dlg.FileNames)
        {
            // Reuse current tab if it is untitled and clean (first file only)
            TabInfo tab;
            if (lastTab == null && _activeTab != null && _activeTab.FilePath == null && !_activeTab.Editor.IsDirty)
            {
                tab = _activeTab;
            }
            else
            {
                tab = CreateTab();
            }

            tab.FilePath = fileName;
            tab.FileEncoding = DetectEncoding(fileName);
            tab.Editor.SetContent(File.ReadAllText(fileName, tab.FileEncoding));
            UpdateTabHeader(tab);
            lastTab = tab;
        }

        if (lastTab != null)
        {
            ActivateTab(lastTab);
            FindBarControl.RefreshSearch();
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        SaveTab(_activeTab);
    }

    private static readonly string[] SaveFilters =
        ["Text Files (*.txt)|*.txt", "Perl Files (*.pl)|*.pl", "All Files (*.*)|*.*"];

    private void OnSaveAs(object sender, RoutedEventArgs e)
    {
        if (_activeTab == null) return;
        SaveTabAs(_activeTab);
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
            return new UnicodeEncoding(false, true);
        if (read >= 2 && bom[0] == 0xFE && bom[1] == 0xFF)
            return new UnicodeEncoding(true, true);
        if (read >= 4 && bom[0] == 0 && bom[1] == 0 && bom[2] == 0xFE && bom[3] == 0xFF)
            return new UTF32Encoding(true, true);

        return new UTF8Encoding(false);
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow(ThemeManager, Editor.TabSize, _settings.Editor.Caret.BlockCaret, _settings.Editor.Caret.BlinkMs,
            Editor.FontFamilyName, Editor.EditorFontSize, Editor.EditorFontWeight, Editor.LineHeightMultiplier,
            _settings.Application.ColorTheme, _settings.Editor.Find.BarPosition) { Owner = this };
        dlg.Applied += (_, _) => ApplySettingsFromDialog(dlg);
        if (dlg.ShowDialog() == true)
            ApplySettingsFromDialog(dlg);
    }

    private void ApplySettingsFromDialog(SettingsWindow dlg)
    {
        _settings.Editor.TabSize = dlg.TabSize;
        _settings.Editor.Caret.BlockCaret = dlg.BlockCaret;
        _settings.Editor.Caret.BlinkMs = dlg.CaretBlinkMs;
        _settings.Editor.Font.Family = dlg.SelectedFontFamily;
        _settings.Editor.Font.Size = dlg.SelectedFontSize;
        _settings.Editor.Font.Weight = dlg.SelectedFontWeight;
        _settings.Editor.Font.LineHeight = dlg.SelectedLineHeight;
        _settings.Application.ColorTheme = dlg.ColorThemeName;
        _settings.Editor.Find.BarPosition = dlg.FindBarPosition;
        _settings.Save();
        ApplySettings();
        ThemeManager.Apply(dlg.ColorThemeName);
    }

    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            Keyboard.ClearFocus();
            DragMove();
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeRestore(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseTab(object sender, RoutedEventArgs e) => CloseTab(_activeTab!);
    private void OnClose(object sender, RoutedEventArgs e) => Close();
    private void OnExit(object sender, RoutedEventArgs e) => Close();

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        // Intercept Ctrl+Tab before WPF's built-in tab navigation
        if (ctrl && !shift && e.Key == Key.Tab) { SwitchTab(+1); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.Tab) { SwitchTab(-1); e.Handled = true; return; }

        base.OnPreviewKeyDown(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && !shift && e.Key == Key.N) { OnNew(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.O) { OnOpen(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.F) { FindBarControl.Open(); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.H) { FindBarControl.ToggleReplace(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.P) { OpenCommandPalette(); e.Handled = true; }
        else if (ctrl && shift && e.Key == Key.S) { OnSaveAs(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.S) { OnSave(this, new RoutedEventArgs()); e.Handled = true; }
        else if (ctrl && !shift && e.Key == Key.W) { CloseTab(_activeTab!); e.Handled = true; }
        else if (ctrl && (e.Key == Key.OemPlus || e.Key == Key.Add)) { StepFontSize(1); e.Handled = true; }
        else if (ctrl && (e.Key == Key.OemMinus || e.Key == Key.Subtract)) { StepFontSize(-1); e.Handled = true; }
        else base.OnKeyDown(e);
    }

    private void StepFontSize(int direction)
    {
        var sizes = AppSettings.FontSizeOptions;
        int idx = Array.IndexOf(sizes, Editor.EditorFontSize);
        if (idx < 0) idx = Array.IndexOf(sizes, 14);
        int next = idx + direction;
        if (next < 0 || next >= sizes.Length) return;
        var newSize = sizes[next];
        foreach (var tab in _tabs)
            tab.Editor.EditorFontSize = newSize;
        _settings.Editor.Font.Size = newSize;
        _settings.Save();
    }

    private void OpenCommandPalette()
    {
        var commands = new List<PaletteCommand>
        {
            new("Change Theme", GetOptions: () =>
            {
                var original = _settings.Application.ColorTheme;
                return ThemeManager.GetAvailableThemes().Select(name => new PaletteOption(
                    name,
                    ApplyPreview: () => ThemeManager.Apply(name),
                    Commit: () => { _settings.Application.ColorTheme = name; _settings.Save(); },
                    Revert: () => ThemeManager.Apply(original)
                )).ToList();
            }),

            new("Change Font Size", GetOptions: () =>
            {
                var original = Editor.EditorFontSize;
                return AppSettings.FontSizeOptions.Select(size => new PaletteOption(
                    size.ToString(),
                    ApplyPreview: () => { foreach (var t in _tabs) t.Editor.EditorFontSize = size; },
                    Commit: () => { _settings.Editor.Font.Size = size; _settings.Save(); },
                    Revert: () => { foreach (var t in _tabs) t.Editor.EditorFontSize = original; }
                )).ToList();
            }),

            new("Change Font Family", GetOptions: () =>
            {
                var original = Editor.FontFamilyName;
                return EditorControl.GetMonospaceFonts().Select(name => new PaletteOption(
                    name,
                    ApplyPreview: () => { foreach (var t in _tabs) t.Editor.FontFamilyName = name; },
                    Commit: () => { _settings.Editor.Font.Family = name; _settings.Save(); },
                    Revert: () => { foreach (var t in _tabs) t.Editor.FontFamilyName = original; }
                )).ToList();
            }),

            new("Change Font Weight", GetOptions: () =>
            {
                var original = Editor.EditorFontWeight;
                return AppSettings.FontWeightOptions.Select(w => new PaletteOption(
                    w,
                    ApplyPreview: () => { foreach (var t in _tabs) t.Editor.EditorFontWeight = w; },
                    Commit: () => { _settings.Editor.Font.Weight = w; _settings.Save(); },
                    Revert: () => { foreach (var t in _tabs) t.Editor.EditorFontWeight = original; }
                )).ToList();
            }),

            new("Change Line Height", GetOptions: () =>
            {
                var original = Editor.LineHeightMultiplier;
                return AppSettings.LineHeightOptions.Select(lh => new PaletteOption(
                    lh.ToString("0.0") + "x",
                    ApplyPreview: () => { foreach (var t in _tabs) t.Editor.LineHeightMultiplier = lh; },
                    Commit: () => { _settings.Editor.Font.LineHeight = lh; _settings.Save(); },
                    Revert: () => { foreach (var t in _tabs) t.Editor.LineHeightMultiplier = original; }
                )).ToList();
            }),

            new("Change Tab Size", GetOptions: () =>
            {
                var original = Editor.TabSize;
                return AppSettings.TabSizeOptions.Select(size => new PaletteOption(
                    size.ToString(),
                    ApplyPreview: () => { foreach (var t in _tabs) { t.Editor.TabSize = size; t.Editor.InvalidateVisual(); } },
                    Commit: () => { _settings.Editor.TabSize = size; _settings.Save(); },
                    Revert: () => { foreach (var t in _tabs) { t.Editor.TabSize = original; t.Editor.InvalidateVisual(); } }
                )).ToList();
            }),

            new("Toggle Block Caret", Toggle: () =>
            {
                _settings.Editor.Caret.BlockCaret = !_settings.Editor.Caret.BlockCaret;
                foreach (var t in _tabs)
                {
                    t.Editor.BlockCaret = _settings.Editor.Caret.BlockCaret;
                    t.Editor.InvalidateVisual();
                }
                _settings.Save();
            }),

            new("Find Bar Position", GetOptions: () =>
            {
                var original = _settings.Editor.Find.BarPosition;
                return AppSettings.FindBarPositionOptions.Select(pos => new PaletteOption(
                    pos,
                    ApplyPreview: () => FindBarControl.SetPosition(pos),
                    Commit: () => { _settings.Editor.Find.BarPosition = pos; _settings.Save(); },
                    Revert: () => FindBarControl.SetPosition(original)
                )).ToList();
            }),
        };

        CmdPalette.SetCommands(commands);
        CmdPalette.Open();
    }
}
