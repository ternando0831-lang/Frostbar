using System.Runtime.InteropServices;
using Frostbar.Core.Menus;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Input;

/// <summary>
/// Sends key chords to the window in front with real scan codes (some apps read KeyboardEvent.code; Bipane's
/// lesson: SendKeys sends scan code 0). Scan codes come from the target's keyboard layout. If the user is holding Win
/// or Alt, an unassigned key (0xE8) is tapped first so their release does not open Start or the app's menu bar.
/// </summary>
public static class InputInjector
{
    const uint INPUT_KEYBOARD = 1, KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, MAPVK_VK_TO_VSC_EX = 4;
    const ushort VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_MASK = 0xE8;
    const ushort VK_LSHIFT = 0xA0, VK_RSHIFT = 0xA1, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    static bool Down(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    static bool Extended(ushort vk, uint sc) =>
        (sc & 0xFF00) == 0xE000 || vk is VK_LWIN or VK_RWIN or 0x21 or 0x22 or 0x23 or 0x24 or 0x25 or 0x26 or 0x27 or 0x28 or 0x2D or 0x2E or 0x6F or 0x5D;

    static INPUT Key(ushort vk, bool up, IntPtr hkl)
    {
        uint sc = vk == VK_MASK ? 0 : Native.MapVirtualKeyEx(vk, MAPVK_VK_TO_VSC_EX, hkl);
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            u = new INPUTUNION { ki = new KEYBDINPUT { wVk = vk, wScan = (ushort)(sc & 0xFF), dwFlags = (Extended(vk, sc) ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0) } },
        };
    }

    /// <summary>Sends the chord to whatever is in front now. Returns false if Windows dropped it.</summary>
    public static bool SendChord(Shortcut s, IntPtr targetWindow)
    {
        IntPtr hkl = Native.GetKeyboardLayout(Native.GetWindowThreadProcessId(targetWindow, out _));
        var inputs = new List<INPUT>();
        if (Down(VK_LWIN) || Down(VK_RWIN) || Down(VK_MENU)) { inputs.Add(Key(VK_MASK, false, hkl)); inputs.Add(Key(VK_MASK, true, hkl)); }

        // Physically held modifiers that are not part of the chord would change it (Shift held while clicking → Ctrl+Shift+S).
        var wanted = new List<ushort>();
        if (s.Mods.HasFlag(Mods.Ctrl)) wanted.Add(VK_CONTROL);
        if (s.Mods.HasFlag(Mods.Shift)) wanted.Add(VK_SHIFT);
        if (s.Mods.HasFlag(Mods.Alt)) wanted.Add(VK_MENU);
        if (s.Mods.HasFlag(Mods.Win)) wanted.Add(VK_LWIN);
        foreach (var (vk, general) in new[] { (VK_LSHIFT, VK_SHIFT), (VK_RSHIFT, VK_SHIFT), (VK_LCONTROL, VK_CONTROL), (VK_RCONTROL, VK_CONTROL), (VK_LMENU, VK_MENU), (VK_RMENU, VK_MENU) })
            if (Down(vk) && !wanted.Contains(general)) inputs.Add(Key(vk, true, hkl));

        foreach (var m in wanted) inputs.Add(Key(m, false, hkl));
        inputs.Add(Key(s.Vk, false, hkl));
        inputs.Add(Key(s.Vk, true, hkl));
        for (int i = wanted.Count - 1; i >= 0; i--) inputs.Add(Key(wanted[i], true, hkl));

        uint sent = Native.SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>());
        if (sent != inputs.Count) Log.Warn($"SendInput sent {sent}/{inputs.Count} (error {Marshal.GetLastWin32Error()})");
        return sent == inputs.Count;
    }

    /// <summary>True when a modifier is still held after we sent everything (should never stay true: tests check it).</summary>
    public static bool AnyModifierDown() => Down(VK_SHIFT) || Down(VK_CONTROL) || Down(VK_MENU) || Down(VK_LWIN) || Down(VK_RWIN);
}
