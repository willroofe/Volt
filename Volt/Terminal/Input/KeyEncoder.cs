using System.Windows.Input;

namespace Volt;

public static class KeyEncoder
{
    public static byte[]? Encode(Key key, ModifierKeys modifiers)
    {
        switch (key)
        {
            case Key.Enter: return new byte[] { 0x0D };
            case Key.Tab: return modifiers.HasFlag(ModifierKeys.Shift) ? new byte[] { 0x1B, (byte)'[', (byte)'Z' } : new byte[] { 0x09 };
            case Key.Back: return new byte[] { 0x7F };
            case Key.Escape: return new byte[] { 0x1B };

            case Key.Up:    return Arrow('A', modifiers);
            case Key.Down:  return Arrow('B', modifiers);
            case Key.Right: return Arrow('C', modifiers);
            case Key.Left:  return Arrow('D', modifiers);

            case Key.Home:  return modifiers == ModifierKeys.None ? Ascii("\x1b[H") : Arrow('H', modifiers);
            case Key.End:   return modifiers == ModifierKeys.None ? Ascii("\x1b[F") : Arrow('F', modifiers);
            case Key.PageUp:   return Ascii("\x1b[5~");
            case Key.PageDown: return Ascii("\x1b[6~");
            case Key.Insert:   return Ascii("\x1b[2~");
            case Key.Delete:   return Ascii("\x1b[3~");

            case Key.F1: return Ascii("\x1bOP");
            case Key.F2: return Ascii("\x1bOQ");
            case Key.F3: return Ascii("\x1bOR");
            case Key.F4: return Ascii("\x1bOS");
            case Key.F5: return Ascii("\x1b[15~");
            case Key.F6: return Ascii("\x1b[17~");
            case Key.F7: return Ascii("\x1b[18~");
            case Key.F8: return Ascii("\x1b[19~");
            case Key.F9: return Ascii("\x1b[20~");
            case Key.F10: return Ascii("\x1b[21~");
            case Key.F11: return Ascii("\x1b[23~");
            case Key.F12: return Ascii("\x1b[24~");
        }

        // Ctrl+letter → control byte
        if (modifiers == ModifierKeys.Control && key >= Key.A && key <= Key.Z)
            return new byte[] { (byte)(key - Key.A + 1) };

        return null;
    }

    private static byte[] Arrow(char final, ModifierKeys mods)
    {
        if (mods == ModifierKeys.None) return new byte[] { 0x1B, (byte)'[', (byte)final };
        int modCode = 1;
        if (mods.HasFlag(ModifierKeys.Shift)) modCode += 1;
        if (mods.HasFlag(ModifierKeys.Alt)) modCode += 2;
        if (mods.HasFlag(ModifierKeys.Control)) modCode += 4;
        return Ascii($"\x1b[1;{modCode}{final}");
    }

    private static byte[] Ascii(string s)
    {
        var b = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) b[i] = (byte)s[i];
        return b;
    }
}
