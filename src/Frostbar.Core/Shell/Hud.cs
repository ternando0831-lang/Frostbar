namespace Frostbar.Core.Shell;

public enum HudKind { Volume, Brightness }

/// <summary>
/// Where the volume and brightness display stands and how its bar fills (macOS 27.2's, from its pictures, 2026-11-01: it
/// stood at the top centre until then, the user's choice C of stage 2-2, before 27.2 moved it). Under the middle of the
/// menu bar item that matches it, kept on the screen. With no such item (none for brightness here, a hidden one, the bar
/// out of sight) at the screen's right edge, where macOS 26 has it; what 27.2 does then is said by no source seen.
/// </summary>
public static class HudLayout
{
    /// <summary>The bar's parts: 27.2's pictures show sixteen, macOS's steps of a key.</summary>
    public const int Parts = 16;

    /// <summary>The menu bar item the display stands under (null: the bar has none for it).</summary>
    public static string? ItemOf(HudKind kind) => kind == HudKind.Volume ? "volume" : null;

    /// <summary>How full part <paramref name="index"/> is at a level of 0 to 1: a level between steps fills its part partly (27.2 shows macOS's quarter steps so).</summary>
    public static double PartFill(double level, int index) => Math.Clamp(Math.Clamp(level, 0, 1) * Parts - index, 0, 1);

    /// <summary>The display's left, <paramref name="width"/> wide, <paramref name="inset"/> or more inside the screen's <paramref name="left"/> and <paramref name="right"/>.</summary>
    public static double Left(double? itemMiddle, double width, double left, double right, double inset) =>
        itemMiddle is { } m ? Math.Clamp(m - width / 2, left + inset, right - inset - width) : right - inset - width;
}

/// <summary>
/// What the volume and brightness display shows: the kind, the level and whether the sound is muted, until
/// <see cref="ShowMs"/> after the last key. A level that changes while it shows (the key's own change, a moment after the
/// key) updates it without keeping it longer. Times are milliseconds of any clock.
/// </summary>
public sealed class HudModel
{
    public const long ShowMs = 1500;

    public HudKind Kind { get; private set; }
    /// <summary>0 to 1.</summary>
    public double Level { get; private set; }
    public bool Muted { get; private set; }
    /// <summary>When it goes (null: not shown).</summary>
    public long? HideAt { get; private set; }
    public bool Visible => HideAt != null;

    public int Percent => (int)Math.Round(Level * 100);
    public int Waves => WavesOf(Level, Muted);

    /// <summary>A key: shown (again) for <see cref="ShowMs"/>.</summary>
    public void Show(HudKind kind, double level, bool muted, long now)
    {
        Kind = kind;
        Level = Math.Clamp(level, 0, 1);
        Muted = muted && kind == HudKind.Volume;
        HideAt = now + ShowMs;
    }

    /// <summary>The level of what shows changed. Returns whether it showed that kind (and so changed).</summary>
    public bool Update(HudKind kind, double level, bool muted)
    {
        if (!Visible || kind != Kind) return false;
        double l = Math.Clamp(level, 0, 1);
        bool m = muted && kind == HudKind.Volume;
        if (l == Level && m == Muted) return false;
        Level = l;
        Muted = m;
        return true;
    }

    /// <summary>Returns whether it went now.</summary>
    public bool Expire(long now)
    {
        if (HideAt is not { } t || now < t) return false;
        HideAt = null;
        return true;
    }

    public void Hide() => HideAt = null;

    /// <summary>The speaker's waves, as the menu bar's volume item has them: none when muted or at 0, else 1 to 3.</summary>
    public static int WavesOf(double level, bool muted) => muted || level <= 0.001 ? 0 : level < 0.34 ? 1 : level < 0.67 ? 2 : 3;
}

public enum OsdCause { Volume, Brightness, Unknown }

/// <summary>
/// Windows' own volume and brightness display (Explorer's XamlExplorerHostIslandWindow, ADR-015): which of Explorer's
/// island windows it is, and what it came out for. Windows shows it for the keys, so its showing tells that a key was
/// pressed: a volume key also comes through the shell hook (APPCOMMAND) just before; a brightness key changes the
/// brightness (WMI) around the same moment, before or after. Times are milliseconds of one clock.
/// </summary>
public static class OsdJudge
{
    public const long AppCommandWithinMs = 400, BrightnessWithinMs = 600;

    /// <summary>
    /// A small window, as Windows' display is (292×77 px at 125%, 2026-10-15): not one of Explorer's larger islands
    /// (Alt+Tab's switcher, snap layouts). <paramref name="scale"/> is the monitor's (1.25 at 125%).
    /// </summary>
    public static bool LooksLikeDisplay(int widthPx, int heightPx, double scale) =>
        widthPx > 0 && heightPx > 0 && widthPx <= 520 * scale && heightPx <= 160 * scale;

    /// <summary>Why it came out: a volume key just before, a brightness change around it, or neither (yet).</summary>
    public static OsdCause Cause(long shownAt, long? appCommandAt, long? brightnessAt) =>
        appCommandAt is { } a && shownAt - a >= 0 && shownAt - a <= AppCommandWithinMs ? OsdCause.Volume
        : brightnessAt is { } b && Math.Abs(shownAt - b) <= BrightnessWithinMs ? OsdCause.Brightness
        : OsdCause.Unknown;

    /// <summary>A volume key this short a time ago is being held (its repeats come faster), not left for the brightness keys.</summary>
    public const long VolumeHeldMs = 150;

    /// <summary>
    /// A brightness change is the keys' (to show, and to keep showing): Windows' display is out. It stays out 2.2 s after
    /// a key, and a key meanwhile brings no new showing, so its coming out alone showed nothing after a volume key and let
    /// a long adjustment's display go after its first 1.5 s (2026-11-01, the user's report). With the display not out it is
    /// not a key's (adaptive brightness), nor while a volume key is held with the display out for it.
    /// </summary>
    public static bool BrightnessIsKeys(bool displayOut, long now, long volumeKeyAt) => displayOut && now - volumeKeyAt >= VolumeHeldMs;

    /// <summary>The volume keys among the shell hook's APPCOMMANDs: mute (8), down (9), up (10).</summary>
    public static bool IsVolumeKey(int appCommand) => appCommand is 8 or 9 or 10;
}
