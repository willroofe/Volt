using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace TextEdit;

public record PaletteOption(string Label, Action ApplyPreview, Action Commit, Action Revert);

public record PaletteCommand(string Name, Action? Toggle = null, Func<List<PaletteOption>>? GetOptions = null);

public class CommandPalette : UserControl
{
    private readonly Border _overlay;
    private readonly Border _panel;
    private readonly TextBox _input;
    private readonly TextBlock _prefix;
    private readonly ListBox _list;

    private List<PaletteCommand> _commands = [];
    private List<PaletteOption>? _currentOptions;
    private PaletteCommand? _activeCommand;
    private int _selectedIndex = -1;
    private string _prefixText = "";

    public event EventHandler? Closed;

    public CommandPalette()
    {
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) _input.Focus();
        };

        // -- Overlay (semi-transparent dark background) --
        _overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _overlay.MouseLeftButtonDown += (_, _) => Cancel();

        // -- Input text box --
        _prefix = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
        };
        _prefix.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextFgMuted");

        _input = new TextBox
        {
            BorderThickness = new Thickness(0),
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 14,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brushes.Transparent,
        };
        _input.SetResourceReference(ForegroundProperty, "ThemeTextFg");
        _input.TextChanged += OnInputTextChanged;

        var inputRow = new DockPanel { Margin = new Thickness(10, 8, 10, 8) };
        DockPanel.SetDock(_prefix, Dock.Left);
        inputRow.Children.Add(_prefix);
        inputRow.Children.Add(_input);

        var inputBorder = new Border();
        inputBorder.SetResourceReference(Border.BackgroundProperty, "ThemeContentBg");
        inputBorder.Child = inputRow;

        // -- List --
        _list = new ListBox
        {
            BorderThickness = new Thickness(0),
            MaxHeight = 320, // ~10 items
            Padding = new Thickness(0),
            FocusVisualStyle = null,
            Focusable = false,
        };
        _list.SetResourceReference(ListBox.BackgroundProperty, "ThemeMenuPopupBg");

        // -- Panel --
        var stack = new StackPanel();
        stack.Children.Add(inputBorder);
        stack.Children.Add(new Border { Height = 1 });
        var separatorBorder = (Border)stack.Children[1];
        separatorBorder.SetResourceReference(Border.BackgroundProperty, "ThemeMenuPopupBorder");
        stack.Children.Add(_list);

        _panel = new Border
        {
            Width = 420,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 16, Opacity = 0.3, ShadowDepth = 2 },
        };
        _panel.SetResourceReference(Border.BackgroundProperty, "ThemeMenuPopupBg");
        _panel.SetResourceReference(Border.BorderBrushProperty, "ThemeMenuPopupBorder");

        // -- Compose --
        var root = new Grid();
        root.Children.Add(_overlay);
        root.Children.Add(_panel);

        Content = root;
        Visibility = Visibility.Collapsed;
    }

    public void SetCommands(List<PaletteCommand> commands)
    {
        _commands = commands;
    }

    public void Open()
    {
        _currentOptions = null;
        _activeCommand = null;
        _prefixText = "";
        _prefix.Text = "";
        _input.Text = "";
        _selectedIndex = -1;
        Visibility = Visibility.Visible;
        RefreshList();
        _input.Focus();
    }

    public void Cancel()
    {
        if (_currentOptions != null)
        {
            // Revert preview and go back to top-level
            RevertCurrentPreview();
            _currentOptions = null;
            _activeCommand = null;
            _prefixText = "";
            _prefix.Text = "";
            _input.Text = "";
            _selectedIndex = -1;
            RefreshList();
            return;
        }

        Visibility = Visibility.Collapsed;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void RevertCurrentPreview()
    {
        if (_currentOptions != null && _selectedIndex >= 0 && _selectedIndex < _currentOptions.Count)
        {
            var filtered = GetFilteredOptions();
            if (_selectedIndex < filtered.Count)
                filtered[_selectedIndex].Revert();
        }
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e)
    {
        _selectedIndex = -1;
        RefreshList();
        // Auto-select first item
        if (_list.Items.Count > 0)
        {
            _selectedIndex = 0;
            UpdateListSelection();
            if (_currentOptions != null)
                ApplyPreviewForIndex(0);
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                Cancel();
                e.Handled = true;
                break;

            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;

            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;

            case Key.Enter:
                Confirm();
                e.Handled = true;
                break;

            case Key.Back:
                if (_input.Text.Length == 0 && _currentOptions != null)
                {
                    Cancel(); // go back to top-level
                    e.Handled = true;
                }
                break;
        }

        if (!e.Handled)
            base.OnPreviewKeyDown(e);
    }

    private void MoveSelection(int delta)
    {
        int count = _list.Items.Count;
        if (count == 0) return;

        // Revert current preview before moving
        if (_currentOptions != null && _selectedIndex >= 0)
        {
            var filtered = GetFilteredOptions();
            if (_selectedIndex < filtered.Count)
                filtered[_selectedIndex].Revert();
        }

        _selectedIndex += delta;
        if (_selectedIndex < 0) _selectedIndex = count - 1;
        if (_selectedIndex >= count) _selectedIndex = 0;

        UpdateListSelection();

        if (_currentOptions != null)
            ApplyPreviewForIndex(_selectedIndex);
    }

    private void ApplyPreviewForIndex(int index)
    {
        var filtered = GetFilteredOptions();
        if (index >= 0 && index < filtered.Count)
            filtered[index].ApplyPreview();
    }

    private void Confirm()
    {
        if (_currentOptions == null)
        {
            // Top-level: enter sub-list or execute toggle
            var filtered = GetFilteredCommands();
            if (_selectedIndex < 0 || _selectedIndex >= filtered.Count) return;
            var cmd = filtered[_selectedIndex];

            if (cmd.Toggle != null)
            {
                cmd.Toggle();
                Visibility = Visibility.Collapsed;
                Closed?.Invoke(this, EventArgs.Empty);
                return;
            }

            if (cmd.GetOptions != null)
            {
                _activeCommand = cmd;
                _currentOptions = cmd.GetOptions();
                _prefixText = cmd.Name + ": ";
                _prefix.Text = _prefixText;
                _input.Text = "";
                _selectedIndex = -1;
                RefreshList();
                // Auto-select first
                if (_list.Items.Count > 0)
                {
                    _selectedIndex = 0;
                    UpdateListSelection();
                    ApplyPreviewForIndex(0);
                }
            }
        }
        else
        {
            // Sub-list: commit selection
            var filtered = GetFilteredOptions();
            if (_selectedIndex >= 0 && _selectedIndex < filtered.Count)
                filtered[_selectedIndex].Commit();
            Visibility = Visibility.Collapsed;
            Closed?.Invoke(this, EventArgs.Empty);
        }
    }

    private List<PaletteCommand> GetFilteredCommands()
    {
        var filter = _input.Text.Trim();
        if (string.IsNullOrEmpty(filter)) return _commands;
        return _commands.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private List<PaletteOption> GetFilteredOptions()
    {
        if (_currentOptions == null) return [];
        var filter = _input.Text.Trim();
        if (string.IsNullOrEmpty(filter)) return _currentOptions;
        return _currentOptions.Where(o => o.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void RefreshList()
    {
        _list.Items.Clear();

        if (_currentOptions == null)
        {
            foreach (var cmd in GetFilteredCommands())
            {
                var item = MakeListItem(cmd.Name);
                _list.Items.Add(item);
            }
        }
        else
        {
            foreach (var opt in GetFilteredOptions())
            {
                var item = MakeListItem(opt.Label);
                _list.Items.Add(item);
            }
        }

        if (_selectedIndex >= 0 && _selectedIndex < _list.Items.Count)
            UpdateListSelection();
    }

    private ListBoxItem MakeListItem(string text)
    {
        var tb = new TextBlock
        {
            Text = text,
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 13,
            Margin = new Thickness(12, 6, 12, 6),
        };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "ThemeTextFg");

        var item = new ListBoxItem
        {
            Content = tb,
            Padding = new Thickness(0),
            FocusVisualStyle = null,
            Focusable = false,
        };
        item.SetResourceReference(ListBoxItem.BackgroundProperty, "ThemeMenuPopupBg");

        // Mouse click to select
        item.MouseLeftButtonUp += (_, _) =>
        {
            _selectedIndex = _list.Items.IndexOf(item);
            UpdateListSelection();
            Confirm();
        };

        // Mouse hover highlight
        item.MouseEnter += (_, _) =>
        {
            var idx = _list.Items.IndexOf(item);
            if (idx == _selectedIndex) return;

            // Revert old preview
            if (_currentOptions != null && _selectedIndex >= 0)
            {
                var filtered = GetFilteredOptions();
                if (_selectedIndex < filtered.Count)
                    filtered[_selectedIndex].Revert();
            }

            _selectedIndex = idx;
            UpdateListSelection();

            if (_currentOptions != null)
                ApplyPreviewForIndex(_selectedIndex);
        };

        return item;
    }

    private void UpdateListSelection()
    {
        for (int i = 0; i < _list.Items.Count; i++)
        {
            var item = (ListBoxItem)_list.Items[i];
            if (i == _selectedIndex)
            {
                item.SetResourceReference(ListBoxItem.BackgroundProperty, "ThemeMenuItemHover");
                item.BringIntoView();
            }
            else
            {
                item.SetResourceReference(ListBoxItem.BackgroundProperty, "ThemeMenuPopupBg");
            }
        }
    }
}
