using System.Windows.Media;
using Frostbar.Core.Settings;
using Frostbar.Core.Shell;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Sys;
using WColor = Windows.UI.Color;

namespace Frostbar.App.Common;

/// <summary>
/// Theme → materials and text colours. Tints and rims are provisional (fixed in M2/M3 and recorded in the design notes).
/// Blur falls back to a solid fill when Windows turns transparency off, in high contrast and in energy saver (ADR-003).
/// </summary>
public static class Materials
{
    static WColor C(byte a, byte r, byte g, byte b) => Composition.Color(a, r, g, b);

    // High contrast: the user's theme's window and text colours (D5, 2026-10-19: black and white whatever theme was chosen).
    static WColor HcBack => From(System.Windows.SystemColors.WindowColor);
    static WColor HcText => From(System.Windows.SystemColors.WindowTextColor);
    static WColor From(Color c) => C(255, c.R, c.G, c.B);

    /// <summary>The 背景 setting for a surface (BackdropRule: "transparent" clears the menu bar only).</summary>
    static MaterialKind Kind(ThemeSnapshot t, AppearanceSettings a, bool menuBar = false) => BackdropRule.For(a.Material, t.BlurAllowed, menuBar) switch
    {
        Backdrop.Blur => MaterialKind.Blur,
        Backdrop.Clear => MaterialKind.Clear,
        _ => MaterialKind.Solid,
    };

    /// <summary>
    /// The menu bar's material. A clear bar is frosted when it stands <paramref name="overApps"/> (over a full-screen
    /// app, or with no strip of its own): its words are dark or light for the wallpaper (C2), not for an app's window,
    /// and on a white page they would not read (2026-11-01, when clear became the default).
    /// </summary>
    public static Material MenuBar(ThemeSnapshot t, AppearanceSettings a, bool overApps = false)
    {
        var kind = Kind(t, a, menuBar: true);
        if (kind == MaterialKind.Clear && overApps) kind = MaterialKind.Blur;
        if (t.HighContrast) return new Material(MaterialKind.Solid, HcBack, default, 0);
        return kind switch
        {
            MaterialKind.Blur => new Material(kind, t.Dark ? C(0x80, 0x1E, 0x1E, 0x1E) : C(0x99, 0xF4, 0xF4, 0xF4), default, 0),
            MaterialKind.Solid => new Material(kind, t.Dark ? C(255, 0x2B, 0x2B, 0x2B) : C(255, 0xEC, 0xEC, 0xEC), default, 0),
            _ => new Material(MaterialKind.Clear, default, default, 0),
        };
    }

    /// <summary>
    /// Liquid Glass (macOS 26, the settings' switch): the rims catch the light and the glass lets more through. Only over
    /// a blurred backdrop (a solid fill is not glass).
    /// </summary>
    static bool Glass(ThemeSnapshot t, AppearanceSettings a) => a.LiquidGlass && GlassPossible(t, a);

    /// <summary>Liquid Glass shows at all: not over a solid fill (chosen, or Windows' transparency effects off) nor in high contrast.</summary>
    public static bool GlassPossible(ThemeSnapshot t, AppearanceSettings a) => !t.HighContrast && Kind(t, a) != MaterialKind.Solid;

    /// <summary>The lens a glass plate has (ADR-016): with the settings' switch, where this PC and this shell can run it.</summary>
    static LensStyle Lens(AppearanceSettings a, LensStyle style) => a.Lens && LensCapture.Available ? style : LensStyle.None;

    /// <summary>The lens can run at all (the settings page dims its switch otherwise).</summary>
    public static bool LensPossible(ThemeSnapshot t, AppearanceSettings a) => a.LiquidGlass && GlassPossible(t, a) && LensCapture.Available;

    /// <summary>The shadow under every glass plate (Liquid Glass's shapes, the user's "C"); none in high contrast.</summary>
    static readonly PlateShadow Shadow = new((float)GlassShape.ShadowBlur, (float)GlassShape.ShadowDrop, (float)GlassShape.ShadowAlpha);

    static Material Shadowed(ThemeSnapshot t, Material m) => t.HighContrast ? m : m with { Shadow = Shadow };

    /// <summary>
    /// Accessibility's "Increase contrast" (2026-10-22, macOS's): the glass gets a plain border a step stronger than its
    /// faint outline (it stays with the lens, as the outline does). Set by the shell from the settings.
    /// </summary>
    public static bool HighContrastEdges { get; set; }

    /// <summary>The border "Increase contrast" draws: white 45% on dark glass, black 45% on light.</summary>
    public static WColor ContrastRim(bool dark) => dark ? C(0x73, 255, 255, 255) : C(0x73, 0, 0, 0);

    static Material Contrasted(ThemeSnapshot t, Material m) => !HighContrastEdges || t.HighContrast ? m : m with { Rim = ContrastRim(t.Dark) };

