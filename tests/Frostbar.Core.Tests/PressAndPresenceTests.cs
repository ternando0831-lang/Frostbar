using Frostbar.Core.AppGrid;
using Frostbar.Core.Dock;
using Frostbar.Core.Shell;
using Xunit;

namespace Frostbar.Core.Tests;

/// <summary>A press giving and its light spilling (2026-10-23, the user's "A 沈む＋隣へ光る").</summary>
public sealed class PressGiveTests
{
    [Theory]
    [InlineData(300, 28, false, PressKind.Row)]       // a menu's or the settings' row
    [InlineData(60, 60, true, PressKind.Glass)]       // a Control Center piece
    [InlineData(336, 84, true, PressKind.Glass)]      // a notification's card: its own glass, however wide
    [InlineData(48, 48, false, PressKind.Icon)]       // an icon
    [InlineData(30, 26, false, PressKind.Icon)]       // the settings' ‹ ›
    [InlineData(88, 24, false, PressKind.Button)]     // a push button
    [InlineData(120, 68, false, PressKind.Picture)]   // a wallpaper's picture
    [InlineData(84, 92, false, PressKind.Picture)]    // a stack's cell
    [InlineData(400, 250, false, PressKind.Window)]   // a window in the overview
    public void Each_kind_from_its_size(double w, double h, bool glass, PressKind kind) => Assert.Equal(kind, PressGive.Of(w, h, glass));

    [Fact]
    public void A_row_does_not_move_and_glass_gives_most()
    {
        Assert.Equal(1, PressGive.Depth(PressKind.Row));
        Assert.Equal(0.9, PressGive.Depth(PressKind.Glass));
        Assert.True(PressGive.Depth(PressKind.Glass) < PressGive.Depth(PressKind.Icon));
        Assert.True(PressGive.Depth(PressKind.Icon) < PressGive.Depth(PressKind.Picture));
        Assert.True(PressGive.Depth(PressKind.Picture) < PressGive.Depth(PressKind.Button));
        Assert.True(PressGive.Depth(PressKind.Button) < PressGive.Depth(PressKind.Window));
    }

    [Fact]
    public void A_press_reaches_its_depth_and_a_let_go_springs_past_full_size_and_settles()
    {
        Assert.Equal(1, PressGive.Pressing(1, PressGive.Icon, 0), 6);
        Assert.Equal(PressGive.Icon, PressGive.Pressing(1, PressGive.Icon, MotionTable.BouncePress), 6);
        double most = Enumerable.Range(0, 481).Select(ms => PressGive.LettingGo(PressGive.Icon, ms)).Max();
        Assert.InRange(most, 1.002, 1.006); // past full size, subtly: Apple's "subtly bounces", not the 3.5% of until 2026-11-01
        Assert.Equal(1, PressGive.LettingGo(PressGive.Icon, MotionTable.BounceRelease), 6);
        Assert.False(PressGive.Settling(MotionTable.BounceRelease));
    }

    [Fact]
    public void A_let_go_midway_springs_from_where_it_is()
    {
        double half = PressGive.Pressing(1, PressGive.Glass, 30);
        Assert.InRange(half, PressGive.Glass + 0.001, 0.999);
        Assert.Equal(half, PressGive.LettingGo(half, 0), 6);
        // And a press during a spring back goes on from there, not from 1.
        double back = PressGive.LettingGo(PressGive.Glass, 80);
        Assert.Equal(back, PressGive.Pressing(back, PressGive.Glass, 0), 6);
    }

    [Fact]
    public void The_shade_comes_quickly_and_goes_from_where_it_is()
    {
        Assert.Equal(0, PressGive.ShadeIn(0, 0), 6);
        Assert.Equal(1, PressGive.ShadeIn(0, MotionTable.PressShadeIn), 6);
        double some = PressGive.ShadeIn(0, 20);
        Assert.InRange(some, 0.3, 0.99);
        Assert.Equal(some, PressGive.ShadeOut(some, 0), 6);
        Assert.Equal(0, PressGive.ShadeOut(some, MotionTable.PressShadeOut), 6);
    }
}

public sealed class GlassSpillTests
{
    [Fact]
    public void A_point_inside_is_at_no_distance()
    {
        var (x, y, d) = GlassSpill.Nearest(new RoundedBox(0, 0, 100, 60, 20), 50, 30);
        Assert.Equal((50.0, 30.0, 0.0), (x, y, d));
    }

