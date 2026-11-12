namespace Frostbar.Core.Shell;

/// <summary>How Liquid Glass's lens bends what is behind a plate (ADR-016): not at all, clear (the Dock), or frosted (menus and panels).</summary>
public enum LensStyle { None, Clear, Frosted }

/// <summary>
/// What the lens does to what is behind a plate, in pixels: Liquid Glass as Apple's own pictures show it (the HIG's
/// Materials and macOS Tahoe's Control Center, enlarged; the user's "B4", 2026-10-18; <c>Lab mock liquid</c>). Within
/// <see cref="Bezel"/> of the rim the plate is a dome: what is behind shows <see cref="Magnify"/> times larger in its
/// middle and squeezed towards the rim (sharper by <see cref="Squeeze"/>), and in the rim's last pixels it reads up to
/// <see cref="Strength"/> beyond the rim; the colours split there by <see cref="Dispersion"/>. It frosts by
/// <see cref="Frost"/> (a round spread of <see cref="SigmaPerFrost"/> of it; <see cref="Lod"/> is the pyramid's level
/// the light around the rim is read on, less one), longer along the rim near it (<see cref="Smear"/>), keeps
/// <see cref="Contrast"/> of what is behind and saturates it by <see cref="Saturation"/>, and gathers light: brighter by
/// <see cref="Gain"/>, its colour kept (not lifted towards white). Dark glass keeps what passes <see cref="Knee"/> to
/// <see cref="Slope"/> of it (white text stays readable over a white window); light glass raises what is under the knee.
/// The band inside the rim (<see cref="FresnelWidth"/>) is lit by what is around it, read <see cref="EnvOffset"/> outside
/// the rim, by <see cref="Fresnel"/>. Before (S31, until 2026-10-18) it only read farther out within a 12–18 DIP rim, cut
/// to a sixth of a small piece's side and frosted over: the user's "全然Liquid Glassの再現ができてない".
/// </summary>
public readonly record struct LensLook(float Bezel, float Strength, float Dispersion, float Frost, float Lod, float Saturation)
{
    public float Magnify { get; init; }
    public float Squeeze { get; init; }
    public float Gain { get; init; }
    public float Knee { get; init; }
    public float Slope { get; init; }
    public bool Dark { get; init; }
    public float Fresnel { get; init; }
    public float FresnelWidth { get; init; }
    public float Smear { get; init; }
    public float Contrast { get; init; }
    public float EnvOffset { get; init; }

    /// <summary>
    /// Light glass: what passes <see cref="Top"/> kept to <see cref="TopSlope"/> of it (white comes out about 0.93). Over
    /// a white window light glass was white itself, its rim lit white too: the Control Center's pieces and the cards
    /// vanished into it (2026-10-19, the user's "ライトの見え方もしっかり検証して"). 1 and 1 on dark glass.
    /// </summary>
    public float Top { get; init; }
    public float TopSlope { get; init; }

    /// <summary>
    /// The rim as Apple's own glass shows it (HIG Materials: the regular glass over a dark picture and over a light one,
    /// measured on the pictures, 2026-11-06; <see cref="Rim"/>): just inside the edge a bright line, what is there lifted
    /// towards white by up to <see cref="Specular"/> (0.49 measured on the top rim over the dark picture), about
    /// <see cref="LinePx"/> wide; the edge's own pixel a dark line, what is outside it darkened (0.46–0.77 of it measured),
    /// taken by <see cref="Contact"/>. macOS 27's "darkened edge along with brighter specular highlights". Before, the
    /// edge was lit by what is around it (0.10 + 0.6 × its brightness): over the dark wallpaper +10 of 255 where Apple's
    /// is +117, and light glass alone had an edge shade (0.14 × the brightness around).
    /// </summary>
    public float Specular { get; init; }
    public float Contact { get; init; }
    public float LinePx { get; init; }

    /// <summary>What the dark line keeps of what is darker, the glass or what is outside it (measured 0.46–0.77, 0.6 typical).</summary>
    public const float ContactKeeps = 0.58f;

    /// <summary>
    /// How much the rim lifts a pixel towards white and how much its dark line takes it (0–1 each), <paramref name="depth"/>
    /// pixels in from the edge (a pixel's centre: 0.5 the outermost) on a rim whose normal's y is <paramref name="ny"/>
    /// (-1 the top rim, 1 the bottom). The bright line is lit most on the top rim, 0.82 of that on the bottom and 0.4 on
    /// the sides (measured 1, 0.78 and 0.40), in between by the normal's y cubed (0.56–0.62 measured at 45°). As the
    /// shader does (LensGpu's ps, which must agree).
    /// </summary>
    public (float lift, float contact) Rim(float depth, float ny)
    {
        if (Specular <= 0 && Contact <= 0) return (0, 0);
        return (Specular * Lit(ny) * LineAt(depth), Contact * DarkAt(depth));
    }

    /// <summary>
    /// Four points across a pixel (a rotated grid, in pixels from its middle) the rim's lines are taken at
    /// (<see cref="Rim(ReadOnlySpan{float}, float)"/>). The lines are under a pixel wide: taken at a pixel's middle alone,
    /// they stepped a pixel at a time along a rounded corner (2026-11-08, the user's "ギザギザを直す"; seen enlarged on
    /// Lab's picture of the Dock, which draws as the shader does).
    /// </summary>
    public static readonly (float x, float y)[] RimSamples = [(0.125f, 0.375f), (-0.375f, 0.125f), (-0.125f, -0.375f), (0.375f, -0.125f)];

    /// <summary>
    /// <see cref="Rim(float, float)"/> over a pixel: the mean of its lines at <paramref name="depths"/> (the pixel's depth at
    /// each of <see cref="RimSamples"/>), each made as strong as one point gives it on a straight rim (where the mean of a
    /// line under a pixel wide is less). As the shader does.
    /// </summary>
    public (float lift, float contact) Rim(ReadOnlySpan<float> depths, float ny)
    {
        if (Specular <= 0 && Contact <= 0) return (0, 0);
        float line = 0, dark = 0, lineStraight = 0, darkStraight = 0, c0 = 1 + 0.5f * Math.Max(LinePx, 0.5f);
        foreach (float d in depths) { line += LineAt(d); dark += DarkAt(d); }
        foreach (var s in RimSamples) { lineStraight += LineAt(c0 + s.y); darkStraight += DarkAt(0.5f + s.y); }
        line = Math.Min(1, line / depths.Length / (lineStraight / RimSamples.Length));
        dark = Math.Min(1, dark / depths.Length / (darkStraight / RimSamples.Length));
        return (Specular * Lit(ny) * line, Contact * dark);
    }

    static float Lit(float ny)
    {
        float v = Math.Min(1, Math.Abs(ny));
        return 0.4f + (ny < 0 ? 0.6f : 0.42f) * v * v * v;
    }

    /// <summary>The bright line, <paramref name="depth"/> pixels in: a sharp rise to its middle, a softer fall inside.</summary>
    float LineAt(float depth)
    {
        float w = Math.Max(LinePx, 0.5f), c0 = 1 + 0.5f * w;
        return depth < c0 ? MathF.Exp(-MathF.Pow((c0 - depth) / (0.55f * w), 2)) : MathF.Exp(-(depth - c0) / w);
    }

    /// <summary>The dark line: whole on the edge's own pixel, gone 1.25 pixels in.</summary>
    static float DarkAt(float depth) => Math.Clamp((1.25f - depth) / 0.75f, 0, 1);

    /// <summary>
    /// A pixel of the glass <paramref name="col"/> with its rim (<see cref="Rim"/>): lifted towards white, then darkened
    /// towards <see cref="ContactKeeps"/> of the darker of it and <paramref name="outside"/> (what lies just outside the
    /// rim). As the shader does.
    /// </summary>
    public (float r, float g, float b) AtRim((float r, float g, float b) col, (float r, float g, float b) outside, float depth, float ny) =>
        WithRim(col, outside, Rim(depth, ny));

    /// <summary><see cref="AtRim(ValueTuple{float, float, float}, ValueTuple{float, float, float}, float, float)"/> over a pixel (<see cref="Rim(ReadOnlySpan{float}, float)"/>). As the shader does.</summary>
    public (float r, float g, float b) AtRim((float r, float g, float b) col, (float r, float g, float b) outside, ReadOnlySpan<float> depths, float ny) =>
        WithRim(col, outside, Rim(depths, ny));

    static (float r, float g, float b) WithRim((float r, float g, float b) col, (float r, float g, float b) outside, (float lift, float contact) rim)
    {
        var (lift, contact) = rim;
        float L(float c) => c + (1 - c) * lift;
        (float r, float g, float b) c = (L(col.r), L(col.g), L(col.b));
        float D(float a, float o) => a + (Math.Min(a, o) * ContactKeeps - a) * contact;
        return (D(c.r, outside.r), D(c.g, outside.g), D(c.b, outside.b));
    }

    /// <summary>
    /// How much the gathered light is rolled off above 0.8 (1: as it gathers, 0: not at all). Only a glass part of the
    /// way in (<see cref="At"/>) has less: the roll-off alone would change what is behind a plate that is not there yet.
    /// </summary>
    public float Roll { get; init; }

    /// <summary>
    /// The frost's spread, a Gaussian's sigma in pixels, for a pixel of <see cref="Frost"/> (2026-11-10). The frost is
    /// read from a smooth pyramid of what is behind (each level the one above through a [1 3 3 1] tent, 16 bits a
    /// channel) through a cubic B-spline, between the two levels whose spreads are either side of the sigma: a point of
    /// light behind the glass spreads round and keeps its shape as it moves. Before, 13 points on a box mip: a blocky
    /// star 1.3–3.6 times as bright one way as another at its radius, whose shape changed by 39–71% as the point moved
    /// a pixel at a time (<c>Lab mock frost</c>, computed as the shader does; the new one 1.2–1.3 and 11–14%; that the
    /// GPU draws what is computed: <c>Lab mock gpu</c>, within one 8-bit level). 0.6 keeps the spread the 13 points
    /// had (their root mean square radius: 2.8, 6.3 and 10.4 px for a frost of 5, 11.3 and 17.5).
    /// </summary>
    public const float SigmaPerFrost = 0.6f;

    /// <summary>
    /// Noise of half an 8-bit level either way over the drawn glass (1: all of it): the frost's slow gradients do not
    /// step on dark glass. With the plate's presence (<see cref="At"/>): none on a plate that is not there yet.
    /// </summary>
    public float Dither { get; init; }

    /// <summary>
    /// The rim's own bend (2026-11-10; 0: none, as until then): within <see cref="RimWidth"/> pixels of the edge what is
    /// behind is read up to <see cref="RimBend"/> pixels farther in, most at the edge and falling off inwards by the
    /// square: a thick glass's edge, what is behind squeezed into it (turned over where the bend is more than the
    /// width). Apple's glass has it (macOS 26's own layers, read by others: a refraction of 40 within 12.5 pt of the
    /// edge; a renderer fitted to captures: 60 within 20 pt, "mirrored" past 1:1); the dome alone bends by 2 px.
    /// 24 DIPs within 12 (the user's "B 中", chosen on photos of none, 12, 24 and 40), on a plate large enough: no more
    /// than 0.4 of its shorter side within 0.2 of it (<see cref="Fit"/>: the bend stays this side of the plate's
    /// middle). Its colours do not split (the shader keeps <see cref="Dispersion"/> to the dome's bend).
    /// </summary>
    public float RimBend { get; init; }
    public float RimWidth { get; init; }

    /// <summary><see cref="RimBend"/> and <see cref="RimWidth"/> on a large plate, DIPs.</summary>
    public const float RimBendMost = 24, RimWidthMost = 12;

    /// <summary>
    /// Where the light falls from, radians (0: from above, as the glass rests): the rim's bright line and the band
    /// inside it are lit by the rim's normal turned by this. A plate coming by its lens (<see cref="At"/>) has it turned
    /// by <see cref="MaterializeTurn"/> at nothing and none when it is there: the light travels round the rim to the top
    /// as the glass arrives, and away as it goes (Apple, WWDC25 "Meet Liquid Glass": lights move, "causing light to
    /// travel around the material, defining its silhouette"; the user's "アニメーションも足りないものすべて追加",
    /// 2026-11-10).
    /// </summary>
    public float LightTurn { get; init; }

    /// <summary>How far round the rim the light is when a plate begins to come (radians: 100°, from the left rim to the top).</summary>
    public const float MaterializeTurn = 1.75f;

    /// <summary>The y of a rim's normal as the light sees it: the normal turned by <see cref="LightTurn"/>. As the shader does.</summary>
    public float TurnedY(float nx, float ny) => LightTurn == 0 ? ny : nx * MathF.Sin(LightTurn) + ny * MathF.Cos(LightTurn);

    /// <summary>How far in the rim reads <paramref name="depth"/> pixels from the edge. As the shader does.</summary>
    public float RimBendAt(float depth)
    {
        if (RimBend <= 0 || RimWidth <= 0) return 0;
        float k = Math.Clamp(1 - depth / RimWidth, 0, 1);
        return RimBend * k * k;
    }

    /// <summary>The coarsest level of the smooth pyramid a frost of <paramref name="frost"/> pixels reads (0: the picture itself). As the shader chooses.</summary>
    public static int FrostLevel(float frost)
    {
        double want = SigmaPerFrost * frost * (double)SigmaPerFrost * frost;
        if (want <= 1e-6) return 0;
        if (want <= 1 / 3.0) return 0;
        return (int)Math.Clamp(Math.Floor(0.5 * Math.Log2((want + 0.25) / (7 / 12.0))), 0, 4) + 1;
    }

    /// <summary>Pixels per DIP (a plate is fitted by its size in DIPs).</summary>
    public float Scale { get; init; }

    // What Fit chooses between: the bezel's most (DIPs), the frost (DIPs) and the magnification of a small plate (70 DIPs
    // and under: the Control Center's pieces, the Dock) and of a large one (180 and over: a menu, a panel).
    float BezelMost { get; init; }
    float FrostSmall { get; init; }
    float FrostLarge { get; init; }
    float MagnifySmall { get; init; }
    float MagnifyLarge { get; init; }

    // And the light it gathers: a small plate brighter and more vivid; a large one calmer (less gain and saturation, the
    // bright held down more, a shorter smear along its rim). A view the Control Center's width with the small plates'
    // look showed what was behind it as a vivid smear of colour under its rows (the user's "bluetoothとかの個々の画面
    // すごい滲み方する", 2026-10-18); Apple's large glass scatters softly and stays calm.
    (float small, float large) GainBy { get; init; }
    (float small, float large) SaturationBy { get; init; }
    (float small, float large) KneeBy { get; init; }
    (float small, float large) SlopeBy { get; init; }
    (float small, float large) SmearBy { get; init; }

    /// <summary>The look of a style at a scale (1.25 at 125%) in dark or light glass; the values below are in DIPs.</summary>
    public static LensLook For(LensStyle style, double scale, bool dark = true) => style switch
    {
        // The Dock: clearer (4 DIPs of frost).
        LensStyle.Clear => Gathering(scale, dark, frostSmall: 4, frostLarge: 4, magnifySmall: 1.12f, magnifyLarge: 1.12f),
        // Menus, panels and the Control Center's pieces: 9 DIPs of frost on a piece, 14 on a menu, a flatter dome there.
        LensStyle.Frosted => Gathering(scale, dark, frostSmall: 9, frostLarge: 14, magnifySmall: 1.12f, magnifyLarge: 1.05f),
        _ => default,
    };

    static LensLook Gathering(double scale, bool dark, float frostSmall, float frostLarge, float magnifySmall, float magnifyLarge)
    {
        float bezel = Px(30, scale), frost = Px(frostSmall, scale);
        var gain = dark ? (1.35f, 1.05f) : (1.25f, 1.0f);
        // Light glass raises the dark more on a large plate: a menu over a dark wallpaper came out a dull mid grey (its
        // floor at 0.70; 2026-10-19). Apple's light menus stay light over anything.
        var knee = dark ? (0.34f, 0.24f) : (0.72f, 0.84f);
        var slope = dark ? (0.3f, 0.25f) : (0.3f, 0.25f);
        return new LensLook(bezel, RimPull * bezel, 0.12f, frost, LodFor(frost), 1.6f)
        {
            Magnify = magnifySmall, Squeeze = 3, Gain = gain.Item1, Roll = 1, Dither = 1,
            RimBend = Px(RimBendMost, scale), RimWidth = Px(RimWidthMost, scale),
            Knee = knee.Item1, Slope = slope.Item1, Dark = dark,
            Top = dark ? 1 : 0.88f, TopSlope = dark ? 1 : 0.45f,
            Specular = 0.5f, Contact = 1, LinePx = Px(0.75, scale),
            Fresnel = 0.22f, FresnelWidth = Px(4, scale), Smear = Px(7, scale), Contrast = 0.95f, EnvOffset = Px(4, scale),
            Scale = (float)scale, BezelMost = 30, FrostSmall = frostSmall, FrostLarge = frostLarge, MagnifySmall = magnifySmall, MagnifyLarge = magnifyLarge,
            GainBy = gain, SaturationBy = (1.6f, 1.25f), KneeBy = knee, SlopeBy = slope, SmearBy = (7, 3),
        };
    }

    static float Between((float small, float large) v, float k) => v.small + (v.large - v.small) * k;

    /// <summary>How far the rim's last pixels read beyond it, as a part of the bezel.</summary>
    const float RimPull = 0.08f;

    static float Px(double dip, double scale) => (float)(dip * scale);

    /// <summary>The mip level to frost on: its texels about a third of the frost (a 13-point disc on it stays smooth).</summary>
    static float LodFor(float frostPx) => frostPx <= 0 ? 0 : Math.Clamp(MathF.Round(MathF.Log2(frostPx / 3)), 0, 3);

    /// <summary>
    /// The look on a plate whose shorter side is <paramref name="side"/> pixels: a whole dome up to the bezel's most (a
    /// 60-DIP circle is all dome, as Apple's are), and between a small plate's frost, magnification and gathered light
    /// and a large one's (calmer).
    /// </summary>
    public LensLook Fit(double side)
    {
        if (Bezel <= 0 || !(side > 0) || Scale <= 0) return this;
        float k = (float)Math.Clamp((side / Scale - 70) / 110, 0, 1);
        float bezel = (float)Math.Min(BezelMost * Scale, side / 2);
        float frost = (FrostSmall + (FrostLarge - FrostSmall) * k) * Scale;
        return this with
        {
            Bezel = bezel, Strength = RimPull * bezel, Frost = frost, Lod = LodFor(frost),
            Magnify = MagnifySmall + (MagnifyLarge - MagnifySmall) * k,
            Gain = Between(GainBy, k), Saturation = Between(SaturationBy, k), Knee = Between(KneeBy, k), Slope = Between(SlopeBy, k),
            Smear = Between(SmearBy, k) * Scale,
            RimBend = RimBend <= 0 ? 0 : (float)Math.Min(RimBendMost * Scale, 0.4 * side),
            RimWidth = RimBend <= 0 ? 0 : (float)Math.Min(RimWidthMost * Scale, 0.2 * side),
        };
    }

    /// <summary>
    /// The look part of the way in, <paramref name="presence"/> 0–1: Liquid Glass materializing, its light bending and
    /// lensing brought in by degrees instead of a fade (Apple, WWDC25 "Meet Liquid Glass"; the user's "A レンズの強さで
    /// 実体化", 2026-10-23). At 0 every term leaves what is behind as it is: no bend and no split, no frost on the
    /// full-size picture, saturation, contrast and gain 1, nothing rolled off, the knee's and top's slopes 1 (they keep
    /// what they are given), no light at the rim and no dark line: the plate is not there. At 1 it is this look.
    /// </summary>
    public LensLook At(double presence)
    {
        float p = (float)Math.Clamp(presence, 0, 1);
        if (p >= 1) return this;
        float Toward1(float v) => 1 + (v - 1) * p;
        return this with
        {
            Strength = Strength * p, Dispersion = Dispersion * p, Frost = Frost * p, Lod = Lod * p, Smear = Smear * p, RimBend = RimBend * p,
            Saturation = Toward1(Saturation), Contrast = Toward1(Contrast), Gain = Toward1(Gain), Magnify = Toward1(Magnify),
            Slope = Toward1(Slope), TopSlope = Toward1(TopSlope), Roll = Roll * p, Dither = Dither * p,
            Fresnel = Fresnel * p, Specular = Specular * p, Contact = Contact * p,
            LightTurn = LightTurn + MaterializeTurn * (1 - p),
        };
    }

    /// <summary>
    /// What the glass makes of a colour behind it (after the frost), as the shader does (LensGpu's ps, which must agree):
    /// saturation, contrast, the gain rolled off above 0.8 (by <see cref="Roll"/>), then dark glass's knee, or light
    /// glass's floor and top.
    /// </summary>
    public (float r, float g, float b) Tone(float r, float g, float b)
    {
        static float L(float r, float g, float b) => 0.2126f * r + 0.7152f * g + 0.0722f * b;
        static float Rolled(float v) => v <= 0.8f ? v : 0.8f + 0.2f * (1 - MathF.Exp(-(v - 0.8f) / 0.2f));
        float l = L(r, g, b), s = Saturation > 0 ? Saturation : 1, c = Contrast > 0 ? Contrast : 1, gain = Gain > 0 ? Gain : 1, roll = Roll;
        float RollOff(float v) => v + (Rolled(v) - v) * roll;
        (r, g, b) = (Math.Max(0, l + (r - l) * s), Math.Max(0, l + (g - l) * s), Math.Max(0, l + (b - l) * s));
        (r, g, b) = (0.5f + (r - 0.5f) * c, 0.5f + (g - 0.5f) * c, 0.5f + (b - 0.5f) * c);
        (r, g, b) = (RollOff(Math.Max(0, r) * gain), RollOff(Math.Max(0, g) * gain), RollOff(Math.Max(0, b) * gain));
        float lc = Math.Max(L(r, g, b), 1e-3f);
        if (Dark)
        {
            if (lc > Knee) { float k = (Knee + (lc - Knee) * Slope) / lc; (r, g, b) = (r * k, g * k, b * k); }
        }
        else
        {
            if (lc < Knee) { float add = Knee - (Knee - lc) * Slope - lc; (r, g, b) = (r + add, g + add, b + add); }
            float lt = Math.Max(L(r, g, b), 1e-3f), top = Top > 0 ? Top : 1, ts = TopSlope > 0 ? TopSlope : 1;
            if (lt > top) { float k = (top + (lt - top) * ts) / lt; (r, g, b) = (r * k, g * k, b * k); }
        }
        return (Math.Clamp(r, 0, 1), Math.Clamp(g, 0, 1), Math.Clamp(b, 0, 1));
    }

    /// <summary>
    /// How far beyond its plate the lens reads (pixels), whichever reaches farther: the rim's pull and its split, the
    /// frost about it (two sigmas: what lies farther weighs under a twentieth; a texel of the coarsest level it reads)
    /// and a quarter of the smear (it runs along the rim, and leaves it only round a corner); or the light read around
    /// the rim and a texel of its level. No more than is read: a plate is drawn again for every change within its
    /// reach, and the capture kept fast for 3 s after each. With the smear whole and a texel and a half (the first
    /// version of this frost, 2026-11-10) the Dock reached 28 px where it had reached 25, and on a screen with a
    /// desktop pet's window moving just above the Dock the lens was drawn 137 times a minute for 13, the idle shell
    /// taking 1.5% of a core for 0.36% (idle-cpu lens, the version before beside it; 13 and 0.40% with this reach).
    /// </summary>
    public int Reach => Strength <= 0 && Frost <= 0 ? 0
        : (int)Math.Ceiling(Math.Max(Strength * (1 + Dispersion) + 2 * SigmaPerFrost * Frost + (1 << FrostLevel(Frost)) + Smear / 4,
            EnvOffset + (1 << ((int)Lod + 1))) + 2);
}

