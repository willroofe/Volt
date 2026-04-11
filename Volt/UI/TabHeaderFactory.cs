using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Volt;

/// <summary>
/// Creates tab header UI elements and manages tab drag-to-reorder.
/// Extracted from MainWindow to reduce its size and isolate tab header concerns.
/// </summary>
internal class TabHeaderFactory
{
    private TabInfo? _dragTab;
    private Point _dragStartPos;
    private bool _isTabDragging;
    private int _dragTargetIndex = -1;
    private Popup? _dragGhost;

    public bool FixedWidth { get; set; }
    private const double FixedTabWidth = 160;

    public event Action<TabInfo>? TabActivated;
    public event Action<TabInfo>? TabClosed;
    public event Action<TabInfo, int>? TabReordered;

    public void ApplyFixedWidth(Border header)
    {
        if (FixedWidth)
        {
            header.Width = FixedTabWidth;
            header.MinWidth = FixedTabWidth;
        }
        else
        {
            header.Width = double.NaN;
            header.MinWidth = 60;
        }
    }

    public Border CreateHeader(TabInfo tab, Panel tabStrip, FrameworkElement dropIndicator)
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
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.TextFg);

        var closeBtn = new Button { Style = (Style)Application.Current.FindResource("TabCloseButton") };
        closeBtn.Click += (_, _) => TabClosed?.Invoke(tab);

        var panel = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(closeBtn, Dock.Right);
        panel.Children.Add(closeBtn);
        panel.Children.Add(textBlock);

        var header = new Border
        {
            Child = panel,
            Height = UIConstants.TabBarHeight,
            MinWidth = 60,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0, 0, 1, 0)
        };
        if (FixedWidth)
        {
            header.Width = FixedTabWidth;
            header.MinWidth = FixedTabWidth;
        }
        header.SetResourceReference(Border.BorderBrushProperty, ThemeResourceKeys.TabBorder);

        header.MouseLeftButtonDown += (_, e) =>
        {
            TabActivated?.Invoke(tab);
            _dragTab = tab;
            _dragStartPos = e.GetPosition(tabStrip);
            _isTabDragging = false;
            _dragTargetIndex = -1;
            header.CaptureMouse();
            e.Handled = true;
        };

        header.MouseMove += (_, e) =>
        {
            if (_dragTab != tab || e.LeftButton != MouseButtonState.Pressed) return;
            var pos = e.GetPosition(tabStrip);
            if (!_isTabDragging)
            {
                if (Math.Abs(pos.X - _dragStartPos.X) < SystemParameters.MinimumHorizontalDragDistance)
                    return;
                _isTabDragging = true;
                ShowDragGhost(tab, header);
                header.Opacity = 0.4;
            }
            UpdateDragGhost(e, header);
            UpdateDropIndicator(pos.X, tabStrip, dropIndicator, tab);
        };

        header.MouseLeftButtonUp += (_, e) =>
        {
            if (_dragTab == tab)
            {
                // Capture state before ReleaseMouseCapture, which fires
                // LostMouseCapture synchronously and clears drag state.
                bool wasDragging = _isTabDragging;
                int targetIndex = _dragTargetIndex;
                header.ReleaseMouseCapture();
                if (wasDragging)
                {
                    header.Opacity = 1.0;
                    HideDragGhost();
                    dropIndicator.Visibility = Visibility.Collapsed;
                    if (targetIndex >= 0)
                        TabReordered?.Invoke(tab, targetIndex);
                }
                _dragTab = null;
                _isTabDragging = false;
                _dragTargetIndex = -1;
            }
        };

        header.LostMouseCapture += (_, _) =>
        {
            if (_dragTab == tab && _isTabDragging)
            {
                header.Opacity = 1.0;
                HideDragGhost();
                dropIndicator.Visibility = Visibility.Collapsed;
                _dragTab = null;
                _isTabDragging = false;
                _dragTargetIndex = -1;
            }
        };

        header.MouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                TabClosed?.Invoke(tab);
                e.Handled = true;
            }
        };

        header.MouseRightButtonUp += (_, e) =>
        {
            if (tab.FilePath == null || (!File.Exists(tab.FilePath) && !Directory.Exists(tab.FilePath)))
                return;
            var menu = ContextMenuHelper.Create();
            menu.Items.Add(ContextMenuHelper.Item("Reveal in File Explorer", "\uE8B7",
                () => FileHelper.RevealInFileExplorer(tab.FilePath!)));
            header.ContextMenu = menu;
            menu.IsOpen = true;
            e.Handled = true;
        };

        return header;
    }

    private void ShowDragGhost(TabInfo tab, Visual relativeTo)
    {
        var text = new TextBlock
        {
            Text = tab.DisplayName,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 10, 0)
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.TextFg);

        var border = new Border
        {
            Child = text,
            Height = 30,
            MinWidth = 60,
            CornerRadius = new CornerRadius(4),
            Opacity = 0.85
        };
        border.SetResourceReference(Border.BackgroundProperty, ThemeResourceKeys.TabActive);
        border.SetResourceReference(Border.BorderBrushProperty, ThemeResourceKeys.TabBorder);
        border.BorderThickness = new Thickness(1);

        _dragGhost = new Popup
        {
            Child = border,
            AllowsTransparency = true,
            Placement = PlacementMode.AbsolutePoint,
            IsHitTestVisible = false,
            IsOpen = true
        };
    }

    private void UpdateDragGhost(MouseEventArgs e, Visual relativeTo)
    {
        if (_dragGhost == null) return;
        var window = Window.GetWindow(relativeTo);
        if (window == null) return;
        var screenPos = window.PointToScreen(e.GetPosition(window));
        var source = PresentationSource.FromVisual(window);
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

    private void UpdateDropIndicator(double mouseX, Panel tabStrip, FrameworkElement indicator, TabInfo dragTab)
    {
        double offset = 0;
        int insertIdx = -1;
        double indicatorX = 0;
        int dragSourceIdx = -1;

        for (int i = 0; i < tabStrip.Children.Count; i++)
        {
            if (tabStrip.Children[i] is FrameworkElement el)
            {
                if (el == dragTab.HeaderElement) dragSourceIdx = i;
                double width = el.ActualWidth;
                double midpoint = offset + width / 2;
                if (insertIdx < 0 && mouseX < midpoint)
                {
                    insertIdx = i;
                    indicatorX = offset;
                }
                offset += width;
            }
        }

        if (insertIdx < 0)
        {
            insertIdx = tabStrip.Children.Count;
            indicatorX = offset;
        }

        if (insertIdx == dragSourceIdx || insertIdx == dragSourceIdx + 1)
        {
            indicator.Visibility = Visibility.Collapsed;
            _dragTargetIndex = -1;
            return;
        }

        _dragTargetIndex = insertIdx > dragSourceIdx ? insertIdx - 1 : insertIdx;
        indicator.Visibility = Visibility.Visible;
        indicator.Margin = new Thickness(indicatorX - 1, 0, 0, 0);
    }
}
