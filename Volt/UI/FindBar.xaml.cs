using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace Volt;

public partial class FindBar : UserControl
{
    // IMPORTANT: These margins must stay in sync with MainWindow.xaml layout heights.
    // FindBar is now inside EditorColumnGrid (scoped to the editor panel).
    // Top margin = TabStrip DockPanel Height (33px) + border below tab bar (1px) = 34px
    // Bottom margin = visual padding (19px) — the status bar lives outside EditorColumnGrid
    // If you change the tab bar height in MainWindow.xaml (DockPanel Height="33")
    // or the border thickness below it, update FindBarTopMargin accordingly.
    private const double FindBarTopMargin = 34;  // 33 (tab bar height) + 1 (border)
    private const double FindBarBottomMargin = 19; // visual padding below the find bar

    private bool _matchCase;
    private bool _useRegex;
    private bool _wholeWord;
    private bool _findInSelection;
    private (int startLine, int startCol, int endLine, int endCol)? _selectionBounds;
    private (int startLine, int startCol, int endLine, int endCol)? _selectionBoundsAtOpen;
    private EditorControl? _editor;

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
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                    () => { if (_input != null) { Keyboard.Focus(_input); _input.SelectAll(); } });
            }
        };
    }

    public void SetEditor(EditorControl editor)
    {
        _editor = editor;
    }

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
        Visibility = Visibility.Visible;
        SetReplaceVisible(showReplace);
        UpdateSearch();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
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
            Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
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
        Visibility = Visibility.Collapsed;
        _matchCount.Text = "";
        _findInSelection = false;
        _selectionBounds = null;
        _selectionBoundsAtOpen = null;
        _findInSelBtn.SetResourceReference(ForegroundProperty, ThemeResourceKeys.TextFgMuted);
        _findInSelBtn.SetResourceReference(BackgroundProperty, ThemeResourceKeys.MenuPopupBg);
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public void RefreshSearch()
    {
        if (IsVisible) UpdateSearch();
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearch();
    }

    private void OnMatchCaseClick(object sender, RoutedEventArgs e)
    {
        _matchCase = !_matchCase;
        _matchCaseBtn.SetResourceReference(ForegroundProperty,
            _matchCase ? ThemeResourceKeys.TextFg : ThemeResourceKeys.TextFgMuted);
        _matchCaseBtn.SetResourceReference(BackgroundProperty,
            _matchCase ? ThemeResourceKeys.MenuItemHover : ThemeResourceKeys.MenuPopupBg);
        UpdateSearch();
    }

    private void OnRegexClick(object sender, RoutedEventArgs e)
    {
        _useRegex = !_useRegex;
        _regexBtn.SetResourceReference(ForegroundProperty,
            _useRegex ? ThemeResourceKeys.TextFg : ThemeResourceKeys.TextFgMuted);
        _regexBtn.SetResourceReference(BackgroundProperty,
            _useRegex ? ThemeResourceKeys.MenuItemHover : ThemeResourceKeys.MenuPopupBg);
        UpdateSearch();
    }

    private void OnWholeWordClick(object sender, RoutedEventArgs e)
    {
        _wholeWord = !_wholeWord;
        _wholeWordBtn.SetResourceReference(ForegroundProperty,
            _wholeWord ? ThemeResourceKeys.TextFg : ThemeResourceKeys.TextFgMuted);
        _wholeWordBtn.SetResourceReference(BackgroundProperty,
            _wholeWord ? ThemeResourceKeys.MenuItemHover : ThemeResourceKeys.MenuPopupBg);
        UpdateSearch();
    }

    private void OnFindInSelectionClick(object sender, RoutedEventArgs e)
    {
        _findInSelection = !_findInSelection;
        if (_findInSelection)
            _selectionBounds = _editor?.GetSelectionBounds() ?? _selectionBoundsAtOpen;
        else
            _selectionBounds = null;
        _findInSelBtn.SetResourceReference(ForegroundProperty,
            _findInSelection ? ThemeResourceKeys.TextFg : ThemeResourceKeys.TextFgMuted);
        _findInSelBtn.SetResourceReference(BackgroundProperty,
            _findInSelection ? ThemeResourceKeys.MenuItemHover : ThemeResourceKeys.MenuPopupBg);
        UpdateSearch();
    }

    private void OnToggleReplaceClick(object sender, RoutedEventArgs e)
    {
        bool show = _replaceRow.Visibility != Visibility.Visible;
        SetReplaceVisible(show);
    }

    private void OnPrevClick(object sender, RoutedEventArgs e)
    {
        _editor?.FindPrevious();
        UpdateMatchCountLabel();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        _editor?.FindNext();
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

    private void UpdateSearch()
    {
        if (_editor == null) return;

        var query = _input.Text;
        if (string.IsNullOrEmpty(query))
        {
            _editor.ClearFindMatches();
            _matchCount.Text = "";
            return;
        }

        _editor.SetFindMatches(query, _matchCase, _useRegex, _wholeWord,
            _findInSelection ? _selectionBounds : null,
            preserveSelection: _selectionBoundsAtOpen != null);
        UpdateMatchCountLabel();
    }

    private void UpdateMatchCountLabel()
    {
        if (_editor == null || _editor.FindMatchCount == 0)
        {
            _matchCount.Text = _input.Text.Length > 0 ? "No results" : "";
            return;
        }
        _matchCount.Text = $"{_editor.CurrentMatchIndex + 1} of {_editor.FindMatchCount}";
    }

    private void DoReplace()
    {
        if (_editor == null || _editor.FindMatchCount == 0) return;
        _editor.ReplaceCurrent(_replaceInput.Text);
        UpdateSearch();
    }

    private void DoReplaceAll()
    {
        if (_editor == null || _editor.FindMatchCount == 0) return;
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
                else if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
                    _editor?.FindPrevious();
                else
                    _editor?.FindNext();
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