/// <summary>
/// Which plates a captured frame's changes reach (ADR-016). The shell's glass is left out of the capture, so a change
/// wholly inside a plate was the shell itself drawing there (the plate's own lens among them: drawing it again on that
/// would never stop, S31) or something small wholly hidden under the plate (missed until a change reaches out from
/// under it). Not the shell's whole windows: the Dock's is far larger than its plate, and every change behind the
/// plate would have been inside it. Any other change reaches each plate whose surroundings (the plate and the lens's
/// reach) it touches; the shell drawing beside a plate (an icon bouncing above the Dock) reaches it too, a drawing
/// each time, and its echo is inside the plate.
/// </summary>
public static class LensDamage
{
    /// <param name="changed">The frame's dirty rectangles.</param>
    /// <param name="own">The plates the lens draws on (the shell's glass, left out of the capture).</param>
    /// <param name="read">What each plate's lens reads (the plate grown by its reach).</param>
    /// <returns>For each plate (as <paramref name="read"/>), whether the frame reached it.</returns>
    public static bool[] Reached(IReadOnlyList<ScreenRect> changed, IReadOnlyList<ScreenRect> own, IReadOnlyList<ScreenRect> read)
    {
        var reached = new bool[read.Count];
        foreach (var c in changed)
        {
            if (Empty(c) || own.Any(o => Inside(c, o))) continue;
            for (int i = 0; i < read.Count; i++)
                if (!reached[i] && Meet(c, read[i])) reached[i] = true;
        }
        return reached;
    }