    [Fact]
    public void Beside_an_edge_the_nearest_point_is_on_it()
    {
        var (x, y, d) = GlassSpill.Nearest(new RoundedBox(0, 0, 100, 60, 20), 50, 100);
        Assert.Equal(50, x, 6);
        Assert.Equal(60, y, 6);
        Assert.Equal(40, d, 6);
    }

    [Fact]
    public void A_corner_is_measured_to_its_arc()
    {
        // Off the top left corner along the diagonal: the arc's point, not the box's corner.
        double off = 30 / Math.Sqrt(2);
        var (x, y, d) = GlassSpill.Nearest(new RoundedBox(0, 0, 100, 100, 30), -off, -off);
        double arc = 30 - 30 / Math.Sqrt(2);
        Assert.Equal(arc, x, 6);
        Assert.Equal(arc, y, 6);
        Assert.Equal((30 + off) * Math.Sqrt(2) - 30, d, 6); // from the corner's centre (30, 30), less the radius
        // A square box's corner would be nearer.
        Assert.True(d > GlassSpill.Nearest(new RoundedBox(0, 0, 100, 100, 0), -off, -off).distance);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(90, 0.5)]
    [InlineData(180, 0)]
    [InlineData(400, 0)]
    public void The_light_fades_with_distance(double d, double s) => Assert.Equal(s, GlassSpill.Strength(d), 6);

    [Fact]
    public void The_light_rises_then_goes()
    {
        Assert.Equal(0, GlassSpill.Envelope(0));
        Assert.Equal(1, GlassSpill.Envelope(MotionTable.SpillRise), 6);
        Assert.Equal(0, GlassSpill.Envelope(1));
        double last = 0;
        for (double t = 0; t <= MotionTable.SpillRise; t += 0.01) { double e = GlassSpill.Envelope(t); Assert.True(e >= last - 1e-9); last = e; }
        last = 1;
        for (double t = MotionTable.SpillRise; t <= 1; t += 0.01) { double e = GlassSpill.Envelope(t); Assert.True(e <= last + 1e-9); last = e; }
    }

    [Fact]
    public void Only_the_pieces_within_reach_are_lit_and_not_the_one_pressed()
    {
        RoundedBox[] boxes = [new(0, 0, 60, 60, 30), new(72, 0, 60, 60, 30), new(400, 0, 60, 60, 30), new(0, 72, 132, 60, 30)];
        var lit = GlassSpill.Spread(boxes, 30, 30, own: 0);
        Assert.Equal([1, 3], lit.Select(l => l.index).Order().ToArray());
        Assert.All(lit, l => Assert.InRange(l.strength, 0.01, 1));
        // The piece next door: 42 DIPs from the press to its rim.
        Assert.Equal(1 - 42.0 / GlassSpill.Reach, lit.Single(l => l.index == 1).strength, 6);
        Assert.Equal((72.0, 30.0), (lit.Single(l => l.index == 1).x, lit.Single(l => l.index == 1).y));
    }
}

public sealed class LensPresenceTests
{
    static IEnumerable<LensLook> Looks()
    {
        foreach (var style in new[] { LensStyle.Clear, LensStyle.Frosted })
            foreach (bool dark in new[] { true, false })
                foreach (double side in new[] { 60 * 1.25, 400 * 1.25 })
                    yield return LensLook.For(style, 1.25, dark).Fit(side);
    }

    [Fact]
    public void A_lens_not_there_leaves_every_colour_as_it_is()
    {
        float[] steps = [0, 0.125f, 0.25f, 0.375f, 0.5f, 0.625f, 0.75f, 0.875f, 1];
        foreach (var look in Looks())
        {
            var none = look.At(0);
            foreach (float r in steps) foreach (float g in steps) foreach (float b in steps)
            {
                var (tr, tg, tb) = none.Tone(r, g, b);
                Assert.Equal(r, tr, 5);
                Assert.Equal(g, tg, 5);
                Assert.Equal(b, tb, 5);
            }
        }
    }

