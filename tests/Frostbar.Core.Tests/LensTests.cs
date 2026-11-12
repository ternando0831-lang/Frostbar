using Frostbar.Core.Settings;
using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

public sealed class LensTests
{
    static ScreenRect R(int l, int t, int r, int b) => new(l, t, r, b);

    // The Dock's plate (its window is far larger: the whole bottom), what its lens reads around it, and a menu's.
    static readonly ScreenRect DockPlate = R(600, 990, 1320, 1065);
    static readonly ScreenRect DockRead = LensDamage.Grow(DockPlate, 40);
    static readonly ScreenRect MenuPlate = R(100, 30, 400, 330);
    static readonly ScreenRect MenuRead = LensDamage.Grow(MenuPlate, 30);

    [Fact]
    public void The_plates_own_drawing_reaches_nothing()
    {
        // S31: the lens drawn again on its plate came back as a change there, 60 times a second without end.
        var reached = LensDamage.Reached([DockPlate, R(120, 60, 380, 82)], [DockPlate, MenuPlate], [DockRead, MenuRead]);
        Assert.Equal([false, false], reached);
    }

    [Fact]
    public void A_change_beside_a_plate_reaches_it_and_no_other()
    {
        // Behind the Dock's window but beside its plate: inside the window, outside the plate.
        var reached = LensDamage.Reached([R(500, 960, 580, 990)], [DockPlate, MenuPlate], [DockRead, MenuRead]);
        Assert.Equal([true, false], reached);
    }

    [Fact]
    public void An_icon_bouncing_above_the_plate_reaches_it()
    {
        // The shell drawing beside its plate: one drawing for each frame of it (its echo is inside the plate).
        var reached = LensDamage.Reached([R(700, 940, 760, 1010)], [DockPlate], [DockRead]);
        Assert.Equal([true], reached);
    }

    [Fact]
    public void A_window_behind_that_reaches_out_from_under_the_plate_counts()
    {
        // A video under the menu: its change is the video's rectangle, larger than the menu.
        var reached = LensDamage.Reached([R(80, 100, 700, 500)], [MenuPlate, DockPlate], [DockRead, MenuRead]);
        Assert.Equal([false, true], reached);
    }

    [Fact]
    public void A_change_far_away_reaches_no_plate() =>
        Assert.Equal([false, false], LensDamage.Reached([R(1500, 300, 1700, 500)], [], [DockRead, MenuRead]));

    [Fact]
    public void A_change_only_just_touching_the_read_area_counts_and_one_only_beside_it_does_not()
    {
        var read = R(100, 100, 200, 200);
        Assert.Equal([true], LensDamage.Reached([R(199, 150, 260, 160)], [], [read]));
        Assert.Equal([false], LensDamage.Reached([R(200, 150, 260, 160)], [], [read]));
    }

    [Fact]
    public void Empty_changes_are_skipped() =>
        Assert.Equal([false], LensDamage.Reached([R(150, 150, 150, 170)], [], [R(100, 100, 200, 200)]));

    [Fact]
    public void A_change_over_two_plates_is_not_inside_either()
    {
        // The change spans both, so it is new to whatever reads there.
        var reached = LensDamage.Reached([R(100, 30, 1320, 1065)], [MenuPlate, DockPlate], [DockRead]);
        Assert.Equal([true], reached);
    }

    [Theory]
    [InlineData(1, 64)]
    [InlineData(64, 64)]
    [InlineData(65, 128)]
    [InlineData(900, 960)]
    public void Surfaces_are_rounded_up_to_64(int px, int want) => Assert.Equal(want, LensSurfaceSize.Round(px));

    [Theory]
    [InlineData(960, 900, false)]  // the Dock magnifying within its surface
    [InlineData(960, 961, true)]   // outgrown
    [InlineData(960, 480, true)]   // under half: a smaller one
    [InlineData(960, 481, false)]
    [InlineData(64, 20, false)]    // a notification's ×: the smallest surface is its own (it was made anew at each drawing)
    [InlineData(64, 32, false)]
    [InlineData(128, 20, true)]    // it was a larger plate's: a smaller one
    public void A_new_surface_only_when_outgrown_or_much_too_large(int have, int want, bool remake) =>
        Assert.Equal(remake, LensSurfaceSize.Remake(have, want));

