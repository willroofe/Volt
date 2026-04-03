using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Volt;

public partial class PanelShell : UserControl
{
    private readonly Dictionary<string, PanelRegistration> _panels = new(StringComparer.OrdinalIgnoreCase);

    public static readonly DependencyProperty CenterContentProperty =
        DependencyProperty.Register(nameof(CenterContent), typeof(UIElement), typeof(PanelShell));

    public UIElement? CenterContent
    {
        get => (UIElement?)GetValue(CenterContentProperty);
        set => SetValue(CenterContentProperty, value);
    }

    /// <summary>
    /// Fired when a panel's layout changes (resize, show/hide, move).
    /// Parameters: panelId, placement, size.
    /// </summary>
    public event Action<string, PanelPlacement, double>? PanelLayoutChanged;

    public PanelShell()
    {
        InitializeComponent();
        LeftSplitter.DragCompleted += OnSplitterDragCompleted;
        RightSplitter.DragCompleted += OnSplitterDragCompleted;
        TopSplitter.DragCompleted += OnSplitterDragCompleted;
        BottomSplitter.DragCompleted += OnSplitterDragCompleted;
    }

    public void RegisterPanel(IPanel panel, PanelPlacement placement, double defaultSize)
    {
        var reg = new PanelRegistration(panel, placement, defaultSize);
        _panels[panel.PanelId] = reg;
        PlacePanel(reg);
    }

    public void ShowPanel(string panelId)
    {
        if (!_panels.TryGetValue(panelId, out var reg)) return;
        if (reg.IsVisible) return;
        reg.IsVisible = true;
        ApplyRegionSize(reg.Placement, reg.Size);
        ShowSplitter(reg.Placement);
        PanelLayoutChanged?.Invoke(panelId, reg.Placement, reg.Size);
    }

    public void HidePanel(string panelId)
    {
        if (!_panels.TryGetValue(panelId, out var reg)) return;
        if (!reg.IsVisible) return;
        reg.IsVisible = false;
        ApplyRegionSize(reg.Placement, 0);
        HideSplitter(reg.Placement);
        PanelLayoutChanged?.Invoke(panelId, reg.Placement, reg.Size);
    }

    public bool IsPanelVisible(string panelId)
    {
        return _panels.TryGetValue(panelId, out var reg) && reg.IsVisible;
    }

    public void TogglePanel(string panelId)
    {
        if (IsPanelVisible(panelId))
            HidePanel(panelId);
        else
            ShowPanel(panelId);
    }

    public void MovePanel(string panelId, PanelPlacement newPlacement)
    {
        if (!_panels.TryGetValue(panelId, out var reg)) return;
        if (reg.Placement == newPlacement) return;

        bool wasVisible = reg.IsVisible;
        if (wasVisible) HidePanel(panelId);

        ClearRegionContent(reg.Placement);
        reg.Placement = newPlacement;
        PlacePanel(reg);

        if (wasVisible) ShowPanel(panelId);
    }

    public void RestoreLayout(IReadOnlyList<PanelSlotConfig> configs)
    {
        foreach (var config in configs)
        {
            if (!_panels.TryGetValue(config.PanelId, out var reg)) continue;

            if (reg.Placement != config.Placement)
            {
                ClearRegionContent(reg.Placement);
                reg.Placement = config.Placement;
                PlacePanel(reg);
            }

            reg.Size = Math.Max(config.Size, GetMinSize(config.Placement));

            if (config.Visible)
                ShowPanel(config.PanelId);
            else
                HidePanel(config.PanelId);
        }
    }

    public List<PanelSlotConfig> GetCurrentLayout()
    {
        var result = new List<PanelSlotConfig>();
        foreach (var reg in _panels.Values)
        {
            result.Add(new PanelSlotConfig
            {
                PanelId = reg.Panel.PanelId,
                Placement = reg.Placement,
                Size = reg.Size,
                Visible = reg.IsVisible
            });
        }
        return result;
    }

    private void PlacePanel(PanelRegistration reg)
    {
        var presenter = GetContentPresenter(reg.Placement);
        presenter.Content = reg.Panel.Content;
    }

    private void ClearRegionContent(PanelPlacement placement)
    {
        var presenter = GetContentPresenter(placement);
        presenter.Content = null;
    }