    public static bool Empty(ScreenRect r) => r.Right <= r.Left || r.Bottom <= r.Top;

    public static bool Inside(ScreenRect inner, ScreenRect outer) =>
        inner.Left >= outer.Left && inner.Top >= outer.Top && inner.Right <= outer.Right && inner.Bottom <= outer.Bottom;

    public static bool Meet(ScreenRect a, ScreenRect b) =>
        !Empty(a) && !Empty(b) && a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    public static ScreenRect Grow(ScreenRect r, int by) => new(r.Left - by, r.Top - by, r.Right + by, r.Bottom + by);
}

/// <summary>
/// The size of a lens's drawing surface for a plate (pixels): rounded up to 64 so that a plate growing frame by frame
/// (the Dock as it magnifies) does not make a new surface every frame; a new one only when the plate outgrows it or
/// is under half of it, and a smaller one would be smaller. A plate of 32 px or less (a notification's ×, "Clear All")
/// was always under half of the smallest surface, 64: it was made anew at each of its drawings, up to 30 a second
/// (2026-11-06).
/// </summary>
public static class LensSurfaceSize
{
    public static int Round(int px) => Math.Max(64, (px + 63) / 64 * 64);

    public static bool Remake(int have, int want) => want > have || want * 2 <= have && Round(want) < have;
}

