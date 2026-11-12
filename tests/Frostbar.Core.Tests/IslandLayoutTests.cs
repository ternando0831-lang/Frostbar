using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

/// <summary>
/// The Dynamic Island's measures after Apple's Human Interface Guidelines (Live Activities, 2026-10-13: "macOS: Use the
/// provided iOS dimensions"), at the menu bar's height (2026-11-08, the user's "A 高さ 22").
/// </summary>
public class IslandLayoutTests
{
    static IslandPlan Plan(IslandShape s, IslandKind? main = null, bool second = false, NoticeKind? notice = null) => IslandLayout.For(s, main, second, notice);
    const double CamL = -IslandLayout.Camera / 2, CamR = IslandLayout.Camera / 2;

    [Fact]
    public void The_idle_island_has_the_iPhones_proportions_within_the_menu_bar()
    {
        var p = Plan(IslandShape.Idle);
        Assert.Equal(new IslandBox(CamL, 0, 76, 22), p.Body);
        Assert.InRange(p.Body.Width - 22 * 126 / 37.33, -2, 2); // the iPhone's 126 × 37.33 points, to the 4-DIP grid
        Assert.True(1 + p.Body.Height <= 24); // hung a DIP under the top, inside the 24-DIP bar
        Assert.Equal(11, p.Radius);
        Assert.Null(p.Picture);
    }

    [Theory]
    [InlineData(IslandKind.Playing)]
    [InlineData(IslandKind.Progress)]
    public void Compact_is_narrow_its_two_ends_balanced_beside_the_camera(IslandKind kind)
    {
        var p = Plan(IslandShape.Compact, kind);
        // Each end as the guidelines' 52.33 for 36.67, the camera between them.
        Assert.InRange(IslandLayout.Side - IslandLayout.Height * 52.33 / 36.67, -2, 2);
        Assert.Equal(IslandLayout.Camera + 2 * IslandLayout.Side, p.Body.Width);
        Assert.Equal(0, p.Body.MiddleX);
        var pic = p.Picture!.Value;
        Assert.True(pic.Right <= CamL, $"the picture {pic} keeps off the camera");
        Assert.True(p.End!.Value.X - IslandLayout.MeterWidth / 2 >= CamR, "the meter keeps off the camera");
        // Balanced: the picture's middle and the meter's mirror each other, half the height in from their ends.
        Assert.Equal(pic.MiddleX - p.Body.X, p.Body.Right - p.End.Value.X);
        Assert.Equal(p.Body.Height / 2, pic.MiddleX - p.Body.X);
        Assert.Equal(pic.MiddleY, p.End.Value.Y);
    }

    [Fact]
    public void The_picture_keeps_even_margins_concentric_with_the_islands_corners()
    {
        foreach (var p in new[] { Plan(IslandShape.Compact, IslandKind.Playing), Plan(IslandShape.Compact, IslandKind.Playing, second: true), Plan(IslandShape.Open, IslandKind.Playing) })
        {
            var pic = p.Picture!.Value;
            double left = pic.X - p.Body.X, top = pic.Y - p.Body.Y;
            Assert.Equal(left, top); // even margins
            Assert.Equal(p.Radius - top, p.PictureRadius); // ContainerRelativeShape
            Assert.Equal(p.PictureRadius, IslandLayout.PictureRadiusAt(p.Body.Height, top));
        }
        // In the band the sleeve is as round as its margin makes it: a circle.
        Assert.Equal(IslandLayout.Picture / 2, Plan(IslandShape.Compact, IslandKind.Playing).PictureRadius);
    }

    [Fact]
    public void Two_activities_one_attached_one_detached_the_camera_where_it_was()
    {
        var p = Plan(IslandShape.Compact, IslandKind.Playing, second: true);
        Assert.Equal(CamR, p.Body.Right); // grown on the leading side only
        Assert.Equal(CamL - IslandLayout.Height, p.Body.X); // by a minimal view (as wide as high)
        Assert.True(p.Picture!.Value.Right <= CamL);
        Assert.Null(p.End); // a minimal view: the picture only
        var second = p.Second!.Value;
        Assert.Equal(new IslandBox(CamR + IslandLayout.Gap, 0, 22, 22), second);
    }

