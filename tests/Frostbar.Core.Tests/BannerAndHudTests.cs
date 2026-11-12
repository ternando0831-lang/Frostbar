using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

public sealed class BannerQueueTests
{
    static Notice N(uint id, string appId = "", string app = "") => new(id, app, appId, DateTimeOffset.UnixEpoch, $"title {id}", $"text {id}");

    [Fact]
    public void A_banner_shows_for_five_seconds()
    {
        var q = new BannerQueue();
        Assert.True(q.Add(N(1, "a"), 1000, quiet: false));
        Assert.Equal(6000, q.NextDue);
        Assert.False(q.Expire(5999));
        Assert.Single(q.Shown);
        Assert.True(q.Expire(6000));
        Assert.Empty(q.Shown);
        Assert.Null(q.NextDue);
    }

    [Fact]
    public void Newest_on_top_and_at_most_three()
    {
        var q = new BannerQueue();
        q.Add(N(1, "a"), 0, false);
        q.Add(N(2, "b"), 10, false);
        q.Add(N(3, "c"), 20, false);
        q.Add(N(4, "d"), 30, false);
        Assert.Equal([4u, 3u, 2u], q.Shown.Select(n => n.Id));
    }

    [Fact]
    public void A_new_one_from_the_same_app_takes_the_place_of_its_last()
    {
        var q = new BannerQueue();
        q.Add(N(1, "a"), 0, false);
        q.Add(N(2, "b"), 100, false);
        q.Add(N(3, "a"), 4000, false);
        Assert.Equal([3u, 2u], q.Shown.Select(n => n.Id));
        // The newer one has its own five seconds: b goes first.
        Assert.True(q.Expire(5100));
        Assert.Equal([3u], q.Shown.Select(n => n.Id));
        Assert.Equal(9000, q.NextDue);
    }

    [Fact]
    public void Apps_are_told_apart_by_their_id_then_their_name_and_unnamed_ones_never_merge()
    {
        var q = new BannerQueue();
        q.Add(N(1, app: "x"), 0, false);
        q.Add(N(2, app: "x"), 0, false);
        Assert.Equal([2u], q.Shown.Select(n => n.Id));
        q.Add(N(3), 0, false);
        q.Add(N(4), 0, false);
        Assert.Equal([4u, 3u, 2u], q.Shown.Select(n => n.Id));
    }

    [Fact]
    public void Nothing_shows_while_it_is_quiet()
    {
        var q = new BannerQueue();
        Assert.False(q.Add(N(1, "a"), 0, quiet: true));
        Assert.Empty(q.Shown);
    }

    [Fact]
    public void Under_the_pointer_nothing_counts_down_and_after_it_leaves_at_least_two_seconds_more()
    {
        var q = new BannerQueue();
        q.Add(N(1, "a"), 0, false);      // due at 5000
        q.Add(N(2, "b"), 4000, false);   // due at 9000
        q.Pause(4500);                   // 500 and 4500 left
        Assert.Null(q.NextDue);
        Assert.False(q.Expire(100_000));
        Assert.Equal(2, q.Shown.Count);
        q.Add(N(3, "c"), 50_000, false); // came while paused: a whole five seconds once it goes on
        q.Resume(60_000);
        Assert.Equal(62_000, q.NextDue); // 1: 500 left, so the two seconds after leaving
        Assert.True(q.Expire(62_000));
        Assert.Equal([3u, 2u], q.Shown.Select(n => n.Id));
        Assert.False(q.Expire(64_499));
        Assert.True(q.Expire(64_500));   // 2: its 4500
        Assert.True(q.Expire(65_000));   // 3: its 5000
        Assert.Empty(q.Shown);
    }

    [Fact]
    public void Dismissed_and_cleared()
    {
        var q = new BannerQueue();
        q.Add(N(1, "a"), 0, false);
        q.Add(N(2, "b"), 0, false);
        Assert.True(q.Dismiss(1));
        Assert.False(q.Dismiss(1));
        Assert.Equal([2u], q.Shown.Select(n => n.Id));
        Assert.True(q.Clear());
        Assert.False(q.Clear());
    }
}

