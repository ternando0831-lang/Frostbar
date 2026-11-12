using Frostbar.Core.Localization;

namespace Frostbar.Core.Menus;

/// <summary>
/// Builds the menu bar's menus for the app in front: its app menu, the menus of its profile (or the generic File/Edit/View),
/// then Window and Help. Items that send keys or move or size the app's windows are disabled for elevated apps (UIPI refuses them).
/// </summary>
public static class MenuBuilder
{
    public static IReadOnlyList<Menu> ForApp(FrontApp app, Strings s)
    {
        var profile = Profiles.Find(app.ExeName, app.IsDesktop);
        var menus = new List<Menu> { AppMenu(app, profile, s) };
        string privateWindow = s[Profiles.PrivateWindowKey(app.ExeName)];
        menus.AddRange(profile.Menus(s).Select(m => m with { Items = m.Items.Select(i => i.Id == "new-private" ? i with { Label = privateWindow } : i).ToList() }));
        menus.Add(WindowMenu(app, s));
        if (profile.Help is { } help) menus.Add(HelpMenu(app, help, s)); // none where F1 is something else
        return app.Elevated ? menus.Select(m => m with { Items = DisableForElevated(m.Items, s) }).ToList() : menus;
    }

    /// <summary>
    /// What does nothing to an app running as administrator (UIPI), disabled with the reason: keys, and the operations
    /// that move or size its windows (zoom, fill, centre, tile, bring all to front: each call was refused and nothing
    /// happened, measured 2026-10-12 with the elevated test target). Minimize, Hide and Quit stay: its windows take
    /// their window menu's Minimize and Close (<see cref="WindowOps.ReachesElevated"/>). So do its About and the list
    /// of its windows.
    /// </summary>
    static IReadOnlyList<MenuItem> DisableForElevated(IReadOnlyList<MenuItem> items, Strings s) =>
        items.Select(i => Refused(i.Action) is { } why ? i with { Enabled = false, Hint = s[why] }
            : i.Children != null ? Submenu(i, DisableForElevated(i.Children, s)) : i).ToList();

    /// <summary>A submenu all of whose items are refused is refused as well, for their reason (Move &amp; Resize).</summary>
    static MenuItem Submenu(MenuItem i, IReadOnlyList<MenuItem> children) =>
        children.Where(c => !c.IsSeparator).All(c => !c.Enabled) && children.FirstOrDefault(c => c.Hint != null) is { } refused
            ? i with { Children = children, Enabled = false, Hint = refused.Hint }
            : i with { Children = children };

    static string? Refused(MenuAction a) => a.Kind switch
    {
        ActionKind.Keys => "app.elevated",
        ActionKind.Window when !WindowOps.ReachesElevated(a.Name) => "app.elevatedWindows",
        _ => null,
    };

    /// <summary>
    /// macOS's Apple menu: About, the settings and the store, Frostbar's own settings and quitting it (the user's "アプリってのは
    /// このMac OSね", 2026-10-28: the shell had no way out in its menus), Recent Items (<paramref name="recent"/>, its own
    /// section as on macOS; none without it), Force Quit, the power items, Lock and Log Out.
    /// </summary>
    public static Menu SystemMenu(Strings s, MenuItem? recent = null) => new("system", "", Tidy(
    [
        Sys("about-pc", s["sys.aboutPc"], SystemOps.AboutPc),
        MenuItem.Separator,
        Sys("settings", s["sys.settings"], SystemOps.Settings),
        Sys("store", s["sys.store"], SystemOps.Store),
        Sys("shell-settings", s["sys.shellSettings"], SystemOps.ShellSettings),
        Sys("quit-shell", s["sys.quitShell"], SystemOps.QuitShell),
        MenuItem.Separator,
        .. recent is { } r ? [r, MenuItem.Separator] : Array.Empty<MenuItem>(),
        Sys("force-quit", s["sys.forceQuit"], SystemOps.ForceQuit, "Ctrl+Alt+Esc"),
        MenuItem.Separator,
        Sys("sleep", s["sys.sleep"], SystemOps.Sleep),
        Sys("restart", s["sys.restart"], SystemOps.Restart),
        Sys("shutdown", s["sys.shutdown"], SystemOps.Shutdown),
        MenuItem.Separator,
        Sys("lock", s["sys.lock"], SystemOps.Lock, "Win+L"),
        Sys("logout", s["sys.logout"], SystemOps.Logout),
    ]));

    static MenuItem Sys(string id, string label, string op, string? shown = null) =>
        new(id, label, Shortcut.Parse(shown), MenuAction.System(op));

