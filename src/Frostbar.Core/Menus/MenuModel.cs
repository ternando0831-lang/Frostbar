namespace Frostbar.Core.Menus;

public enum ActionKind { None, Keys, Window, App, System, Open }

/// <summary>
/// What a menu item does: send a key chord to the app, a window operation, an app operation, a system one, or open a
/// program, a document or an AppsFolder entry (Name is what to open: the system menu's Recent Items).
/// </summary>
public sealed record MenuAction(ActionKind Kind, string Name, Shortcut? Keys = null)
{
    public static readonly MenuAction Nothing = new(ActionKind.None, "");
    public static MenuAction Send(Shortcut s) => new(ActionKind.Keys, "keys", s);
    public static MenuAction Window(string op) => new(ActionKind.Window, op);
    public static MenuAction App(string op) => new(ActionKind.App, op);
    public static MenuAction System(string op) => new(ActionKind.System, op);
    public static MenuAction Open(string target) => new(ActionKind.Open, target);
}

/// <param name="Icon">Where a small icon before the label comes from (a program, a document, an AppsFolder entry); null: none.</param>
public sealed record MenuItem(string Id, string Label, Shortcut? Shortcut, MenuAction Action,
    bool Enabled = true, bool Checked = false, IReadOnlyList<MenuItem>? Children = null, string? Hint = null, string? Icon = null)
{
    public const string SeparatorId = "-";
    public static readonly MenuItem Separator = new(SeparatorId, "", null, MenuAction.Nothing, Enabled: false);
    public bool IsSeparator => Id == SeparatorId;
}

public sealed record Menu(string Id, string Title, IReadOnlyList<MenuItem> Items, bool Bold = false);

/// <summary>One window of the front app, for the Window menu's list.</summary>
public sealed record AppWindowInfo(long Hwnd, string Title, bool Active, bool Minimized);

/// <summary>What the menu bar knows about the app in front.</summary>
/// <param name="FrameKept">Its window has a frame from before Move &amp; Resize to return to (<see cref="Wm.TileMemory"/>).</param>
public sealed record FrontApp(string DisplayName, string ExeName, bool Elevated, IReadOnlyList<AppWindowInfo> Windows, bool IsDesktop = false,
    bool FrameKept = false);

/// <summary>Window operations understood by the platform layer (MenuAction.Window names).</summary>
public static class WindowOps
{
    public const string Minimize = "minimize", Zoom = "zoom", Fill = "fill", Center = "center",
        TileLeft = "tile-left", TileRight = "tile-right", TileTop = "tile-top", TileBottom = "tile-bottom",
        TileTopLeft = "tile-top-left", TileTopRight = "tile-top-right", TileBottomLeft = "tile-bottom-left", TileBottomRight = "tile-bottom-right",
        ReturnFrame = "return-frame", FrontAll = "front-all", ActivatePrefix = "activate:";

    /// <summary>Where an operation of Move &amp; Resize (with Fill and Center) puts the window; null for the others.</summary>
    public static Wm.TilePlace? Place(string op) => op switch
    {
        TileLeft => Wm.TilePlace.Left,
        TileRight => Wm.TilePlace.Right,
        TileTop => Wm.TilePlace.Top,
        TileBottom => Wm.TilePlace.Bottom,
        TileTopLeft => Wm.TilePlace.TopLeft,
        TileTopRight => Wm.TilePlace.TopRight,
        TileBottomLeft => Wm.TilePlace.BottomLeft,
        TileBottomRight => Wm.TilePlace.BottomRight,
        Fill => Wm.TilePlace.Fill,
        Center => Wm.TilePlace.Center,
        _ => null,
    };

    /// <summary>
    /// What an app running as administrator lets us do to its window (UIPI): Minimize, which it takes as its window
    /// menu's command (SC_MINIMIZE), and bringing one of its windows forward. Not the moves and sizes nor Zoom (its
    /// window refuses SetWindowPos, ShowWindowAsync and SC_MAXIMIZE), nor Bring All to Front (it puts them in order with
    /// SetWindowPos). Measured with elevated-ops (2026-10-16); Minimize let through since the user's choice of 2026-10-22.
    /// </summary>
    public static bool ReachesElevated(string op) => op == Minimize || op.StartsWith(ActivatePrefix, StringComparison.Ordinal);
}

/// <summary>App operations (MenuAction.App names).</summary>
public static class AppOps
{
    public const string About = "about-app", Hide = "hide", HideOthers = "hide-others", ShowAll = "show-all", Quit = "quit";
}

/// <summary>System operations (MenuAction.System names).</summary>
public static class SystemOps
{
    public const string AboutPc = "about-pc", Settings = "settings", Store = "store", ForceQuit = "force-quit", Sleep = "sleep",
        Restart = "restart", Shutdown = "shutdown", Lock = "lock", Logout = "logout", ShellSettings = "shell-settings",
        QuitShell = "quit-shell", ClearRecent = "clear-recent";
}
