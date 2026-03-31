using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TextEdit;

public class FindBar : UserControl
{
    private readonly Border _panel;
    private readonly TextBox _input;
    private readonly TextBlock _matchCount;
    private readonly CheckBox _matchCase;

    private EditorControl? _editor;

    public event EventHandler? Closed;

    public FindBar()
    {
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
                    () => { if (_input != null) { Keyboard.Focus(_input); _input.SelectAll(); } });
        };

        // Search input
        _input = new TextBox
        {
            Width = 220,
            BorderThickness = new Thickness(1),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Padding = new Thickness(4, 3, 4, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
        };
        _input.SetResourceReference(ForegroundProperty, "ThemeTextFg");
        _input.SetResourceReference(TextBox.CaretBrushProperty, "ThemeTextFg");
        _input.SetResourceReference(BorderBrushProperty, "ThemeMenuPopupBorder");
        _input.TextChanged += OnInputTextChanged;

        // Match case checkbox
        _matchCase = new CheckBox
        {
            Content = new TextBlock
            {
                Text = "Aa",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
            },
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Focusable = false,
        };
        _matchCase.SetResourceReference(ForegroundProperty, "ThemeTextFg");
        _matchCase.Checked += (_, _) => UpdateSearch();
        _matchCase.Unchecked += (_, _) => UpdateSearch();

        // Match count label
        _matchCount = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            MinWidth = 60,
        };
        _matchCount.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextFgMuted");

        // Nav buttons
        var prevBtn = MakeNavButton("\uE70E", "Previous match (Shift+Enter)");
        prevBtn.Click += (_, _) => { _editor?.FindPrevious(); UpdateMatchCountLabel(); };
        var nextBtn = MakeNavButton("\uE70D", "Next match (Enter)");
        nextBtn.Click += (_, _) => { _editor?.FindNext(); UpdateMatchCountLabel(); };

        // Close button
        var closeBtn = MakeNavButton("\uE711", "Close (Escape)");
        closeBtn.Click += (_, _) => Close();

        // Layout
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(8, 6, 8, 6),
        };
        row.Children.Add(_input);
        row.Children.Add(_matchCase);
        row.Children.Add(_matchCount);
        row.Children.Add(prevBtn);
        row.Children.Add(nextBtn);
        row.Children.Add(closeBtn);

        _panel = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 8),
            Child = row,
            Effect = new DropShadowEffect { BlurRadius = 8, Opacity = 0.25, ShadowDepth = 1 },
        };
        _panel.SetResourceReference(Border.BackgroundProperty, "ThemeMenuPopupBg");
        _panel.SetResourceReference(Border.BorderBrushProperty, "ThemeMenuPopupBorder");

        Content = _panel;
        Visibility = Visibility.Collapsed;
    }

    public void SetEditor(EditorControl editor)
    {
        _editor = editor;
    }

    public void Open()
    {
        Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input,
            () => { Keyboard.Focus(_input); _input.SelectAll(); });
    }

    public void Close()
    {
        _editor?.ClearFindMatches();
        Visibility = Visibility.Collapsed;
        _matchCount.Text = "";
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSearch();
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

        _editor.SetFindMatches(query, _matchCase.IsChecked == true);
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

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;

            case Key.Enter:
                if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
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

    private static Button MakeNavButton(string icon, string tooltip)
    {
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = icon,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 12,
            },
            Width = 26,
            Height = 26,
            Margin = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = tooltip,
            Background = Brushes.Transparent,
        };
        btn.SetResourceReference(ForegroundProperty, "ThemeTextFg");
        return btn;
    }
}