    /// <summary>A glass's tint with the appearance's slider (macOS 27's ultra clear to tinted; the middle is today's).</summary>
    static WColor Tinted(WColor c, AppearanceSettings a) => C(GlassTints.Alpha(c.A, a.GlassTint), c.R, c.G, c.B);

    /// <summary>
    /// A glass with its other tone (<see cref="Material.Opposite"/>: the same glass in the other appearance), for a window
    /// that suits what is behind it (item 4, the user's "A", 2026-10-19). Only with the lens (it reads what is behind),
    /// and in the dark appearance (Core's GlassTone.Adapts: the light appearance's glass stays light).
    /// </summary>
    static Material WithOpposite(ThemeSnapshot t, Func<ThemeSnapshot, Material> look)
    {
        var m = look(t);
        return m.Lens == LensStyle.None || m.Kind != MaterialKind.Blur || !GlassTone.Adapts(t.Dark) ? m : m with { Opposite = look(t with { Dark = !t.Dark }) };
    }

    /// <summary>The text colour on glass of tone <paramref name="dark"/> (a part turned: the Dock's dots).</summary>
    public static Brush Text(ThemeSnapshot t, bool dark) => Text(t with { Dark = dark });

    public static Material DockPlate(ThemeSnapshot t, AppearanceSettings a, float radiusPx) => WithOpposite(t, x => Contrasted(x, Shadowed(x, DockPlateLook(x, a, radiusPx))));

    static Material DockPlateLook(ThemeSnapshot t, AppearanceSettings a, float radiusPx)
    {
        var kind = Kind(t, a);
        if (t.HighContrast) return new Material(MaterialKind.Solid, HcBack, HcText, radiusPx);
        // Clearer glass: the icons stand on the light, not on a slab. Its edge is the glass's own lit rim (a second line
        // beside it read as layers); a faint dark outline stays in the light theme, where white on light would vanish.
        // macOS 27's material (the user's "A", 2026-10-18): the darkened edge, the brighter highlight, the band inside.
        if (kind == MaterialKind.Blur && Glass(t, a))
        {
            // With the lens (the user's "B4", 2026-10-18): the lens gathers the light and holds the bright down itself; a
            // faint tint over it. Without it, the blurred backdrop as before.
            var lens = Lens(a, LensStyle.Clear);
            var tint = lens != LensStyle.None ? (t.Dark ? C(0x28, 0x0C, 0x0C, 0x10) : C(0x10, 0xFF, 0xFF, 0xFF)) : t.Dark ? C(0x40, 0x28, 0x28, 0x28) : C(0x59, 0xF5, 0xF5, 0xF5);
            return new Material(kind, Tinted(tint, a), t.Dark ? default : C(0x12, 0, 0, 0), radiusPx, 1, lens, Edge: true, Dark: t.Dark);
        }
        return kind == MaterialKind.Blur
            ? new Material(kind, t.Dark ? C(0x66, 0x28, 0x28, 0x28) : C(0x80, 0xF5, 0xF5, 0xF5), t.Dark ? C(0x33, 255, 255, 255) : C(0x1A, 0, 0, 0), radiusPx)
            : new Material(kind, t.Dark ? C(255, 0x30, 0x30, 0x30) : C(255, 0xE8, 0xE8, 0xE8), t.Dark ? C(0x33, 255, 255, 255) : C(0x1A, 0, 0, 0), radiusPx);
    }

    /// <summary>Menus and panels: more opaque than the bar so text stays readable over busy windows.</summary>
    public static Material Menu(ThemeSnapshot t, AppearanceSettings a, float radiusPx) => WithOpposite(t, x => Contrasted(x, Shadowed(x, MenuLook(x, a, radiusPx))));

    static Material MenuLook(ThemeSnapshot t, AppearanceSettings a, float radiusPx)
    {
        var rim = t.Dark ? C(0x30, 255, 255, 255) : C(0x1F, 0, 0, 0);
        if (t.HighContrast) return new Material(MaterialKind.Solid, HcBack, HcText, radiusPx);
        if (Kind(t, a) == MaterialKind.Blur && Glass(t, a)) // a little clearer, still frosted enough to read; macOS 27's edge (the user's "A")
        {
            // With the lens (the user's "B4"): frosted 14 DIPs, the bright held down, so a lighter tint reads as well.
            var lens = Lens(a, LensStyle.Frosted);
            var tint = lens != LensStyle.None ? (t.Dark ? C(0x68, 0x16, 0x16, 0x1A) : C(0x58, 0xF6, 0xF6, 0xF8)) : t.Dark ? C(0xB0, 0x26, 0x26, 0x26) : C(0xC2, 0xF7, 0xF7, 0xF7);
            return new Material(MaterialKind.Blur, Tinted(tint, a), t.Dark ? default : C(0x14, 0, 0, 0), radiusPx, 0.85f, lens, Edge: true, Dark: t.Dark);
        }
        return Kind(t, a) == MaterialKind.Blur
            ? new Material(MaterialKind.Blur, t.Dark ? C(0xC8, 0x26, 0x26, 0x26) : C(0xD6, 0xF7, 0xF7, 0xF7), rim, radiusPx)
            : new Material(MaterialKind.Solid, t.Dark ? C(255, 0x2C, 0x2C, 0x2C) : C(255, 0xF2, 0xF2, 0xF2), rim, radiusPx);
    }

