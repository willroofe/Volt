using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Volt;

public partial class TabRegion : UserControl
{
    private readonly List<TabEntry> _tabs = [];
    private List<string>? _panelIdsCache;
    private TabEntry? _activeTab;
    private Point? _dragStartPoint;
    private string? _dragPanelId;

    private const double DragDeadZone = 5;

    /// <summary>Raised when the user requests to add a panel via the "+" button. Parameter: the TabRegion raising the event.</summary>
    public event Action<TabRegion>? AddPanelRequested;

    /// <summary>Raised when a panel tab is closed via context menu.</summary>
    public event Action<string>? PanelClosed;

    /// <summary>Raised when the user drags a tab past the dead zone.</summary>
    public event Action<string>? PanelDragStarted;

    /// <summary>Raised when the active tab changes.</summary>
    public event Action<string>? ActiveTabChanged;

    /// <summary>Raised when the user clicks the region close button.</summary>
    public event Action<TabRegion>? RegionCloseRequested;

    public TabRegion()
    {
        InitializeComponent();
        AddButton.Click += (_, _) => AddPanelRequested?.Invoke(this);
        CloseRegionButton.Click += (_, _) => RegionCloseRequested?.Invoke(this);
    }

    public string? ActivePanelId => _activeTab?.Container.PanelId;
    public int TabCount => _tabs.Count;
    public bool IsEmpty => _tabs.Count == 0;
    public IReadOnlyList<string> PanelIds => _panelIdsCache ??= _tabs.Select(t => t.Container.PanelId).ToList();

    public void SetDropHighlight(bool highlight)
    {
        if (highlight)
        {
            var fg = (Brush)Application.Current.Resources[ThemeResourceKeys.TextFg];
            var brush = fg.Clone();
            brush.Opacity = 0.2;
            brush.Freeze();
            HeaderBorder.Background = brush;
        }
        else
        {
            HeaderBorder.SetResourceReference(Border.BackgroundProperty, ThemeResourceKeys.ExplorerHeaderBg);
        }
    }

    public void AddPanel(PanelContainer container)
    {
        var entry = CreateTabEntry(container);
        _tabs.Add(entry);
        _panelIdsCache = null;
        TabStrip.Children.Add(entry.Header);
        SetActiveTab(entry);
    }

    public void RemovePanel(string panelId)
    {
        var entry = _tabs.Find(t => t.Container.PanelId == panelId);
        if (entry == null) return;

        int idx = _tabs.IndexOf(entry);
        _tabs.Remove(entry);
        _panelIdsCache = null;

        // Unsubscribe mouse event handlers before removing from visual tree
        entry.Header.MouseLeftButtonDown -= entry.OnMouseLeftButtonDown;
        entry.Header.MouseMove -= entry.OnMouseMove;
        entry.Header.MouseLeftButtonUp -= entry.OnMouseLeftButtonUp;
        entry.Header.MouseRightButtonUp -= entry.OnMouseRightButtonUp;
        TabStrip.Children.Remove(entry.Header);

        // Unsubscribe from title changes
        entry.Container.Panel.TitleChanged -= entry.OnTitleChanged;

        if (_activeTab == entry)
        {
            if (_tabs.Count > 0)
            {
                int newIdx = Math.Min(idx, _tabs.Count - 1);
                SetActiveTab(_tabs[newIdx]);
            }
            else
            {
                _activeTab = null;
                ContentArea.Content = null;
            }
        }
    }

    public void SetActiveTab(string panelId)
    {
        var entry = _tabs.Find(t => t.Container.PanelId == panelId);
        if (entry != null) SetActiveTab(entry);
    }

    public int GetTabIndex(string panelId)
    {
        return _tabs.FindIndex(t => t.Container.PanelId == panelId);
    }

    public bool IsActiveTab(string panelId)
    {
        return _activeTab?.Container.PanelId == panelId;
    }

    private void SetActiveTab(TabEntry entry)
    {
        if (_activeTab == entry) return;

        // Deactivate previous
        if (_activeTab != null)
        {
            _activeTab.Header.SetResourceReference(Border.BackgroundProperty, ThemeResourceKeys.ExplorerHeaderBg);
        }

        _activeTab = entry;
        ContentArea.Content = entry.Container;
        entry.Header.SetResourceReference(Border.BackgroundProperty, ThemeResourceKeys.TabActive);
        ActiveTabChanged?.Invoke(entry.Container.PanelId);
    }

