using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace TextEdit;

public record PaletteOption(string Label, Action ApplyPreview, Action Commit, Action Revert);

public record PaletteCommand(string Name, Action? Toggle = null, Func<List<PaletteOption>>? GetOptions = null, Func<string>? CurrentValue = null);

public partial class CommandPalette : UserControl
{
    private List<PaletteCommand> _commands = [];
    private List<PaletteOption>? _currentOptions;
    private PaletteCommand? _activeCommand;
    private int _selectedIndex = -1;
    private string _prefixText = "";

    public event EventHandler? Closed;

    public CommandPalette()
    {
        InitializeComponent();

        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible) Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => { if (_filterInput != null) Keyboard.Focus(_filterInput); });
        };
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
        _filterPrefix.Text = "";
        _filterInput.Text = "";
        _selectedIndex = -1;
        Visibility = Visibility.Visible;
        RefreshList();
        Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Input, () => Keyboard.Focus(_filterInput));
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
            _filterPrefix.Text = "";
            _filterInput.Text = "";
            _selectedIndex = -1;
            RefreshList();
            return;
        }

        Visibility = Visibility.Collapsed;
        Closed?.Invoke(this, EventArgs.Empty);
    }

    private void OnOverlayClick(object sender, MouseButtonEventArgs e)
    {
        Cancel();
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
        if (_commandList.Items.Count > 0)
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
                if (_filterInput.Text.Length == 0 && _currentOptions != null)
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
        int count = _commandList.Items.Count;
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
                // Capture current value before any preview changes the editor state
                var currentValue = cmd.CurrentValue?.Invoke();

                _activeCommand = cmd;
                _currentOptions = cmd.GetOptions();
                _prefixText = cmd.Name + ": ";
                _filterPrefix.Text = _prefixText;
                _filterInput.Text = "";
                _selectedIndex = -1;
                RefreshList();
                // Select the current value if available, otherwise first item
                if (_commandList.Items.Count > 0)
                {
                    _selectedIndex = 0;
                    if (currentValue != null)
                    {
                        var idx = _currentOptions.FindIndex(o =>
                            o.Label.Equals(currentValue, StringComparison.OrdinalIgnoreCase));
                        if (idx >= 0) _selectedIndex = idx;
                    }
                    UpdateListSelection();
                    // ScrollIntoView handles deferred layout internally
                    _commandList.ScrollIntoView(_commandList.Items[_selectedIndex]);
                    ApplyPreviewForIndex(_selectedIndex);
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
        var filter = _filterInput.Text.Trim();
        if (string.IsNullOrEmpty(filter)) return _commands;
        return _commands.Where(c => c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private List<PaletteOption> GetFilteredOptions()
    {
        if (_currentOptions == null) return [];
        var filter = _filterInput.Text.Trim();
        if (string.IsNullOrEmpty(filter)) return _currentOptions;
        return _currentOptions.Where(o => o.Label.Contains(filter, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void RefreshList()
    {
        _commandList.Items.Clear();
        var filter = _filterInput.Text.Trim();
        bool belowThreshold = _currentOptions == null && filter.Length < 3;

        if (belowThreshold)
        {
            // Not enough characters — show placeholder instead of list
            _commandList.Visibility = Visibility.Collapsed;
            _noResults.Visibility = Visibility.Collapsed;
            _placeholder.Visibility = Visibility.Visible;
            return;
        }

        _placeholder.Visibility = Visibility.Collapsed;

        if (_currentOptions == null)
        {
            foreach (var cmd in GetFilteredCommands())
            {
                var item = MakeListItem(cmd.Name);
                _commandList.Items.Add(item);
            }
        }
        else
        {
            foreach (var opt in GetFilteredOptions())
            {
                var item = MakeListItem(opt.Label);
                _commandList.Items.Add(item);
            }
        }

        bool empty = _commandList.Items.Count == 0 && filter.Length > 0;
        _noResults.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        _commandList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;

        if (_selectedIndex >= 0 && _selectedIndex < _commandList.Items.Count)
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
            _selectedIndex = _commandList.Items.IndexOf(item);
            UpdateListSelection();
            Confirm();
        };

        // Mouse hover highlight
        item.MouseEnter += (_, _) =>
        {
            var idx = _commandList.Items.IndexOf(item);
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
        for (int i = 0; i < _commandList.Items.Count; i++)
        {
            var item = (ListBoxItem)_commandList.Items[i];
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