    static readonly PlateShadow PieceShadow = new((float)GlassShape.PieceShadowBlur, (float)GlassShape.PieceShadowDrop, (float)GlassShape.PieceShadowAlpha);
    static readonly PlateShadow LightPieceShadow = new((float)GlassShape.LightPieceShadowBlur, (float)GlassShape.LightPieceShadowDrop, (float)GlassShape.LightPieceShadowAlpha);

    /// <summary>
    /// A piece of macOS 27's Control Center and Notification Center (the user's "A", 2026-10-18): clearer than a menu
    /// (27's glass over the wallpaper: a light tint, the darkened edge, the brighter highlight and the band of light
    /// inside the rim), with its own lens and a lighter shadow. Over a solid fill (Liquid Glass off, or Windows'
    /// transparency off) a plain piece of the menu's colour.
    /// </summary>
    public static Material Piece(ThemeSnapshot t, AppearanceSettings a, float radiusPx) => WithOpposite(t, x => Contrasted(x, PieceLook(x, a, radiusPx)));

    static Material PieceLook(ThemeSnapshot t, AppearanceSettings a, float radiusPx)
    {
        if (t.HighContrast) return new Material(MaterialKind.Solid, HcBack, HcText, radiusPx);
        if (Kind(t, a) == MaterialKind.Blur && Glass(t, a))
        {
            // With the lens (the user's "B4", 2026-10-18): light gathered, vivid, a faint tint.
            var lens = Lens(a, LensStyle.Frosted);
            var tint = lens != LensStyle.None ? (t.Dark ? C(0x30, 0x0C, 0x0C, 0x10) : C(0x10, 0xFF, 0xFF, 0xFF)) : t.Dark ? C(0x48, 0x1C, 0x1C, 0x20) : C(0x70, 0xF8, 0xF8, 0xF8);
            return new Material(MaterialKind.Blur, Tinted(tint, a), t.Dark ? default : C(0x14, 0, 0, 0), radiusPx, 1, lens, t.Dark ? PieceShadow : LightPieceShadow, Edge: true, Dark: t.Dark);
        }
        return Kind(t, a) == MaterialKind.Blur
            ? new Material(MaterialKind.Blur, t.Dark ? C(0xC8, 0x26, 0x26, 0x26) : C(0xD6, 0xF7, 0xF7, 0xF7), t.Dark ? C(0x30, 255, 255, 255) : C(0x1F, 0, 0, 0), radiusPx, 0, LensStyle.None, PieceShadow)
            : new Material(MaterialKind.Solid, t.Dark ? C(255, 0x2C, 0x2C, 0x2C) : C(255, 0xF2, 0xF2, 0xF2), t.Dark ? C(0x30, 255, 255, 255) : C(0x1F, 0, 0, 0), radiusPx, 0, LensStyle.None, PieceShadow);
    }

    /// <summary>The app grid's full-screen backdrop: the desktop blurred and darkened in both themes (Launchpad), white text on it.</summary>
    public static Material AppGrid(ThemeSnapshot t, AppearanceSettings a) =>
        t.HighContrast ? new Material(MaterialKind.Solid, HcBack, default, 0)
        : Kind(t, a) == MaterialKind.Blur ? new Material(MaterialKind.Blur, C(0x73, 0x16, 0x16, 0x1A), default, 0)
        : new Material(MaterialKind.Solid, C(255, 0x22, 0x22, 0x26), default, 0);

    /// <summary>
    /// The window overview's backdrop (M7): the desktop blurred and darkened more than the app grid's, so the real windows
    /// left under it do not compete with their thumbnails. Without blur, nearly opaque.
    /// </summary>
    public static Material Overview(ThemeSnapshot t, AppearanceSettings a) =>
        t.HighContrast ? new Material(MaterialKind.Solid, HcBack, default, 0)
        : Kind(t, a) == MaterialKind.Blur ? new Material(MaterialKind.Blur, C(0x99, 0x12, 0x12, 0x16), default, 0)
        : new Material(MaterialKind.Solid, C(0xE6, 0x12, 0x12, 0x16), default, 0);

    public static Brush Text(ThemeSnapshot t)
    {
        if (t.HighContrast) return System.Windows.SystemColors.WindowTextBrush;
        var b = new SolidColorBrush(t.Dark ? Color.FromArgb(0xEB, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0xD9, 0, 0, 0));
        b.Freeze();
        return b;
    }

    /// <summary>Segoe UI Variable for Latin; Japanese characters fall to Yu Gothic UI (not a Chinese face) even in an English UI.</summary>
    public static readonly FontFamily UiFont = new("Segoe UI Variable Text, Yu Gothic UI");
}