    private ContentPresenter GetContentPresenter(PanelPlacement placement) => placement switch
    {
        PanelPlacement.Left => LeftContent,
        PanelPlacement.Right => RightContent,
        PanelPlacement.Top => TopContent,
        PanelPlacement.Bottom => BottomContent,
        _ => throw new ArgumentOutOfRangeException(nameof(placement))
    };

    private GridSplitter GetSplitter(PanelPlacement placement) => placement switch
    {
        PanelPlacement.Left => LeftSplitter,
        PanelPlacement.Right => RightSplitter,
        PanelPlacement.Top => TopSplitter,
        PanelPlacement.Bottom => BottomSplitter,
        _ => throw new ArgumentOutOfRangeException(nameof(placement))
    };

    private void ApplyRegionSize(PanelPlacement placement, double size)
    {
        switch (placement)
        {
            case PanelPlacement.Left:
                LeftCol.MinWidth = size > 0 ? GetMinSize(placement) : 0;
                LeftCol.MaxWidth = size > 0 ? GetMaxSize(placement) : double.PositiveInfinity;
                LeftCol.Width = new GridLength(size);
                break;
            case PanelPlacement.Right:
                RightCol.MinWidth = size > 0 ? GetMinSize(placement) : 0;
                RightCol.MaxWidth = size > 0 ? GetMaxSize(placement) : double.PositiveInfinity;
                RightCol.Width = new GridLength(size);
                break;
            case PanelPlacement.Top:
                TopRow.MinHeight = size > 0 ? GetMinSize(placement) : 0;
                TopRow.MaxHeight = size > 0 ? GetMaxSize(placement) : double.PositiveInfinity;
                TopRow.Height = new GridLength(size);
                break;
            case PanelPlacement.Bottom:
                BottomRow.MinHeight = size > 0 ? GetMinSize(placement) : 0;
                BottomRow.MaxHeight = size > 0 ? GetMaxSize(placement) : double.PositiveInfinity;
                BottomRow.Height = new GridLength(size);
                break;
        }
    }

    private void ShowSplitter(PanelPlacement placement)
    {
        GetSplitter(placement).Visibility = Visibility.Visible;
        SetSplitterRowCol(placement, 1);
    }

    private void HideSplitter(PanelPlacement placement)
    {
        GetSplitter(placement).Visibility = Visibility.Collapsed;
        SetSplitterRowCol(placement, 0);
    }

    private void SetSplitterRowCol(PanelPlacement placement, double size)
    {
        switch (placement)
        {
            case PanelPlacement.Left:
                LeftSplitterCol.Width = new GridLength(size);
                break;
            case PanelPlacement.Right:
                RightSplitterCol.Width = new GridLength(size);
                break;
            case PanelPlacement.Top:
                TopSplitterRow.Height = new GridLength(size);
                break;
            case PanelPlacement.Bottom:
                BottomSplitterRow.Height = new GridLength(size);
                break;
        }
    }

    private static double GetMinSize(PanelPlacement placement) => placement switch
    {
        PanelPlacement.Left or PanelPlacement.Right => 150,
        PanelPlacement.Top or PanelPlacement.Bottom => 100,
        _ => 150
    };

    private static double GetMaxSize(PanelPlacement placement) => placement switch
    {
        PanelPlacement.Left or PanelPlacement.Right => 600,
        PanelPlacement.Top or PanelPlacement.Bottom => 400,
        _ => 600
    };

    private void OnSplitterDragCompleted(object? sender, DragCompletedEventArgs e)
    {
        foreach (var reg in _panels.Values)
        {
            if (!reg.IsVisible) continue;
            if (GetSplitter(reg.Placement) != sender) continue;

            double newSize = reg.Placement switch
            {
                PanelPlacement.Left => LeftCol.ActualWidth,
                PanelPlacement.Right => RightCol.ActualWidth,
                PanelPlacement.Top => TopRow.ActualHeight,
                PanelPlacement.Bottom => BottomRow.ActualHeight,
                _ => reg.Size
            };
            reg.Size = newSize;
            PanelLayoutChanged?.Invoke(reg.Panel.PanelId, reg.Placement, newSize);
            break;
        }
    }

    private class PanelRegistration(IPanel panel, PanelPlacement placement, double size)
    {
        public IPanel Panel { get; } = panel;
        public PanelPlacement Placement { get; set; } = placement;
        public double Size { get; set; } = size;
        public bool IsVisible { get; set; }
    }
}