    [Fact]
    public void Expanded_is_one_width_its_height_what_its_content_needs()
    {
        var playing = Plan(IslandShape.Open, IslandKind.Playing);
        var progress = Plan(IslandShape.Open, IslandKind.Progress);
        Assert.Equal(371, playing.Body.Width);
        Assert.Equal(371, progress.Body.Width);
        Assert.Equal(0, playing.Body.MiddleX);
        foreach (var p in new[] { playing, progress })
            Assert.InRange(p.Body.Height, 84, 160); // the guidelines' range
        Assert.True(progress.Body.Height < playing.Body.Height);
        // Even margins: the picture as far from the top as from the side, the bottom row as far from the bottom.
        Assert.Equal(IslandLayout.Margin, playing.Picture!.Value.X - playing.Body.X);
        Assert.Equal(IslandLayout.Margin, playing.Picture.Value.Y);
        Assert.Equal(IslandLayout.Margin, playing.Body.Right - (playing.End!.Value.X + IslandLayout.MeterWidth / 2));
        Assert.Equal(IslandLayout.Margin + IslandLayout.Art + 14 + 30 + IslandLayout.Margin, playing.Body.Height);
        Assert.Null(progress.End); // its track says how far: no ring besides
    }

    [Theory]
    [InlineData(NoticeKind.Headphones, 300, 46)]
    [InlineData(NoticeKind.Battery, 300, 46)]
    [InlineData(NoticeKind.Focus, 240, 22)]
    [InlineData(NoticeKind.Output, 240, 22)]
    public void A_notice_is_a_capsule_in_the_middle(NoticeKind kind, double w, double h)
    {
        var p = Plan(IslandShape.Notice, IslandKind.Playing, notice: kind);
        Assert.Equal(new IslandBox(-w / 2, 0, w, h), p.Body);
        Assert.Equal(h / 2, p.Radius);
        Assert.Null(p.Picture);
    }

    [Fact]
    public void The_picture_moves_only_between_faces_that_show_the_same_activity()
    {
        Assert.True(IslandLayout.Carries(IslandShape.Compact, "music", IslandShape.Open, "music"));
        Assert.True(IslandLayout.Carries(IslandShape.Open, "music", IslandShape.Compact, "music"));
        Assert.False(IslandLayout.Carries(IslandShape.Compact, "music", IslandShape.Compact, "copy"));
        Assert.False(IslandLayout.Carries(IslandShape.Compact, "music", IslandShape.Notice, "music")); // a notice holds its own
        Assert.False(IslandLayout.Carries(IslandShape.Notice, "music", IslandShape.Compact, "music"));
        Assert.False(IslandLayout.Carries(IslandShape.Idle, null, IslandShape.Compact, "music"));
    }

    [Fact]
    public void A_key_line_when_the_background_is_dark_and_something_shows()
    {
        Assert.True(IslandLayout.KeyLine(IslandShape.Compact, darkBehind: true));
        Assert.True(IslandLayout.KeyLine(IslandShape.Open, darkBehind: true));
        Assert.True(IslandLayout.KeyLine(IslandShape.Notice, darkBehind: true));
        Assert.False(IslandLayout.KeyLine(IslandShape.Idle, darkBehind: true));
        Assert.False(IslandLayout.KeyLine(IslandShape.Compact, darkBehind: false));
    }

    [Fact]
    public void Shrinking_its_height_settles_at_most_two_under_the_band_not_a_sliver()
    {
        double from = IslandLayout.PlayingOpenHeight, to = IslandLayout.Height, fall = from - to;
        double lowest = double.MaxValue;
        for (int i = 0; i <= 1000; i++)
        {
            double k = IslandLayout.Under(MotionTable.Spring(i / 1000.0, MotionTable.IslandSpring), fall);
            lowest = Math.Min(lowest, from + (to - from) * k);
        }
        Assert.InRange(lowest, to - IslandLayout.MostUnder - 1e-9, to);
        Assert.True(lowest < to - 1, "it still settles past its mark (a spring), a little");
        // The spring itself goes much further: half the band.
        double plain = Enumerable.Range(0, 1001).Min(i => from + (to - from) * MotionTable.Spring(i / 1000.0, MotionTable.IslandSpring));
        Assert.True(plain < to - 8, $"{plain}");
        Assert.Equal(0.5, IslandLayout.Under(0.5, fall)); // on its way: as the spring
        Assert.Equal(1.08, IslandLayout.Under(1.08, 0)); // not shrinking: the whole spring
    }

    [Fact]
    public void Corners_follow_the_height_up_to_the_expanded_islands()
    {
        Assert.Equal(11, IslandLayout.RadiusAt(22));
        Assert.Equal(23, IslandLayout.RadiusAt(46));
        Assert.Equal(30, IslandLayout.RadiusAt(132));
        Assert.Equal(0, IslandLayout.PictureRadiusAt(4, 3)); // never below none
    }
}
