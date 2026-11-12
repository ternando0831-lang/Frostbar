using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

public sealed class GlassToneTests
{
    // What is behind a part: a white document, the dark wallpaper, a mid grey on the line, a dark window.
    const double White = 0.95, DarkWallpaper = 0.18, Grey = 0.56, DarkWindow = 0.1;

    [Fact]
    public void A_part_showing_takes_its_tone_from_its_first_reading_at_once()
    {
        var tone = new GlassTone(appearanceDark: true);
        Assert.True(tone.Read(White, now: 0));
        Assert.False(tone.Dark);
        Assert.True(tone.Turned);
    }

    [Fact]
    public void Over_the_dark_wallpaper_it_stays_in_the_dark_appearance()
    {
        var tone = new GlassTone(appearanceDark: true);
        Assert.False(tone.Read(DarkWallpaper, 0));
        Assert.True(tone.Dark);
        Assert.False(tone.Read(DarkWallpaper, 1000));
        Assert.True(tone.Dark);
    }

    [Fact]
    public void A_white_window_coming_under_it_turns_it_light_only_once_it_stays_there()
    {
        var tone = new GlassTone(appearanceDark: true);
        tone.Read(DarkWallpaper, 0);
        Assert.False(tone.Read(White, 1000));
        Assert.Equal(1000 + GlassTone.HoldMs, tone.DueAt);
        Assert.False(tone.Read(White, 1000 + GlassTone.HoldMs - 1));
        Assert.True(tone.Dark);
        Assert.True(tone.Read(White, 1000 + GlassTone.HoldMs));
        Assert.False(tone.Dark);
        Assert.Null(tone.DueAt);
    }

    [Fact]
    public void A_window_dragged_across_it_does_not_flash_it()
    {
        var tone = new GlassTone(appearanceDark: true);
        tone.Read(DarkWallpaper, 0);
        tone.Read(White, 1000);
        Assert.False(tone.Read(DarkWallpaper, 1100));
        Assert.Null(tone.DueAt);
        Assert.False(tone.Tick(2000));
        Assert.True(tone.Dark);
    }

    [Fact]
    public void Nothing_behind_changing_the_hold_still_ends()
    {
        // The lens reads only when what is behind changes: a window put down under the part and left there.
        var tone = new GlassTone(appearanceDark: true);
        tone.Read(DarkWallpaper, 0);
        tone.Read(White, 1000);
        Assert.False(tone.Tick(1000 + GlassTone.HoldMs - 1));
        Assert.True(tone.Tick(1000 + GlassTone.HoldMs));
        Assert.False(tone.Dark);
    }

    [Fact]
    public void Near_the_line_it_keeps_whatever_it_is()
    {
        // Between where it turns and where it turns back: no flicker as what is behind hovers about.
        Assert.True(GlassTone.Wanted(appearanceDark: true, dark: true, Grey));
        Assert.False(GlassTone.Wanted(appearanceDark: true, dark: false, Grey));
    }

    [Fact]
    public void It_turns_back_sooner_than_it_turned()
    {
        Assert.True(GlassTone.DarkAgainUnder < GlassTone.LightOver);
        var tone = new GlassTone(appearanceDark: true);
        tone.Read(White, 0);
        Assert.False(tone.Dark);
        tone.Read(DarkWallpaper, 100);
        Assert.True(tone.Read(DarkWallpaper, 100 + GlassTone.HoldMs));
        Assert.True(tone.Dark);
    }

    [Fact]
    public void In_the_light_appearance_it_stays_light_over_anything()
    {
        // A dark wallpaper and a dark window read alike (0.03 to 0.2): the light appearance's glass stays light.
        Assert.False(GlassTone.Adapts(appearanceDark: false));
        var tone = new GlassTone(appearanceDark: false);
        Assert.False(tone.Read(DarkWindow, 0));
        Assert.False(tone.Read(DarkWallpaper, 100));
        Assert.False(tone.Tick(100 + GlassTone.HoldMs));
        Assert.False(tone.Read(White, 1000));
        Assert.False(tone.Dark);
    }

    [Fact]
    public void Hidden_it_keeps_its_tone_and_decides_at_once_again()
    {
        var tone = new GlassTone(appearanceDark: true);
        tone.Read(White, 0);
        tone.Read(DarkWallpaper, 100);
        tone.Hidden();
        Assert.False(tone.Dark);
        Assert.True(double.IsNaN(tone.Luma));
        Assert.Null(tone.DueAt);
        Assert.True(tone.Read(DarkWallpaper, 110));
        Assert.True(tone.Dark);
    }

    [Fact]
    public void No_reading_keeps_it_as_it_is()
    {
        Assert.True(GlassTone.Wanted(true, true, double.NaN));
        Assert.False(GlassTone.Wanted(true, false, double.NaN));
        Assert.False(new GlassTone(true).Tick(10_000));
    }

    [Fact]
    public void A_shadow_is_deeper_over_something_busy_and_shallow_over_a_plain_light_background()
    {
        // Nothing read: as it was.
        Assert.Equal(1, ShadowDepth.Of(double.NaN, double.NaN));
        // A plain dark wallpaper: as it was.
        Assert.Equal(1, ShadowDepth.Of(0.08, 0.01), 3);
        // A plain white document: at its shallowest.
        Assert.Equal(ShadowDepth.Shallowest, ShadowDepth.Of(0.97, 0.0), 3);
        // A picture, or a window's edge across the plate: at its deepest, light or dark.
        Assert.Equal(ShadowDepth.Deepest, ShadowDepth.Of(0.5, 0.3), 3);
        Assert.Equal(ShadowDepth.Deepest, ShadowDepth.Of(0.9, 0.2), 3);
        // Text on white (the brightness varies a little): deeper than over the bare page.
        Assert.True(ShadowDepth.Of(0.9, 0.07) > ShadowDepth.Of(0.9, 0.0) + 0.3);
        // More behind it, never less of a shadow.
        double before = 0;
        for (double d = 0; d <= 0.3; d += 0.01)
        {
            double depth = ShadowDepth.Of(0.9, d);
            Assert.True(depth >= before - 1e-9);
            Assert.InRange(depth, ShadowDepth.Shallowest - 1e-9, ShadowDepth.Deepest + 1e-9);
            before = depth;
        }
    }

    [Fact]
    public void A_shadows_depth_is_shown_anew_only_when_it_differs_enough()
    {
        Assert.False(ShadowDepth.Differs(1, 1.02));
        Assert.True(ShadowDepth.Differs(1, 1.05));
        Assert.True(ShadowDepth.Differs(1.4, 1));
    }
}
