using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Frostbar.App.Common;
using Frostbar.App.MenuBar;
using Frostbar.Core.Menus;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Input;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Shell;
using Frostbar.Platform.Sys;
using Menu = Frostbar.Core.Menus.Menu;

namespace Frostbar.App.Menus;

/// <summary>
/// Opening, switching and closing the menu bar's menus, macOS style: click a title to open, move along the bar to switch,
/// click outside or Esc to close, arrows/Enter to navigate. Menus never take focus; while one is open the navigation keys
/// are taken with RegisterHotKey and outside clicks land on the click shield (ADR-004).
/// </summary>
public sealed class MenuController
{
    readonly MenuBarWindow _bar;
    readonly HotkeyService _hotkeys;
    readonly Func<IReadOnlyList<Menu>> _appMenus;
    readonly Func<Menu> _systemMenu;
    readonly Func<Menu, MenuPopup> _makePopup;
    readonly Action<MenuItem> _execute;
    readonly Dispatcher _ui;
    readonly List<int> _keyIds = new();
    readonly ClickShields _shields = new();
    IReadOnlyList<Menu>? _menus;
    MenuPopup? _popup;
    int _open = int.MinValue;
    /// <summary>Time spent in <see cref="Open"/> (building and showing the menu).</summary>
    public long LastOpenMs { get; private set; }
    /// <summary>From the click or hover message to the first frame rendered with the menu; -1 when opened from code.</summary>
    public int LastInputToFrameMs { get; private set; } = -1;

    public MenuController(MenuBarWindow bar, HotkeyService hotkeys, Func<IReadOnlyList<Menu>> appMenus, Func<Menu> systemMenu,
        Func<Menu, MenuPopup> makePopup, Action<MenuItem> execute, Dispatcher ui)
    {
        _bar = bar; _hotkeys = hotkeys; _appMenus = appMenus; _systemMenu = systemMenu; _makePopup = makePopup; _execute = execute; _ui = ui;
        bar.TitlePressed += i => { if (_open == i) Close(); else Open(i); };
        bar.TitleEntered += i => { if (IsOpen && i != _open) Open(i); };
        bar.BackgroundPressed += Close;
        _shields.Clicked += Close;
    }

    public bool IsOpen => _popup != null;
    public int OpenIndex => _open;
    public MenuPopup? Popup => _popup;

    /// <summary>
    /// A menu or a panel is about to open (not a switch between menus): the Dock's menus close first, so that their
    /// keys (arrows, Enter, Space, Esc: registered while open) do not stay with them (2026-10-12).
    /// </summary>
    public event Action? Opening;

    /// <summary>-1 opens the system menu, 0 the app menu, then the app's menus.</summary>
    public void Open(int index)
    {
        var sw = Stopwatch.StartNew();
        int input = _bar.LastInputTime;
        _bar.LastInputTime = 0;
        // Message → this handler on the message clock (~16 ms steps); from here on, precisely.
        int queued = input != 0 ? Math.Max(0, Environment.TickCount - input) : 0;
        // A status item's panel open (the Control Center): the menu takes its place. It stayed open beside the menu and
        // kept the arrows and Enter (the menu's keys could not be registered): Enter meant for the menu ran a tile.
        if (_panel != null) Close();
        bool first = !IsOpen;
        if (first) Opening?.Invoke();
        _menus ??= _appMenus();
        if (index >= _menus.Count) return;
        Fades.CutShort(); // a menu still fading out goes at once (drawing it slowed this one)
        ClosePopup();
        var menu = index < 0 ? _systemMenu() : _menus[index];
        // Opened again while this one is being made (making its glass can let messages through, as the volume display's
        // did on 2026-10-28): that one is not made, or the one made first would take its place and leave it open for good.
        if (_making) return;
        _making = true;
        MenuPopup popup;
        try { popup = _makePopup(menu); }
        finally { _making = false; }
        popup.Hovered += i => Hover(popup, i);
        popup.Clicked += i => Activate(popup, i);
        var title = _bar.TitleRect(index);
        // Within the bar's monitor (the virtual screen's right let a menu run onto a monitor on the right).
        double screenRight = _bar.RightDip;
        _popup = popup;
        _open = index;
        _bar.SetOpen(index);
        if (first)
        {
            _shields.Show(Native.FrameBounds(_bar.Handle));
            RegisterKeys();
        }
        var mon = Monitors.Of(_bar.Handle);
        double bottom = (mon?.Work ?? Native.WorkArea()).Bottom / (mon?.Scale ?? 1); // taller than the room left, it scrolls
        popup.ShowAt(title.IsEmpty ? _bar.Left : title.Left, _bar.BottomDip + 1, screenRight, bottom);
        RaiseAboveShield();
        LastOpenMs = sw.ElapsedMilliseconds;
        LastInputToFrameMs = -1;
        if (input != 0)
        {
            EventHandler? frame = null;
            frame = (_, _) =>
            {
                System.Windows.Media.CompositionTarget.Rendering -= frame;
                LastInputToFrameMs = queued + (int)sw.ElapsedMilliseconds;
            };
            System.Windows.Media.CompositionTarget.Rendering += frame;
        }
        Log.Debug($"menu {menu.Id} opened in {LastOpenMs} ms");
    }

