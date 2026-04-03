using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Volt;

public class ExplorerTreeControl : FrameworkElement, IScrollInfo
{
    private const double RowHeight = 24;
    private const double IndentWidth = 10;
    private const double ArrowZoneWidth = 20;
    private const double IconZoneWidth = 16;
    private const double IconGap = 4;
    private const double HighlightMargin = 2;
    private const double HighlightRadius = 4;

    // Segoe MDL2 Assets glyphs
    private const string ChevronRight = "\uE76C";
    private const string ChevronDown = "\uE70D";
    private const string FolderIcon = "\uED41";
    private const string FolderOpenIcon = "\uED43";
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

    // Drag-and-drop state
    private int _dragStartRowIndex = -1;
    private Point _dragStartPoint;
    private bool _isDragging;
    private int _dropTargetRowIndex = -1;
    private const double DragThreshold = 5.0;

    // Tooltip (managed manually — WPF's auto-tooltip doesn't work per-row on a single control)
    private readonly ToolTip _rowToolTip = new() { Placement = PlacementMode.Mouse };
    private readonly DispatcherTimer _tooltipTimer;
    private string? _pendingTooltipText;

    // Cached FormattedText for fixed icon glyphs (invalidated on theme/DPI change)
    private FormattedText? _chevronRightMuted, _chevronRightText, _chevronDownText;
    private FormattedText? _fileIconText, _folderIconText, _folderOpenIconText;
    private double _cachedDpi;
    private Brush? _cachedTextBrush, _cachedMutedBrush;

    public event Action<string>? FileOpenRequested;
    public event Action<FileTreeItem>? SelectionChanged;
    public event Action<FileTreeItem?>? ItemRightClicked;
    public event Action<string, string>? FileMoveRequested;
    public event Action<FileTreeItem>? RenameRequested;
    public event Action<FileTreeItem>? DeleteRequested;
    public event Action? UndoRequested;
    public event Action? RedoRequested;

    public FileTreeItem? SelectedItem =>
        _selectedRowIndex >= 0 && _selectedRowIndex < _flatRows.Count
            ? _flatRows[_selectedRowIndex].Item
            : null;

    public ExplorerTreeControl()
    {
        ClipToBounds = true;
        Focusable = true;
        AllowDrop = true;

        _tooltipTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _tooltipTimer.Tick += (_, _) =>
        {
            _tooltipTimer.Stop();
            if (_pendingTooltipText != null)
            {
                _rowToolTip.Content = _pendingTooltipText;
                _rowToolTip.PlacementTarget = this;
                _rowToolTip.IsOpen = true;
            }
        };

        Loaded += (_, _) =>
        {
            App.Current.ThemeManager.ThemeChanged += OnThemeChanged;
        };
        Unloaded += (_, _) =>
        {
            App.Current.ThemeManager.ThemeChanged -= OnThemeChanged;
        };
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        InvalidateGlyphCache();
        InvalidateVisual();
    }

    private void InvalidateGlyphCache()
    {
        _chevronRightMuted = _chevronRightText = _chevronDownText = null;
        _fileIconText = _folderIconText = _folderOpenIconText = null;
        _cachedTextBrush = _cachedMutedBrush = null;
    }

    private void EnsureGlyphCache(Brush textBrush, Brush mutedBrush, double dpi)
    {
        if (_cachedTextBrush == textBrush && _cachedMutedBrush == mutedBrush && _cachedDpi == dpi)
            return;
        _cachedTextBrush = textBrush;
        _cachedMutedBrush = mutedBrush;
        _cachedDpi = dpi;
        _chevronRightMuted = new FormattedText(ChevronRight, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, IconTypeface, 8, mutedBrush, dpi);
        _chevronRightText = new FormattedText(ChevronRight, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, IconTypeface, 8, textBrush, dpi);
        _chevronDownText = new FormattedText(ChevronDown, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, IconTypeface, 8, textBrush, dpi);
        _fileIconText = new FormattedText(FileIcon, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, IconTypeface, 12, textBrush, dpi);
        _folderIconText = new FormattedText(FolderIcon, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, IconTypeface, 12, textBrush, dpi);
        _folderOpenIconText = new FormattedText(FolderOpenIcon, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, IconTypeface, 12, textBrush, dpi);
    }

    // --- Public API ---

    public void SetRootItems(ObservableCollection<FileTreeItem>? items)
    {
        _rootItems = items;
        _selectedRowIndex = -1;
        _hoverRowIndex = -1;
        _verticalOffset = 0;
        RebuildFlatList();
    }

