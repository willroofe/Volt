using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace TextEdit;

public class ExplorerTreeControl : FrameworkElement, IScrollInfo
{
    private const double RowHeight = 24;
    private const double IndentWidth = 20;
    private const double ArrowZoneWidth = 20;
    private const double IconZoneWidth = 16;
    private const double IconGap = 4;
    private const double HighlightMargin = 2;
    private const double HighlightRadius = 4;

    // Segoe MDL2 Assets glyphs
    private const string ChevronRight = "\uE76C";
    private const string ChevronDown = "\uE70D";
    private const string FolderIcon = "\uED41";
    private const string FileIcon = "\uE8A5";

    private static readonly Typeface NormalTypeface = new("Segoe UI");
    private static readonly Typeface SemiBoldTypeface = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private static readonly Typeface ItalicTypeface = new(new FontFamily("Segoe UI"), FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface IconTypeface = new("Segoe MDL2 Assets");

    private readonly List<FlatRow> _flatRows = [];
    private ObservableCollection<FileTreeItem>? _rootItems;
    private int _hoverRowIndex = -1;
    private int _selectedRowIndex = -1;

    // IScrollInfo state
    private double _verticalOffset;
    private Size _viewport;
    private Size _extent;

    public event Action<string>? FileOpenRequested;
    public event Action<FileTreeItem>? SelectionChanged;
    public event Action<FileTreeItem?>? ItemRightClicked;

    public FileTreeItem? SelectedItem =>
        _selectedRowIndex >= 0 && _selectedRowIndex < _flatRows.Count
            ? _flatRows[_selectedRowIndex].Item
            : null;

    public ExplorerTreeControl()
    {
        ClipToBounds = true;
        Focusable = true;

        Loaded += (_, _) =>
        {
            var tm = ((App)Application.Current).ThemeManager;
            tm.ThemeChanged += OnThemeChanged;
        };
        Unloaded += (_, _) =>
        {
            var tm = ((App)Application.Current).ThemeManager;
            tm.ThemeChanged -= OnThemeChanged;
        };
    }

    private void OnThemeChanged(object? sender, EventArgs e) => InvalidateVisual();

    // --- Public API ---

    public void SetRootItems(ObservableCollection<FileTreeItem>? items)
    {
        _rootItems = items;
        _selectedRowIndex = -1;
        _hoverRowIndex = -1;
        _verticalOffset = 0;
        RebuildFlatList();
    }

    public void RefreshFlatList()
    {
        // Preserve selection by item reference
        var selectedItem = SelectedItem;
        RebuildFlatList();
        if (selectedItem != null)
        {
            _selectedRowIndex = _flatRows.FindIndex(r => r.Item == selectedItem);
        }
    }

    // --- Flat list ---

    private void RebuildFlatList()
    {
        _flatRows.Clear();
        if (_rootItems != null)
        {
            foreach (var item in _rootItems)
                Flatten(item, 0);
        }
        UpdateExtent();
        InvalidateVisual();
    }

    private void Flatten(FileTreeItem item, int depth)
    {
        _flatRows.Add(new FlatRow(item, depth));
        if (item.IsExpanded)
        {
            foreach (var child in item.Children)
                Flatten(child, depth + 1);
        }
    }

    // --- IScrollInfo ---

    public ScrollViewer? ScrollOwner { get; set; }
    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _verticalOffset;

    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        offset = Math.Max(0, Math.Min(offset, _extent.Height - _viewport.Height));
        if (Math.Abs(offset - _verticalOffset) < 0.01) return;
        _verticalOffset = offset;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateVisual();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle) => rectangle;
    public void LineUp() => SetVerticalOffset(_verticalOffset - RowHeight);
    public void LineDown() => SetVerticalOffset(_verticalOffset + RowHeight);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageUp() => SetVerticalOffset(_verticalOffset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_verticalOffset + _viewport.Height);
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelUp() => SetVerticalOffset(_verticalOffset - RowHeight * 3);
    public void MouseWheelDown() => SetVerticalOffset(_verticalOffset + RowHeight * 3);
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }

    private void UpdateExtent()
    {
        var newExtent = new Size(_viewport.Width, _flatRows.Count * RowHeight);
        if (Math.Abs(newExtent.Height - _extent.Height) > 0.5)
        {
            _extent = newExtent;
            // Clamp offset if extent shrank
            if (_verticalOffset > Math.Max(0, _extent.Height - _viewport.Height))
            {
                _verticalOffset = Math.Max(0, _extent.Height - _viewport.Height);
            }
            ScrollOwner?.InvalidateScrollInfo();
        }
    }

    // --- Layout ---

    protected override Size MeasureOverride(Size availableSize)
    {
        _viewport = new Size(
            double.IsInfinity(availableSize.Width) ? 300 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 300 : availableSize.Height);
        _extent = new Size(_viewport.Width, _flatRows.Count * RowHeight);
        ScrollOwner?.InvalidateScrollInfo();
        return _viewport;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _viewport = finalSize;
        _extent = new Size(_viewport.Width, _flatRows.Count * RowHeight);
        // Clamp offset on resize
        var maxOffset = Math.Max(0, _extent.Height - _viewport.Height);
        if (_verticalOffset > maxOffset)
        {
            _verticalOffset = maxOffset;
        }
        ScrollOwner?.InvalidateScrollInfo();
        return finalSize;
    }

    // --- Rendering ---

    protected override void OnRender(DrawingContext dc)
    {
        var bg = GetBrush("ThemeExplorerBg");
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_flatRows.Count == 0) return;

        int firstVisible = Math.Max(0, (int)(_verticalOffset / RowHeight));
        int lastVisible = Math.Min(_flatRows.Count - 1,
            firstVisible + (int)Math.Ceiling(_viewport.Height / RowHeight));

        var hoverBrush = GetBrush("ThemeExplorerItemHover");
        var selectedBrush = GetBrush("ThemeExplorerItemSelected");
        var textBrush = GetBrush("ThemeTextFg");
        var mutedBrush = GetBrush("ThemeTextFgMuted");
        var headerFgBrush = GetBrush("ThemeExplorerHeaderFg");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        for (int i = firstVisible; i <= lastVisible; i++)
        {
            var row = _flatRows[i];
            double y = i * RowHeight - _verticalOffset;
            double indent = row.Depth * IndentWidth;

            // Hover / selection highlight (rounded corners, with margin)
            if (i == _selectedRowIndex)
            {
                dc.DrawRoundedRectangle(selectedBrush, null,
                    new Rect(HighlightMargin, y, ActualWidth - HighlightMargin * 2, RowHeight),
                    HighlightRadius, HighlightRadius);
            }
            else if (i == _hoverRowIndex)
            {
                dc.DrawRoundedRectangle(hoverBrush, null,
                    new Rect(HighlightMargin, y, ActualWidth - HighlightMargin * 2, RowHeight),
                    HighlightRadius, HighlightRadius);
            }

            double x = indent;
            bool hasChildren = row.Item.IsDirectory ||
                               row.Item.Kind == FileTreeItemKind.VirtualFolder ||
                               row.Item.Kind == FileTreeItemKind.ProjectRoot;

            // Arrow chevron
            if (hasChildren)
            {
                string chevron = row.Item.IsExpanded ? ChevronDown : ChevronRight;
                var arrowText = new FormattedText(chevron, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, IconTypeface, 8, mutedBrush, dpi);
                double arrowX = x + (ArrowZoneWidth - arrowText.Width) / 2;
                double arrowY = y + (RowHeight - arrowText.Height) / 2;
                dc.DrawText(arrowText, new Point(arrowX, arrowY));
            }
            x += ArrowZoneWidth;

            // Icon (only for directories and files, not project root or virtual folders)
            if (row.Item.Kind == FileTreeItemKind.Directory || row.Item.Kind == FileTreeItemKind.File)
            {
                string icon = row.Item.Kind == FileTreeItemKind.Directory ? FolderIcon : FileIcon;
                var iconBrush = row.Item.Kind == FileTreeItemKind.Directory ? textBrush : mutedBrush;
                var iconText = new FormattedText(icon, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, IconTypeface, 12, iconBrush, dpi);
                double iconX = x + (IconZoneWidth - iconText.Width) / 2;
                double iconY = y + (RowHeight - iconText.Height) / 2;
                dc.DrawText(iconText, new Point(iconX, iconY));
                x += IconZoneWidth + IconGap;
            }

            // Name text
            Brush nameBrush;
            Typeface nameTypeface;
            switch (row.Item.Kind)
            {
                case FileTreeItemKind.ProjectRoot:
                    nameBrush = headerFgBrush;
                    nameTypeface = SemiBoldTypeface;
                    break;
                case FileTreeItemKind.VirtualFolder:
                    nameBrush = headerFgBrush;
                    nameTypeface = ItalicTypeface;
                    break;
                default:
                    nameBrush = textBrush;
                    nameTypeface = NormalTypeface;
                    break;
            }

            double maxTextWidth = Math.Max(0, ActualWidth - x - 8);
            var nameText = new FormattedText(row.Item.Name, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, nameTypeface, 13, nameBrush, dpi)
            {
                MaxTextWidth = maxTextWidth > 0 ? maxTextWidth : 1,
                Trimming = TextTrimming.CharacterEllipsis,
                MaxLineCount = 1
            };
            double nameY = y + (RowHeight - nameText.Height) / 2;
            dc.DrawText(nameText, new Point(x, nameY));
        }
    }

    private static Brush GetBrush(string key) =>
        (Brush)Application.Current.Resources[key];

    // --- Mouse interaction (Task 2) ---
    // Will be added in Task 2

    readonly record struct FlatRow(FileTreeItem Item, int Depth);
}
