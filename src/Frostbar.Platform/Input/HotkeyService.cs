using System.Windows.Interop;
using Frostbar.Core.Menus;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Input;

/// <summary>
/// RegisterHotKey on a message-only window. Used for the global shortcuts (search) and, only while a menu is open, for
/// the menu's navigation keys: the app in front keeps focus (menus never activate) but does not see those keys.
/// The system frees the keys if we crash, unlike a low-level hook (ADR-004).
/// </summary>
public sealed class HotkeyService : IDisposable
{
    public const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;
    readonly HwndSource _source;
    readonly Dictionary<int, Action> _actions = new();
    int _next = 1;
    const int MaxId = 0xBFFF;

    public HotkeyService()
    {
        // WindowStyle 0: the default includes WS_VISIBLE, and a visible window created while we may set the foreground
        // takes it (even a message-only one), leaving the user's keystrokes going nowhere.
        _source = new HwndSource(new HwndSourceParameters("Frostbar.Hotkeys") { WindowStyle = 0, ParentWindow = new IntPtr(-3) /*HWND_MESSAGE*/, Width = 0, Height = 0 });
        _source.AddHook(Hook);
    }

    /// <returns>An id to unregister with, or null when the key is taken (by Windows or another app).</returns>
    public int? Register(uint mods, uint vk, Action action)
    {
        // An app's ids are 0x0000–0xBFFF (RegisterHotKey): a menu takes 9 at each opening, and the ids only went up, past
        // that after some thousands of openings (2026-11-06 review). Free ones are used again.
        int id = _next;
        while (_actions.ContainsKey(id)) id = id >= MaxId ? 1 : id + 1;
        _next = id >= MaxId ? 1 : id + 1;
        if (!Native.RegisterHotKey(_source.Handle, id, mods, vk)) return null;
        _actions[id] = action;
        return id;
    }

    /// <summary>A global shortcut (no auto-repeat); null when Windows or another app already has it.</summary>
    public int? Register(Shortcut s, Action action)
    {
        uint mods = MOD_NOREPEAT;
        if (s.Mods.HasFlag(Mods.Ctrl)) mods |= MOD_CONTROL;
        if (s.Mods.HasFlag(Mods.Alt)) mods |= MOD_ALT;
        if (s.Mods.HasFlag(Mods.Shift)) mods |= MOD_SHIFT;
        if (s.Mods.HasFlag(Mods.Win)) mods |= MOD_WIN;
        return Register(mods, s.Vk, action);
    }

    public void Unregister(int id)
    {
        if (_actions.Remove(id)) Native.UnregisterHotKey(_source.Handle, id);
    }

    /// <summary>When the hotkey being handled was pressed (TickCount ms; for timing what it opens).</summary>
    public int LastPressTime { get; private set; }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if ((uint)msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var a))
        {
            handled = true;
            LastPressTime = Native.GetMessageTime();
            try { a(); } catch (Exception ex) { Log.Error("hotkey action failed", ex); }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        foreach (var id in _actions.Keys.ToList()) Unregister(id);
        _source.Dispose();
    }
}
