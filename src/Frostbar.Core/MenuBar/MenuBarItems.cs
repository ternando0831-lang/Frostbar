namespace Frostbar.Core.MenuBar;

/// <summary>
/// The menu bar's status items the user shows, hides and moves (stage 5-1, macOS's "Show in Menu Bar" and ⌘-drag):
/// now playing, input mode, Wi-Fi, sound, battery, focus and search, in the order kept in the settings (left to right).
/// The tray's icons and the » stay at the left of them, the Control Center and the clock at the right end (as on macOS).
/// </summary>
public static class MenuBarItems
{
    /// <summary>
    /// The items that can be hidden and moved, in their first order (left to right). Now Playing (stage 5-2) comes and
    /// goes with what plays, so it stands first: the items right of it stay put.
    /// </summary>
    public static readonly IReadOnlyList<string> Movable = ["nowPlaying", "ime", "wifi", "volume", "battery", "focus", "search"];

    /// <summary>The items settings of schema 1 (stage 5-1) did not know: shown when such settings are read (their default).</summary>
    public static readonly IReadOnlyList<string> NewInSchema2 = ["nowPlaying"];

    /// <summary>
    /// When the menu bar hides by itself (macOS's "Automatically hide and show the menu bar"): the settings' choices in
    /// macOS's order, Always, On Desktop Only, In Full Screen Only, Never (2026-11-01; the full screen one and always
    /// until then).
    /// </summary>
    public static readonly IReadOnlyList<string> AutoHides = ["always", "desktop", "fullScreen", "never"];

    /// <summary>
    /// The bar is out of sight, waiting at the top edge, with hiding <paramref name="mode"/>, a full-screen app in front
    /// or not. Else it stands: over a full-screen app too ("desktop" and "never", as macOS's), covering the app's top.
    /// </summary>
    public static bool Hides(string mode, bool fullScreen) => mode switch
    {
        "always" => true,
        "desktop" => !fullScreen,
        "never" => false,
        _ => fullScreen,
    };

    /// <summary>The bar keeps its strip at the top of the screen (the windows below it): unless it hides on the desktop.</summary>
    public static bool TakesStrip(string mode) => mode is not ("always" or "desktop");

    /// <summary>The settings' list made sound: known items only, each once, in the order given; null is the first order.</summary>
    public static List<string> Clean(IEnumerable<string>? shown) =>
        shown == null ? [.. Movable] : shown.Where(k => k != null && Movable.Contains(k)).Distinct().ToList();

    /// <summary>
    /// <paramref name="key"/> shown or hidden: put back where it first stood among the ones shown (after the last shown
    /// one that stood before it), or taken out.
    /// </summary>
    public static List<string> Set(IReadOnlyList<string> shown, string key, bool on)
    {
        var list = Clean(shown);
        if (!Movable.Contains(key)) return list;
        list.Remove(key);
        if (!on) return list;
        int first = IndexIn(Movable, key);
        int at = 0;
        for (int i = 0; i < list.Count; i++) if (IndexIn(Movable, list[i]) < first) at = i + 1;
        list.Insert(at, key);
        return list;
    }

    /// <summary><paramref name="key"/> moved to <paramref name="index"/> among the ones shown (a drag on the bar); the rest keep their order.</summary>
    public static List<string> Move(IReadOnlyList<string> shown, string key, int index)
    {
        var list = Clean(shown);
        if (!list.Remove(key)) return list;
        list.Insert(Math.Clamp(index, 0, list.Count), key);
        return list;
    }

    static int IndexIn(IReadOnlyList<string> list, string key)
    {
        for (int i = 0; i < list.Count; i++) if (list[i] == key) return i;
        return -1;
    }
}
