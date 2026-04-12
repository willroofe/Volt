using System.Collections.Generic;
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
    private const double IconZoneWidth = 18;
    private const double ChevronIconEmSize = 10;
    private const double FileFolderIconEmSize = 14;
    private const double IconGap = 4;
    private const double HighlightMargin = 4;
    private const double HighlightRadius = 4;
    private const double RowLeftPadding = 4;

    private const string ChevronRight = Codicons.ChevronRight;
    private const string ChevronDown = Codicons.ChevronDown;
    private const string FolderIcon = Codicons.Folder;
    private const string FolderOpenIcon = Codicons.FolderOpened;

    private static readonly Typeface NormalTypeface = new("Segoe UI");
    private static Typeface IconTypeface => Codicons.IconTypeface;

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
    private FormattedText? _folderIconText, _folderOpenIconText;
    private Dictionary<(string Glyph, uint TintKey), FormattedText>? _fileIconCache;
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
    public event Action? NavigateAboveFirst;
    /// <summary>Fired for Ctrl+V when the tree is focused; host resolves target directory and performs paste.</summary>
    public event Action? PasteRequested;

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
        _folderIconText = _folderOpenIconText = null;
        _fileIconCache = null;
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
            FlowDirection.LeftToRight, IconTypeface, ChevronIconEmSize, mutedBrush, dpi);
        _chevronRightText = new FormattedText(ChevronRight, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, IconTypeface, ChevronIconEmSize, textBrush, dpi);
        _chevronDownText = new FormattedText(ChevronDown, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, IconTypeface, ChevronIconEmSize, textBrush, dpi);
        _fileIconCache = null;
        _folderIconText = new FormattedText(FolderIcon, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, IconTypeface, FileFolderIconEmSize, textBrush, dpi);
        _folderOpenIconText = new FormattedText(FolderOpenIcon, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, IconTypeface, FileFolderIconEmSize, textBrush, dpi);
    }

    private FormattedText GetFileIconFormattedText(
        ExplorerFileIconMap.FileIconSpec spec, Brush textBrush, double dpi)
    {
        _fileIconCache ??= new Dictionary<(string Glyph, uint TintKey), FormattedText>();
        uint tintKey = spec.TintArgb ?? 0;
        var key = (spec.Glyph, tintKey);
        if (_fileIconCache.TryGetValue(key, out var cached))
            return cached;
        var brush = spec.TintArgb is uint ta ? ExplorerFileIconMap.TintBrush(ta) : textBrush;
        var ft = new FormattedText(spec.Glyph, CultureInfo.CurrentCulture,
            FlowDirection.LeftToRight, IconTypeface, FileFolderIconEmSize, brush, dpi);
        _fileIconCache[key] = ft;
        return ft;
    }

    // --- Filter ---

    private string _filterText = "";

    public string FilterText
    {
        get => _filterText;
        set
        {
            var normalized = value ?? "";
            if (_filterText == normalized) return;
            _filterText = normalized;
            _selectedRowIndex = -1;
            if (string.IsNullOrEmpty(normalized))
                RebuildFlatList();
            else
                _ = ApplyFilterAsync(normalized);
        }
    }

    private async Task ApplyFilterAsync(string filter)
    {
        // Load all unloaded directories so we can search their contents.
        // Collect first, then load — avoids per-item UI thread round-trips.
        if (_rootItems != null)
        {
            var unloaded = new List<FileTreeItem>();
            CollectUnloadedDirs(_rootItems, unloaded);
            for (int i = 0; i < unloaded.Count; i++)
            {
                if (_filterText != filter) return; // filter changed — bail
                await unloaded[i].EnsureChildrenLoaded();
                // Newly loaded children may themselves have unloaded subdirs
                CollectUnloadedDirs(unloaded[i].Children, unloaded);
            }
        }
        if (_filterText != filter) return;
        RebuildFlatList();
    }

    private static void CollectUnloadedDirs(IEnumerable<FileTreeItem> items, List<FileTreeItem> result)
    {
        foreach (var item in items)
        {
            if (!item.IsDirectory) continue;
            // Placeholder check: single child with empty FullPath means not yet loaded
            if (item.Children.Count == 1 && string.IsNullOrEmpty(item.Children[0].FullPath))
                result.Add(item);
            else
                CollectUnloadedDirs(item.Children, result);
        }
    }

    public void SelectFirstAndFocus()
    {
        if (_flatRows.Count > 0)
        {
            int target = 0;
            if (!string.IsNullOrEmpty(_filterText))
            {
                for (int i = 0; i < _flatRows.Count; i++)
                {
                    if (_flatRows[i].Item.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase))
                    { target = i; break; }
                }
            }
            SelectRow(target);
        }
        Focus();
    }

    // --- Public API ---

    public void SetRootItems(ObservableCollection<FileTreeItem>? items)
    {
        _rootItems = items;
        _selectedRowIndex = -1;
        _hoverRowIndex = -1;
        _verticalOffset = 0;
        _filterText = "";
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
            bool filtering = !string.IsNullOrEmpty(_filterText);
            if (filtering)
            {
                var results = new List<FlatRow>();
                foreach (var item in _rootItems)
                    FlattenFiltered(item, 0, results);
                _flatRows.AddRange(results);
            }
            else
            {
                foreach (var item in _rootItems)
                    Flatten(item, 0);
            }
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

    private bool FlattenFiltered(FileTreeItem item, int depth, List<FlatRow> output)
    {
        bool nameMatches = item.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase);

        // Walk children if loaded (not just a placeholder)
        bool hasLoadedChildren = item.IsDirectory && item.Children.Count > 0
            && !(item.Children.Count == 1 && string.IsNullOrEmpty(item.Children[0].FullPath));

        // Collect matching descendants into a temp list — only append if this subtree has matches
        List<FlatRow>? childRows = null;
        bool anyChildMatch = false;

        if (hasLoadedChildren)
        {
            childRows = [];
            foreach (var child in item.Children)
            {
                if (FlattenFiltered(child, depth + 1, childRows))
                    anyChildMatch = true;
            }
        }

        if (!nameMatches && !anyChildMatch)
            return false;

        output.Add(new FlatRow(item, depth));
        if (childRows is { Count: > 0 })
            output.AddRange(childRows);
        return true;
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

        bool filtering = !string.IsNullOrEmpty(_filterText);
        var hoverBrush = GetBrush(ThemeResourceKeys.ExplorerItemHover);
        var selectedBrush = GetBrush(ThemeResourceKeys.ExplorerItemSelected);
        var textBrush = GetBrush(ThemeResourceKeys.TextFg);
        var mutedBrush = GetBrush(ThemeResourceKeys.TextFgMuted);
        var headerFgBrush = GetBrush(ThemeResourceKeys.ExplorerHeaderFg);
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        EnsureGlyphCache(textBrush, mutedBrush, dpi);

        if (filtering)
        {
            var measure = new FormattedText("X", CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, NormalTypeface, 13, textBrush, dpi) { MaxLineCount = 1 };
            _cachedTextHeight = measure.Height;
        }

        for (int i = firstVisible; i <= lastVisible; i++)
        {
            var row = _flatRows[i];
            double y = i * RowHeight - _verticalOffset;
            double indent = RowLeftPadding + row.Depth * IndentWidth;

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

            // In filter mode, dirs are visually expanded if the next row is a child
            bool visuallyExpanded = row.Item.IsExpanded
                || (filtering && row.Item.IsDirectory
                    && i + 1 < _flatRows.Count && _flatRows[i + 1].Depth > row.Depth);

            // Arrow chevron (muted when collapsed, normal text color when expanded or highlighted)
            bool isHighlighted = i == _selectedRowIndex || i == _hoverRowIndex;
            if (HasChildren(row.Item))
            {
                var arrowText = visuallyExpanded ? _chevronDownText!
                    : isHighlighted ? _chevronRightText! : _chevronRightMuted!;
                double arrowX = x + (ArrowZoneWidth - arrowText.Width) / 2;
                double arrowY = y + (RowHeight - arrowText.Height) / 2;
                dc.DrawText(arrowText, new Point(arrowX, arrowY));
            }
            x += ArrowZoneWidth;

            // Icon
            {
                FormattedText iconText = !row.Item.IsDirectory
                    ? GetFileIconFormattedText(
                        ExplorerFileIconMap.Resolve(row.Item.FullPath, row.Item.Name),
                        textBrush, dpi)
                    : visuallyExpanded ? _folderOpenIconText! : _folderIconText!;
                double iconX = x + (IconZoneWidth - iconText.Width) / 2;
                double iconY = y + (RowHeight - iconText.Height) / 2;
                dc.DrawText(iconText, new Point(iconX, iconY));
                x += IconZoneWidth + IconGap;
            }

            // Name text
            double maxTextWidth = Math.Max(0, ActualWidth - x - 8);

            if (filtering)
            {
                DrawHighlightedName(dc, row.Item.Name, _filterText, x, y, maxTextWidth, textBrush, dpi);
            }
            else
            {
                var nameText = new FormattedText(row.Item.Name, CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, NormalTypeface, 13, textBrush, dpi)
                {
                    MaxTextWidth = maxTextWidth > 0 ? maxTextWidth : 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                    MaxLineCount = 1
                };
                double nameY = y + (RowHeight - nameText.Height) / 2;
                dc.DrawText(nameText, new Point(x, nameY));
            }
        }
    }

    private double _cachedTextHeight;

    private void DrawHighlightedName(DrawingContext dc, string name, string filter,
        double x, double y, double maxWidth, Brush normalBrush, double dpi)
    {
        var highlightBrush = App.Current.ThemeManager.FindMatchBrush;
        double currentX = x;
        double limitX = x + maxWidth;
        int searchStart = 0;

        double textY = y + (RowHeight - _cachedTextHeight) / 2;

        while (searchStart < name.Length && currentX < limitX)
        {
            int matchIndex = name.IndexOf(filter, searchStart, StringComparison.OrdinalIgnoreCase);
            if (matchIndex < 0)
            {
                var ft = new FormattedText(name[searchStart..], CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, NormalTypeface, 13, normalBrush, dpi) { MaxLineCount = 1 };
                dc.DrawText(ft, new Point(currentX, textY));
                break;
            }

            // Text before match
            if (matchIndex > searchStart)
            {
                var ft = new FormattedText(name[searchStart..matchIndex], CultureInfo.CurrentCulture,
                    FlowDirection.LeftToRight, NormalTypeface, 13, normalBrush, dpi) { MaxLineCount = 1 };
                dc.DrawText(ft, new Point(currentX, textY));
                currentX += ft.Width;
            }

            // Matching portion with highlight background
            var matchStr = name[matchIndex..(matchIndex + filter.Length)];
            var matchFt = new FormattedText(matchStr, CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, NormalTypeface, 13, normalBrush, dpi) { MaxLineCount = 1 };
            dc.DrawRoundedRectangle(highlightBrush, null,
                new Rect(currentX, textY, matchFt.Width, matchFt.Height), 2, 2);
            dc.DrawText(matchFt, new Point(currentX, textY));
            currentX += matchFt.Width;
            searchStart = matchIndex + filter.Length;
        }
    }

    private static Brush GetBrush(string key) =>
        Application.Current.Resources[key] as Brush ?? Brushes.Magenta;

    // --- Mouse interaction ---

    private int HitTestRow(MouseEventArgs e) => HitTestRowFromPoint(e.GetPosition(this));

    private bool IsInArrowZone(MouseEventArgs e, int rowIndex)
    {
        if (rowIndex < 0 || rowIndex >= _flatRows.Count) return false;
        var row = _flatRows[rowIndex];
        double x = e.GetPosition(this).X;
        double indent = RowLeftPadding + row.Depth * IndentWidth;
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
            else if (!item.IsDirectory && !string.IsNullOrEmpty(item.FullPath))
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

        if (ctrl && e.Key == Key.C)
        {
            if (_selectedRowIndex >= 0 && _selectedRowIndex < _flatRows.Count)
            {
                var path = _flatRows[_selectedRowIndex].Item.FullPath;
                if (!string.IsNullOrEmpty(path))
                {
                    try
                    {
                        Clipboard.SetDataObject(new DataObject(DataFormats.FileDrop, new[] { path }), copy: true);
                    }
                    catch { /* clipboard busy */ }
                }
            }
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key == Key.V)
        {
            PasteRequested?.Invoke();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            int next = NextMatchRow(_selectedRowIndex, +1);
            if (next >= 0) SelectRow(next);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Up)
        {
            int prev = NextMatchRow(_selectedRowIndex, -1);
            if (prev < 0)
                NavigateAboveFirst?.Invoke();
            else
                SelectRow(prev);
            e.Handled = true;
            return;
        }

        if (_selectedRowIndex < 0 || _selectedRowIndex >= _flatRows.Count) return;
        var item = _flatRows[_selectedRowIndex].Item;
        if (string.IsNullOrEmpty(item.FullPath)) return;

        if (e.Key == Key.Enter)
        {
            if (item.IsDirectory)
            {
                item.IsExpanded = !item.IsExpanded;
                RefreshFlatList();
            }
            else
            {
                FileOpenRequested?.Invoke(item.FullPath);
            }
            e.Handled = true;
        }
        else if (e.Key == Key.Right && item.IsDirectory)
        {
            if (!item.IsExpanded) { item.IsExpanded = true; RefreshFlatList(); }
            e.Handled = true;
        }
        else if (e.Key == Key.Left && item.IsDirectory)
        {
            if (item.IsExpanded) { item.IsExpanded = false; RefreshFlatList(); }
            e.Handled = true;
        }
        else if (e.Key == Key.F2)
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

    private void SelectRow(int index)
    {
        if (index < 0 || index >= _flatRows.Count) return;
        _selectedRowIndex = index;
        ScrollIntoView(index);
        InvalidateVisual();
        SelectionChanged?.Invoke(_flatRows[index].Item);
    }

    /// <summary>
    /// Returns the next row index in the given direction. When filtering,
    /// skips ancestor-only rows and lands on actual matches.
    /// </summary>
    private int NextMatchRow(int current, int direction)
    {
        bool filtering = !string.IsNullOrEmpty(_filterText);
        int i = current + direction;
        while (i >= 0 && i < _flatRows.Count)
        {
            if (!filtering || _flatRows[i].Item.Name.Contains(_filterText, StringComparison.OrdinalIgnoreCase))
                return i;
            i += direction;
        }
        return -1;
    }

    private void ScrollIntoView(int rowIndex)
    {
        double rowTop = rowIndex * RowHeight;
        double rowBottom = rowTop + RowHeight;
        if (rowTop < _verticalOffset)
            SetVerticalOffset(rowTop);
        else if (rowBottom > _verticalOffset + _viewport.Height)
            SetVerticalOffset(rowBottom - _viewport.Height);
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
            _pendingTooltipText = !item.IsDirectory ? item.FullPath : item.Name;
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