/// <summary>
/// Whether what is behind a plate moves on and on (a video, a page scrolling): then the lens follows it at the
/// display's pace (LensCapture's third pace, 2026-11-10), which costs twice the usual following (`lens-pace`: 8% of a
/// core for 4%, a bar sweeping beside the Dock). The fourth change to reach a plate within <see cref="WithinMs"/> says
/// so: a change every 100 ms or slower (a clock's seconds, a caret, a desktop pet's idle animation every 0.4 s) never
/// does, a video at 24 frames a second does at once.
/// </summary>
public sealed class LensFollow
{
    public const double WithinMs = 250;

    /// <summary>With no change reaching a plate for this long, it no longer moves on and on.</summary>
    public const double CalmMs = 250;

    readonly double[] _at = [double.NaN, double.NaN, double.NaN];
    int _next;

    /// <summary>A change reached a plate at <paramref name="now"/> (ms): whether changes go on and on.</summary>
    public bool Reached(double now)
    {
        double fourthLast = _at[_next];
        _at[_next] = now;
        _next = (_next + 1) % _at.Length;
        return !double.IsNaN(fourthLast) && now - fourthLast <= WithinMs;
    }
}

/// <summary>A neighbour's rounded rectangle in the pixels a joined lens is drawn in: its centre, half its size, its radius.</summary>
public readonly record struct JoinBox(float X, float Y, float HalfW, float HalfH, float Radius);

