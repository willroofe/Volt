using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Volt;

public partial class FindBar : UserControl
{
    private const double FindBarTopMargin = 67;    // title bar + tab bar height
    private const double FindBarBottomMargin = 44;  // status bar height

    private bool _matchCase;
    private EditorControl? _editor;

    public event EventHandler? Closed;

    public FindBar()
    {
        InitializeComponent();

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                    () => { if (_input != null) { Keyboard.Focus(_input); _input.SelectAll(); } });
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
        _panel.Margin = new Thickness(0, 8, 0, 8);
    }

    public void Open(bool showReplace = false)
    {
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
        _findRow.Margin = new Thickness(8, 6, 8, visible ? 2 : 6);
    }

    public void Close()
    {
        _editor?.ClearFindMatches();
        Visibility = Visibility.Collapsed;
        _matchCount.Text = "";
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
            _matchCase ? "ThemeTextFg" : "ThemeTextFgMuted");
        _matchCaseBtn.SetResourceReference(BackgroundProperty,
            _matchCase ? "ThemeMenuItemHover" : "ThemeMenuPopupBg");
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

        _editor.SetFindMatches(query, _matchCase);
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
        _editor.ReplaceAll(_input.Text, _replaceInput.Text, _matchCase);
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
}