public sealed class HudLayoutTests
{
    [Theory]
    [InlineData(800, 646)]  // under the item's middle
    [InlineData(1500, 1220)] // near the right edge: kept on the screen, the inset from it
    [InlineData(20, 8)]     // and the left
    public void It_stands_under_the_items_middle_on_the_screen(double middle, double expected) =>
        Assert.Equal(expected, HudLayout.Left(middle, 308, 0, 1536, 8));

    [Fact]
    public void With_no_item_it_stands_at_the_right_edge() => Assert.Equal(1536 - 8 - 308, HudLayout.Left(null, 308, 0, 1536, 8));

    [Fact]
    public void Only_the_volume_has_an_item()
    {
        Assert.Equal("volume", HudLayout.ItemOf(HudKind.Volume));
        Assert.Null(HudLayout.ItemOf(HudKind.Brightness));
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(0.5, 7, 1)]     // half: eight parts full
    [InlineData(0.5, 8, 0)]
    [InlineData(1, 15, 1)]      // all sixteen
    [InlineData(0.53125, 8, 0.5)] // between two steps: the part half full
    [InlineData(2, 15, 1)]      // out of range: as its end
    [InlineData(-1, 0, 0)]
    public void The_bar_fills_part_by_part(double level, int part, double fill) => Assert.Equal(fill, HudLayout.PartFill(level, part), 6);

    [Fact]
    public void The_bar_has_macOS_sixteen_steps() => Assert.Equal(16, HudLayout.Parts);
}

public sealed class HudTests
{
    [Fact]
    public void Shown_for_a_second_and_a_half_after_the_last_key()
    {
        var h = new HudModel();
        Assert.False(h.Visible);
        h.Show(HudKind.Volume, 0.6, false, 1000);
        Assert.True(h.Visible);
        Assert.False(h.Expire(2499));
        h.Show(HudKind.Volume, 0.62, false, 2000); // the next key keeps it
        Assert.False(h.Expire(3499));
        Assert.True(h.Expire(3500));
        Assert.False(h.Visible);
        Assert.False(h.Expire(9999));
    }

    [Fact]
    public void A_change_while_it_shows_updates_it_without_keeping_it_longer()
    {
        var h = new HudModel();
        Assert.False(h.Update(HudKind.Volume, 0.5, false)); // not shown: nothing
        h.Show(HudKind.Volume, 0.60, false, 0);
        Assert.True(h.Update(HudKind.Volume, 0.62, false));
        Assert.Equal(62, h.Percent);
        Assert.False(h.Update(HudKind.Volume, 0.62, false)); // the same: no change
        Assert.False(h.Update(HudKind.Brightness, 0.9, false)); // the other kind: not shown
        Assert.Equal(1500, h.HideAt);
    }

    [Fact]
    public void Brightness_is_never_muted_and_levels_are_kept_within_0_to_1()
    {
        var h = new HudModel();
        h.Show(HudKind.Brightness, 1.4, muted: true, 0);
        Assert.False(h.Muted);
        Assert.Equal(100, h.Percent);
        h.Show(HudKind.Volume, -0.2, muted: false, 0);
        Assert.Equal(0, h.Percent);
    }

    [Theory]
    [InlineData(0.0, false, 0)]
    [InlineData(0.5, true, 0)]
    [InlineData(0.02, false, 1)]
    [InlineData(0.339, false, 1)]
    [InlineData(0.34, false, 2)]
    [InlineData(0.669, false, 2)]
    [InlineData(0.67, false, 3)]
    [InlineData(1.0, false, 3)]
    public void Waves_as_the_menu_bars_volume_item(double level, bool muted, int waves) =>
        Assert.Equal(waves, HudModel.WavesOf(level, muted));
}

