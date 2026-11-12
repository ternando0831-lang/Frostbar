using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

/// <summary>The Dynamic Island's choices (2026-11-08): what it shows, a notice's while, opening and closing.</summary>
public class IslandTests
{
    static IslandActivity Progress(string key, double p = 0.5) => new(IslandKind.Progress, key, p);

    [Fact]
    public void With_nothing_going_on_it_is_a_small_pill_that_does_not_open()
    {
        var m = new IslandModel();
        Assert.Equal(IslandShape.Idle, m.At(0).Shape);
        Assert.False(m.Open(0));
        Assert.Equal(IslandShape.Idle, m.At(0).Shape);
    }

    [Fact]
    public void What_plays_comes_first_and_a_progress_goes_beside_it()
    {
        var m = new IslandModel();
        m.SetProgress([Progress("copy"), Progress("download")]);
        var v = m.At(0);
        Assert.Equal(IslandShape.Compact, v.Shape);
        Assert.Equal("copy", v.Main!.Key);
        Assert.Equal("download", v.Second!.Key);
        m.SetPlaying("Spotify");
        v = m.At(0);
        Assert.Equal(IslandKind.Playing, v.Main!.Kind);
        Assert.Equal("copy", v.Second!.Key);
        m.SetPlaying(null);
        m.SetProgress([]);
        Assert.Equal(IslandShape.Idle, m.At(0).Shape);
    }

    [Fact]
    public void A_notice_shows_for_its_while_over_the_band_then_the_band_comes_back()
    {
        var m = new IslandModel();
        m.SetPlaying("app");
        m.Notify(new IslandNotice(NoticeKind.Focus, "Do Not Disturb", On: true), 1000);
        Assert.Equal(IslandShape.Notice, m.At(1000).Shape);
        Assert.Equal(1000 + IslandModel.NoticeMs, m.NextChange(1000));
        Assert.Equal(IslandShape.Notice, m.At(1000 + IslandModel.NoticeMs - 1).Shape);
        Assert.Equal(IslandShape.Compact, m.At(1000 + IslandModel.NoticeMs).Shape);
        Assert.Null(m.NextChange(1000 + IslandModel.NoticeMs));
        // A new notice takes the place of one showing, its while from its own time.
        m.Notify(new IslandNotice(NoticeKind.Battery, "20%"), 5000);
        m.Notify(new IslandNotice(NoticeKind.Output, "Speakers"), 6000);
        Assert.Equal(NoticeKind.Output, m.At(7000).Notice!.Kind);
        Assert.Equal(IslandShape.Notice, m.At(6000 + IslandModel.NoticeMs - 1).Shape);
    }

    [Fact]
    public void A_click_opens_until_closed_and_it_closes_when_its_activity_goes()
    {
        var m = new IslandModel();
        m.SetPlaying("app");
        m.Notify(new IslandNotice(NoticeKind.Output, "Speakers"), 0);
        Assert.True(m.Open(10));
        Assert.Equal(IslandShape.Open, m.At(20).Shape); // over the notice: it opens what goes on under it
        m.Notify(new IslandNotice(NoticeKind.Focus, "Focus"), 30); // let go while open
        Assert.Equal(IslandShape.Open, m.At(40).Shape);
        m.Close();
        Assert.Equal(IslandShape.Compact, m.At(50).Shape);
        m.Open(60);
        m.SetPlaying(null);
        Assert.Equal(IslandShape.Idle, m.At(70).Shape);
        m.SetPlaying("app");
        Assert.Equal(IslandShape.Compact, m.At(80).Shape); // it does not open again by itself
    }

    [Theory]
    [InlineData(21, 20, true, 20)]
    [InlineData(25, 8, true, 10)]
    [InlineData(11, 10, true, 10)]
    [InlineData(20, 19, true, null)]   // already past 20
    [InlineData(21, 20, false, null)]  // on the charger
    [InlineData(10, 12, true, null)]   // going up
    public void The_battery_tells_once_as_it_falls_past_20_and_10(int before, int now, bool onBattery, int? level)
    {
        Assert.Equal(level, LowBattery.Crossed(before, now, onBattery));
        Assert.Null(LowBattery.Crossed(null, now, onBattery)); // not at the first reading
    }

    [Fact]
    public void The_output_tells_when_it_changes_headphones_as_headphones()
    {
        Assert.Null(OutputNotice.For(null, "Speakers", false));
        Assert.Null(OutputNotice.For("Speakers", "Speakers", false));
        Assert.Equal(NoticeKind.Headphones, OutputNotice.For("Speakers", "Earbuds Pro", true));
        Assert.Equal(NoticeKind.Output, OutputNotice.For("Earbuds Pro", "Speakers", false));
    }
}