    public void SelectByPath(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            if (_selectedRowIndex != -1)
            {
                _selectedRowIndex = -1;
                InvalidateVisual();
            }
            return;
        }

        var idx = _flatRows.FindIndex(r =>
            string.Equals(r.Item.FullPath, path, StringComparison.OrdinalIgnoreCase));
        if (idx < 0 || idx == _selectedRowIndex) return;

        _selectedRowIndex = idx;
        // Scroll the selected row into view
        double rowTop = idx * RowHeight;
        if (rowTop < _verticalOffset)
            SetVerticalOffset(rowTop);
        else if (rowTop + RowHeight > _verticalOffset + _viewport.Height)
            SetVerticalOffset(rowTop + RowHeight - _viewport.Height);

        InvalidateVisual();
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
    // Note: Uses FormattedText rather than GlyphRun here intentionally.
    // The explorer has a modest number of visible rows (typically <50), so
    // FormattedText is acceptable and simpler — unlike the editor's hot path.

    protected override void OnRender(DrawingContext dc)
    {
        var bg = GetBrush(ThemeResourceKeys.ExplorerBg);
        dc.DrawRectangle(bg, null, new Rect(0, 0, ActualWidth, ActualHeight));

        if (_flatRows.Count == 0 || ActualWidth <= 0 || ActualHeight <= 0) return;

        int firstVisible = Math.Max(0, (int)(_verticalOffset / RowHeight));
        int lastVisible = Math.Min(_flatRows.Count - 1,
            firstVisible + (int)Math.Ceiling(_viewport.Height / RowHeight));

        var hoverBrush = GetBrush(ThemeResourceKeys.ExplorerItemHover);
        var selectedBrush = GetBrush(ThemeResourceKeys.ExplorerItemSelected);
        var textBrush = GetBrush(ThemeResourceKeys.TextFg);
        var mutedBrush = GetBrush(ThemeResourceKeys.TextFgMuted);
        var headerFgBrush = GetBrush(ThemeResourceKeys.ExplorerHeaderFg);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        EnsureGlyphCache(textBrush, mutedBrush, dpi);

        for (int i = firstVisible; i <= lastVisible; i++)
        {
            var row = _flatRows[i];
            double y = i * RowHeight - _verticalOffset;
            double indent = row.Depth * IndentWidth;

            // Hover / selection / drop-target highlight (rounded corners, with margin)
            if (i == _dropTargetRowIndex)
            {
                var dropBrush = GetBrush(ThemeResourceKeys.ExplorerDropTarget);
                dc.DrawRoundedRectangle(dropBrush, null,
                    new Rect(HighlightMargin, y, ActualWidth - HighlightMargin * 2, RowHeight),
                    HighlightRadius, HighlightRadius);
            }
            else if (i == _selectedRowIndex)
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

            // Arrow chevron (muted when collapsed, normal text color when expanded or highlighted)
            bool isHighlighted = i == _selectedRowIndex || i == _hoverRowIndex;
            if (HasChildren(row.Item))
            {
                var arrowText = row.Item.IsExpanded ? _chevronDownText!
                    : isHighlighted ? _chevronRightText! : _chevronRightMuted!;
                double arrowX = x + (ArrowZoneWidth - arrowText.Width) / 2;
                double arrowY = y + (RowHeight - arrowText.Height) / 2;
                dc.DrawText(arrowText, new Point(arrowX, arrowY));
            }
            x += ArrowZoneWidth;

            // Icon
            {
                var iconText = row.Item.Kind == FileTreeItemKind.File
                    ? _fileIconText!
                    : row.Item.IsExpanded ? _folderOpenIconText! : _folderIconText!;
                double iconX = x + (IconZoneWidth - iconText.Width) / 2;
                double iconY = y + (RowHeight - iconText.Height) / 2;
                dc.DrawText(iconText, new Point(iconX, iconY));
                x += IconZoneWidth + IconGap;
            }

            // Name text
            Brush nameBrush = textBrush;
            Typeface nameTypeface = NormalTypeface;

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
        Application.Current.Resources[key] as Brush ?? Brushes.Magenta;

    // --- Mouse interaction ---

    private int HitTestRow(MouseEventArgs e)
    {
        double y = e.GetPosition(this).Y;
        int row = (int)((y + _verticalOffset) / RowHeight);
        return row >= 0 && row < _flatRows.Count ? row : -1;
    }

    private bool IsInArrowZone(MouseEventArgs e, int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _flatRows.Count) return false;
        var row = _flatRows[rowIndex];
        double x = e.GetPosition(this).X;
        double indent = row.Depth * IndentWidth;
        return x >= indent && x < indent + ArrowZoneWidth;
    }

    private bool HasChildren(FileTreeItem item) =>
        item.IsDirectory;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        int row = HitTestRow(e);
        if (row < 0) return;

        var item = _flatRows[row].Item;

        // Double-click: directory toggles, file opens
        if (e.ClickCount == 2)
        {
            if (HasChildren(item))
            {
                item.IsExpanded = !item.IsExpanded;
                RefreshFlatList();
            }
            else if (item.Kind == FileTreeItemKind.File && !string.IsNullOrEmpty(item.FullPath))
            {
                FileOpenRequested?.Invoke(item.FullPath);
            }
            e.Handled = true;
            return;
        }

        // Select the row
        if (_selectedRowIndex != row)
        {
            _selectedRowIndex = row;
            InvalidateVisual();
            SelectionChanged?.Invoke(item);
        }

        // Click in arrow zone toggles expand/collapse
        if (HasChildren(item) && IsInArrowZone(e, row))
        {
            item.IsExpanded = !item.IsExpanded;
            RefreshFlatList();
        }
        else
        {
            // Record potential drag start (not in arrow zone)
            _dragStartRowIndex = row;
            _dragStartPoint = e.GetPosition(this);
            _isDragging = false;
        }
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        _dragStartRowIndex = -1;
        _isDragging = false;
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        if (ctrl && e.Key == Key.Z)
        {
            if (shift)
                RedoRequested?.Invoke();
            else
                UndoRequested?.Invoke();
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.Y)
        {
            RedoRequested?.Invoke();
            e.Handled = true;
            return;
        }

        if (_selectedRowIndex < 0 || _selectedRowIndex >= _flatRows.Count) return;
        var item = _flatRows[_selectedRowIndex].Item;
        if (string.IsNullOrEmpty(item.FullPath)) return;

        if (e.Key == Key.F2)
        {
            RenameRequested?.Invoke(item);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            DeleteRequested?.Invoke(item);
            e.Handled = true;
        }
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        int row = HitTestRow(e);

        if (row >= 0)
        {
            var item = _flatRows[row].Item;
            if (_selectedRowIndex != row)
            {
                _selectedRowIndex = row;
                InvalidateVisual();
                SelectionChanged?.Invoke(item);
            }
            ItemRightClicked?.Invoke(item);
        }
        else
        {
            // Right-clicked empty area — fire with null so panel can show context menu
            _selectedRowIndex = -1;
            InvalidateVisual();
            ItemRightClicked?.Invoke(null);
        }
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        int row = HitTestRow(e);
        if (row != _hoverRowIndex)
        {
            _hoverRowIndex = row;
            InvalidateVisual();
            UpdateTooltip(row);
        }

        // Drag initiation
        if (e.LeftButton == MouseButtonState.Pressed &&
            _dragStartRowIndex >= 0 && _dragStartRowIndex < _flatRows.Count && !_isDragging)
        {
            var pos = e.GetPosition(this);
            if (Math.Abs(pos.X - _dragStartPoint.X) > DragThreshold ||
                Math.Abs(pos.Y - _dragStartPoint.Y) > DragThreshold)
            {
                _isDragging = true;
                var item = _flatRows[_dragStartRowIndex].Item;
                if (!string.IsNullOrEmpty(item.FullPath))
                {
                    var data = new DataObject(DataFormats.FileDrop, new[] { item.FullPath });
                    DragDrop.DoDragDrop(this, data, DragDropEffects.Move);
                }
                _isDragging = false;
                _dragStartRowIndex = -1;
                ClearDropTarget();
            }
        }
    }

