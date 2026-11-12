using Frostbar.Core.Menus;

namespace Frostbar.Core.HotCorners;

/// <summary>
/// What a hot corner and a shortcut can do, in the order the settings page lists them. One list for the settings page
/// and the settings' validator: the validator kept its own, which lacked "appWindows" (added 2026-10-15), so a corner
/// set to it went back to its default at the next start.
/// </summary>
public static class HotCornerActions
{
    /// <summary>A corner's actions, in the order of the settings' pop-ups.</summary>
    public static readonly string[] ForCorners = ["none", "overview", "appWindows", "appGrid", "search", "desktop", "notifications", "lock"];

    /// <summary>What keys can open (macOS's Mission Control shortcuts), in the order the settings page lists them.</summary>
    public static readonly string[] ForOpenKeys = ["overview", "appWindows", "appGrid", "desktop", "notifications"];

    /// <summary>
    /// What keys can do to the window in front (stage 3-2: macOS's shortcuts of the Window menu's Move &amp; Resize, Fill
    /// and Center), in the page's order. None set by default.
    /// </summary>
    public static readonly string[] ForWindowKeys =
        ["tileLeft", "tileRight", "tileTop", "tileBottom", "tileTopLeft", "tileTopRight", "tileBottomLeft", "tileBottomRight", "fill", "center", "returnFrame"];

    /// <summary>Every action a shortcut can have.</summary>
    public static readonly string[] ForKeys = [.. ForOpenKeys, .. ForWindowKeys];

    /// <summary>The Window menu's operation a window shortcut runs (null: not one of them).</summary>
    public static string? WindowOp(string action) => action switch
    {
        "tileLeft" => WindowOps.TileLeft,
        "tileRight" => WindowOps.TileRight,
        "tileTop" => WindowOps.TileTop,
        "tileBottom" => WindowOps.TileBottom,
        "tileTopLeft" => WindowOps.TileTopLeft,
        "tileTopRight" => WindowOps.TileTopRight,
        "tileBottomLeft" => WindowOps.TileBottomLeft,
        "tileBottomRight" => WindowOps.TileBottomRight,
        "fill" => WindowOps.Fill,
        "center" => WindowOps.Center,
        "returnFrame" => WindowOps.ReturnFrame,
        _ => null,
    };

    /// <summary>Keys the shell may take from every app: only with Ctrl, Alt or Win (a key alone, or with Shift only, would be gone from all of them).</summary>
    public static bool KeysAllowed(Shortcut keys) => (keys.Mods & (Mods.Ctrl | Mods.Alt | Mods.Win)) != 0;
}
