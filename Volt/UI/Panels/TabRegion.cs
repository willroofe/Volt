using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Volt;

public partial class TabRegion : UserControl
{
    private readonly List<TabEntry> _tabs = [];
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
    public IReadOnlyList<string> PanelIds => _tabs.Select(t => t.Container.PanelId).ToList();

    public void SetDropHighlight(bool highlight)
    {
        if (highlight)
        {
            var fg = (Brush)Application.Current.Resources["ThemeTextFg"];
            var brush = fg.Clone();
            brush.Opacity = 0.2;
            brush.Freeze();
            HeaderBorder.Background = brush;
        }
        else
        {
            HeaderBorder.SetResourceReference(Border.BackgroundProperty, "ThemeExplorerHeaderBg");
        }
    }

    public void AddPanel(PanelContainer container)
    {
        var entry = CreateTabEntry(container);
        _tabs.Add(entry);
        TabStrip.Children.Add(entry.Header);
        SetActiveTab(entry);
    }

    public void RemovePanel(string panelId)
    {
        var entry = _tabs.Find(t => t.Container.PanelId == panelId);
        if (entry == null) return;

        int idx = _tabs.IndexOf(entry);
        _tabs.Remove(entry);
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
            _activeTab.Header.SetResourceReference(Border.BackgroundProperty, "ThemeExplorerHeaderBg");
        }

        _activeTab = entry;
        ContentArea.Content = entry.Container;
        entry.Header.SetResourceReference(Border.BackgroundProperty, "ThemeTabActive");
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
        textBlock.SetResourceReference(TextBlock.ForegroundProperty, "ThemeExplorerHeaderFg");

        var closeBtn = new Button
        {
            Content = "\uE8BB",
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 8,
            Width = 20,
            Height = 20,
            Background = System.Windows.Media.Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Focusable = false,
            Cursor = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0)
        };
        closeBtn.SetResourceReference(Button.ForegroundProperty, "ThemeButtonFg");
        closeBtn.Click += (_, _) => PanelClosed?.Invoke(container.PanelId);

        // Apply the same hover template as the "+" button
        var closeBtnTemplate = new System.Windows.Controls.ControlTemplate(typeof(Button));
        var bdFactory = new System.Windows.FrameworkElementFactory(typeof(Border));
        bdFactory.SetValue(Border.BackgroundProperty, System.Windows.Media.Brushes.Transparent);
        bdFactory.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        bdFactory.Name = "Bd";
        var cpFactory = new System.Windows.FrameworkElementFactory(typeof(ContentPresenter));
        cpFactory.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cpFactory.SetValue(ContentPresenter.VerticalAlignmentProperty, VerticalAlignment.Center);
        bdFactory.AppendChild(cpFactory);
        closeBtnTemplate.VisualTree = bdFactory;
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
            new System.Windows.DynamicResourceExtension("ThemeButtonHover"), "Bd"));
        closeBtnTemplate.Triggers.Add(hoverTrigger);
        closeBtn.Template = closeBtnTemplate;

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
        header.SetResourceReference(Border.BorderBrushProperty, "ThemeTabBorder");
        header.SetResourceReference(Border.BackgroundProperty, "ThemeExplorerHeaderBg");

        // Title change subscription
        Action onTitleChanged = () => textBlock.Text = panel.Title;
        panel.TitleChanged += onTitleChanged;

        // Click to activate
        header.MouseLeftButtonDown += (_, e) =>
        {
            var tabEntry = _tabs.Find(t => t.Container.PanelId == container.PanelId);
            if (tabEntry != null) SetActiveTab(tabEntry);
            // Start drag tracking
            _dragStartPoint = e.GetPosition(this);
            _dragPanelId = container.PanelId;
            header.CaptureMouse();
            e.Handled = true;
        };

        header.MouseMove += (sender, e) =>
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

        header.MouseLeftButtonUp += (sender, _) => CancelDragTracking(sender);

        // Right-click context menu with "Close"
        header.MouseRightButtonUp += (_, e) =>
        {
            var menu = ContextMenuHelper.Create();
            menu.Items.Add(ContextMenuHelper.Item("Close", () => PanelClosed?.Invoke(container.PanelId)));
            header.ContextMenu = menu;
            menu.IsOpen = true;
            e.Handled = true;
        };

        return new TabEntry(container, header, onTitleChanged);
    }

    private void CancelDragTracking(object sender)
    {
        _dragStartPoint = null;
        _dragPanelId = null;
        ((UIElement)sender).ReleaseMouseCapture();
    }

    private class TabEntry(PanelContainer container, Border header, Action onTitleChanged)
    {
        public PanelContainer Container { get; } = container;
        public Border Header { get; } = header;
        public Action OnTitleChanged { get; } = onTitleChanged;
    }
}
