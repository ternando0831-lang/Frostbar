using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

public sealed class TaskbarSettleTests
{
    static readonly ScreenRect Monitor = new(0, 0, 1920, 1080);
    static BarPlace Top(int left, int top, int right, int bottom) => new(1, new ScreenRect(left, top, right, bottom));
    static BarPlace Bottom(int left, int top, int right, int bottom) => new(3, new ScreenRect(left, top, right, bottom));

    [Fact]
    public void A_bottom_taskbar_whose_space_came_back_is_released() =>
        Assert.True(TaskbarSettle.Released(Monitor, new(0, 30, 1920, 995), [Top(0, 0, 1920, 30), Bottom(0, 995, 1920, 1080)], autoHide: true));

    [Fact]
    public void A_bottom_taskbar_still_reserving_its_space_is_not() =>
        Assert.False(TaskbarSettle.Released(Monitor, new(0, 30, 1920, 935), [Top(0, 0, 1920, 30), Bottom(0, 935, 1920, 1020)], autoHide: true));

    /// <summary>
    /// Explorer's taskbar at the top (60 px), not switched to auto-hide yet: the menu bar sits under it. The work area
    /// under the menu bar passed the check before 2026-10-15, and the tray started in the middle of the switch.
    /// </summary>
    [Fact]
    public void A_top_taskbar_still_reserving_its_space_is_not() =>
        Assert.False(TaskbarSettle.Released(Monitor, new(0, 90, 1920, 995), [Top(0, 60, 1920, 90), Bottom(0, 995, 1920, 1080)], autoHide: true));

    [Fact]
    public void A_left_taskbar_still_reserving_its_space_is_not() =>
        Assert.False(TaskbarSettle.Released(Monitor, new(60, 30, 1920, 995), [Top(60, 0, 1920, 30), Bottom(60, 995, 1920, 1080)], autoHide: true));

    [Fact]
    public void Not_released_before_auto_hide_is_on() =>
        Assert.False(TaskbarSettle.Released(Monitor, new(0, 30, 1920, 995), [Top(0, 0, 1920, 30), Bottom(0, 995, 1920, 1080)], autoHide: false));

    /// <summary>With the Dock set to hide itself there is no bottom strip: the menu bar alone.</summary>
    [Fact]
    public void The_menu_bar_alone_when_the_Dock_hides_itself() =>
        Assert.True(TaskbarSettle.Released(Monitor, new(0, 30, 1920, 1080), [Top(0, 0, 1920, 30)], autoHide: true));
}
