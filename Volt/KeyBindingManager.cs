using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Input;

namespace Volt;

public enum VoltCommand
{
    NewTab,
    OpenFile,
    Save,
    SaveAs,
    CloseTab,
    OpenFind,
    ToggleReplace,
    CommandPalette,
    OpenFolder,
    Settings,
    ZoomIn,
    ZoomOut,
    ToggleLeftPanel,
    ToggleRightPanel,
    ToggleTopPanel,
    ToggleBottomPanel,
    SwitchTabForward,
    SwitchTabBackward,
    FoldBlock,
    UnfoldBlock,
    GoToLine,
    FocusExplorer,
}

[JsonConverter(typeof(KeyComboJsonConverter))]
public readonly struct KeyCombo : IEquatable<KeyCombo>
{
    public Key Key { get; }
    public ModifierKeys Modifiers { get; }

    public KeyCombo(Key key, ModifierKeys modifiers)
    {
        Key = key;
        Modifiers = modifiers;
    }

    public static KeyCombo None => new(Key.None, ModifierKeys.None);
    public bool IsNone => Key == Key.None;

    public bool Matches(Key key, ModifierKeys modifiers)
    {
        return NormalizeKey(key) == NormalizeKey(Key) && modifiers == Modifiers;
    }

    private static Key NormalizeKey(Key key) => key switch
    {
        Key.Add => Key.OemPlus,
        Key.Subtract => Key.OemMinus,
        _ => key,
    };

    public override string ToString()
    {
        if (IsNone) return "";
        var parts = new List<string>(4);
        if ((Modifiers & ModifierKeys.Control) != 0) parts.Add("Ctrl");
        if ((Modifiers & ModifierKeys.Alt) != 0) parts.Add("Alt");
        if ((Modifiers & ModifierKeys.Shift) != 0) parts.Add("Shift");
        parts.Add(KeyToString(Key));
        return string.Join("+", parts);
    }

    public static KeyCombo Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return None;
        var parts = text.Split('+');
        var modifiers = ModifierKeys.None;
        Key key = Key.None;
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Equals("Ctrl", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModifierKeys.Control;
            else if (trimmed.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModifierKeys.Alt;
            else if (trimmed.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                modifiers |= ModifierKeys.Shift;
            else
                key = StringToKey(trimmed);
        }
        return key == Key.None ? None : new KeyCombo(key, modifiers);
    }

    private static string KeyToString(Key key) => key switch
    {
        Key.OemPlus => "=",
        Key.OemMinus => "-",
        Key.OemOpenBrackets => "[",
        Key.OemCloseBrackets => "]",
        Key.OemPeriod => ".",
        Key.OemComma => ",",
        Key.OemSemicolon => ";",
        Key.OemQuotes => "'",
        Key.OemQuestion => "/",
        Key.OemPipe => "\\",
        Key.OemTilde => "`",
        Key.Tab => "Tab",
        Key.Return => "Enter",
        Key.Back => "Backspace",
        Key.Delete => "Delete",
        Key.Escape => "Escape",
        Key.Space => "Space",
        _ when key >= Key.A && key <= Key.Z => key.ToString(),
        _ when key >= Key.D0 && key <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        _ when key >= Key.F1 && key <= Key.F12 => key.ToString(),
        _ => key.ToString(),
    };

    private static Key StringToKey(string s)
    {
        if (s.Length == 1)
        {
            char c = char.ToUpper(s[0]);
            if (c >= 'A' && c <= 'Z') return Key.A + (c - 'A');
            if (c >= '0' && c <= '9') return Key.D0 + (c - '0');
        }
        return s switch
        {
            "=" => Key.OemPlus,
            "-" => Key.OemMinus,
            "[" => Key.OemOpenBrackets,
            "]" => Key.OemCloseBrackets,
            "." => Key.OemPeriod,
            "," => Key.OemComma,
            ";" => Key.OemSemicolon,
            "'" => Key.OemQuotes,
            "/" => Key.OemQuestion,
            "\\" => Key.OemPipe,
            "`" => Key.OemTilde,
            "Tab" => Key.Tab,
            "Enter" => Key.Return,
            "Backspace" => Key.Back,
            "Delete" => Key.Delete,
            "Escape" => Key.Escape,
            "Space" => Key.Space,
            _ when Enum.TryParse<Key>(s, ignoreCase: true, out var k) => k,
            _ => Key.None,
        };
    }

    public bool Equals(KeyCombo other) => Key == other.Key && Modifiers == other.Modifiers;
    public override bool Equals(object? obj) => obj is KeyCombo other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(Key, Modifiers);
    public static bool operator ==(KeyCombo a, KeyCombo b) => a.Equals(b);
    public static bool operator !=(KeyCombo a, KeyCombo b) => !a.Equals(b);
}

public class KeyComboJsonConverter : JsonConverter<KeyCombo>
{
    public override KeyCombo Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => KeyCombo.Parse(reader.GetString() ?? "");

    public override void Write(Utf8JsonWriter writer, KeyCombo value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}

public class KeyBindingManager
{
    public static readonly Dictionary<VoltCommand, KeyCombo> Defaults = new()
    {
        [VoltCommand.NewTab] = new(Key.N, ModifierKeys.Control),
        [VoltCommand.OpenFile] = new(Key.O, ModifierKeys.Control),
        [VoltCommand.Save] = new(Key.S, ModifierKeys.Control),
        [VoltCommand.SaveAs] = new(Key.S, ModifierKeys.Control | ModifierKeys.Shift),
        [VoltCommand.CloseTab] = new(Key.W, ModifierKeys.Control),
        [VoltCommand.OpenFind] = new(Key.F, ModifierKeys.Control),
        [VoltCommand.ToggleReplace] = new(Key.H, ModifierKeys.Control),
        [VoltCommand.CommandPalette] = new(Key.P, ModifierKeys.Control | ModifierKeys.Shift),
        [VoltCommand.OpenFolder] = new(Key.O, ModifierKeys.Control | ModifierKeys.Shift),
        [VoltCommand.Settings] = new(Key.S, ModifierKeys.Control | ModifierKeys.Alt),
        [VoltCommand.ZoomIn] = new(Key.OemPlus, ModifierKeys.Control),
        [VoltCommand.ZoomOut] = new(Key.OemMinus, ModifierKeys.Control),
        [VoltCommand.ToggleLeftPanel] = new(Key.B, ModifierKeys.Control),
        [VoltCommand.ToggleRightPanel] = new(Key.B, ModifierKeys.Control | ModifierKeys.Alt),
        [VoltCommand.ToggleTopPanel] = new(Key.J, ModifierKeys.Control | ModifierKeys.Alt),
        [VoltCommand.ToggleBottomPanel] = new(Key.J, ModifierKeys.Control),
        [VoltCommand.SwitchTabForward] = new(Key.Tab, ModifierKeys.Control),
        [VoltCommand.SwitchTabBackward] = new(Key.Tab, ModifierKeys.Control | ModifierKeys.Shift),
        [VoltCommand.FoldBlock] = new(Key.OemOpenBrackets, ModifierKeys.Control | ModifierKeys.Shift),
        [VoltCommand.UnfoldBlock] = new(Key.OemCloseBrackets, ModifierKeys.Control | ModifierKeys.Shift),
        [VoltCommand.GoToLine] = new(Key.G, ModifierKeys.Control),
        [VoltCommand.FocusExplorer] = new(Key.E, ModifierKeys.Control),
    };

    private readonly Dictionary<VoltCommand, KeyCombo> _bindings = new();

    public KeyBindingManager()
    {
        ResetAll();
    }

    public void Load(KeyBindingSettings? saved)
    {
        ResetAll();
        if (saved?.CustomBindings == null) return;
        foreach (var (cmdStr, comboStr) in saved.CustomBindings)
        {
            if (Enum.TryParse<VoltCommand>(cmdStr, out var cmd) && Defaults.ContainsKey(cmd))
            {
                var combo = KeyCombo.Parse(comboStr);
                _bindings[cmd] = combo;
            }
        }
    }

    public KeyBindingSettings GetSaveState()
    {
        var settings = new KeyBindingSettings();
        foreach (var (cmd, combo) in _bindings)
        {
            if (Defaults.TryGetValue(cmd, out var def) && combo != def)
                settings.CustomBindings[cmd.ToString()] = combo.ToString();
        }
        return settings;
    }

    public bool TryGetCommand(Key key, ModifierKeys modifiers, out VoltCommand command)
    {
        foreach (var (cmd, combo) in _bindings)
        {
            if (!combo.IsNone && combo.Matches(key, modifiers))
            {
                command = cmd;
                return true;
            }
        }
        command = default;
        return false;
    }

    public KeyCombo GetBinding(VoltCommand command)
        => _bindings.TryGetValue(command, out var combo) ? combo : KeyCombo.None;

    public void SetBinding(VoltCommand command, KeyCombo combo) => _bindings[command] = combo;

    public void SetAll(Dictionary<VoltCommand, KeyCombo> bindings)
    {
        ResetAll();
        foreach (var (cmd, combo) in bindings)
            _bindings[cmd] = combo;
    }

    public void ResetBinding(VoltCommand command)
    {
        if (Defaults.TryGetValue(command, out var def))
            _bindings[command] = def;
    }

    public void ResetAll()
    {
        _bindings.Clear();
        foreach (var (cmd, combo) in Defaults)
            _bindings[cmd] = combo;
    }

    public string GetGestureText(VoltCommand command) => GetBinding(command).ToString();

    public VoltCommand? FindConflict(VoltCommand command, KeyCombo combo)
    {
        if (combo.IsNone) return null;
        foreach (var (cmd, existing) in _bindings)
        {
            if (cmd != command && !existing.IsNone && existing == combo)
                return cmd;
        }
        return null;
    }

    public Dictionary<VoltCommand, KeyCombo> GetAllBindings() => new(_bindings);

    public static bool IsPreviewBinding(VoltCommand command)
        => command is VoltCommand.SwitchTabForward or VoltCommand.SwitchTabBackward;

    public static string GetDisplayName(VoltCommand command) => command switch
    {
        VoltCommand.NewTab => "New Tab",
        VoltCommand.OpenFile => "Open File",
        VoltCommand.Save => "Save",
        VoltCommand.SaveAs => "Save As",
        VoltCommand.CloseTab => "Close Tab",
        VoltCommand.OpenFind => "Find",
        VoltCommand.ToggleReplace => "Find and Replace",
        VoltCommand.CommandPalette => "Command Palette",
        VoltCommand.OpenFolder => "Open Folder",
        VoltCommand.Settings => "Settings",
        VoltCommand.ZoomIn => "Zoom In",
        VoltCommand.ZoomOut => "Zoom Out",
        VoltCommand.ToggleLeftPanel => "Toggle Left Panel",
        VoltCommand.ToggleRightPanel => "Toggle Right Panel",
        VoltCommand.ToggleTopPanel => "Toggle Top Panel",
        VoltCommand.ToggleBottomPanel => "Toggle Bottom Panel",
        VoltCommand.SwitchTabForward => "Next Tab",
        VoltCommand.SwitchTabBackward => "Previous Tab",
        VoltCommand.FoldBlock => "Fold Block",
        VoltCommand.UnfoldBlock => "Unfold Block",
        VoltCommand.FocusExplorer => "Focus Explorer",
        _ => command.ToString(),
    };
}