    /// <summary>
    /// Opens and closes a menu once off-screen after start-up, so the first real click does not pay for JIT
    /// compilation and for creating the click shields.
    /// </summary>
    public void Warm()
    {
        if (IsOpen || _panel != null) return;
        try
        {
            var sw = Stopwatch.StartNew();
            var popup = _makePopup(_systemMenu());
            popup.ShowAt(-32000, -32000, double.MaxValue);
            popup.Close();
            _shields.Show(Native.FrameBounds(_bar.Handle));
            _shields.Hide();
            Log.Debug($"menus warmed up in {sw.ElapsedMilliseconds} ms");
        }
        catch (Exception ex) { Log.Warn("menu warm-up failed", ex); }
    }

    /// <summary>Keeps the bar and the open menus above the shield (shown later, it would cover them).</summary>
    void RaiseAboveShield()
    {
        foreach (var h in new[] { _bar.BackdropHandle, _bar.Handle, _popup?.BackdropHandle ?? IntPtr.Zero, _popup?.Handle ?? IntPtr.Zero,
                     _child?.BackdropHandle ?? IntPtr.Zero, _child?.Handle ?? IntPtr.Zero })
            if (h != IntPtr.Zero) Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    void RegisterKeys()
    {
        void K(uint vk, Action a) { if (_hotkeys.Register(0, vk, a) is int id) _keyIds.Add(id); }
        K(0x26 /*Up*/, () => Move(-1));
        K(0x28 /*Down*/, () => Move(+1));
        K(0x25 /*Left*/, Left);
        K(0x27 /*Right*/, Right);
        K(0x0D /*Enter*/, () => { if (Keyed is { } p) Activate(p, p.Highlight); });
        K(0x20 /*Space*/, () => { if (Keyed is { } p) Activate(p, p.Highlight); });
        K(0x1B /*Esc*/, Close);
        K(0x24 /*Home*/, () => { if (Keyed is { } p) p.SetHighlight(p.Next(-1, +1)); });
        K(0x23 /*End*/, () => { if (Keyed is { } p) p.SetHighlight(p.Next(p.Items.Count, -1)); });
    }

    void Move(int step)
    {
        if (Keyed is not { } p) return;
        p.SetHighlight(p.Next(p.Highlight, step));
        // The keys moved off the open submenu's row: it closes at once. Onto a submenu's row they open nothing (→ does, macOS).
        if (p == _popup && _child != null && p.Highlight != _childRow) CloseChild();
        if (p == _popup) Due(-2, 0);
    }

    // ---- submenus (the Window menu's Move & Resize): beside their row, open while the pointer or the keys are there ----

    /// <summary>How long the pointer rests on a submenu's row before it opens, and how long another row of the menu waits
    /// before closing an open submenu (the pointer crosses rows on its way into it, as on macOS).</summary>
    public const int SubmenuOpenMs = 120, SubmenuCloseMs = 250;
    MenuPopup? _child;
    int _childRow = -1;
    DispatcherTimer? _submenuTimer;
    int _submenuDue = -2; // what the timer does: open that row's submenu (>= 0), close the open one (-1)

    /// <summary>The open submenu (diagnostics).</summary>
    public MenuPopup? Child => _child;

    /// <summary>The menu the keys act on: the submenu once one of its rows is lit, else the menu.</summary>
    MenuPopup? Keyed => _child is { Highlight: >= 0 } c ? c : _popup;

    void Hover(MenuPopup popup, int i)
    {
        popup.SetHighlight(i);
        if (popup == _popup) AfterHighlight();
    }

    /// <summary>The menu's lit row changed: its submenu opens after a moment, another row closes the open one after a moment.</summary>
    void AfterHighlight()
    {
        if (_popup == null) return;
        int i = _popup.Highlight;
        if (_child != null && i == _childRow) { Due(-2, 0); return; } // back on it: it stays
        if (_popup.HasChildren(i)) Due(i, _child != null ? SubmenuCloseMs : SubmenuOpenMs);
        else if (_child != null) Due(-1, SubmenuCloseMs);
        else Due(-2, 0);
    }

    void Due(int what, int ms)
    {
        _submenuDue = what;
        if (_submenuTimer == null)
        {
            _submenuTimer = new DispatcherTimer(DispatcherPriority.Input, _ui);
            _submenuTimer.Tick += (_, _) =>
            {
                _submenuTimer.Stop();
                int due = _submenuDue;
                _submenuDue = -2;
                if (due >= 0) OpenChild(due, keyboard: false);
                else if (due == -1) CloseChild();
            };
        }
        _submenuTimer.Stop();
        if (what == -2) return;
        _submenuTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _submenuTimer.Start();
    }

    /// <summary>Opens row <paramref name="row"/>'s submenu beside it (with the keys: its first row lit).</summary>
    void OpenChild(int row, bool keyboard)
    {
        if (_popup == null || !_popup.HasChildren(row) || !_popup.Selectable(row)) return;
        Due(-2, 0);
        if (_child != null && _childRow == row)
        {
            if (keyboard && _child.Highlight < 0) _child.SetHighlight(_child.Next(-1, +1));
            return;
        }
        CloseChild();
        var parent = _popup;
        var item = parent.Items[row];
        if (_making) return; // as Open: one made at a time
        _making = true;
        MenuPopup child;
        try { child = _makePopup(new Menu(parent.Menu.Id + "/" + item.Id, item.Label, item.Children!)); }
        finally { _making = false; }
        child.Hovered += i =>
        {
            child.SetHighlight(i);
            Due(-2, 0); // in the submenu: it stays, and its row stays lit
            parent.SetHighlight(row);
        };
        child.MouseEnter += (_, _) => { Due(-2, 0); parent.SetHighlight(row); };
        child.Clicked += i => Activate(child, i);
        _child = child;
        _childRow = row;
        parent.OpenChild = row;
        parent.SetHighlight(row);
        var mon = Monitors.Of(_bar.Handle);
        double s = mon?.Scale ?? 1;
        var work = mon?.Work ?? Native.WorkArea();
        child.ShowBeside(parent, parent.RowDip(row), new Rect(work.Left / s, work.Top / s, work.Width / s, work.Height / s));
        RaiseAboveShield();
        if (keyboard) child.SetHighlight(child.Next(-1, +1));
    }

    void CloseChild()
    {
        if (_child == null) return;
        _child.Close();
        _child = null;
        if (_popup != null) _popup.OpenChild = -1;
        _childRow = -1;
    }

    /// <summary>→: into the lit row's submenu; elsewhere the next menu of the bar.</summary>
    void Right()
    {
        if (Keyed == _popup && _popup != null && _popup.HasChildren(_popup.Highlight)) OpenChild(_popup.Highlight, keyboard: true);
        else Switch(+1);
    }

    /// <summary>←: out of the submenu (its row stays lit); elsewhere the menu before on the bar.</summary>
    void Left()
    {
        if (_child != null && Keyed == _child) CloseChild();
        else Switch(-1);
    }

    void Switch(int step)
    {
        if (!IsOpen || _menus == null) return;
        int count = _menus.Count;
        int next = _open + step;
        if (next < -1) next = count - 1;
        if (next >= count) next = -1;
        Open(next);
        _popup?.SetHighlight(_popup.Next(-1, +1));
    }

    bool _choosing, _making;

    void Activate(MenuPopup popup, int index)
    {
        if (!popup.Selectable(index) || _choosing) return;
        // A submenu's row opens it (with the keys: its first row lit); it runs nothing itself.
        if (popup == _popup && popup.HasChildren(index)) { OpenChild(index, keyboard: true); return; }
        var item = popup.Items[index];
        _choosing = true;
        // The chosen row blinks, the menu fades out, then the command runs (macOS).
        popup.Blink(index, () =>
        {
            _choosing = false;
            if (popup != _popup && popup != _child) return; // closed or switched meanwhile
            Close();
            // After the menu and shield are gone (the target must be in front again before keys are sent).
            _ui.BeginInvoke(DispatcherPriority.Background, () => _execute(item));
        });
    }

    void ClosePopup()
    {
        Due(-2, 0);
        CloseChild();
        _popup?.Close();
        _popup = null;
    }

    PanelPopup? _panel;
    public PanelPopup? Panel => _panel;

    /// <summary>A status item's panel (volume, battery): same outside-click and Esc handling as a menu.</summary>
    public void OpenPanel(PanelPopup panel, Rect anchorDip)
    {
        _bar.LastInputTime = 0; // the status item's click: its owner has timed the opening
        Opening?.Invoke();
        Close();
        Fades.CutShort(); // what was open goes at once: this panel takes its place
        _panel = panel;
        _open = int.MaxValue;
        _shields.Show(Native.FrameBounds(_bar.Handle));
        if (_hotkeys.Register(0, 0x1B /*Esc*/, Close) is int id) _keyIds.Add(id);
        // A panel with parts the keys can reach (KeyNav, "D2"): the arrows, Tab, Space and Enter while it is open, as a
        // menu takes them (Esc was its only key).
        // They go to the panel open when pressed: one swapped in for a theme change (SwapPanel) takes them over (they
        // went on to the closed one, and the keys moved a ring on a window no longer shown).
        if (panel.TakesKeys)
        {
            void K(uint mods, uint vk, Action a) { if (_hotkeys.Register(mods, vk, a) is int k) _keyIds.Add(k); }
            K(0, 0x25 /*Left*/, () => _panel?.KeyMove(-1, 0));
            K(0, 0x27 /*Right*/, () => _panel?.KeyMove(1, 0));
            K(0, 0x26 /*Up*/, () => _panel?.KeyMove(0, -1));
            K(0, 0x28 /*Down*/, () => _panel?.KeyMove(0, 1));
            K(0, 0x09 /*Tab*/, () => _panel?.KeyStep(1));
            K(0x0004 /*MOD_SHIFT*/, 0x09, () => _panel?.KeyStep(-1));
            K(0, 0x0D /*Enter*/, () => _panel?.KeyRun());
            K(0, 0x20 /*Space*/, () => _panel?.KeyRun());
        }
        double screenRight = _bar.RightDip; // within the bar's monitor
        // A status item not shown (the bar hidden over a full-screen app, as a hot corner opens the notification centre)
        // has an empty rectangle: the panel stands at the bar's right end. Its left was +∞, which WPF refuses with an
        // exception that quit the shell (2026-10-14 review).
        panel.Left = anchorDip.IsEmpty ? screenRight : anchorDip.Left;
        panel.Top = _bar.BottomDip + 1;
        // Where it will stand, before it fades in: a panel of pieces grows about its item, and placed at the item's left
        // and moved in from the screen's edge only after it showed, it began growing about its own left edge (2026-10-18,
        // cc-motion).
        if (panel.Content is FrameworkElement content)
        {
            content.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double width = content.DesiredSize.Width;
            if (width > 0 && panel.Left + width > screenRight - 4) panel.Left = screenRight - width - 4;
        }
        panel.AnchorDip = anchorDip; // a panel of pieces grows out of it (Fades: materializing)
        panel.FadeIn(); // before it shows (Fades.FadeIn)
        panel.Show();
        panel.UpdateLayout();
        if (panel.Left + panel.ActualWidth > screenRight - 4) panel.Left = screenRight - panel.ActualWidth - 4;
        foreach (var h in new[] { _bar.BackdropHandle, _bar.Handle, panel.BackdropHandle, panel.Handle })
            if (h != IntPtr.Zero) Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>
    /// The open panel replaced by <paramref name="panel"/> (the same panel in the new colours of a theme change) where it
    /// stands, shown whole at once; the old one closes once the new one has drawn, so no frame shows neither. It went
    /// back into its item and grew out again (2026-10-19, the user's "A4"). False: no panel open to replace.
    /// </summary>
    public bool SwapPanel(PanelPopup panel)
    {
        if (_panel is not { } old) return false;
        _panel = panel; // Esc and an outside click close this one now
        panel.AppearAtOnce = true;
        panel.AnchorDip = old.AnchorDip;
        panel.Left = old.Left;
        panel.Top = old.Top;
        panel.Show();
        panel.UpdateLayout();
        double screenRight = _bar.RightDip;
        if (panel.Left + panel.ActualWidth > screenRight - 4) panel.Left = screenRight - panel.ActualWidth - 4;
        foreach (var h in new[] { _bar.BackdropHandle, _bar.Handle, panel.BackdropHandle, panel.Handle })
            if (h != IntPtr.Zero) Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        // After WPF's render pass (the Loaded priority): the new one drawn.
        panel.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, old.Close);
        return true;
    }

    public void Close()
    {
        if (!IsOpen && _panel == null && _open == int.MinValue) return;
        foreach (var id in _keyIds) _hotkeys.Unregister(id);
        _keyIds.Clear();
        // Closing for good fades out (switching to another menu does not: ClosePopup), the submenu with it.
        Due(-2, 0);
        _child?.CloseFading();
        _child = null;
        _childRow = -1;
        _popup?.CloseFading();
        _popup = null;
        _panel?.CloseFading();
        _panel = null;
        _shields.Hide();
        _open = int.MinValue;
        _menus = null;
        _bar.SetOpen(null);
        _bar.SetStatusOpen(null);
        Closed?.Invoke();
    }

    /// <summary>The menus and panels are all closed for good (not one switched for another).</summary>
    public event Action? Closed;

    /// <summary>Closed for good with its bar (a bar made again): its click shields too.</summary>
    public void Dispose()
    {
        Close();
        _shields.Dispose();
    }
}
