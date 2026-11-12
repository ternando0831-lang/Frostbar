namespace Frostbar.Core.Wm;

/// <summary>Where an app window is, for the Dock.</summary>
public enum Presence
{
    /// <summary>On this virtual desktop (not cloaked).</summary>
    Here,
    /// <summary>On another virtual desktop: the app runs, and bringing the window forward goes there.</summary>
    Elsewhere,
    /// <summary>Cloaked for another reason: not counted (an app's own cloak, a suspended Store app's frame).</summary>
    Hidden,
}

public static class WindowPresence
{
    /// <summary>DWMWA_CLOAKED's flags.</summary>
    public const int CloakedApp = 1, CloakedShell = 2, CloakedInherited = 4;

    /// <summary>
    /// A window cloaked by the shell, not on the current desktop, with a desktop of its own, is on another desktop;
    /// any other cloaked window is not counted. Read on this PC (2026-10-15): a closed Settings app leaves a frame
    /// cloaked by the shell with an empty desktop id "on the current desktop", and the text input host's window has no
    /// desktop at all (GetWindowDesktopId fails with TYPE_E_ELEMENTNOTFOUND). Before, every cloaked window was left out,
    /// and an app whose windows were all on another desktop looked quit: a click started it again.
    /// </summary>
    /// <param name="cloaked">DWMWA_CLOAKED (0: not cloaked).</param>
    /// <param name="onCurrent">IsWindowOnCurrentVirtualDesktop, or null when it failed.</param>
    /// <param name="desktop">GetWindowDesktopId, or null when it failed.</param>
    public static Presence Of(int cloaked, bool? onCurrent, Guid? desktop)
    {
        if (cloaked == 0) return Presence.Here;
        if ((cloaked & CloakedShell) != 0 && onCurrent == false && desktop is { } d && d != Guid.Empty) return Presence.Elsewhere;
        return Presence.Hidden;
    }
}
