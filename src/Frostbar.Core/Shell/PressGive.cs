namespace Frostbar.Core.Shell;

/// <summary>What was pressed, for how much it gives (<see cref="PressGive"/>).</summary>
public enum PressKind { Glass, Icon, Picture, Button, Window, Row }

/// <summary>
/// How a pressed thing gives under the pointer (2026-10-23, the user's "A 沈む＋隣へ光る" on everything that takes a
/// press: the menu bar, the Dock and the app grid, the settings and dialogs, search, stacks and the window overview).
/// Glass objects sink and spring back; what is drawn on a plate (a row of a menu, of the search, of the settings) does
/// not move, its light shows the press. Until then only the Control Center's and the Notification Center's glass gave
/// (0.9, the user's "弾むほうが見栄えいいかもしれない", 2026-10-18); a Dock icon only darkened at once, the menu bar did
/// nothing at all.
/// </summary>
public static class PressGive
{
    /// <summary>A piece that is its own glass (the Control Center's, a notification's card): as since 2026-10-18.</summary>
    public const double Glass = 0.90;
    /// <summary>An app's icon (the Dock, the app grid, a folder there), a small square button (the settings' ‹ ›).</summary>
    public const double Icon = 0.92;
    /// <summary>A picture to choose (a stack's cell, a wallpaper, a desktop in the strip).</summary>
    public const double Picture = 0.95;
    /// <summary>A push button with words.</summary>
    public const double Button = 0.96;
    /// <summary>A window's picture in the overview.</summary>
    public const double Window = 0.97;
    /// <summary>A row: it does not move.</summary>
    public const double Row = 1;

    /// <summary>
    /// The spring's damping as it is let go: a little past full size and back, 0.5% or less of it (ζ 0.7, SwiftUI's
    /// .bouncy, whose bounce is 0.3; Apple says of the glass that it "subtly bounces", WWDC26 289). It was 0.32, about 3.5%
    /// past (2026-10-18 to 2026-11-01).
    /// </summary>
    public const double Damping = 0.7;

    public static double Depth(PressKind kind) => kind switch
    {
        PressKind.Glass => Glass,
        PressKind.Icon => Icon,
        PressKind.Picture => Picture,
        PressKind.Button => Button,
        PressKind.Window => Window,
        _ => Row,
    };

    /// <summary>
    /// The kind of a part <paramref name="width"/> × <paramref name="height"/> DIPs whose caller does not say: its own
    /// glass gives most; a row (160 DIPs wide or more, and 2.5 times as wide as high) not at all; a small one (72 DIPs
    /// at most) as an icon; a low one (40 DIPs at most) as a button; a large one (200 × 150 DIPs of area) as a window's
    /// picture; anything else as a picture.
    /// </summary>
    public static PressKind Of(double width, double height, bool ownGlass)
    {
        if (ownGlass) return PressKind.Glass;
        if (width >= 160 && width >= 2.5 * height) return PressKind.Row;
        if (Math.Max(width, height) <= 72) return PressKind.Icon;
        if (height <= 40) return PressKind.Button;
        if (width * height >= 200 * 150) return PressKind.Window;
        return PressKind.Picture;
    }

    /// <summary>The scale <paramref name="ms"/> into a press, from <paramref name="from"/> (where it was: a spring may be under way) to <paramref name="depth"/>.</summary>
    public static double Pressing(double from, double depth, double ms) =>
        from + (depth - from) * MotionTable.Out(ms / MotionTable.BouncePress);

    /// <summary>The scale <paramref name="ms"/> after it was let go at <paramref name="from"/>: a spring back to 1, past it and back.</summary>
    public static double LettingGo(double from, double ms) =>
        from + (1 - from) * MotionTable.Spring(ms / MotionTable.BounceRelease, Damping);

    /// <summary>Still springing back <paramref name="ms"/> after it was let go.</summary>
    public static bool Settling(double ms) => ms < MotionTable.BounceRelease;

    /// <summary>
    /// The shade over a pressed icon (0–1 of its own darkness), <paramref name="ms"/> into the press from
    /// <paramref name="from"/>: in quickly (it was there at once until 2026-10-23).
    /// </summary>
    public static double ShadeIn(double from, double ms) =>
        from + (1 - from) * MotionTable.Out(ms / MotionTable.PressShadeIn);

    /// <summary>The shade <paramref name="ms"/> after the press ended, from <paramref name="from"/>: gone gently.</summary>
    public static double ShadeOut(double from, double ms) =>
        from * (1 - MotionTable.Smooth(ms / MotionTable.PressShadeOut));
}
