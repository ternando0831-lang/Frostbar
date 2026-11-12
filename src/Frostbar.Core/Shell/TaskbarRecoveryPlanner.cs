namespace Frostbar.Core.Shell;

public enum TaskbarFix { RemoveTransparency, Show, MakeTopmost }

/// <param name="UnderFullScreen">A fullscreen app is in front on this taskbar's monitor (Explorer itself keeps its taskbar below then).</param>
public sealed record TaskbarWindowNow(string Class, bool Visible, long ExStyle, bool UnderFullScreen = false);

public sealed record TaskbarFixStep(string Class, TaskbarFix Fix);

public sealed record TaskbarPlan(IReadOnlyList<TaskbarFixStep> Steps, int? AbmState)
{
    public bool IsEmpty => Steps.Count == 0 && AbmState == null;
    public override string ToString() =>
        IsEmpty ? "nothing to restore"
        : string.Join("; ", Steps.Select(s => $"{s.Class}: {s.Fix}").Concat(AbmState is int a ? [$"auto-hide state -> {a}"] : []));
}

/// <summary>
/// Decides how to put Explorer's taskbar back the way it was saved. Pure: the platform layer observes and applies.
/// Windows are matched by class because Explorer may have restarted (new handles) since the snapshot.
/// </summary>
public static class TaskbarRecoveryPlanner
{
    public const long WS_EX_TOPMOST = 0x8, WS_EX_LAYERED = 0x80000;

    /// <summary>
    /// Which of Explorer's taskbars the shell hides: the primary one only. The menu bar and the Dock are on the primary
    /// monitor alone, and hiding a secondary monitor's taskbar too left that monitor with nothing to start apps or switch
    /// between them (2026-10-15). Auto-hide is one setting for all of them, so a secondary taskbar still slides away
    /// until the pointer reaches its edge. The restore still covers every taskbar (an older build hid them all).
    /// </summary>
    public static bool Hides(string windowClass) => windowClass == "Shell_TrayWnd";

    /// <summary>
    /// The extended style to keep as a taskbar window's original. Explorer's taskbars are always on top except while
    /// a fullscreen app is in front of them (Explorer takes the bit off then, and puts it back when the app goes): seen
    /// without it and no fullscreen app in front, an earlier run or another app lost it, and it is kept as always on
    /// top so that the restore puts it back (2026-10-15: the installed shell started on a taskbar a test run had left
    /// below, kept that as the original and so never put it back).
    /// </summary>
    public static long OriginalExStyle(long exStyleNow, bool fullScreenInFront) =>
        fullScreenInFront ? exStyleNow : exStyleNow | WS_EX_TOPMOST;

    /// <param name="abmNow">Explorer's auto-hide state now; -1 when it did not answer (the saved one is set whatever it is).</param>
    public static TaskbarPlan Plan(TaskbarSavedState saved, int abmNow, IReadOnlyList<TaskbarWindowNow> now)
    {
        var steps = new List<TaskbarFixStep>();
        foreach (var w in now)
        {
            var orig = saved.Windows.FirstOrDefault(o => o.Class == w.Class);
            bool wasVisible = orig?.Visible ?? true;
            bool wasLayered = orig != null && (orig.ExStyle & WS_EX_LAYERED) != 0;
            // Explorer's taskbars are always-on-top; hiding through HWND_BOTTOM or ManagedShell's tray drops the bit for good.
            bool wasTopmost = orig == null || (orig.ExStyle & WS_EX_TOPMOST) != 0;
            // A run that never hid it puts back only what its tray took (always on top): the rest is the user's.
            if (!saved.OnlyTopmost && (w.ExStyle & WS_EX_LAYERED) != 0 && !wasLayered) steps.Add(new(w.Class, TaskbarFix.RemoveTransparency));
            if (!saved.OnlyTopmost && wasVisible && !w.Visible) steps.Add(new(w.Class, TaskbarFix.Show));
            // Not over a fullscreen app: that would put the taskbar above it. Explorer puts the bit back when the app goes.
            if (wasTopmost && (w.ExStyle & WS_EX_TOPMOST) == 0 && !w.UnderFullScreen) steps.Add(new(w.Class, TaskbarFix.MakeTopmost));
        }
        // Not known when saved (Explorer did not answer): left as it is rather than set to a guess.
        bool abm = !saved.OnlyTopmost && saved.AbmState >= 0 && abmNow != saved.AbmState;
        return new TaskbarPlan(steps, abm ? saved.AbmState : null);
    }

    /// <summary>A previous run hid the taskbar and is gone without putting it back.</summary>
    public static bool IsStale(ShellState state, Func<OwnerInfo, bool> ownerAlive) =>
        state.Taskbar is { HiddenByUs: true } && (state.Owner is null || !ownerAlive(state.Owner));

    /// <summary>
    /// Which snapshot to keep when hiding: a snapshot still marked hidden-by-us describes the taskbar before *any* of
    /// our runs touched it, so it must not be replaced by a fresh one of the (still hidden) taskbar. A snapshot that only
    /// kept always-on-top (<see cref="TaskbarSavedState.OnlyTopmost"/>: the taskbar was not hidden since) gives way to a
    /// fresh one when the taskbar is hidden now, keeping its always-on-top (the tray may have taken it off since): the
    /// auto-hide and showing now are the user's, not changed by any run.
    /// </summary>
    public static TaskbarSavedState ChooseSnapshot(TaskbarSavedState? saved, TaskbarSavedState fresh)
    {
        if (saved is not { HiddenByUs: true }) return fresh;
        if (!saved.OnlyTopmost) return saved;
        if (fresh.OnlyTopmost) return fresh;
        foreach (var w in fresh.Windows)
            if (saved.Windows.FirstOrDefault(o => o.Class == w.Class) is { } o)
                w.ExStyle = (w.ExStyle & ~WS_EX_TOPMOST) | (o.ExStyle & WS_EX_TOPMOST);
        return fresh;
    }
}

/// <summary>The watchdog restarts a crashed app at most <see cref="Max"/> times within <see cref="Window"/>.</summary>
public static class CrashRelaunchPolicy
{
    public const int Max = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    public static bool ShouldRelaunch(IReadOnlyList<DateTimeOffset> history, DateTimeOffset now) =>
        history.Count(t => now - t < Window && t <= now) < Max;

    public static List<DateTimeOffset> Record(IEnumerable<DateTimeOffset> history, DateTimeOffset now) =>
        history.Where(t => now - t < Window && t <= now).Append(now).ToList();
}
