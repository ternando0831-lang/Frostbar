using System.Windows;
using System.Windows.Threading;
using Frostbar.Core.AppGrid;
using Frostbar.Core.Dock;
using Frostbar.Platform.Input;
using Frostbar.Platform.Interop;
using MenuItem = Frostbar.Core.Menus.MenuItem;

namespace Frostbar.App.Menus;

/// <summary>
/// A menu opened from a point rather than from the menu bar (the Dock's and the app grid's right-click menus): the same
/// never-activating popup, click shields and navigation keys as the bar's menus (ADR-004), placed above its anchor
/// (beside it when the Dock stands on a side; at the pointer in the app grid) and kept on screen.
/// </summary>
public sealed class PopupMenuSession
{
    readonly HotkeyService _hotkeys;
    readonly Func<RECT> _barRect;
    readonly ClickShields _shields = new();
    readonly List<int> _keys = new();
    MenuPopup? _popup;
    PanelPopup? _panel;
    Action<MenuItem>? _run;

    public bool IsOpen => _popup != null || _panel != null;
    public MenuPopup? Popup => _popup;
    /// <summary>A panel shown the same way (a folder of the Dock opened as a stack).</summary>
    public PanelPopup? Panel => _panel;
    public event Action? Closed;
    /// <summary>A menu or a panel is about to open here: the menu bar's close first (their keys would stay with them).</summary>
    public event Action? Opening;

    /// <summary>
    /// Shows <paramref name="panel"/> by the Dock's item at <paramref name="anchor"/> (DIPs on the screen): centred above it
    /// at the bottom, beside it on a side (<see cref="DockFrame.Beside"/>), like a menu: outside clicks and Esc close it.
    /// </summary>
    public void ShowPanelBeside(PanelPopup panel, DockEdge edge, Rect anchor, Rect screen)
    {
        Opening?.Invoke();
        Close(fade: false);
        Common.Fades.CutShort();
        _panel = panel;
        _shields.Show(_barRect());
        panel.Left = -32000; // measured off-screen first
        panel.Top = -32000;
        panel.FadeIn(); // before it shows (Fades.FadeIn)
        panel.Show();
        panel.UpdateLayout();
        (panel.Left, panel.Top) = Beside(edge, anchor, panel.ActualWidth, panel.ActualHeight, screen);
        foreach (var hwnd in new[] { panel.BackdropHandle, panel.Handle })
            if (hwnd != IntPtr.Zero) Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        panel.Rise(edge); // a stack comes out of the Dock
        if (_hotkeys.Register(0, 0x1B /*Esc*/, Close) is int id) _keys.Add(id);
        // Its items reachable with the keys (KeyNav, "D2"), as the bar's panels.
        if (!panel.TakesKeys) return;
        void K(uint mods, uint vk, Action a) { if (_hotkeys.Register(mods, vk, a) is int k) _keys.Add(k); }
        K(0, 0x25 /*Left*/, () => panel.KeyMove(-1, 0));
        K(0, 0x27 /*Right*/, () => panel.KeyMove(1, 0));
        K(0, 0x26 /*Up*/, () => panel.KeyMove(0, -1));
        K(0, 0x28 /*Down*/, () => panel.KeyMove(0, 1));
        K(0, 0x09 /*Tab*/, () => panel.KeyStep(1));
        K(0x0004 /*MOD_SHIFT*/, 0x09, () => panel.KeyStep(-1));
        K(0, 0x0D /*Enter*/, panel.KeyRun);
        K(0, 0x20 /*Space*/, panel.KeyRun);
    }

    /// <param name="barRect">The menu bar's rectangle (the shields leave it uncovered).</param>
    public PopupMenuSession(HotkeyService hotkeys, Func<RECT> barRect)
    {
        _hotkeys = hotkeys;
        _barRect = barRect;
        _shields.Clicked += Close;
    }

    static (double, double) Beside(DockEdge edge, Rect anchor, double w, double h, Rect screen) =>
        DockFrame.Beside(edge, new Box(anchor.X, anchor.Y, anchor.Width, anchor.Height), w, h, new Box(screen.X, screen.Y, screen.Width, screen.Height));