/// <summary>
/// A plate's lens drawn joined with its neighbours' (<see cref="LiquidJoin"/>): <see cref="Margin"/> pixels more all
/// round the plate (the joined shape reaches beyond it), the shapes run together within <see cref="Blend"/> pixels, the
/// neighbours' boxes (at most <see cref="Most"/>, in the drawn area's pixels), and the plate's tint (not premultiplied),
/// which the lens draws itself then (the plate has no clip of its own while it is joined).
/// </summary>
public sealed record LensJoin(int Margin, float Blend, IReadOnlyList<JoinBox> Others, (float R, float G, float B, float A) Tint)
{
    public const int Most = 8;
}

/// <summary>
/// Glass near glass runs together like drops of water (2026-11-10, the user's "アニメーションも足りないものすべて追加";
/// Apple's glass in a container: shapes "start merging like small droplets of water" when nearer than its spacing, and
/// part again as they move apart): a smooth minimum of the plates' rounded rectangles' distances. As the Control
/// Center's pieces come out of their item (0.6 of their size and place, 7 DIPs apart) they are one drop, and part as
/// they reach their places; going back they run together again. The shader draws it (LensGpu's shape, which must agree).
/// </summary>
public static class LiquidJoin
{
    /// <summary>How far apart shapes still run together at the most (DIPs): two edges nearer than half of it are bridged.</summary>
    public const double Most = 26;

