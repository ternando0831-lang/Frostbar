namespace Frostbar.Core.Shell;

/// <summary>
/// macOS 27's glass suits what is behind it (item 4, the user's "A", 2026-10-19; <c>Lab mock adapt</c>): each part of
/// glass on its own (a piece of the Control Center, a card, a menu, the Dock) turns light over a bright backdrop and
/// dark over a dark one, its text with it. The appearance is where a part rests: it leaves it only for a backdrop
/// clearly the other way, and comes back sooner than it left (no flicker while what is behind hovers near the line), and
/// only once the other tone has been wanted for <see cref="HoldMs"/> (a window dragged across a part does not flash it).
/// The first reading of a part showing decides at once (it opens in the tone it will keep). Brightness: the mean of
/// what is behind the part in sRGB luma, 0 to 1 (the lens's picture of the screen, the shell's own glass left out).
/// Only in the dark appearance (<see cref="Adapts"/>): the light appearance's glass stays light, raising what is dark
/// behind it. Turning dark there under 0.2, 8 of the Control Center's 9 pieces and the Dock went dark (the dark
/// wallpaper img27 alone 0.03 under two of them and 0.18 under the Dock, a dark document partly under the others 0.08
/// to 0.19), Now Playing alone light (the wallpaper alone, 0.3): the mean cannot tell a dark wallpaper from a dark
/// window (a dark editor's colours alone 0.12), and the panel came out mixed for no reason one could see (2026-10-19
/// `glass-adapt` in the light).
/// </summary>
public sealed class GlassTone
{
    /// <summary>The dark appearance: a part turns light over a backdrop brighter than this (a white window: 0.97 to 0.98), dark again under the second.</summary>
    public const double LightOver = 0.62, DarkAgainUnder = 0.5;

    /// <summary>Whether glass in this appearance suits what is behind it (the dark appearance's only).</summary>
    public static bool Adapts(bool appearanceDark) => appearanceDark;

    /// <summary>How long the other tone is wanted before a part turns (ms).</summary>
    public const long HoldMs = 150;

    readonly bool _appearanceDark;
    long? _wantedSince;
    bool _measured;

    public GlassTone(bool appearanceDark)
    {
        _appearanceDark = appearanceDark;
        Dark = appearanceDark;
    }

    /// <summary>The part's glass is dark now.</summary>
    public bool Dark { get; private set; }

    /// <summary>The appearance it rests in.</summary>
    public bool AppearanceDark => _appearanceDark;

    /// <summary>Turned away from the appearance now.</summary>
    public bool Turned => Dark != _appearanceDark;

    /// <summary>The last brightness read (NaN before the first).</summary>
    public double Luma { get; private set; } = double.NaN;

    /// <summary>When the other tone will be taken if it is still wanted then (ms, on the clock <see cref="Read"/> is given), or null.</summary>
    public long? DueAt => _wantedSince is { } t ? t + HoldMs : null;

    /// <summary>What the part would be over a backdrop of brightness <paramref name="luma"/>, being <paramref name="dark"/> now.</summary>
    public static bool Wanted(bool appearanceDark, bool dark, double luma)
    {
        if (double.IsNaN(luma) || !Adapts(appearanceDark)) return dark;
        return dark ? !(luma > LightOver) : luma < DarkAgainUnder;
    }

    /// <summary>
    /// A reading of what is behind the part (<paramref name="now"/>: ms on any steady clock). True when the part turned.
    /// </summary>
    public bool Read(double luma, long now)
    {
        Luma = luma;
        bool want = Wanted(_appearanceDark, Dark, luma);
        if (!_measured)
        {
            // The first reading: the part takes its tone at once (it is just showing).
            _measured = true;
            _wantedSince = null;
            if (want == Dark) return false;
            Dark = want;
            return true;
        }
        if (want == Dark) { _wantedSince = null; return false; }
        _wantedSince ??= now;
        if (now - _wantedSince < HoldMs) return false;
        _wantedSince = null;
        Dark = want;
        return true;
    }

    /// <summary>The time passed without a new reading (nothing behind changed): the last reading is read again. True when the part turned.</summary>
    public bool Tick(long now) => !double.IsNaN(Luma) && _wantedSince != null && Read(Luma, now);

    /// <summary>The part hid: it keeps its tone, and its next reading decides at once again (it may show over something else).</summary>
    public void Hidden()
    {
        _measured = false;
        _wantedSince = null;
        Luma = double.NaN;
    }
}

/// <summary>
/// How deep a glass plate's shadow is for what lies behind it (2026-11-10, the user's "アニメーションも足りないもの
/// すべて追加"; Apple, WWDC25 "Meet Liquid Glass": the shadow's opacity rises over text and drops over a solid light
/// background). 1 is the shadow as it was; over something busy (text, a picture, a window's edge: the brightness behind
/// the plate varies) it deepens up to <see cref="Deepest"/>, over a plain light background it goes down to
/// <see cref="Shallowest"/>. What is behind is what the lens read for the plate: the mean of its brightness and how far
/// it varies (the standard deviation over the plate, an eighth across). The numbers are this shell's own (Apple gives
/// none).
/// </summary>
public static class ShadowDepth
{
    public const double Deepest = 1.8, Shallowest = 0.55;

    /// <param name="luma">The mean brightness behind the plate, 0–1 (NaN: not read).</param>
    /// <param name="deviation">How far it varies over the plate (0: plain).</param>
    public static double Of(double luma, double deviation)
    {
        if (double.IsNaN(luma) || double.IsNaN(deviation)) return 1;
        double busy = Smooth(0.03, 0.14, deviation);
        double plainLight = Smooth(0.6, 0.85, luma) * (1 - busy);
        return 1 + (Deepest - 1) * busy - (1 - Shallowest) * plainLight;
    }

    /// <summary>Whether a depth is far enough from the one shown to be shown instead (a read differs a little each time).</summary>
    public static bool Differs(double shown, double now) => Math.Abs(shown - now) >= 0.04;

    static double Smooth(double from, double to, double x)
    {
        double t = Math.Clamp((x - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