    /// <summary>
    /// Shows the menu by the Dock's item at <paramref name="anchor"/> (DIPs on the screen): centred above it at the
    /// bottom, beside it on a side (<see cref="DockFrame.Beside"/>).
    /// </summary>
    public void ShowBeside(MenuPopup popup, DockEdge edge, Rect anchor, Rect screen, Action<MenuItem> run) =>
        Show(popup, (w, h) => Beside(edge, anchor, w, h, screen), run);

    /// <summary>
    /// Shows the menu at the pointer, <paramref name="at"/> (DIPs on the screen: the app grid's right-click menus), as
    /// macOS's context menus: its top left corner there, turned to the left or upwards to stay on <paramref name="screen"/>.
    /// </summary>
    public void ShowAt(MenuPopup popup, Point at, Rect screen, Action<MenuItem> run) =>
        Show(popup, (w, h) => GridMenu.At(at.X, at.Y, w, h, new Box(screen.X, screen.Y, screen.Width, screen.Height)), run);

    void Show(MenuPopup popup, Func<double, double, (double, double)> place, Action<MenuItem> run)
    {
        Opening?.Invoke();
        Close(fade: false); // another menu in its place: at once, as the menu bar switches menus
        Common.Fades.CutShort(); // and one still fading out goes too (drawing it slowed the new one)
        _popup = popup;
        _run = run;
        popup.Hovered += i => popup.SetHighlight(i);
        popup.Clicked += Activate;
        _shields.Show(_barRect());
        popup.ShowAt(-32000, -32000, double.MaxValue); // measure off-screen first
        (popup.Left, popup.Top) = place(popup.ActualWidth, popup.ActualHeight);
        foreach (var hwnd in new[] { popup.BackdropHandle, popup.Handle })
            if (hwnd != IntPtr.Zero) Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        void K(uint vk, Action a) { if (_hotkeys.Register(0, vk, a) is int id) _keys.Add(id); }
        K(0x26 /*Up*/, () => Move(-1));
        K(0x28 /*Down*/, () => Move(+1));
        K(0x0D /*Enter*/, () => { if (_popup != null) Activate(_popup.Highlight); });
        K(0x20 /*Space*/, () => { if (_popup != null) Activate(_popup.Highlight); });
        K(0x1B /*Esc*/, Close);
    }

    void Move(int step)
    {
        if (_popup != null) _popup.SetHighlight(_popup.Next(_popup.Highlight, step));
    }

    bool _choosing;

    /// <summary>The chosen item blinks (as macOS's do), then the menu fades out and the item runs.</summary>
    void Activate(int index)
    {
        if (_popup == null || _choosing || !_popup.Selectable(index)) return;
        var popup = _popup;
        var item = popup.Items[index];
        var run = _run;
        var dispatcher = popup.Dispatcher;
        _choosing = true;
        popup.Blink(index, () =>
        {
            _choosing = false;
            if (_popup != popup) return; // closed meanwhile
            Close();
            if (run != null) dispatcher.BeginInvoke(DispatcherPriority.Background, () => run(item));
        });
    }

    public void Close() => Close(fade: true);

    /// <param name="fade">Fade out as macOS menus do (false: at once).</param>
    void Close(bool fade)
    {
        if (_popup == null && _panel == null) return;
        foreach (var id in _keys) _hotkeys.Unregister(id);
        _keys.Clear();
        if (_popup != null) { if (fade) _popup.CloseFading(); else _popup.Close(); }
        if (_panel != null) { if (fade) _panel.CloseFading(); else _panel.Close(); }
        _popup = null;
        _panel = null;
        _choosing = false;
        _shields.Hide();
        Closed?.Invoke();
    }

    /// <summary>Closed for good with its owner (the Dock or the app grid made again): its click shields too.</summary>
    public void Dispose()
    {
        Close(fade: false);
        _shields.Dispose();
    }
}