    /// <summary>The blend (DIPs) as glass comes, <paramref name="progress"/> 0–1 of its coming: whole until a third of it, then letting go.</summary>
    public static double Coming(double progress) => Most * (1 - Smooth(0.3, 0.95, progress));

    /// <summary>The blend as glass goes: running together over the first two thirds.</summary>
    public static double Going(double progress) => Most * Smooth(0, 0.65, progress);

    /// <summary>The smooth minimum of two distances within <paramref name="blend"/>: under both by up to a quarter of it where they are equal.</summary>
    public static double Smin(double a, double b, double blend)
    {
        if (blend <= 0) return Math.Min(a, b);
        double h = Math.Max(blend - Math.Abs(a - b), 0) / blend;
        return Math.Min(a, b) - h * h * blend * 0.25;
    }

    /// <summary>The distance to a rounded rectangle (under 0 inside), as the shader's.</summary>
    public static double Box(double x, double y, double cx, double cy, double halfW, double halfH, double radius)
    {
        double r = Math.Min(radius, Math.Min(halfW, halfH));
        double qx = Math.Abs(x - cx) - halfW + r, qy = Math.Abs(y - cy) - halfH + r;
        double ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
        return Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - r;
    }

    /// <summary>How far beyond a plate its joined shape can reach (pixels): a quarter of the blend, and the edge's own pixel.</summary>
    public static int Margin(double blendPx) => blendPx <= 0 ? 0 : (int)Math.Ceiling(blendPx / 4) + 2;

    static double Smooth(double from, double to, double x)
    {
        double t = Math.Clamp((x - from) / (to - from), 0, 1);
        return t * t * (3 - 2 * t);
    }
}
