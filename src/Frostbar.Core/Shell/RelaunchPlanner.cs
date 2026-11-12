namespace Frostbar.Core.Shell;

public enum RelaunchKind { None, AfterCrash, AfterCloseApp }

/// <summary>What the watchdog does once the shell has gone and the taskbar is back, and why (for its log).</summary>
public readonly record struct RelaunchDecision(RelaunchKind Kind, string Why);

/// <summary>
/// After the shell has gone: start it again after a crash (the setting, <see cref="CrashRelaunchPolicy"/>), or after it
/// closed for another program (<see cref="CloseAppRelaunchPolicy"/>, <see cref="CloseAppDelay"/> later). Never while
/// Windows shuts down, nor after the session ended: on 2026-10-13 and 10-14, at each restart and shutdown, Windows
/// ended the shell half-way through its stop, the state still read "not clean", and the watchdog started it again as
/// after a crash.
/// </summary>
public static class RelaunchPlanner
{
    /// <summary>The other program's installer goes on after the shell has closed (the Claude app's took 33 s in all).</summary>
    public static readonly TimeSpan CloseAppDelay = TimeSpan.FromSeconds(10);

    public static RelaunchDecision Decide(ShellState st, bool relaunchAfterCrash, bool shuttingDown, DateTimeOffset now)
    {
        if (shuttingDown) return new(RelaunchKind.None, "Windows is shutting down");
        if (st.EndedBy == ShellState.SessionEnded) return new(RelaunchKind.None, "the session ended");
        if (st.EndedBy == ShellState.ClosedForAnotherProgram)
            return CloseAppRelaunchPolicy.ShouldRelaunch(st.CloseAppRelaunches, now)
                ? new(RelaunchKind.AfterCloseApp, "it closed for another program")
                : new(RelaunchKind.None, $"it closed for other programs {CloseAppRelaunchPolicy.Max} times in {CloseAppRelaunchPolicy.Window.TotalMinutes:0} minutes");
        if (st.CleanExit) return new(RelaunchKind.None, "it was quit");
        if (!relaunchAfterCrash) return new(RelaunchKind.None, "relaunch after a crash is off");
        return CrashRelaunchPolicy.ShouldRelaunch(st.Relaunches, now)
            ? new(RelaunchKind.AfterCrash, "it crashed")
            : new(RelaunchKind.None, $"it crashed {CrashRelaunchPolicy.Max} times in {CrashRelaunchPolicy.Window.TotalMinutes:0} minutes");
    }
}

/// <summary>The watchdog starts a shell that closed for another program at most <see cref="Max"/> times within <see cref="Window"/>.</summary>
public static class CloseAppRelaunchPolicy
{
    public const int Max = 3;
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);

    public static bool ShouldRelaunch(IReadOnlyList<DateTimeOffset> history, DateTimeOffset now) =>
        history.Count(t => now - t < Window && t <= now) < Max;

    public static List<DateTimeOffset> Record(IEnumerable<DateTimeOffset> history, DateTimeOffset now) =>
        history.Where(t => now - t < Window && t <= now).Append(now).ToList();
}
