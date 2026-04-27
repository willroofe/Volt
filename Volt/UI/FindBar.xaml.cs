using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Volt;

public partial class FindBar : UserControl
{
    // Top margin = tab bar height + border below tab bar (1px).
    // Bottom margin = visual padding (19px) — the status bar lives outside EditorColumnGrid.
    private static readonly double FindBarTopMargin = UIConstants.TabBarHeight + 1;
    private const double FindBarBottomMargin = 19; // visual padding below the find bar

    private bool _matchCase;
    private bool _useRegex;
    private bool _wholeWord;
    private bool _findInSelection;
    private bool _seedWithSelection = true;
    private (int startLine, int startCol, int endLine, int endCol)? _selectionBounds;
    private (int startLine, int startCol, int endLine, int endCol)? _selectionBoundsAtOpen;
    private EditorControl? _editor;
    private bool _navigating;
    private const int SearchProgressRevealMs = 180;
    private DispatcherTimer? _selectionDebounce;
    private DispatcherTimer? _searchDebounce;
    private DispatcherTimer? _progressRevealTimer;
    private int _progressRevealGeneration = -1;
    private bool _progressVisible;
    private (int startLine, int startCol, int endLine, int endCol)? _pendingSelectionBounds;

    public event EventHandler? Closed;

    public FindBar()
    {
        InitializeComponent();
        UpdatePanelMargin();

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                UpdatePanelMargin();
                Dispatcher.BeginInvoke(DispatcherPriority.Input,
                    () => { if (_input != null) { Keyboard.Focus(_input); _input.SelectAll(); } });
            }
        };
    }

    public bool SeedWithSelection
    {
        get => _seedWithSelection;
        set => _seedWithSelection = value;
    }

    public void SetEditor(EditorControl editor)
    {
        if (_editor != null)
        {
            _editor.CaretMoved -= OnEditorCaretMoved;
            _editor.PreviewMouseLeftButtonUp -= OnEditorMouseUp;
            _editor.FindStateChanged -= OnFindStateChanged;
        }
        _editor = editor;
        if (_editor != null)
        {
            _editor.CaretMoved += OnEditorCaretMoved;
            _editor.PreviewMouseLeftButtonUp += OnEditorMouseUp;
            _editor.FindStateChanged += OnFindStateChanged;
        }
        UpdateFindState(_editor?.FindSnapshot ?? FindSnapshot.Empty);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Live selection tracking (when find-in-selection is active)
    // ──────────────────────────────────────────────────────────────────

    private void OnEditorCaretMoved(object? sender, EventArgs e)
    {
        // Keyboard selection changes (shift+arrow, etc.) — debounced
        if (!_findInSelection || _navigating || _editor == null || !IsVisible) return;
        var bounds = _editor.GetSelectionBounds();
        if (bounds == _selectionBounds) return;

        _pendingSelectionBounds = bounds;
        if (_selectionDebounce == null)
        {
            _selectionDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _selectionDebounce.Tick += OnSelectionDebounce;
        }
        _selectionDebounce.Stop();
        _selectionDebounce.Start();
    }

    private void OnEditorMouseUp(object sender, MouseButtonEventArgs e)
    {
        // Mouse selection changes (drag-select) — immediate on mouse-up
        if (!_findInSelection || _editor == null || !IsVisible) return;
        ApplySelectionBounds(_editor.GetSelectionBounds());
    }

    private void OnSelectionDebounce(object? sender, EventArgs e)
    {
        _selectionDebounce?.Stop();
        if (!_findInSelection || _editor == null || !IsVisible) return;
        ApplySelectionBounds(_pendingSelectionBounds);
    }

    private void ApplySelectionBounds(
        (int startLine, int startCol, int endLine, int endCol)? bounds)
    {
        if (bounds == _selectionBounds) return;
        _selectionBounds = bounds;
        SearchWithoutTrackingSelection();
    }

    /// <summary>
    /// Runs UpdateSearch while suppressing the live selection handlers,
    /// preventing navigation side-effects from re-entering the selection tracking.
    /// </summary>
    private void WithNavigationGuard(Action action)
    {
        _navigating = true;
        action();
        _navigating = false;
    }

    private void SearchWithoutTrackingSelection() => WithNavigationGuard(UpdateSearch);

    // ──────────────────────────────────────────────────────────────────
    //  Open / Close
    // ──────────────────────────────────────────────────────────────────

    public void SetPosition(string position)
    {
        bool top = position == "Top";
        VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        Margin = top ? new Thickness(0, FindBarTopMargin, 0, 0) : new Thickness(0, 0, 0, FindBarBottomMargin);
        _panel.VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        UpdatePanelMargin();
    }

    public void Open(bool showReplace = false)
    {
        // Capture the editor's selection before searching (which clears it via navigation)
        _selectionBoundsAtOpen = _editor?.GetSelectionBounds();
        if (_seedWithSelection && _editor != null)
        {
            var selected = _editor.GetSelectedText();
            if (!string.IsNullOrEmpty(selected) && !selected.Contains('\n') && !selected.Contains('\r'))
                _input.Text = selected;
        }
        Visibility = Visibility.Visible;
        SetReplaceVisible(showReplace);
        UpdateSearch();
        Dispatcher.BeginInvoke(DispatcherPriority.Input,
            () => { Keyboard.Focus(_input); _input.SelectAll(); });
    }

    public void ToggleReplace()
    {
        if (!IsVisible)
        {
            Open(showReplace: true);
            return;
        }
        bool show = _replaceRow.Visibility != Visibility.Visible;
        SetReplaceVisible(show);
        if (show)
            Dispatcher.BeginInvoke(DispatcherPriority.Input,
                () => Keyboard.Focus(_replaceInput));
    }

    private void SetReplaceVisible(bool visible)
    {
        _replaceRow.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        _toggleTransform.Angle = visible ? 180 : 0;
        _findRow.Margin = new Thickness(10, 8, 10, visible ? 2 : 8);
    }

    public void Close()
    {
        _editor?.ClearFindMatches();
        _selectionDebounce?.Stop();
        _searchDebounce?.Stop();
        HidePendingProgress();
        Visibility = Visibility.Collapsed;
        _matchCount.Text = "";
        _findInSelection = false;
        _selectionBounds = null;
        _selectionBoundsAtOpen = null;
        _pendingSelectionBounds = null;
        _findInSelBtn.SetResourceReference(ForegroundProperty, ThemeResourceKeys.TextFgMuted);
        _findInSelBtn.SetResourceReference(BackgroundProperty, ThemeResourceKeys.MenuPopupBg);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshSearch()
    {
        if (IsVisible) UpdateSearch();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Toggle button handlers
    // ──────────────────────────────────────────────────────────────────

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        QueueSearch();
    }

    private void OnMatchCaseClick(object sender, RoutedEventArgs e)
    {
        _matchCase = !_matchCase;
        UpdateToggleButton(_matchCaseBtn, _matchCase);
        UpdateSearch();
    }

    private void OnRegexClick(object sender, RoutedEventArgs e)
    {
        _useRegex = !_useRegex;
        UpdateToggleButton(_regexBtn, _useRegex);
        UpdateSearch();
    }

    private void OnWholeWordClick(object sender, RoutedEventArgs e)
    {
        _wholeWord = !_wholeWord;
        UpdateToggleButton(_wholeWordBtn, _wholeWord);
        UpdateSearch();
    }

    private void OnFindInSelectionClick(object sender, RoutedEventArgs e)
    {
        _findInSelection = !_findInSelection;
        _selectionBounds = _findInSelection ? _editor?.GetSelectionBounds() : null;
        UpdateToggleButton(_findInSelBtn, _findInSelection);
        SearchWithoutTrackingSelection();
    }

    private void UpdateToggleButton(Button btn, bool active)
    {
        btn.SetResourceReference(ForegroundProperty,
            active ? ThemeResourceKeys.TextFg : ThemeResourceKeys.TextFgMuted);
        btn.SetResourceReference(BackgroundProperty,
            active ? ThemeResourceKeys.MenuItemHover : ThemeResourceKeys.MenuPopupBg);
    }

    private void OnToggleReplaceClick(object sender, RoutedEventArgs e)
    {
        bool show = _replaceRow.Visibility != Visibility.Visible;
        SetReplaceVisible(show);
    }

    // ──────────────────────────────────────────────────────────────────
    //  Navigation & replace
    // ──────────────────────────────────────────────────────────────────

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        WithNavigationGuard(() => _editor?.FindPrevious());
        UpdateMatchCountLabel();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        WithNavigationGuard(() => _editor?.FindNext());
        UpdateMatchCountLabel();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnReplaceClick(object sender, RoutedEventArgs e)
    {
        DoReplace();
    }

    private void OnReplaceAllClick(object sender, RoutedEventArgs e)
    {
        DoReplaceAll();
    }

    // ──────────────────────────────────────────────────────────────────
    //  Search core
    // ──────────────────────────────────────────────────────────────────

    private void QueueSearch()
    {
        HidePendingProgress();
        _matchCount.Text = "";
        if (_searchDebounce == null)
        {
            _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce.Stop();
                UpdateSearch();
            };
        }

        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void UpdateSearch()
    {
        _searchDebounce?.Stop();
        if (_editor == null) return;

        var query = _input.Text;
        if (string.IsNullOrEmpty(query))
        {
            _editor.ClearFindMatches();
            _matchCount.Text = "";
            HidePendingProgress();
            return;
        }

        if (_findInSelection && _selectionBounds == null)
        {
            _editor.ClearFindMatches();
            _matchCount.Text = "No results";
            HidePendingProgress();
            return;
        }

        _editor.SetFindMatches(query, _matchCase, _useRegex, _wholeWord,
            _findInSelection ? _selectionBounds : null,
            preserveSelection: _findInSelection || _selectionBoundsAtOpen != null);
        UpdateFindState(_editor.FindSnapshot);
    }

    private void OnFindStateChanged(object? sender, FindSnapshot snapshot)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => UpdateFindState(snapshot));
            return;
        }

        UpdateFindState(snapshot);
    }

    private void UpdateMatchCountLabel()
    {
        UpdateFindState(_editor?.FindSnapshot ?? FindSnapshot.Empty);
    }

    private void EnsureProgressRevealTimer()
    {
        if (_progressRevealTimer != null) return;
        _progressRevealTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SearchProgressRevealMs) };
        _progressRevealTimer.Tick += (_, _) =>
        {
            _progressRevealTimer.Stop();
            _progressVisible = true;
            UpdateFindState(_editor?.FindSnapshot ?? FindSnapshot.Empty);
        };
    }

    private void HidePendingProgress()
    {
        _progressRevealTimer?.Stop();
        _progressRevealGeneration = -1;
        _progressVisible = false;
    }

    private void UpdateFindState(FindSnapshot snapshot)
    {
        if (_input.Text.Length == 0 || !snapshot.HasQuery)
        {
            HidePendingProgress();
            _matchCount.Text = "";
            UpdateReplaceButtons(snapshot);
            return;
        }

        if (snapshot.InvalidRegex)
        {
            HidePendingProgress();
            _matchCount.Text = "Invalid regex";
            UpdateReplaceButtons(snapshot);
            return;
        }

        if (snapshot.TotalMatches == 0 && snapshot.IsComplete)
        {
            HidePendingProgress();
            _matchCount.Text = "No results";
            UpdateReplaceButtons(snapshot);
            return;
        }

        if (snapshot.RetentionLimitExceeded && snapshot.IsComplete)
        {
            HidePendingProgress();
            _matchCount.Text = $"{snapshot.TotalMatches} found";
            UpdateReplaceButtons(snapshot);
            return;
        }

        if (snapshot.IsSearching)
        {
            if (_progressRevealGeneration != snapshot.Generation)
            {
                _progressRevealGeneration = snapshot.Generation;
                _progressVisible = false;
                EnsureProgressRevealTimer();
                _progressRevealTimer!.Stop();
                _progressRevealTimer.Start();
            }

            UpdateReplaceButtons(snapshot);
            if (!_progressVisible)
                return;

            int percent = (int)Math.Floor(snapshot.Progress * 100);
            _matchCount.Text = snapshot.CurrentOrdinal is { } ordinal
                ? $"{ordinal} of {snapshot.TotalMatches}+ · Searching {percent}%"
                : $"{snapshot.TotalMatches}+ found · Searching {percent}%";
            return;
        }

        HidePendingProgress();
        _matchCount.Text = snapshot.CurrentRetainedIndex >= 0
            ? $"{snapshot.CurrentRetainedIndex + 1} of {snapshot.TotalMatches}"
            : $"{snapshot.TotalMatches} found";
        UpdateReplaceButtons(snapshot);
    }

    private void UpdateReplaceButtons(FindSnapshot snapshot)
    {
        bool hasCurrent = snapshot.CurrentMatch != null;
        _replaceButton.IsEnabled = hasCurrent;
        _replaceAllButton.IsEnabled = snapshot.CanReplaceAll;
        _replaceAllButton.ToolTip = snapshot.RetentionLimitExceeded
            ? "Too many matches for Replace All. Narrow the search."
            : "Replace all matches";
    }

    private void DoReplace()
    {
        if (_editor == null || _editor.FindSnapshot.CurrentMatch == null) return;
        WithNavigationGuard(() => _editor.ReplaceCurrent(_replaceInput.Text));
        UpdateSearch();
    }

    private void DoReplaceAll()
    {
        if (_editor == null || !_editor.FindSnapshot.CanReplaceAll)
        {
            UpdateMatchCountLabel();
            return;
        }

        _editor.ReplaceAll(_input.Text, _replaceInput.Text, _matchCase, _useRegex, _wholeWord,
            _findInSelection ? _selectionBounds : null);
        UpdateSearch();
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;

            case Key.Enter:
                if (_replaceInput.IsFocused)
                {
                    DoReplace();
                }
                else
                {
                    WithNavigationGuard(() =>
                    {
                        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                            _editor?.FindPrevious();
                        else
                            _editor?.FindNext();
                    });
                }
                UpdateMatchCountLabel();
                e.Handled = true;
                break;
        }

        if (!e.Handled)
            base.OnPreviewKeyDown(e);
    }

    private void UpdatePanelMargin()
    {
        const double pad = 8;
        double scrollBarWidth = 0;
        if (_editor != null)
        {
            for (DependencyObject? d = _editor; d != null; d = VisualTreeHelper.GetParent(d))
            {
                if (d is ScrollViewer sv)
                {
                    scrollBarWidth = FindVerticalScrollBar(sv)?.ActualWidth ?? 0;
                    break;
                }
            }
        }
        _panel.Margin = new Thickness(pad, 8, pad + scrollBarWidth, 8);
    }

    private static ScrollBar? FindVerticalScrollBar(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollBar { Orientation: Orientation.Vertical } sb)
                return sb;
            var result = FindVerticalScrollBar(child);
            if (result != null) return result;
        }
        return null;
    }
}
