using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

public class TaskbarRecoveryPlannerTests
{
    const long Tool = 0x80, Topmost = 0x8, Layered = 0x80000;

    static TaskbarSavedState Saved(int abm = 0, params (string cls, bool visible, long ex)[] windows) => new()
    {
        HiddenByUs = true,
        AbmState = abm,
        Windows = windows.Select(w => new TaskbarWindowState { Class = w.cls, Visible = w.visible, ExStyle = w.ex }).ToList(),
    };

    /// <summary>
    /// Only the primary taskbar is hidden: the Dock and the menu bar are on the primary monitor only, and an external
    /// monitor whose taskbar was hidden had nothing left to start apps or switch between them (2026-10-15).
    /// </summary>
    [Theory]
    [InlineData("Shell_TrayWnd", true)]
    [InlineData("Shell_SecondaryTrayWnd", false)]
    public void Only_the_primary_taskbar_is_hidden(string windowClass, bool hidden) => Assert.Equal(hidden, TaskbarRecoveryPlanner.Hides(windowClass));

    /// <summary>A secondary taskbar left as it was needs nothing at the restore; one an older build hid is shown again.</summary>
    [Fact]
    public void A_secondary_taskbar_left_alone_needs_nothing_and_one_hidden_before_comes_back()
    {
        var saved = Saved(0, ("Shell_TrayWnd", true, Tool | Topmost), ("Shell_SecondaryTrayWnd", true, Tool | Topmost));
        var alone = TaskbarRecoveryPlanner.Plan(saved, 0, [new("Shell_TrayWnd", false, Tool | Topmost), new("Shell_SecondaryTrayWnd", true, Tool | Topmost)]);
        Assert.Equal([new TaskbarFixStep("Shell_TrayWnd", TaskbarFix.Show)], alone.Steps);
        var hiddenBefore = TaskbarRecoveryPlanner.Plan(saved, 0, [new("Shell_TrayWnd", false, Tool | Topmost), new("Shell_SecondaryTrayWnd", false, Tool | Topmost)]);
        Assert.Contains(new TaskbarFixStep("Shell_SecondaryTrayWnd", TaskbarFix.Show), hiddenBefore.Steps);
    }

    /// <summary>
    /// Explorer's taskbars are always on top but under a fullscreen app: seen without it otherwise, an earlier run lost
    /// it (2026-10-15: the installed shell kept such a taskbar as the original and never put it back).
    /// </summary>
    [Theory]
    [InlineData(Tool, false, Tool | Topmost)]           // lost by an earlier run: kept as always on top
    [InlineData(Tool, true, Tool)]                      // under a fullscreen app: as it is (Explorer's own doing)
    [InlineData(Tool | Topmost, false, Tool | Topmost)]
    [InlineData(Tool | Topmost, true, Tool | Topmost)]
    public void The_original_kept_is_always_on_top_unless_a_fullscreen_app_is_in_front(long now, bool fullScreen, long kept) =>
        Assert.Equal(kept, TaskbarRecoveryPlanner.OriginalExStyle(now, fullScreen));

    /// <summary>Under a fullscreen app the restore leaves always-on-top to Explorer: setting it would put the taskbar above the app.</summary>
    [Fact]
    public void Always_on_top_is_not_put_back_over_a_fullscreen_app()
    {
        var saved = Saved(0, ("Shell_TrayWnd", true, Tool | Topmost));
        Assert.Empty(TaskbarRecoveryPlanner.Plan(saved, 0, [new("Shell_TrayWnd", true, Tool, UnderFullScreen: true)]).Steps);
        Assert.Equal([new TaskbarFixStep("Shell_TrayWnd", TaskbarFix.MakeTopmost)],
            TaskbarRecoveryPlanner.Plan(saved, 0, [new("Shell_TrayWnd", true, Tool)]).Steps);
    }

