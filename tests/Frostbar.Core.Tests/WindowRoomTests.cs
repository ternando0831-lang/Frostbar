using Frostbar.Core.Shell;
using Frostbar.Core.Wm;

namespace Frostbar.Core.Tests;

public sealed class WindowRoomTests
{
    // This PC at 125 %: 1600×900, the menu bar's strip 30 px (24 DIP), the Dock's 85 px (68 DIP).
    static readonly ScreenRect Monitor = new(0, 0, 1600, 900);
    static readonly ScreenRect Work = new(0, 30, 1600, 815);

    static ScreenRect R(int l, int t, int r, int b) => new(l, t, r, b);

    [Fact]
    public void A_window_opening_under_the_menu_bar_comes_down_below_it() =>
        Assert.Equal(R(200, 30, 1000, 540), WindowRoom.Opening(R(200, -10, 1000, 500), Monitor, Work, sizable: true));

    [Fact]
    public void A_window_opening_under_the_Dock_goes_up_above_it() =>
        Assert.Equal(R(200, 415, 1000, 815), WindowRoom.Opening(R(200, 500, 1000, 900), Monitor, Work, sizable: true));

    [Fact]
    public void A_window_opening_taller_than_the_room_is_sized_to_it() =>
        Assert.Equal(R(200, 30, 1000, 815), WindowRoom.Opening(R(200, -20, 1000, 880), Monitor, Work, sizable: true));

    [Fact]
    public void One_that_cannot_be_sized_stands_below_the_menu_bar_and_goes_on_behind_the_Dock() =>
        Assert.Equal(R(200, 30, 1000, 930), WindowRoom.Opening(R(200, 0, 1000, 900), Monitor, Work, sizable: false));

    [Fact]
    public void A_window_opening_between_the_bars_stays() =>
        Assert.Null(WindowRoom.Opening(R(200, 30, 1000, 815), Monitor, Work, sizable: true));

    [Fact]
    public void A_side_no_bar_reserves_is_left_alone()
    {
        // Partly off the screen's left and right, where nothing stands.
        Assert.Null(WindowRoom.Opening(R(-100, 100, 500, 400), Monitor, Work, sizable: true));
        Assert.Null(WindowRoom.Opening(R(1300, 100, 1900, 400), Monitor, Work, sizable: true));
        // The menu bar hiding always: no strip at the top; the Dock hiding: none at the bottom.
        Assert.Null(WindowRoom.Opening(R(200, 0, 1000, 500), Monitor, Work with { Top = 0 }, sizable: true));
        Assert.Null(WindowRoom.Opening(R(200, 500, 1000, 900), Monitor, Work with { Bottom = 900 }, sizable: true));
    }

    [Fact]
    public void The_Dock_on_a_side_holds_that_side()
    {
        var left = R(85, 30, 1600, 900);
        Assert.Equal(R(85, 100, 685, 500), WindowRoom.Opening(R(40, 100, 640, 500), Monitor, left, sizable: true));
        var right = R(0, 30, 1515, 900);
        Assert.Equal(R(915, 100, 1515, 500), WindowRoom.Opening(R(1000, 100, 1600, 500), Monitor, right, sizable: true));
    }

    // Pushed away from the one bar of an axis, a window stops at the monitor's other edge (Gemini's review, 2026-10-24:
    // it went on off the screen, its caption above the top).
    [Fact]
    public void Pushed_away_from_the_only_bar_of_an_axis_it_stays_on_the_screen()
    {
        // The menu bar hiding always, the Dock at the bottom: a window taller than the room is sized to it from the top.
        Assert.Equal(R(200, 0, 1000, 815), WindowRoom.Opening(R(200, 10, 1000, 860), Monitor, Work with { Top = 0 }, sizable: true));
        // One that cannot be sized stops at the screen's top and goes on behind the Dock.
        Assert.Equal(R(200, 0, 1000, 850), WindowRoom.Opening(R(200, 10, 1000, 860), Monitor, Work with { Top = 0 }, sizable: false));
        // The Dock on the left or the right: a window wider than the room is sized to it.
        Assert.Equal(R(85, 100, 1600, 600), WindowRoom.Opening(R(20, 100, 1580, 600), Monitor, R(85, 30, 1600, 900), sizable: true));
        Assert.Equal(R(0, 100, 1515, 600), WindowRoom.Opening(R(20, 100, 1560, 600), Monitor, R(0, 30, 1515, 900), sizable: true));
        // The Dock hiding: under the menu bar and nearly as tall as the screen, it is sized down to the screen's bottom.
        Assert.Equal(R(200, 30, 1000, 900), WindowRoom.Opening(R(200, 0, 1000, 890), Monitor, Work with { Bottom = 900 }, sizable: true));
    }

    [Fact]
    public void A_window_over_its_whole_monitor_or_off_it_is_left_alone()
    {
        Assert.Null(WindowRoom.Opening(Monitor, Monitor, Work, sizable: true));
        Assert.Null(WindowRoom.Opening(R(0, 0, 1600, 899), Monitor, Work, sizable: true)); // a pixel row short (the specs' backdrop)
        Assert.Null(WindowRoom.Opening(R(-32000, -32000, -31800, -31900), Monitor, Work, sizable: true));
    }
}
