using Frostbar.Core.Shell;
using Frostbar.Core.Wm;

namespace Frostbar.Core.Tests;

public sealed class TileLayoutTests
{
    // This PC's work area with the menu bar (30 px) and the Dock's strip (85 px) at 1920×1080: 1920×965 from y 30.
    static readonly ScreenRect Work = new(0, 30, 1920, 995);
    static readonly ScreenRect Window = new(300, 200, 1100, 800);

    [Theory]
    [InlineData(TilePlace.Left, 0, 30, 960, 995)]
    [InlineData(TilePlace.Right, 960, 30, 1920, 995)]
    [InlineData(TilePlace.Top, 0, 30, 1920, 512)]
    [InlineData(TilePlace.Bottom, 0, 512, 1920, 995)]
    [InlineData(TilePlace.TopLeft, 0, 30, 960, 512)]
    [InlineData(TilePlace.TopRight, 960, 30, 1920, 512)]
    [InlineData(TilePlace.BottomLeft, 0, 512, 960, 995)]
    [InlineData(TilePlace.BottomRight, 960, 512, 1920, 995)]
    [InlineData(TilePlace.Fill, 0, 30, 1920, 995)]
    [InlineData(TilePlace.Center, 560, 212, 1360, 812)] // 800×600 kept, in the middle
    public void Each_place_in_the_work_area(TilePlace place, int l, int t, int r, int b) =>
        Assert.Equal(new ScreenRect(l, t, r, b), TileLayout.Frame(place, Work, Window));

    [Fact]
    public void Halves_and_quarters_leave_no_gap_and_no_overlap_with_an_odd_size()
    {
        var odd = new ScreenRect(10, 20, 1011, 721); // 1001 × 701
        var left = TileLayout.Frame(TilePlace.Left, odd, Window);
        var right = TileLayout.Frame(TilePlace.Right, odd, Window);
        Assert.Equal(left.Right, right.Left);
        Assert.Equal(odd.Left, left.Left);
        Assert.Equal(odd.Right, right.Right);
        var top = TileLayout.Frame(TilePlace.TopLeft, odd, Window);
        var bottom = TileLayout.Frame(TilePlace.BottomLeft, odd, Window);
        Assert.Equal(top.Bottom, bottom.Top);
        Assert.Equal(odd.Bottom, bottom.Bottom);
    }

    [Fact]
    public void Center_keeps_a_window_larger_than_the_work_area_within_it()
    {
        var big = new ScreenRect(-50, -50, 2500, 1500);
        Assert.Equal(Work, TileLayout.Frame(TilePlace.Center, Work, big));
    }

    // Margins (macOS 15's "Tiled windows have margins", 2026-10-22): 10 px from the edges and between the tiles.
    [Theory]
    [InlineData(TilePlace.Left, 10, 40, 955, 985)]
    [InlineData(TilePlace.Right, 965, 40, 1910, 985)]
    [InlineData(TilePlace.Top, 10, 40, 1910, 507)]
    [InlineData(TilePlace.Bottom, 10, 517, 1910, 985)]
    [InlineData(TilePlace.TopLeft, 10, 40, 955, 507)]
    [InlineData(TilePlace.TopRight, 965, 40, 1910, 507)]
    [InlineData(TilePlace.BottomLeft, 10, 517, 955, 985)]
    [InlineData(TilePlace.BottomRight, 965, 517, 1910, 985)]
    [InlineData(TilePlace.Fill, 10, 40, 1910, 985)]
    [InlineData(TilePlace.Center, 560, 212, 1360, 812)] // the window's own size: no margins to keep
    public void With_margins_each_place_keeps_room_from_the_edges_and_the_others(TilePlace place, int l, int t, int r, int b) =>
        Assert.Equal(new ScreenRect(l, t, r, b), TileLayout.Frame(place, Work, Window, gap: 10));

    [Fact]
    public void With_margins_the_gap_between_halves_is_the_margin_with_an_odd_size()
    {
        var odd = new ScreenRect(10, 20, 1011, 721); // 1001 × 701
        const int gap = 9;
        var left = TileLayout.Frame(TilePlace.Left, odd, Window, gap);
        var right = TileLayout.Frame(TilePlace.Right, odd, Window, gap);
        Assert.Equal(gap, right.Left - left.Right);
        Assert.Equal(odd.Left + gap, left.Left);
        Assert.Equal(odd.Right - gap, right.Right);
        var top = TileLayout.Frame(TilePlace.TopLeft, odd, Window, gap);
        var bottom = TileLayout.Frame(TilePlace.BottomLeft, odd, Window, gap);
        Assert.Equal(gap, bottom.Top - top.Bottom);
        Assert.Equal(odd.Bottom - gap, bottom.Bottom);
    }
}

public sealed class TileMemoryTests
{
    static readonly TileBefore First = new(new ScreenRect(100, 100, 900, 700), false);

    [Fact]
    public void The_frame_from_before_the_first_move_comes_back_once()
    {
        var m = new TileMemory();
        m.Moving(7, First);
        m.Moving(7, new TileBefore(new ScreenRect(0, 30, 960, 995), false)); // moved again: the first stays
        Assert.True(m.Has(7));
        Assert.Equal(First, m.Take(7));
        Assert.Null(m.Take(7));
    }

    [Fact]
    public void Only_the_windows_still_there_are_kept()
    {
        var m = new TileMemory();
        m.Moving(1, First);
        m.Moving(2, First);
        m.KeepOnly(w => w == 2);
        Assert.False(m.Has(1));
        Assert.True(m.Has(2));
        Assert.Equal(1, m.Count);
    }
}