    [Fact]
    public void Untouched_taskbar_needs_nothing()
    {
        var plan = TaskbarRecoveryPlanner.Plan(Saved(0, ("Shell_TrayWnd", true, Tool | Topmost)), 0,
            [new("Shell_TrayWnd", true, Tool | Topmost)]);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void Hidden_taskbar_is_shown_and_auto_hide_put_back()
    {
        var plan = TaskbarRecoveryPlanner.Plan(Saved(0, ("Shell_TrayWnd", true, Tool | Topmost)), 1,
            [new("Shell_TrayWnd", false, Tool | Topmost)]);
        Assert.Equal([new TaskbarFixStep("Shell_TrayWnd", TaskbarFix.Show)], plan.Steps);
        Assert.Equal(0, plan.AbmState);
    }

    [Fact]
    public void Transparent_layer_we_added_is_removed_before_showing()
    {
        var plan = TaskbarRecoveryPlanner.Plan(Saved(0, ("Shell_TrayWnd", true, Tool | Topmost)), 1,
            [new("Shell_TrayWnd", true, Tool | Topmost | Layered)]);
        Assert.Equal([new TaskbarFixStep("Shell_TrayWnd", TaskbarFix.RemoveTransparency)], plan.Steps);
    }

    [Fact]
    public void A_layer_that_was_there_before_us_is_kept()
    {
        var plan = TaskbarRecoveryPlanner.Plan(Saved(0, ("Shell_TrayWnd", true, Tool | Topmost | Layered)), 0,
            [new("Shell_TrayWnd", true, Tool | Topmost | Layered)]);
        Assert.True(plan.IsEmpty);
    }

    [Fact]
    public void Lost_topmost_is_restored()
    {
        // Measured 2026-10-11: HWND_BOTTOM and ManagedShell's tray both leave the taskbar without WS_EX_TOPMOST.
        var plan = TaskbarRecoveryPlanner.Plan(Saved(0, ("Shell_TrayWnd", true, Tool | Topmost)), 0,
            [new("Shell_TrayWnd", true, Tool)]);
        Assert.Equal([new TaskbarFixStep("Shell_TrayWnd", TaskbarFix.MakeTopmost)], plan.Steps);
    }

    [Fact]
    public void Windows_are_matched_by_class_so_an_explorer_restart_does_not_matter()
    {
        var plan = TaskbarRecoveryPlanner.Plan(
            Saved(0, ("Shell_TrayWnd", true, Tool | Topmost), ("Shell_SecondaryTrayWnd", true, Tool | Topmost)), 0,
            [new("Shell_TrayWnd", false, Tool), new("Shell_SecondaryTrayWnd", false, Tool), new("Shell_SecondaryTrayWnd", true, Tool | Topmost)]);
        Assert.Equal(
            [
                new TaskbarFixStep("Shell_TrayWnd", TaskbarFix.Show), new TaskbarFixStep("Shell_TrayWnd", TaskbarFix.MakeTopmost),
                new TaskbarFixStep("Shell_SecondaryTrayWnd", TaskbarFix.Show), new TaskbarFixStep("Shell_SecondaryTrayWnd", TaskbarFix.MakeTopmost),
            ],
            plan.Steps);
    }

    [Fact]
    public void Unknown_bars_default_to_visible_and_topmost()
    {
        var plan = TaskbarRecoveryPlanner.Plan(Saved(0, ("Shell_TrayWnd", true, Tool | Topmost)), 0,
            [new("Shell_SecondaryTrayWnd", false, Tool)]);
        Assert.Equal(
            [new TaskbarFixStep("Shell_SecondaryTrayWnd", TaskbarFix.Show), new TaskbarFixStep("Shell_SecondaryTrayWnd", TaskbarFix.MakeTopmost)],
            plan.Steps);
    }

    [Fact]
    public void Auto_hide_the_user_had_on_is_kept_on()
    {
        var plan = TaskbarRecoveryPlanner.Plan(Saved(1, ("Shell_TrayWnd", true, Tool | Topmost)), 1,
            [new("Shell_TrayWnd", false, Tool | Topmost)]);
        Assert.Null(plan.AbmState);
        Assert.Single(plan.Steps);
    }

    [Theory]
    [InlineData(true, false, true)]   // hidden, owner gone -> stale
    [InlineData(true, true, false)]   // hidden, owner still running -> not ours to touch
    [InlineData(false, false, false)] // not hidden -> nothing to recover
    public void Stale_state_detection(bool hidden, bool ownerAlive, bool expected)
    {
        var state = new ShellState { Owner = new OwnerInfo { Pid = 42 }, Taskbar = new TaskbarSavedState { HiddenByUs = hidden } };
        Assert.Equal(expected, TaskbarRecoveryPlanner.IsStale(state, _ => ownerAlive));
    }

    [Fact]
    public void A_snapshot_still_marked_hidden_is_kept_over_a_fresh_one()
    {
        // kill-both E2E (2026-10-12): the next run took a snapshot of the still-hidden taskbar and "restored" to hidden.
        var original = Saved(0, ("Shell_TrayWnd", true, Tool | Topmost));
        var freshOfHidden = new TaskbarSavedState { HiddenByUs = true, AbmState = 1, Windows = [new() { Class = "Shell_TrayWnd", Visible = false, ExStyle = Tool | Topmost }] };
        Assert.Same(original, TaskbarRecoveryPlanner.ChooseSnapshot(original, freshOfHidden));
    }

    /// <summary>
    /// A run that never hid the taskbar (Windows' taskbar kept, the tray on) saved it only for always-on-top: the user's
    /// auto-hide turned on in Windows' settings since was set back off at each quit and sign-out (2026-11-06 review).
    /// </summary>
    [Fact]
    public void A_run_that_never_hid_it_puts_back_only_always_on_top()
    {
        var saved = Saved(0, ("Shell_TrayWnd", true, Tool | Topmost));
        saved.OnlyTopmost = true;
        var plan = TaskbarRecoveryPlanner.Plan(saved, 1, [new("Shell_TrayWnd", false, Tool | Layered)]);
        Assert.Null(plan.AbmState);
        Assert.Equal([new TaskbarFixStep("Shell_TrayWnd", TaskbarFix.MakeTopmost)], plan.Steps);
    }

    /// <summary>Explorer did not answer what its auto-hide was when saved: it is not set to a guess (read as 0, it turned auto-hide off).</summary>
    [Fact]
    public void An_auto_hide_not_known_when_saved_is_left_as_it_is()
    {
        Assert.Null(TaskbarRecoveryPlanner.Plan(Saved(-1, ("Shell_TrayWnd", true, Tool | Topmost)), 1, [new("Shell_TrayWnd", true, Tool | Topmost)]).AbmState);
        // And one not answered now is set as saved, whatever it is.
        Assert.Equal(0, TaskbarRecoveryPlanner.Plan(Saved(0, ("Shell_TrayWnd", true, Tool | Topmost)), -1, [new("Shell_TrayWnd", true, Tool | Topmost)]).AbmState);
    }

    [Fact]
    public void Hiding_after_only_keeping_always_on_top_saves_it_afresh_with_that()
    {
        // The tray's snapshot (before it took always-on-top off), then the taskbar hidden: the auto-hide and showing as
        // they are now, always-on-top as it was before the tray.
        var tray = Saved(1, ("Shell_TrayWnd", true, Tool | Topmost));
        tray.OnlyTopmost = true;
        var fresh = new TaskbarSavedState { HiddenByUs = true, AbmState = 0, Windows = [new() { Class = "Shell_TrayWnd", Visible = true, ExStyle = Tool }] };
        var chosen = TaskbarRecoveryPlanner.ChooseSnapshot(tray, fresh);
        Assert.Same(fresh, chosen);
        Assert.Equal(0, chosen.AbmState);
        Assert.Equal(Tool | Topmost, chosen.Windows[0].ExStyle);
        // A snapshot of a run that hid it (and did not put it back) is still kept over the tray's.
        var hidden = Saved(1, ("Shell_TrayWnd", true, Tool | Topmost));
        var trayFresh = Saved(0, ("Shell_TrayWnd", false, Tool));
        trayFresh.OnlyTopmost = true;
        Assert.Same(hidden, TaskbarRecoveryPlanner.ChooseSnapshot(hidden, trayFresh));
    }

    [Fact]
    public void A_restored_snapshot_is_replaced_by_a_fresh_one()
    {
        var old = Saved(1, ("Shell_TrayWnd", true, Tool | Topmost));
        old.HiddenByUs = false; // we put it back last time; the user may have changed settings since
        var fresh = Saved(0, ("Shell_TrayWnd", true, Tool | Topmost));
        Assert.Same(fresh, TaskbarRecoveryPlanner.ChooseSnapshot(old, fresh));
        Assert.Same(fresh, TaskbarRecoveryPlanner.ChooseSnapshot(null, fresh));
    }

    [Fact]
    public void Hidden_without_an_owner_record_is_stale()
    {
        var state = new ShellState { Owner = null, Taskbar = new TaskbarSavedState { HiddenByUs = true } };
        Assert.True(TaskbarRecoveryPlanner.IsStale(state, _ => true));
    }
}

public class CrashRelaunchPolicyTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 12, 1, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Allows_three_relaunches_in_five_minutes()
    {
        var h = new List<DateTimeOffset>();
        for (int i = 0; i < 3; i++)
        {
            Assert.True(CrashRelaunchPolicy.ShouldRelaunch(h, Now.AddSeconds(i)));
            h = CrashRelaunchPolicy.Record(h, Now.AddSeconds(i));
        }
        Assert.False(CrashRelaunchPolicy.ShouldRelaunch(h, Now.AddSeconds(10)));
    }

    [Fact]
    public void Old_relaunches_expire()
    {
        var h = new List<DateTimeOffset> { Now.AddMinutes(-10), Now.AddMinutes(-6), Now.AddMinutes(-5.5) };
        Assert.True(CrashRelaunchPolicy.ShouldRelaunch(h, Now));
        Assert.Single(CrashRelaunchPolicy.Record(h, Now));
    }

    [Fact]
    public void Future_timestamps_from_a_clock_change_are_ignored()
    {
        var h = new List<DateTimeOffset> { Now.AddHours(1), Now.AddHours(1), Now.AddHours(1) };
        Assert.True(CrashRelaunchPolicy.ShouldRelaunch(h, Now));
    }
}
