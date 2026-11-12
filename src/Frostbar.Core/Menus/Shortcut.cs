namespace Frostbar.Core.Menus;

[Flags]
public enum Mods { None = 0, Ctrl = 1, Shift = 2, Alt = 4, Win = 8 }

/// <summary>
/// A key chord stored as modifiers + virtual-key code, never as a character: on a JIS keyboard Ctrl+"=" is the ";" key
/// (VK_OEM_PLUS), so characters would send the wrong key. Display text comes from the active keyboard layout.
/// </summary>
public readonly record struct Shortcut(Mods Mods, ushort Vk)
{
    // Virtual-key codes used by the menus.
    public const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_PRIOR = 0x21, VK_NEXT = 0x22,
        VK_END = 0x23, VK_HOME = 0x24, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28, VK_INSERT = 0x2D, VK_DELETE = 0x2E,
        VK_F1 = 0x70, VK_OEM_1 = 0xBA, VK_OEM_PLUS = 0xBB, VK_OEM_COMMA = 0xBC, VK_OEM_MINUS = 0xBD, VK_OEM_PERIOD = 0xBE, VK_OEM_2 = 0xBF, VK_OEM_3 = 0xC0,
        VK_NUMPAD0 = 0x60;

    static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Enter"] = VK_RETURN, ["Return"] = VK_RETURN, ["Esc"] = VK_ESCAPE, ["Escape"] = VK_ESCAPE, ["Space"] = VK_SPACE, ["Tab"] = VK_TAB,
        ["Backspace"] = VK_BACK, ["Delete"] = VK_DELETE, ["Del"] = VK_DELETE, ["Insert"] = VK_INSERT, ["Home"] = VK_HOME, ["End"] = VK_END,
        ["PageUp"] = VK_PRIOR, ["PageDown"] = VK_NEXT, ["Left"] = VK_LEFT, ["Right"] = VK_RIGHT, ["Up"] = VK_UP, ["Down"] = VK_DOWN,
        ["="] = VK_OEM_PLUS, ["Plus"] = VK_OEM_PLUS, ["-"] = VK_OEM_MINUS, ["Minus"] = VK_OEM_MINUS, [","] = VK_OEM_COMMA, ["."] = VK_OEM_PERIOD,
        ["/"] = VK_OEM_2, [";"] = VK_OEM_1, ["`"] = VK_OEM_3,
    };

    /// <summary>"Ctrl+Shift+S", "Ctrl+=", "Alt+Left", "F11", "Win+.", "Ctrl+Shift+Tab". Returns null when it cannot be read.</summary>
    public static Shortcut? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var mods = Mods.None;
        string rest = text.Trim();
        while (rest.Length >= 2)
        {
            int plus = rest.IndexOf('+', 1); // a key named "+" is not supported; "Plus" or "=" is
            if (plus < 0) break;
            var head = rest[..plus];
            Mods? m = head.ToLowerInvariant() switch { "ctrl" or "control" => Mods.Ctrl, "shift" => Mods.Shift, "alt" => Mods.Alt, "win" => Mods.Win, _ => null };
            if (m == null) break;
            mods |= m.Value;
            rest = rest[(plus + 1)..];
        }
        if (rest.Length == 0) return null;
        if (rest.Length == 1 && char.IsAsciiLetterOrDigit(rest[0])) return new Shortcut(mods, char.ToUpperInvariant(rest[0]));
        if (rest.Length is >= 2 and <= 3 && (rest[0] == 'F' || rest[0] == 'f') && int.TryParse(rest[1..], out int f) && f is >= 1 and <= 24)
            return new Shortcut(mods, (ushort)(VK_F1 + f - 1));
        if (rest.Length == 7 && rest.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase) && char.IsAsciiDigit(rest[6]))
            return new Shortcut(mods, (ushort)(VK_NUMPAD0 + rest[6] - '0'));
        return Named.TryGetValue(rest, out var vk) ? new Shortcut(mods, vk) : null;
    }

    /// <summary>"Ctrl+Shift+S". <paramref name="keyName"/> names the main key (from the keyboard layout); null uses built-in names.</summary>
    public string Format(Func<ushort, string?>? keyName = null)
    {
        var parts = new List<string>(5);
        if (Mods.HasFlag(Mods.Win)) parts.Add("Win");
        if (Mods.HasFlag(Mods.Ctrl)) parts.Add("Ctrl");
        if (Mods.HasFlag(Mods.Alt)) parts.Add("Alt");
        if (Mods.HasFlag(Mods.Shift)) parts.Add("Shift");
        parts.Add(keyName?.Invoke(Vk) ?? DefaultName(Vk));
        return string.Join("+", parts);
    }

    public static string DefaultName(ushort vk) => vk switch
    {
        >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        >= VK_F1 and <= VK_F1 + 23 => "F" + (vk - VK_F1 + 1),
        >= VK_NUMPAD0 and <= VK_NUMPAD0 + 9 => "Num " + (vk - VK_NUMPAD0),
        VK_RETURN => "Enter", VK_ESCAPE => "Esc", VK_SPACE => "Space", VK_TAB => "Tab", VK_BACK => "Backspace", VK_DELETE => "Delete",
        VK_INSERT => "Insert", VK_HOME => "Home", VK_END => "End", VK_PRIOR => "PageUp", VK_NEXT => "PageDown",
        VK_LEFT => "←", VK_RIGHT => "→", VK_UP => "↑", VK_DOWN => "↓",
        VK_OEM_PLUS => "=", VK_OEM_MINUS => "-", VK_OEM_COMMA => ",", VK_OEM_PERIOD => ".", VK_OEM_2 => "/", VK_OEM_1 => ";", VK_OEM_3 => "`",
        _ => $"0x{vk:X2}",
    };

    public override string ToString() => Format();

    // The names the settings keep keys by (one for each key Parse reads by name).
    static readonly Dictionary<ushort, string> Stored = new()
    {
        [VK_RETURN] = "Enter", [VK_ESCAPE] = "Esc", [VK_SPACE] = "Space", [VK_TAB] = "Tab", [VK_BACK] = "Backspace", [VK_DELETE] = "Delete",
        [VK_INSERT] = "Insert", [VK_HOME] = "Home", [VK_END] = "End", [VK_PRIOR] = "PageUp", [VK_NEXT] = "PageDown",
        [VK_LEFT] = "Left", [VK_RIGHT] = "Right", [VK_UP] = "Up", [VK_DOWN] = "Down",
        [VK_OEM_PLUS] = "Plus", [VK_OEM_MINUS] = "Minus", [VK_OEM_COMMA] = ",", [VK_OEM_PERIOD] = ".", [VK_OEM_2] = "/", [VK_OEM_1] = ";", [VK_OEM_3] = "`",
    };

    /// <summary>
    /// The text <see cref="Parse"/> reads back ("Ctrl+Alt+Up", "Win+F5"), for the settings (<see cref="Format"/> shows
    /// "↑", which it does not read); null for a key it has no name for.
    /// </summary>
    public string? Store()
    {
        string? key = Vk switch
        {
            >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A => ((char)Vk).ToString(),
            >= VK_F1 and <= VK_F1 + 23 => "F" + (Vk - VK_F1 + 1),
            >= VK_NUMPAD0 and <= VK_NUMPAD0 + 9 => "NumPad" + (Vk - VK_NUMPAD0),
            _ => Stored.TryGetValue(Vk, out var name) ? name : null,
        };
        if (key == null) return null;
        var parts = new List<string>(5);
        if (Mods.HasFlag(Mods.Win)) parts.Add("Win");
        if (Mods.HasFlag(Mods.Ctrl)) parts.Add("Ctrl");
        if (Mods.HasFlag(Mods.Alt)) parts.Add("Alt");
        if (Mods.HasFlag(Mods.Shift)) parts.Add("Shift");
        parts.Add(key);
        return string.Join("+", parts);
    }
}