    [Fact]
    public void A_lens_not_there_bends_and_frosts_nothing_and_lights_no_rim()
    {
        foreach (var look in Looks())
        {
            var none = look.At(0);
            Assert.Equal(0, none.Strength);
            Assert.Equal(0, none.Frost);
            Assert.Equal(0, none.Lod);
            Assert.Equal(0, none.Smear);
            Assert.Equal(0, none.Dispersion);
            Assert.Equal(0, none.Fresnel);
            Assert.Equal(0, none.Specular);
            Assert.Equal(0, none.Contact);
            // Its rim leaves what is behind as it is (the edge's own pixel too).
            Assert.Equal((0.3f, 0.5f, 0.7f), none.AtRim((0.3f, 0.5f, 0.7f), (0.1f, 0.1f, 0.1f), 0.5f, -1));
            Assert.Equal(1, none.Magnify);
            Assert.Equal(0, none.Reach);
            // What does not change what is behind stays: the bezel, where the light around is read.
            Assert.Equal(look.Bezel, none.Bezel);
            Assert.Equal(look.EnvOffset, none.EnvOffset);
        }
    }

    [Fact]
    public void All_the_way_in_it_is_the_look()
    {
        foreach (var look in Looks()) Assert.Equal(look, look.At(1));
    }

    [Fact]
    public void It_comes_by_degrees()
    {
        foreach (var look in Looks())
        {
            double last = -1;
            for (double p = 0; p <= 1.0001; p += 0.125)
            {
                var at = look.At(p);
                // How far a mid colour and a bright one move from themselves, and how much it bends and frosts.
                var (r1, g1, b1) = at.Tone(0.9f, 0.6f, 0.3f);
                var (r2, g2, b2) = at.Tone(0.2f, 0.4f, 0.95f);
                double moved = Math.Abs(r1 - 0.9) + Math.Abs(g1 - 0.6) + Math.Abs(b1 - 0.3) + Math.Abs(r2 - 0.2) + Math.Abs(g2 - 0.4) + Math.Abs(b2 - 0.95)
                    + at.Strength + at.Frost + (at.Magnify - 1) + at.Fresnel;
                Assert.True(moved >= last - 1e-6, $"{look.Dark} {p}: {moved} after {last}");
                last = moved;
            }
        }
    }

    [Fact]
    public void Without_its_roll_off_a_bright_colour_keeps_its_gain()
    {
        var look = LensLook.For(LensStyle.Frosted, 1, dark: true) with { Saturation = 1, Contrast = 1, Gain = 1, Knee = 2 };
        Assert.True(look.Tone(0.95f, 0.95f, 0.95f).r < 0.95f); // rolled off
        Assert.Equal(0.95f, (look with { Roll = 0 }).Tone(0.95f, 0.95f, 0.95f).r, 5);
    }

    [Fact]
    public void A_menu_comes_and_goes_and_its_content_goes_first()
    {
        Assert.Equal(0, LensPresence.Open(0));
        Assert.Equal(1, LensPresence.Open(1));
        Assert.Equal(1, LensPresence.Close(0));
        Assert.Equal(0, LensPresence.Close(1));
        Assert.Equal(0, LensPresence.ContentClose(MotionTable.PresenceContentOut), 6);
        Assert.Equal(0, LensPresence.ContentClose(0.5, share: 0.5), 6); // any share: gone by then
        Assert.True(LensPresence.ContentClose(0.5, share: 1) > 0.4);
        Assert.Equal(0, LensPresence.ContentClose(0.1, share: 0), 6); // no share: gone at once
        Assert.True(LensPresence.Close(MotionTable.PresenceContentOut) > 0.1); // the glass still there when its content is gone
    }

    [Fact]
    public void A_panel_of_pieces_has_its_glass_before_its_content_and_after_it_going()
    {
        Assert.Equal(1, LensPresence.Materialize(1 / 2.6), 6);
        Assert.Equal(1, LensPresence.Dematerialize(0.3), 6);
        Assert.Equal(0, LensPresence.Dematerialize(1), 6);
    }

    [Fact]
    public void A_plate_over_another_lens_fades_instead()
    {
        var menu = new ScreenRect(100, 30, 400, 330);
        Assert.False(LensPresence.Allowed(new ScreenRect(390, 80, 650, 300), [menu])); // a submenu over its parent
        Assert.True(LensPresence.Allowed(new ScreenRect(400, 80, 650, 300), [menu]));  // beside it
        Assert.True(LensPresence.Allowed(menu, []));
    }

    [Fact]
    public void A_curve_sampled_for_key_frames()
    {
        var keys = MotionTable.Sample(GlassSpill.Envelope, 24);
        Assert.Equal(25, keys.Length);
        Assert.Equal((0f, 0f), keys[0]);
        Assert.Equal(1f, keys[^1].at);
        Assert.Equal(6, Array.FindIndex(keys, k => k.value == keys.Max(m => m.value))); // 0.25 of 24
    }
}