    // ---- the look: Liquid Glass as Apple's own pictures show it (the user's "B4", 2026-10-18) ----

    [Fact]
    public void The_Docks_lens_frosts_less_than_the_menus()
    {
        var clear = LensLook.For(LensStyle.Clear, 1.25).Fit(80);
        var frosted = LensLook.For(LensStyle.Frosted, 1.25).Fit(300);
        Assert.True(clear.Frost < frosted.Frost);
        Assert.True(clear.Lod < frosted.Lod);
    }

    [Theory]
    [InlineData(LensStyle.Clear)]
    [InlineData(LensStyle.Frosted)]
    public void Every_lens_gathers_light_and_is_a_dome(LensStyle style)
    {
        var look = LensLook.For(style, 1.25);
        Assert.True(look.Gain > 1.2f);       // brighter, its colour kept
        Assert.True(look.Saturation > 1.4f); // more vivid
        Assert.True(look.Magnify > 1);       // what is behind shows larger
        Assert.True(look.Fresnel > 0 && look.FresnelWidth > 0);
        Assert.True(look.Smear > 0);
    }

    [Fact]
    public void Looks_are_in_DIPs()
    {
        Assert.Equal(LensLook.For(LensStyle.Clear, 1).Bezel * 2, LensLook.For(LensStyle.Clear, 2).Bezel, 3);
        Assert.Equal(LensLook.For(LensStyle.Frosted, 1).Smear * 2, LensLook.For(LensStyle.Frosted, 2).Smear, 3);
        Assert.Equal(default, LensLook.For(LensStyle.None, 1.25));
        Assert.Equal(0, default(LensLook).Reach);
    }

    [Fact]
    public void Dark_glass_holds_down_the_bright_and_light_glass_raises_the_dark()
    {
        var dark = LensLook.For(LensStyle.Frosted, 1.25, dark: true);
        var light = LensLook.For(LensStyle.Frosted, 1.25, dark: false);
        Assert.True(dark.Dark && !light.Dark);
        Assert.True(dark.Knee < 0.5f && light.Knee > 0.5f); // white text over dark glass, black over light
        Assert.InRange(dark.Slope, 0.05f, 0.6f);
    }

