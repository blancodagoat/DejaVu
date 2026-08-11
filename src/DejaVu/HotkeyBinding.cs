using System.Text;

namespace DejaVu;

internal enum HotkeyId
{
    Save = 1,
}

/// <summary>
/// A global hotkey: a non-modifier key plus any of Ctrl/Alt/Shift/Win.
/// Round-trips through a human-readable string such as "Ctrl+Shift+S" or "PrintScreen".
/// </summary>
internal readonly record struct HotkeyBinding(Keys Key, bool Ctrl, bool Alt, bool Shift, bool Win)
{
    public static readonly HotkeyBinding DefaultSave = new(Keys.F10, false, true, false, false);

    /// <summary>Win32 fsModifiers, including MOD_NOREPEAT so a held key fires once.</summary>
    public uint Modifiers
    {
        get
        {
            uint m = Native.MOD_NOREPEAT;
            if (Ctrl) m |= Native.MOD_CONTROL;
            if (Alt) m |= Native.MOD_ALT;
            if (Shift) m |= Native.MOD_SHIFT;
            if (Win) m |= Native.MOD_WIN;
            return m;
        }
    }

    public uint VirtualKey => (uint)Key;

    public bool IsValid => Key != Keys.None && !IsModifier(Key);

    public static bool IsModifier(Keys key) => key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu
        or Keys.LControlKey or Keys.RControlKey or Keys.LShiftKey or Keys.RShiftKey
        or Keys.LMenu or Keys.RMenu or Keys.LWin or Keys.RWin;

    public override string ToString()
    {
        var sb = new StringBuilder();
        if (Ctrl) sb.Append("Ctrl+");
        if (Alt) sb.Append("Alt+");
        if (Shift) sb.Append("Shift+");
        if (Win) sb.Append("Win+");
        sb.Append(KeyName(Key));
        return sb.ToString();
    }

    public static bool TryParse(string? text, out HotkeyBinding binding)
    {
        binding = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        bool ctrl = false, alt = false, shift = false, win = false;
        Keys key = Keys.None;

        foreach (var raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToLowerInvariant())
            {
                case "ctrl" or "control": ctrl = true; continue;
                case "alt": alt = true; continue;
                case "shift": shift = true; continue;
                case "win" or "windows": win = true; continue;
            }

            if (!TryParseKey(raw, out key))
            {
                return false;
            }
        }

        binding = new HotkeyBinding(key, ctrl, alt, shift, win);
        return binding.IsValid;
    }

    /// <summary>
    /// Keys has several duplicate values (Snapshot/PrintScreen, Return/Enter), so the
    /// display name is mapped explicitly rather than left to Enum.ToString.
    /// </summary>
    private static string KeyName(Keys key) => key switch
    {
        Keys.PrintScreen => "PrintScreen",
        Keys.Return => "Enter",
        Keys.Capital => "CapsLock",
        Keys.Next => "PageDown",
        Keys.Prior => "PageUp",
        Keys.Back => "Backspace",
        Keys.Escape => "Esc",
        Keys.OemMinus => "-",
        Keys.Oemplus => "=",
        Keys.OemOpenBrackets => "[",
        Keys.OemCloseBrackets => "]",
        Keys.OemSemicolon => ";",
        Keys.OemQuotes => "'",
        Keys.Oemcomma => ",",
        Keys.OemPeriod => ".",
        Keys.OemQuestion => "/",
        Keys.OemPipe => "\\",
        Keys.Oemtilde => "`",
        >= Keys.D0 and <= Keys.D9 => ((char)('0' + (key - Keys.D0))).ToString(),
        _ => key.ToString(),
    };

    private static bool TryParseKey(string name, out Keys key)
    {
        switch (name.ToLowerInvariant())
        {
            case "printscreen" or "prtsc" or "snapshot": key = Keys.PrintScreen; return true;
            case "enter" or "return": key = Keys.Return; return true;
            case "capslock": key = Keys.Capital; return true;
            case "pagedown": key = Keys.Next; return true;
            case "pageup": key = Keys.Prior; return true;
            case "backspace": key = Keys.Back; return true;
            case "esc" or "escape": key = Keys.Escape; return true;
            case "-": key = Keys.OemMinus; return true;
            case "=": key = Keys.Oemplus; return true;
            case "[": key = Keys.OemOpenBrackets; return true;
            case "]": key = Keys.OemCloseBrackets; return true;
            case ";": key = Keys.OemSemicolon; return true;
            case "'": key = Keys.OemQuotes; return true;
            case ",": key = Keys.Oemcomma; return true;
            case ".": key = Keys.OemPeriod; return true;
            case "/": key = Keys.OemQuestion; return true;
            case "\\": key = Keys.OemPipe; return true;
            case "`": key = Keys.Oemtilde; return true;
        }

        if (name.Length == 1 && name[0] is >= '0' and <= '9')
        {
            key = Keys.D0 + (name[0] - '0');
            return true;
        }

        return Enum.TryParse(name, ignoreCase: true, out key);
    }
}