    private TabEntry CreateTabEntry(PanelContainer container)
    {
        var panel = container.Panel;

        var textBlock = new TextBlock
        {
            Text = panel.Title,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 4, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 120
        };
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, ThemeResourceKeys.ExplorerHeaderFg);

        var closeBtn = new Button
        {
            Margin = new Thickness(0, 0, 6, 0)
        };
        if (Application.Current?.TryFindResource("TabCloseButton") is Style closeBtnStyle)
            closeBtn.Style = closeBtnStyle;
        closeBtn.Click += (_, _) => PanelClosed?.Invoke(container.PanelId);

        var tabPanel = new DockPanel { VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(closeBtn, Dock.Right);
        tabPanel.Children.Add(closeBtn);
        tabPanel.Children.Add(textBlock);

        var header = new Border
        {
            Child = tabPanel,
            Height = 33,
            MinWidth = 40,
            Cursor = Cursors.Hand,
            BorderThickness = new Thickness(0, 0, 1, 0)
        };
        header.SetResourceReference(Border.BorderBrushProperty, ThemeResourceKeys.TabBorder);
        header.SetResourceReference(Border.BackgroundProperty, ThemeResourceKeys.ExplorerHeaderBg);

        // Title change subscription
        Action onTitleChanged = () => textBlock.Text = panel.Title;
        panel.TitleChanged += onTitleChanged;

        // Named event handlers for proper unsubscription
        MouseButtonEventHandler onMouseLeftButtonDown = (_, e) =>
        {
            var tabEntry = _tabs.Find(t => t.Container.PanelId == container.PanelId);
            if (tabEntry != null) SetActiveTab(tabEntry);
            _dragStartPoint = e.GetPosition(this);
            _dragPanelId = container.PanelId;
            header.CaptureMouse();
            e.Handled = true;
        };

        MouseEventHandler onMouseMove = (sender, e) =>
        {
            if (_dragStartPoint == null || _dragPanelId == null) return;
            if (e.LeftButton != MouseButtonState.Pressed)
            {
                CancelDragTracking(sender);
                return;
            }
            var pos = e.GetPosition(this);
            var delta = pos - _dragStartPoint.Value;
            if (Math.Abs(delta.X) > DragDeadZone || Math.Abs(delta.Y) > DragDeadZone)
            {
                var panelId = _dragPanelId;
                CancelDragTracking(sender);
                PanelDragStarted?.Invoke(panelId);
            }
        };

        MouseButtonEventHandler onMouseLeftButtonUp = (sender, _) => CancelDragTracking(sender);

        // Context menu created lazily on first right-click, then reused
        MouseButtonEventHandler onMouseRightButtonUp = (_, e) =>
        {
            if (header.ContextMenu == null)
            {
                var menu = ContextMenuHelper.Create();
                menu.Items.Add(ContextMenuHelper.Item("Close", () => PanelClosed?.Invoke(container.PanelId)));
                header.ContextMenu = menu;
            }
            header.ContextMenu.IsOpen = true;
            e.Handled = true;
        };

        header.MouseLeftButtonDown += onMouseLeftButtonDown;
        header.MouseMove += onMouseMove;
        header.MouseLeftButtonUp += onMouseLeftButtonUp;
        header.MouseRightButtonUp += onMouseRightButtonUp;

        return new TabEntry(container, header, onTitleChanged, onMouseLeftButtonDown, onMouseMove, onMouseLeftButtonUp, onMouseRightButtonUp);
    }

    private void CancelDragTracking(object sender)
    {
        _dragStartPoint = null;
        _dragPanelId = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private class TabEntry(
        PanelContainer container, Border header, Action onTitleChanged,
        MouseButtonEventHandler onMouseLeftButtonDown, MouseEventHandler onMouseMove,
        MouseButtonEventHandler onMouseLeftButtonUp, MouseButtonEventHandler onMouseRightButtonUp)
    {
        public PanelContainer Container { get; } = container;
        public Border Header { get; } = header;
        public Action OnTitleChanged { get; } = onTitleChanged;
        public MouseButtonEventHandler OnMouseLeftButtonDown { get; } = onMouseLeftButtonDown;
        public MouseEventHandler OnMouseMove { get; } = onMouseMove;
        public MouseButtonEventHandler OnMouseLeftButtonUp { get; } = onMouseLeftButtonUp;
        public MouseButtonEventHandler OnMouseRightButtonUp { get; } = onMouseRightButtonUp;
    }
}