    static float Luma((float r, float g, float b) c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

    [Theory]
    [InlineData(75.0)]          // a piece of the Control Center
    [InlineData(296 * 1.25)]    // a view, a menu
    public void Light_glass_over_white_stays_a_little_under_white_and_has_an_edge(double side)
    {
        // It was white itself over a white window, its rim lit white: the pieces and the cards vanished (2026-10-19).
        var light = LensLook.For(LensStyle.Frosted, 1.25, dark: false).Fit(side);
        var white = light.Tone(1, 1, 1);
        Assert.InRange(Luma(white), 0.88f, 0.96f);
        // Its edge's pixel a dark line over the white window: its outline (Apple's light picture: 0.55–0.8 of what is outside).
        Assert.InRange(Luma(light.AtRim(white, (1, 1, 1), 0.5f, 0)), 0.4f, 0.65f);
        // Dark glass holds the bright down (about 0.53 on a piece).
        var dark = LensLook.For(LensStyle.Frosted, 1.25, dark: true).Fit(side);
        Assert.True(Luma(dark.Tone(1, 1, 1)) < 0.6f, $"dark glass over white {Luma(dark.Tone(1, 1, 1))}");
    }

    [Theory]
    [InlineData(LensStyle.Frosted, true, 75.0)]
    [InlineData(LensStyle.Frosted, false, 75.0)]
    [InlineData(LensStyle.Frosted, true, 296 * 1.25)]
    [InlineData(LensStyle.Clear, true, 70.0)]
    public void The_rim_is_Apples_a_bright_line_inside_a_dark_one(LensStyle style, bool dark, double side)
    {
        // HIG Materials' regular glass over its dark picture, measured on it (2026-11-06): 27 of 255 inside the glass and
        // 12 outside it; the bright line just inside the edge 144 on the top rim, 118 on the bottom and 74 on the sides;
        // the edge's own pixel 6–17 (0.46–0.77 of what is outside). Before, the rim reached 33 there and had no dark line.
        var look = LensLook.For(style, 1.25, dark).Fit(side);
        float inside = 27 / 255f, outside = 12 / 255f;
        (float, float, float) In = (inside, inside, inside), Out = (outside, outside, outside);
        float Peak(float ny) => Enumerable.Range(0, 8).Max(i => look.AtRim(In, Out, i + 0.5f, ny).Item1) * 255;
        Assert.InRange(Peak(-1), 120, 165);  // the top
        Assert.InRange(Peak(1), 100, 140);   // the bottom
        Assert.InRange(Peak(0), 60, 95);     // the sides
        Assert.InRange(Peak(-0.7071f), 85, 115); // 45° (measured 91–99 on the top's side)
        float edge = look.AtRim(In, Out, 0.5f, -1).Item1;
        Assert.InRange(edge, outside * 0.4f, outside * 0.95f); // darker than what is outside
        Assert.Equal(inside, look.AtRim(In, Out, 8.5f, -1).Item1, 3); // well inside: the glass as it is
        // The line is about a DIP: a pixel more in it is under half of its peak.
        Assert.True(look.AtRim(In, Out, 3.5f, -1).Item1 - inside < (Peak(-1) / 255 - inside) / 2);
    }

    /// <summary>
    /// The rim's lines are taken at four points across a pixel (<see cref="LensLook.RimSamples"/>): on a straight rim the
    /// brightest and the darkest pixels are as one point at the middle gave them; on a 45° rim, a pixel the bright line
    /// passes beside (0.9 of a pixel out from its middle) takes part of it, where one point took little. Taken at the
    /// middle alone, the lines stepped a pixel at a time along a rounded corner (2026-11-08, the user's "ギザギザを直す").
    /// </summary>
    [Theory]
    [InlineData(LensStyle.Frosted, true, 75.0)]
    [InlineData(LensStyle.Clear, true, 70.0)]
    [InlineData(LensStyle.Frosted, false, 296 * 1.25)]
    public void The_rims_lines_are_taken_across_the_pixel(LensStyle style, bool dark, double side)
    {
        var look = LensLook.For(style, 1.25, dark).Fit(side);
        // A straight top rim (its normal (0, -1)): a point y below the middle is y deeper.
        float[] Straight(float depth) => LensLook.RimSamples.Select(s => depth + s.y).ToArray();
        float Most(Func<float, float> at) => Enumerable.Range(0, 8).Max(i => at(i + 0.5f));
        // (The line's middle need not be a pixel's: one point then gives a little less than the line's own most.)
        Assert.InRange(Most(d => look.Rim(Straight(d), -1).lift) - Most(d => look.Rim(d, -1).lift), -0.03f, 0.03f);
        Assert.InRange(look.Rim(Straight(0.5f), -1).contact - look.Rim(0.5f, -1).contact, -0.03f, 0.03f);
        // A 45° rim (its normal (-0.71, -0.71)): a point (x, y) from the middle is (x + y) × 0.71 deeper.
        float w = Math.Max(look.LinePx, 0.5f), beside = 1 + 0.5f * w - 0.9f;
        float one = look.Rim(beside, -0.7071f).lift;
        float across = look.Rim(LensLook.RimSamples.Select(s => beside + (s.x + s.y) * 0.7071f).ToArray(), -0.7071f).lift;
        Assert.True(across > 2 * one, $"across the pixel {across:0.000}, at its middle {one:0.000}");
    }

    [Fact]
    public void Light_glass_over_black_is_light_and_lighter_on_a_large_plate()
    {
        // A light menu over a dark wallpaper came out a dull mid grey (2026-10-19): Apple's light menus stay light.
        var look = LensLook.For(LensStyle.Frosted, 1.25, dark: false);
        float piece = Luma(look.Fit(75).Tone(0, 0, 0)), menu = Luma(look.Fit(296 * 1.25).Tone(0, 0, 0));
        Assert.True(piece >= 0.45f, $"a piece over black {piece}");
        Assert.True(menu >= 0.6f, $"a menu over black {menu}");
    }

    [Fact]
    public void The_reach_covers_the_pull_the_light_around_the_frost_and_its_smear()
    {
        foreach (double side in new[] { 75.0, 400.0 })
        {
            var l = LensLook.For(LensStyle.Frosted, 1.25).Fit(side);
            // The pull, two sigmas of the frost and a texel of the coarsest level it reads; the light around and its level's texel.
            Assert.True(l.Reach >= l.Strength * (1 + l.Dispersion) + 2 * LensLook.SigmaPerFrost * l.Frost + (1 << LensLook.FrostLevel(l.Frost)), $"{side}: {l.Reach}");
            Assert.True(l.Reach >= l.EnvOffset + (1 << ((int)l.Lod + 1)), $"{side}: {l.Reach}");
        }
    }

    [Theory]
    // A plate is drawn again for every change within its reach (and the capture kept fast for 3 s after each): no
    // farther than the 13-point frost reached (25, 43 and 44 px at 125%: the Dock, a piece, a menu). Three pixels more
    // on the Dock let a desktop pet's idle animation above it draw the lens 137 times a minute (2026-11-10).
    [InlineData(LensStyle.Clear, 85.0, 25)]
    [InlineData(LensStyle.Frosted, 75.0, 43)]
    [InlineData(LensStyle.Frosted, 300.0, 44)]
    public void The_reach_is_no_more_than_it_was(LensStyle style, double side, int was) =>
        Assert.InRange(LensLook.For(style, 1.25).Fit(side).Reach, 12, was);

    [Theory]
    // The spread a level of the smooth pyramid gives through the spline: 7/12 × 4^level − 1/4 px² (0.58, 1.44, 3.0, 6.1
    // and 12.2 px). A frost reads the two levels either side of its own (0.6 × its pixels, squared).
    [InlineData(0f, 0)]      // none: the picture as it is
    [InlineData(0.9f, 0)]    // 0.54 px: between the picture and level 0
    [InlineData(1.5f, 1)]    // 0.9 px: levels 0 and 1
    [InlineData(5f, 2)]      // the Dock at 125%: 3.0 px, levels 1 and 2
    [InlineData(11.25f, 4)]  // a piece of the Control Center: 6.75 px, levels 3 and 4
    [InlineData(17.5f, 4)]   // a menu: 10.5 px, levels 3 and 4
    [InlineData(28f, 5)]     // a menu at 200%: 16.8 px, levels 4 and 5
    [InlineData(400f, 5)]    // no level past the fifth
    public void A_frost_reads_the_two_levels_either_side_of_its_spread(float frost, int coarsest) =>
        Assert.Equal(coarsest, LensLook.FrostLevel(frost));

    [Theory]
    // The rim's own bend, the user's "B 中": 24 DIPs within 12 of the edge on a plate large enough (60 DIPs and over);
    // on a smaller one no more than 0.4 of its side within 0.2 of it, so that the bend stays this side of its middle.
    [InlineData(75.0, 30f, 15f)]     // a piece of the Control Center at 125%: 24 and 12 DIPs
    [InlineData(300.0, 30f, 15f)]    // a menu
    [InlineData(32.5, 13f, 6.5f)]    // a 26-DIP capsule ("Clear All")
    [InlineData(25.0, 10f, 5f)]      // a notification's ×
    public void The_rims_bend_is_24_DIPs_within_12_and_less_on_a_small_plate(double side, float bend, float width)
    {
        var l = LensLook.For(LensStyle.Frosted, 1.25).Fit(side);
        Assert.Equal(bend, l.RimBend, 3);
        Assert.Equal(width, l.RimWidth, 3);
        Assert.Equal(bend, l.RimBendAt(0), 3);
        Assert.Equal(bend / 4, l.RimBendAt(width / 2), 3);
        Assert.Equal(0, l.RimBendAt(width));
        for (float depth = 0; depth <= l.RimWidth; depth += 0.5f)
            Assert.True(depth + l.RimBendAt(depth) <= side / 2 + 1e-3, $"{depth} px in, the rim reads this side of the plate's middle");
        Assert.Equal(0, l.At(0).RimBend);
    }

    [Fact]
    public void A_plate_coming_by_its_lens_has_its_light_travel_round_its_rim()
    {
        var look = LensLook.For(LensStyle.Frosted, 1.25).Fit(75);
        // At rest: lit from above (the top rim's normal is (0, -1)).
        Assert.Equal(0, look.LightTurn);
        Assert.Equal(-1, look.TurnedY(0, -1));
        // Beginning to come: the light is 100° round, nearest the left rim; half way, between; there, at the top.
        var coming = look.At(0);
        Assert.Equal(LensLook.MaterializeTurn, coming.LightTurn, 3);
        Assert.True(coming.TurnedY(-1, 0) < -0.95f, "the left rim is lit first");
        Assert.True(coming.TurnedY(0, -1) > -0.3f, "not the top yet");
        float half = look.At(0.5).TurnedY(0, -1);
        Assert.InRange(half, -0.8f, -0.5f);
        Assert.Equal(0, look.At(1).LightTurn);
    }

    [Fact]
    public void The_noise_that_keeps_gradients_from_stepping_comes_with_the_plate()
    {
        var look = LensLook.For(LensStyle.Frosted, 1.25);
        Assert.Equal(1, look.Dither);
        Assert.Equal(0, look.At(0).Dither);
        Assert.Equal(0.5f, look.At(0.5).Dither, 3);
        Assert.Equal(0, default(LensLook).Dither);
    }

    [Fact]
    public void The_lens_is_on_by_default() => Assert.True(new AppearanceSettings().Lens);

    [Fact]
    public void A_small_piece_is_all_dome()
    {
        // macOS 27's Control Center: a 60-DIP circle at 125% is 75 px, all of it dome (as Apple's are), the most magnified.
        var look = LensLook.For(LensStyle.Frosted, 1.25);
        var fit = look.Fit(75);
        Assert.Equal(75 / 2f, fit.Bezel, 3);
        Assert.Equal(0.08f * 75 / 2f, fit.Strength, 3);
        Assert.Equal(9 * 1.25f, fit.Frost, 3);
        Assert.Equal(1.12f, fit.Magnify, 3);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_large_plate_gathers_less_light_than_a_small_piece(bool dark)
    {
        // A view the Control Center's width showed what was behind it as a vivid smear of colour under its rows with the
        // small pieces' look (the user's "bluetoothとかの個々の画面すごい滲み方する", 2026-10-18).
        var look = LensLook.For(LensStyle.Frosted, 1.25, dark);
        var piece = look.Fit(75);
        var view = look.Fit(296 * 1.25);
        Assert.True(view.Gain < piece.Gain - 0.15f, $"gain {piece.Gain} → {view.Gain}");
        Assert.True(view.Saturation < piece.Saturation - 0.2f, $"saturation {piece.Saturation} → {view.Saturation}");
        Assert.True(view.Smear < piece.Smear, $"smear {piece.Smear} → {view.Smear}");
        if (dark) Assert.True(view.Knee < piece.Knee); // the bright held down more
        else Assert.True(view.Knee > piece.Knee);      // the dark raised more
        // The Control Center's modules (82 DIP high) keep nearly the pieces' look.
        Assert.True(look.Fit(82 * 1.25).Gain > piece.Gain - 0.05f);
    }

    [Fact]
    public void A_large_plate_has_a_rounded_rim_more_frost_and_a_flatter_dome()
    {
        var look = LensLook.For(LensStyle.Frosted, 1.25);
        var menu = look.Fit(240 * 1.25);
        Assert.Equal(30 * 1.25f, menu.Bezel, 3); // the rim, not half its side
        Assert.Equal(14 * 1.25f, menu.Frost, 3);
        Assert.Equal(1.05f, menu.Magnify, 3);
        Assert.Equal(3, menu.Lod);
        // Between the two, by its size.
        var mid = look.Fit(125 * 1.25);
        Assert.InRange(mid.Frost, 9 * 1.25f + 0.1f, 14 * 1.25f - 0.1f);
        Assert.Equal(default, default(LensLook).Fit(40));
    }

    [Theory]
    // What moves on and on behind a plate is followed at the display's pace (twice the cost): the fourth change to
    // reach a plate within a quarter of a second says so.
    [InlineData(16.7, true)]    // a bar sweeping at every frame
    [InlineData(33.3, true)]    // followed at 30 a second
    [InlineData(41.7, true)]    // a film at 24 frames a second
    [InlineData(100.0, false)]  // a test target drawn every 100 ms
    [InlineData(421.0, false)]  // a desktop pet's idle animation (2026-11-10)
    [InlineData(1000.0, false)] // a clock's seconds
    public void Changes_that_go_on_and_on_are_told_from_ones_now_and_then(double every, bool onAndOn)
    {
        var follow = new LensFollow();
        bool told = false;
        for (int i = 0; i < 12; i++) told |= follow.Reached(1000 + i * every);
        Assert.Equal(onAndOn, told);
    }

    [Fact]
    public void Glass_near_glass_runs_together_and_glass_far_apart_does_not()
    {
        // Two 60-px circles side by side, the point half way between them (px from the first's middle: 30 + gap / 2).
        static double Between(double gap, double blend) =>
            LiquidJoin.Smin(LiquidJoin.Box(30 + gap / 2, 0, 0, 0, 30, 30, 30), LiquidJoin.Box(30 + gap / 2, 0, 60 + gap, 0, 30, 30, 30), blend);
        // 8 px apart within 26: bridged (the point between them is inside the joined shape); 20 px apart: not.
        Assert.True(Between(8, 26) < 0);
        Assert.True(Between(20, 26) > 0);
        // No blend: each its own shape.
        Assert.Equal(4, Between(8, 0), 6);
        // Away from the other, a shape is its own (the smooth minimum is the nearer one where they differ by the blend).
        Assert.Equal(LiquidJoin.Box(-40, 0, 0, 0, 30, 30, 30), LiquidJoin.Smin(LiquidJoin.Box(-40, 0, 0, 0, 30, 30, 30), LiquidJoin.Box(-40, 0, 68, 0, 30, 30, 30), 26), 6);
        // The joined shape reaches at most a quarter of the blend beyond a plate: the margin covers it and the edge's pixel.
        Assert.Equal(0, LiquidJoin.Margin(0));
        Assert.Equal(9, LiquidJoin.Margin(26));
        Assert.True(LiquidJoin.Margin(32.5) >= 32.5 / 4 + 1);
    }

    [Fact]
    public void A_panels_pieces_come_as_one_drop_and_part_and_run_together_again_as_they_go()
    {
        Assert.Equal(LiquidJoin.Most, LiquidJoin.Coming(0));
        Assert.Equal(LiquidJoin.Most, LiquidJoin.Coming(0.3), 6);
        Assert.Equal(0, LiquidJoin.Coming(1));
        Assert.Equal(0, LiquidJoin.Going(0));
        Assert.Equal(LiquidJoin.Most, LiquidJoin.Going(0.65), 6);
        Assert.Equal(LiquidJoin.Most, LiquidJoin.Going(1));
        double before = LiquidJoin.Most;
        for (double t = 0; t <= 1; t += 0.05)
        {
            Assert.True(LiquidJoin.Coming(t) <= before + 1e-9, "letting go only");
            before = LiquidJoin.Coming(t);
        }
        // Coming out at 0.6 of their size, the Control Center's pieces are 7 DIPs apart: under half the blend, one drop.
        Assert.True(12 * 0.6 < LiquidJoin.Most / 2);
    }

    [Fact]
    public void Three_changes_at_once_and_then_a_pause_are_not_going_on_and_on()
    {
        var follow = new LensFollow();
        Assert.False(follow.Reached(0));
        Assert.False(follow.Reached(10));
        Assert.False(follow.Reached(20));
        Assert.False(follow.Reached(400)); // the fourth, but 400 ms after the first
        Assert.False(follow.Reached(410));
        Assert.False(follow.Reached(420));
        Assert.True(follow.Reached(430)); // the fourth of a new run: 400, 410, 420, 430
    }
}