    private void UpdateTooltip(int row)
    {
        _rowToolTip.IsOpen = false;
        _tooltipTimer.Stop();
        _pendingTooltipText = null;

        if (row < 0 || row >= _flatRows.Count) return;

        var item = _flatRows[row].Item;
        double indent = _flatRows[row].Depth * IndentWidth + ArrowZoneWidth;
        indent += IconZoneWidth + IconGap;
        double maxTextWidth = Math.Max(0, ActualWidth - indent - 8);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var typeface = NormalTypeface;
        var ft = new FormattedText(item.Name, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, typeface, 13, Brushes.Black, dpi);

        if (ft.Width > maxTextWidth)
        {
            _pendingTooltipText = item.Kind == FileTreeItemKind.File ? item.FullPath : item.Name;
            _tooltipTimer.Start();
        }
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hoverRowIndex != -1)
        {
            _hoverRowIndex = -1;
            _rowToolTip.IsOpen = false;
            _tooltipTimer.Stop();
            _pendingTooltipText = null;
            InvalidateVisual();
        }
    }

    // --- Drag-and-drop ---

    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effects = DragDropEffects.None;
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { ClearDropTarget(); return; }

        var sourcePaths = e.Data.GetData(DataFormats.FileDrop) as string[];
        var sourcePath = sourcePaths is { Length: > 0 } ? sourcePaths[0] : null;
        if (sourcePath == null) { ClearDropTarget(); return; }

        int row = HitTestRowFromPoint(e.GetPosition(this));
        if (row >= 0 && row < _flatRows.Count)
        {
            var targetItem = _flatRows[row].Item;
            // Resolve drop target to a directory
            string? targetDir;
            int targetRowIdx;
            if (targetItem.IsDirectory)
            {
                targetDir = targetItem.FullPath;
                targetRowIdx = row;
            }
            else
            {
                var parent = FindParentDirectoryRow(row);
                if (parent == null) { ClearDropTarget(); e.Handled = true; return; }
                targetDir = parent.Value.Item.FullPath;
                targetRowIdx = _flatRows.IndexOf(parent.Value);
            }

            var sourceParent = System.IO.Path.GetDirectoryName(sourcePath);
            // Don't allow drop onto same parent or into own subtree
            if (!string.Equals(sourceParent, targetDir, StringComparison.OrdinalIgnoreCase) &&
                !targetDir!.StartsWith(sourcePath + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(targetDir, sourcePath, StringComparison.OrdinalIgnoreCase))
            {
                e.Effects = DragDropEffects.Move;
                SetDropTarget(targetRowIdx);
            }
            else
            {
                ClearDropTarget();
            }
        }
        else
        {
            ClearDropTarget();
        }
        e.Handled = true;
    }

    protected override void OnDrop(DragEventArgs e)
    {
        ClearDropTarget();
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var sourcePaths = e.Data.GetData(DataFormats.FileDrop) as string[];
        var sourcePath = sourcePaths is { Length: > 0 } ? sourcePaths[0] : null;
        if (sourcePath == null) return;

        int row = HitTestRowFromPoint(e.GetPosition(this));
        if (row < 0 || row >= _flatRows.Count) return;

        var targetItem = _flatRows[row].Item;
        string? targetDir;
        if (targetItem.IsDirectory)
        {
            targetDir = targetItem.FullPath;
        }
        else
        {
            var parent = FindParentDirectoryRow(row);
            targetDir = parent?.Item.FullPath;
        }
        if (targetDir == null) return;

        var fileName = System.IO.Path.GetFileName(sourcePath);
        var destPath = System.IO.Path.Combine(targetDir, fileName!);

        if (string.Equals(sourcePath, destPath, StringComparison.OrdinalIgnoreCase)) return;
        if (System.IO.Directory.Exists(sourcePath) &&
            destPath.StartsWith(sourcePath + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return;

        FileMoveRequested?.Invoke(sourcePath, destPath);
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        ClearDropTarget();
    }

    private int HitTestRowFromPoint(Point pos)
    {
        int row = (int)((pos.Y + _verticalOffset) / RowHeight);
        return row >= 0 && row < _flatRows.Count ? row : -1;
    }

    private FlatRow? FindParentDirectoryRow(int childIndex)
    {
        if (childIndex < 0 || childIndex >= _flatRows.Count) return null;
        int targetDepth = _flatRows[childIndex].Depth - 1;
        for (int i = childIndex - 1; i >= 0; i--)
        {
            if (_flatRows[i].Depth == targetDepth && _flatRows[i].Item.IsDirectory)
                return _flatRows[i];
        }
        return null;
    }

    private void SetDropTarget(int rowIndex)
    {
        if (_dropTargetRowIndex == rowIndex) return;
        _dropTargetRowIndex = rowIndex;
        InvalidateVisual();
    }

    private void ClearDropTarget()
    {
        if (_dropTargetRowIndex < 0) return;
        _dropTargetRowIndex = -1;
        InvalidateVisual();
    }

    readonly record struct FlatRow(FileTreeItem Item, int Depth);
}