    /// <summary>
    /// The app's name menu. "Settings…" only where the app opens its settings with keys (Ctrl+, in the editors, Terminal,
    /// Obsidian, Discord, Bipane; not in browsers or Office). Quit closes every window of the app, so it shows no keys
    /// (Alt+F4 closes one).
    /// </summary>
    static Menu AppMenu(FrontApp app, AppProfile profile, Strings s) => new("app", app.DisplayName, Tidy(
    [
        new("about-app", s.F("app.about", app.DisplayName), null, MenuAction.App(AppOps.About)),
        MenuItem.Separator,
        .. profile.Settings is { } settings ? [Keys("settings", s["app.settings"], settings)] : Array.Empty<MenuItem>(),
        MenuItem.Separator,
        new("hide", s.F("app.hide", app.DisplayName), null, MenuAction.App(AppOps.Hide), Enabled: !app.IsDesktop),
        new("hide-others", s["app.hideOthers"], null, MenuAction.App(AppOps.HideOthers)),
        new("show-all", s["app.showAll"], null, MenuAction.App(AppOps.ShowAll)),
        MenuItem.Separator,
        new("quit", s.F("app.quit", app.DisplayName), null, MenuAction.App(AppOps.Quit), Enabled: !app.IsDesktop),
    ]), Bold: true);

    /// <summary>
    /// macOS's Window menu (15): Minimize, Zoom, Fill, Center, then Move &amp; Resize with the halves, the quarters and
    /// Return to Previous Size (only while the window in front has a frame kept from before), Bring All to Front, and
    /// the app's windows. Each item's id is its operation's name.
    /// </summary>
    static Menu WindowMenu(FrontApp app, Strings s)
    {
        bool any = app.Windows.Count > 0;
        MenuItem W(string op, string label, bool enabled = true) => new(op, label, null, MenuAction.Window(op), Enabled: any && enabled);
        var items = new List<MenuItem>
        {
            W(WindowOps.Minimize, s["win.minimize"]),
            W(WindowOps.Zoom, s["win.zoom"]),
            W(WindowOps.Fill, s["win.fill"]),
            W(WindowOps.Center, s["win.center"]),
            MenuItem.Separator,
            new("move-resize", s["win.moveResize"], null, MenuAction.Nothing, Enabled: any, Children: Tidy(
            [
                W(WindowOps.TileLeft, s["win.left"]),
                W(WindowOps.TileRight, s["win.right"]),
                W(WindowOps.TileTop, s["win.top"]),
                W(WindowOps.TileBottom, s["win.bottom"]),
                MenuItem.Separator,
                W(WindowOps.TileTopLeft, s["win.topLeft"]),
                W(WindowOps.TileTopRight, s["win.topRight"]),
                W(WindowOps.TileBottomLeft, s["win.bottomLeft"]),
                W(WindowOps.TileBottomRight, s["win.bottomRight"]),
                MenuItem.Separator,
                W(WindowOps.ReturnFrame, s["win.returnFrame"], app.FrameKept),
            ])),
            MenuItem.Separator,
            W(WindowOps.FrontAll, s["win.front"]),
            MenuItem.Separator,
        };
        foreach (var w in app.Windows.Take(20))
            items.Add(new($"window-{w.Hwnd}", string.IsNullOrWhiteSpace(w.Title) ? s["win.untitled"] : w.Title, null,
                MenuAction.Window(WindowOps.ActivatePrefix + w.Hwnd), Checked: w.Active));
        return new Menu("window", s["menu.window"], Tidy(items));
    }

    static Menu HelpMenu(FrontApp app, string keys, Strings s) =>
        new("help", s["menu.help"], [Keys("help", s.F("help.app", app.DisplayName), keys)]);

    internal static MenuItem Keys(string id, string label, string shortcut)
    {
        var sc = Shortcut.Parse(shortcut) ?? throw new ArgumentException("bad shortcut " + shortcut);
        return new MenuItem(id, label, sc, MenuAction.Send(sc));
    }

    /// <summary>No separator at either end, never two in a row.</summary>
    public static IReadOnlyList<MenuItem> Tidy(IEnumerable<MenuItem> items)
    {
        var list = new List<MenuItem>();
        foreach (var i in items)
        {
            if (i.IsSeparator && (list.Count == 0 || list[^1].IsSeparator)) continue;
            list.Add(i);
        }
        while (list.Count > 0 && list[^1].IsSeparator) list.RemoveAt(list.Count - 1);
        return list;
    }
}