public sealed class LaunchTests
{
    static DockItem App(string key, string id, string? target, params RunningWindow[] windows) =>
        new(DockItemKind.App, key, id, id, target, key.StartsWith(DockModel.PinPrefix), windows, false);

    static RunningWindow W(long h, string app, string exe) => new(h, app, app, exe, false);

    [Theory]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", false, LaunchKind.AppsFolder)]
    [InlineData(@"C:\Users\u\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\App.lnk", false, LaunchKind.Shortcut)]
    [InlineData(@"C:\Program Files\App\app.exe", false, LaunchKind.Program)]
    [InlineData(@"C:\Users\u\Documents\a.txt", false, LaunchKind.Document)]
    [InlineData(@"C:\Users\u\Documents", true, LaunchKind.Folder)]
    [InlineData("https://example.com/", false, LaunchKind.Uri)]
    [InlineData("ms-settings:display", false, LaunchKind.Uri)]
    [InlineData(@"\\server\share\app.exe", false, LaunchKind.Program)]
    public void What_a_launch_opens(string target, bool dir, LaunchKind kind) => Assert.Equal(kind, LaunchTargets.Kind(target, dir));

    [Fact]
    public void An_app_grid_target_names_its_AppID() =>
        Assert.Equal("Microsoft.WindowsNotepad_8wekyb3d8bbwe!App", LaunchTargets.AppsFolderId(@"shell:AppsFolder\Microsoft.WindowsNotepad_8wekyb3d8bbwe!App"));

    [Fact]
    public void The_Dock_item_is_found_by_its_AppID_then_by_its_program()
    {
        DockItem[] items =
        [
            App("pin:a", "A.Id", @"C:\a\a.lnk"),
            new(DockItemKind.Separator, DockModel.SeparatorKey, "", "", null, false, [], false),
            App("running:b", "B.Id", @"C:\b\b.exe", W(1, "B.Id", @"C:\b\b.exe")),
        ];
        string? ProgramOf(DockItem i) => i.Key == "pin:a" ? @"C:\a\a.exe" : i.Windows.FirstOrDefault()?.ExePath ?? i.LaunchTarget;
        Assert.Equal((0, LaunchMatchBy.AppId), LaunchMatch.Find(items, "a.id", null, ProgramOf));
        // A pin whose AppID was kept from an older shortcut: its program.
        Assert.Equal((0, LaunchMatchBy.Program), LaunchMatch.Find(items, "A.Other", @"C:\A\A.EXE", ProgramOf));
        Assert.Equal((2, LaunchMatchBy.Program), LaunchMatch.Find(items, null, @"C:\b\b.exe", ProgramOf));
        Assert.Equal((-1, LaunchMatchBy.None), LaunchMatch.Find(items, "C.Id", @"C:\c\c.exe", ProgramOf));
    }

    [Fact]
    public void What_the_Dock_does()
    {
        var kept = App("pin:a", "A", @"C:\a\a.lnk");
        var running = App("running:b", "B", @"C:\b\b.exe", W(1, "B", @"C:\b\b.exe"));
        Assert.Equal(new LaunchDecision(true, false, false), LaunchPlan.Decide(kept, LaunchKind.AppsFolder));
        Assert.Equal(new LaunchDecision(false, false, true), LaunchPlan.Decide(running, LaunchKind.Shortcut));
        Assert.Equal(new LaunchDecision(true, true, false), LaunchPlan.Decide(null, LaunchKind.Program));
        Assert.Equal(LaunchDecision.None, LaunchPlan.Decide(null, LaunchKind.Document));
        Assert.Equal(LaunchDecision.None, LaunchPlan.Decide(running, LaunchKind.Uri));
        Assert.Equal(LaunchDecision.None, LaunchPlan.Decide(kept, LaunchKind.Folder));
        Assert.Equal(new LaunchDecision(false, true, false), LaunchPlan.Decide(null, LaunchKind.Program, bounce: false));
    }
}