public sealed class OsdJudgeTests
{
    [Theory]
    [InlineData(292, 77, 1.25, true)]   // Windows' display as measured at 125%
    [InlineData(211, 76, 1.25, true)]   // as it starts, before it widens
    [InlineData(1200, 400, 1.25, false)] // Alt+Tab's switcher
    [InlineData(0, 0, 1.25, false)]
    [InlineData(450, 150, 2.0, true)]   // the same display at 200%
    public void Looks_like_the_display(int w, int h, double scale, bool expected) =>
        Assert.Equal(expected, OsdJudge.LooksLikeDisplay(w, h, scale));

    [Theory]
    [InlineData(true, 5000, 1000, true)]   // Windows' display out, long after any volume key: the brightness keys
    [InlineData(true, 1150, 1000, true)]   // a moment after a volume key: the finger moved to the brightness keys
    [InlineData(true, 1149, 1000, false)]  // a volume key still held, the display out for it: adaptive brightness
    [InlineData(false, 5000, 1000, false)] // the display not out: adaptive brightness, not a key
    public void A_brightness_change_is_the_keys_while_the_display_is_out(bool displayOut, long now, long volumeKeyAt, bool keys) =>
        Assert.Equal(keys, OsdJudge.BrightnessIsKeys(displayOut, now, volumeKeyAt));

    [Fact]
    public void Each_brightness_change_with_the_display_out_keeps_it_showing_past_the_first_time()
    {
        // The second symptom: a long adjustment. Shown again at each change, it goes only after the last.
        var h = new HudModel();
        long now = 1000;
        h.Show(HudKind.Brightness, 0.5, false, now);
        for (int i = 0; i < 10; i++)
        {
            now += 500;
            Assert.False(h.Expire(now));
            h.Show(HudKind.Brightness, 0.5 + i / 100.0, false, now);
        }
        Assert.False(h.Expire(now + HudModel.ShowMs - 1));
        Assert.True(h.Expire(now + HudModel.ShowMs));
    }

    [Fact]
    public void A_brightness_key_after_a_volume_key_takes_the_display_over()
    {
        // The first symptom: only showing it again changes what it shows; an update of another kind is not taken.
        var h = new HudModel();
        h.Show(HudKind.Volume, 0.3, false, 1000);
        Assert.False(h.Update(HudKind.Brightness, 0.8, false));
        Assert.Equal(HudKind.Volume, h.Kind);
        h.Show(HudKind.Brightness, 0.8, false, 1500);
        Assert.Equal(HudKind.Brightness, h.Kind);
        Assert.Equal(80, h.Percent);
    }

    [Fact]
    public void A_volume_key_just_before_makes_it_the_volumes()
    {
        Assert.Equal(OsdCause.Volume, OsdJudge.Cause(1009, appCommandAt: 1000, brightnessAt: null));
        Assert.Equal(OsdCause.Volume, OsdJudge.Cause(1400, 1000, 1350)); // the key wins over a brightness change
        Assert.Equal(OsdCause.Unknown, OsdJudge.Cause(1401, 1000, null)); // too long after
        Assert.Equal(OsdCause.Unknown, OsdJudge.Cause(990, 1000, null)); // a key after it did not bring it out
    }

    [Fact]
    public void A_brightness_change_around_it_makes_it_the_brightnesss()
    {
        Assert.Equal(OsdCause.Brightness, OsdJudge.Cause(1000, null, 1500));
        Assert.Equal(OsdCause.Brightness, OsdJudge.Cause(1000, null, 400));
        Assert.Equal(OsdCause.Unknown, OsdJudge.Cause(1000, null, 1601));
        Assert.Equal(OsdCause.Brightness, OsdJudge.Cause(1000, 100, 1100)); // an old key does not count
    }

    [Theory]
    [InlineData(8, true)]
    [InlineData(9, true)]
    [InlineData(10, true)]
    [InlineData(14, false)] // play/pause
    [InlineData(0, false)]
    public void Volume_keys(int command, bool expected) => Assert.Equal(expected, OsdJudge.IsVolumeKey(command));
}
