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
    private readonly TextBox _replaceInput;
    private readonly TextBlock _matchCount;
    private readonly Button _matchCaseBtn;
    private bool _matchCase;
    private readonly Grid _findRow;
    private readonly Grid _replaceRow;
    private readonly RotateTransform _toggleTransform;

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

        // Search input (borderless — the wrapper border provides the outline)
        _input = new TextBox
        {
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Padding = new Thickness(4, 3, 0, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
        };
        _input.SetResourceReference(ForegroundProperty, "ThemeTextFg");
        _input.SetResourceReference(TextBox.CaretBrushProperty, "ThemeTextFg");
        _input.TextChanged += OnInputTextChanged;

        // Match count label (inside search box)
        _matchCount = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0),
        };
        _matchCount.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextFgMuted");

        // Match case toggle button (inside search box)
        _matchCaseBtn = new Button
        {
            Content = new TextBlock
            {
                Text = "Aa",
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
            },
            Width = 24,
            Height = 22,
            Margin = new Thickness(0, 2, 2, 2),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = "Match case",
            Background = Brushes.Transparent,
        };
        _matchCaseBtn.SetResourceReference(ForegroundProperty, "ThemeTextFgMuted");
        _matchCaseBtn.Style = CreateMatchCaseButtonStyle();
        _matchCaseBtn.Click += (_, _) =>
        {
            _matchCase = !_matchCase;
            _matchCaseBtn.SetResourceReference(ForegroundProperty,
                _matchCase ? "ThemeTextFg" : "ThemeTextFgMuted");
            _matchCaseBtn.SetResourceReference(BackgroundProperty,
                _matchCase ? "ThemeMenuItemHover" : "ThemeMenuPopupBg");
            UpdateSearch();
        };

        // Composite search box: [input (stretches) | match count | Aa button]
        var searchBox = new Border
        {
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            SnapsToDevicePixels = true,
        };
        searchBox.SetResourceReference(Border.BorderBrushProperty, "ThemeMenuPopupBorder");
        var searchInner = new DockPanel();
        DockPanel.SetDock(_matchCaseBtn, Dock.Right);
        DockPanel.SetDock(_matchCount, Dock.Right);
        searchInner.Children.Add(_matchCaseBtn);
        searchInner.Children.Add(_matchCount);
        searchInner.Children.Add(_input); // fills remaining space
        searchBox.Child = searchInner;

        // Replace input
        _replaceInput = new TextBox
        {
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Padding = new Thickness(4, 3, 4, 3),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
            Style = CreateRoundedTextBoxStyle(),
        };
        _replaceInput.SetResourceReference(ForegroundProperty, "ThemeTextFg");
        _replaceInput.SetResourceReference(TextBox.CaretBrushProperty, "ThemeTextFg");

        // Nav buttons
        var prevBtn = MakeNavButton("\uE70E", "Previous match (Shift+Enter)");
        prevBtn.Click += (_, _) => { _editor?.FindPrevious(); UpdateMatchCountLabel(); };
        var nextBtn = MakeNavButton("\uE70D", "Next match (Enter)");
        nextBtn.Click += (_, _) => { _editor?.FindNext(); UpdateMatchCountLabel(); };

        // Close button
        var closeBtn = MakeNavButton("\uE711", "Close (Escape)");
        closeBtn.Click += (_, _) => Close();

        // Replace / Replace All buttons
        var replaceBtn = MakeTextButton("Replace", "Replace current match");
        replaceBtn.Click += (_, _) => DoReplace();
        var replaceAllBtn = MakeTextButton("Replace All", "Replace all matches");
        replaceAllBtn.Click += (_, _) => DoReplaceAll();

        // Replace row (created before toggle button so lambda can capture the field)
        _replaceRow = new Grid
        {
            Margin = new Thickness(42, 2, 8, 6), // 8 (find row margin) + 28 (toggle btn) + 6 (btn spacing)
            Visibility = Visibility.Collapsed,
        };
        _replaceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _replaceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _replaceRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_replaceInput, 0);
        Grid.SetColumn(replaceBtn, 1);
        Grid.SetColumn(replaceAllBtn, 2);
        _replaceRow.Children.Add(_replaceInput);
        _replaceRow.Children.Add(replaceBtn);
        _replaceRow.Children.Add(replaceAllBtn);

        // Find row (declared before toggle lambda so it can be captured)
        _findRow = new Grid { Margin = new Thickness(8, 6, 8, 6) };
        _findRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // toggle
        _findRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // search box
        _findRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // prev
        _findRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // next
        _findRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // close

        // Toggle replace row button (chevron: down = collapsed, up = visible)
        _toggleTransform = new RotateTransform(0);
        var toggleReplaceBtn = MakeNavButton("\uE70D", "Toggle replace (Ctrl+H)");
        toggleReplaceBtn.Margin = new Thickness(0, 0, 6, 0);
        toggleReplaceBtn.RenderTransformOrigin = new Point(0.5, 0.5);
        toggleReplaceBtn.RenderTransform = _toggleTransform;
        toggleReplaceBtn.Click += (_, _) =>
        {
            bool show = _replaceRow.Visibility != Visibility.Visible;
            _replaceRow.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            _toggleTransform.Angle = show ? 180 : 0;
            _findRow.Margin = new Thickness(8, 6, 8, show ? 2 : 6);
        };

        Grid.SetColumn(toggleReplaceBtn, 0);
        Grid.SetColumn(searchBox, 1);
        Grid.SetColumn(prevBtn, 2);
        Grid.SetColumn(nextBtn, 3);
        Grid.SetColumn(closeBtn, 4);
        _findRow.Children.Add(toggleReplaceBtn);
        _findRow.Children.Add(searchBox);
        _findRow.Children.Add(prevBtn);
        _findRow.Children.Add(nextBtn);
        _findRow.Children.Add(closeBtn);

        var stack = new StackPanel { Orientation = Orientation.Vertical };
        stack.Children.Add(_findRow);
        stack.Children.Add(_replaceRow);

        _panel = new Border
        {
            CornerRadius = new CornerRadius(4),
            BorderThickness = new Thickness(1),
            Width = 480,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 8),
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 10, Opacity = 0.3, ShadowDepth = 0 },
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

    public void SetPosition(string position)
    {
        bool top = position == "Top";
        VerticalAlignment = top ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        Margin = top ? new Thickness(0, 33, 0, 0) : new Thickness(0, 0, 0, 44);
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
            Width = 28,
            Height = 28,
            Margin = new Thickness(2, 0, 0, 0),
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = tooltip,
            Background = Brushes.Transparent,
        };
        btn.SetResourceReference(ForegroundProperty, "ThemeTextFg");

        // Themed hover style
        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(TemplateProperty, CreateNavButtonTemplate()));
        btn.Style = style;

        return btn;
    }

    private static Button MakeTextButton(string label, string tooltip)
    {
        var btn = new Button
        {
            Content = new TextBlock
            {
                Text = label,
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 12,
            },
            Height = 28,
            Margin = new Thickness(6, 0, 0, 0),
            BorderThickness = new Thickness(0),
            Cursor = Cursors.Hand,
            Focusable = false,
            ToolTip = tooltip,
            Background = Brushes.Transparent,
        };
        btn.SetResourceReference(ForegroundProperty, "ThemeTextFg");

        var style = new Style(typeof(Button));
        style.Setters.Add(new Setter(TemplateProperty, CreateTextButtonTemplate()));
        btn.Style = style;

        return btn;
    }

    private static ControlTemplate CreateTextButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetResourceReference(Border.BorderBrushProperty, "ThemeMenuPopupBorder");
        border.SetValue(Border.PaddingProperty, new Thickness(10, 0, 10, 0));
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.Name = "border";

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        template.VisualTree = border;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
            new DynamicResourceExtension("ThemeButtonHover"), "border"));
        template.Triggers.Add(hoverTrigger);

        var pressedTrigger = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressedTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
            new DynamicResourceExtension("ThemeMenuItemHover"), "border"));
        template.Triggers.Add(pressedTrigger);

        return template;
    }

    private static ControlTemplate CreateNavButtonTemplate()
    {
        var template = new ControlTemplate(typeof(Button));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetResourceReference(Border.BorderBrushProperty, "ThemeMenuPopupBorder");
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.Name = "border";

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        template.VisualTree = border;

        // Mouse-over trigger
        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
            new DynamicResourceExtension("ThemeButtonHover"), "border"));
        template.Triggers.Add(hoverTrigger);

        // Pressed trigger
        var pressedTrigger = new Trigger { Property = Button.IsPressedProperty, Value = true };
        pressedTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
            new DynamicResourceExtension("ThemeMenuItemHover"), "border"));
        template.Triggers.Add(pressedTrigger);

        return template;
    }

    private static Style CreateRoundedTextBoxStyle()
    {
        var style = new Style(typeof(TextBox));
        var template = new ControlTemplate(typeof(TextBox));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(3));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetResourceReference(Border.BorderBrushProperty, "ThemeMenuPopupBorder");
        border.SetValue(Border.BackgroundProperty, Brushes.Transparent);
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);

        var host = new FrameworkElementFactory(typeof(ScrollViewer));
        host.Name = "PART_ContentHost";
        border.AppendChild(host);

        template.VisualTree = border;
        style.Setters.Add(new Setter(Control.TemplateProperty, template));
        return style;
    }

    private static Style CreateMatchCaseButtonStyle()
    {
        var style = new Style(typeof(Button));
        var template = new ControlTemplate(typeof(Button));

        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        border.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetValue(Border.SnapsToDevicePixelsProperty, true);
        border.Name = "border";

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        template.VisualTree = border;

        var hoverTrigger = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Border.BackgroundProperty,
            new DynamicResourceExtension("ThemeButtonHover"), "border"));
        template.Triggers.Add(hoverTrigger);

        style.Setters.Add(new Setter(TemplateProperty, template));
        return style;
    }
}