public sealed class DockLaunchingTests
{
    static RunningWindow W(long h, string app) => new(h, app, app + " name", @"C:\x\" + app + ".exe", false);
    static PinnedApp P(string app) => new("pin:" + app, app, app, @"C:\x\" + app + ".lnk");
    static PinnedApp L(string app) => new("running:" + app, app, app + " starting", @"C:\x\" + app + ".exe");
    static PinnedApp R(string app) => new("recent:" + app, app, app, @"C:\x\" + app + ".exe");

    static string[] Keys(IReadOnlyList<DockItem> items) => items.Select(i => i.Key).ToArray();

    [Fact]
    public void A_starting_app_has_its_item_at_once_and_keeps_it_when_its_window_comes()
    {
        var order = new List<string>();
        var before = DockModel.Build([P("a")], [W(1, "b")], null, order, "Apps", "Trash", launching: [L("c")]);
        Assert.Equal(["pin:a", "running:b", "running:c", DockModel.SeparatorKey, DockModel.AppGridKey, DockModel.TrashKey], Keys(before));
        var starting = before.Single(i => i.Key == "running:c");
        Assert.False(starting.Running);
        Assert.Equal("c starting", starting.Name);
        // Its window comes (still starting a moment more): the same item, in the same place, and no second one.
        var after = DockModel.Build([P("a")], [W(2, "c"), W(1, "b")], null, order, "Apps", "Trash", launching: [L("c")]);
        Assert.Equal(Keys(before), Keys(after));
        Assert.True(after.Single(i => i.Key == "running:c").Running);
        // No longer starting: still there by its window, in its place.
        Assert.Equal(Keys(before), Keys(DockModel.Build([P("a")], [W(2, "c"), W(1, "b")], null, order, "Apps", "Trash")));
    }

    [Fact]
    public void A_starting_app_keeps_its_place_when_another_starts_meanwhile()
    {
        var order = new List<string>();
        DockModel.Build([], [], null, order, "Apps", "Trash", launching: [L("c")]);
        // Another app's window comes while c has none yet: c stays first.
        var items = DockModel.Build([], [W(1, "d")], null, order, "Apps", "Trash", launching: [L("c")]);
        Assert.Equal(["running:c", "running:d"], Keys(items).Where(k => k.StartsWith("running:")).ToArray());
        items = DockModel.Build([], [W(1, "d"), W(2, "c")], null, order, "Apps", "Trash", launching: [L("c")]);
        Assert.Equal(["running:c", "running:d"], Keys(items).Where(k => k.StartsWith("running:")).ToArray());
    }

    [Fact]
    public void A_kept_app_starting_has_no_second_item()
    {
        var items = DockModel.Build([P("a")], [], null, new List<string>(), "Apps", "Trash", launching: [L("a")]);
        Assert.Equal(["pin:a", DockModel.SeparatorKey, DockModel.AppGridKey, DockModel.TrashKey], Keys(items));
    }

    [Fact]
    public void A_start_that_never_came_leaves_no_item()
    {
        var order = new List<string>();
        DockModel.Build([], [], null, order, "Apps", "Trash", launching: [L("c")]);
        Assert.DoesNotContain("running:c", Keys(DockModel.Build([], [], null, order, "Apps", "Trash")));
        Assert.Empty(order);
    }

    [Fact]
    public void An_app_started_from_the_recent_ones_stays_in_their_place()
    {
        var order = new List<string>();
        PinnedApp[] recent = [R("d"), R("e"), R("f")];
        var before = DockModel.Build([P("a")], [W(1, "b")], null, order, "Apps", "Trash", recent);
        var stay = new HashSet<string> { "e" };
        var after = DockModel.Build([P("a")], [W(1, "b"), W(2, "e")], "e", order, "Apps", "Trash", recent, stayRecent: stay);
        Assert.Equal(Keys(before), Keys(after));
        var e = after.Single(i => i.Key == "recent:e");
        Assert.True(e.Running);
        Assert.True(e.Active);
        Assert.DoesNotContain("e", order);
        // Without staying, as before: it moves to the running apps.
        Assert.Contains("running:e", Keys(DockModel.Build([P("a")], [W(1, "b"), W(2, "e")], null, new List<string>(), "Apps", "Trash", recent)));
    }

    [Theory]
    [InlineData(-0.1, 0)]
    [InlineData(0, 1)]
    [InlineData(0.49, 1)]
    [InlineData(0.5, 2)]
    [InlineData(1.2, 3)]
    public void Hops_begun(double t, int hops) => Assert.Equal(hops, DockMotion.HopsBegun(t, 0.5));

    [Fact]
    public void A_box_scaled_keeps_its_centre()
    {
        var b = new Box(10, 20, 40, 60).Scaled(0.9);
        Assert.Equal(30, b.X + b.Width / 2, 6);
        Assert.Equal(50, b.Y + b.Height / 2, 6);
        Assert.Equal(36, b.Width, 6);
        Assert.Equal(54, b.Height, 6);
    }
}
