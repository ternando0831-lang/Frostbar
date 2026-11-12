using Frostbar.Core.Settings;
using Frostbar.Core.Shell;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Shell;

namespace Frostbar.Platform.Infrastructure;

/// <summary>
/// A separate, WPF-free process that puts the taskbar back if the shell dies or hangs (ADR-002), and starts it again
/// after a crash (at most 3 times in 5 minutes) or after it closed for another program's installer (RelaunchPlanner).
/// It is started through a short-lived launcher so it is nobody's child:
/// "End task" on the shell does not take it down, and it holds none of our handles.
/// </summary>
public static class Watchdog
{
    /// <summary>From the shell: start the launcher, which starts the watchdog and exits.</summary>
    public static void Spawn(int ownerPid, long ownerStart, string passThroughArgs)
    {
        int pid = Native.StartDetached(AppPaths.ExePath, $"--launch-watchdog {ownerPid} {ownerStart} {passThroughArgs}".TrimEnd());
        if (pid == 0) Log.Warn("could not start the watchdog");
    }

    /// <summary>The launcher: start the real watchdog detached and leave at once.</summary>
    public static int Launch(int ownerPid, long ownerStart, string passThroughArgs)
    {
        int pid = Native.StartDetached(AppPaths.ExePath, $"--watchdog {ownerPid} {ownerStart} {passThroughArgs}".TrimEnd());
        return pid == 0 ? 1 : 0;
    }

    public static int Run(int ownerPid, long ownerStart)
    {
        Log.Init(AppPaths.Logs, "watchdog", LogLevel.Info);
        var store = new StateStore(AppPaths.StateFile);
        IntPtr proc = Native.OpenProcess(Native.SYNCHRONIZE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)ownerPid);
        if (proc == IntPtr.Zero || !Native.ProcessAlive(ownerPid, ownerStart))
        {
            Log.Warn($"owner {ownerPid} is not running");
            if (proc != IntPtr.Zero) Native.CloseHandle(proc);
            OnOwnerGone(store, ownerPid);
            return 0;
        }
        Log.Info($"watching {ownerPid}");
        // The hang is timed from the last answer, not from the first unanswered ping: that ping itself waits up to 1 s
        // after a 1 s pause, which put the restore at 12.2 s instead of ~11 s (2026-10-12). A sleep is not a hang
        // (HangJudge: the clock goes on while the PC sleeps).
        var judge = new HangJudge(Environment.TickCount64);
        bool restoredForHang = false;
        DateTime stateStamp = DateTime.MinValue;
        IntPtr hb = IntPtr.Zero;
        bool replaced = false;
        Heartbeat.Reader? pulse = null;
        try
        {
            while (true)
            {
                if (Native.WaitForSingleObject(proc, 1000) == 0 /*WAIT_OBJECT_0*/) { OnOwnerGone(store, ownerPid); return 0; }
                // Re-read state.json only when it changed (reading it every tick cost more CPU than the shell itself).
                var stamp = File.Exists(store.Path) ? File.GetLastWriteTimeUtc(store.Path) : DateTime.MinValue;
                if (stamp != stateStamp)
                {
                    stateStamp = stamp;
                    var st = store.Load();
                    replaced = st.Owner != null && st.Owner.Pid != ownerPid;
                    hb = st.Owner?.Pid == ownerPid ? new IntPtr(st.Owner.HeartbeatHwnd) : IntPtr.Zero;
                }
                if (replaced || hb == IntPtr.Zero) { judge.Skip(Environment.TickCount64); continue; } // replaced, or no heartbeat yet
                pulse ??= Heartbeat.Reader.Open(ownerPid);
                // A fresh beat says the UI thread worked through its queue just now, so it would answer: no ping then.
                // The ping woke the shell's UI thread every second while idle (2026-11-08 idle-cpu: the UI thread 0.051%
                // of a core with it, 0.006% without; the watchdog 0.050% → 0.022%). A hang stops the beats,
                // and the pings come back within a beat period; the judge's stuck-beat rule times the hang from the
                // last beat as before.
                bool fresh = pulse != null && Environment.TickCount64 - pulse.LastBeat <= Heartbeat.PeriodMs + 1000;
                bool answered = fresh || Native.SendMessageTimeout(hb, Native.WM_NULL, IntPtr.Zero, IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 1000, out _) != IntPtr.Zero;
                // Answering WM_NULL is not enough: a UI thread stuck in a CLR wait still does. Its own pulse must go on too.
                // A skipped ping counts as answered at the beat, not now: a loop that stops right after a beat is still
                // told "does not answer" (Gemini's review, 2026-11-08).
                var verdict = judge.Observe(Environment.TickCount64, answered, pulse?.LastBeat, fresh ? pulse!.LastBeat : null);
                if (verdict == HangVerdict.Resumed) { Log.Info("resumed (the PC slept, or this process was stopped): counting from now"); continue; }
                if (verdict == HangVerdict.Fine)
                {
                    if (restoredForHang) Log.Info("owner responsive again (it hides the taskbar again itself)");
                    restoredForHang = false;
                    continue;
                }
                if (!restoredForHang)
                {
                    string why = verdict == HangVerdict.NotAnswering ? "does not answer" : "answers, but its UI thread is stuck";
                    Log.Warn($"owner {why} for over {HangJudge.HangMs / 1000} s: " + RestoreTaskbar(store));
                    // Its own volume display cannot come out meanwhile: Windows' again (the shell hides it again at the next key).
                    string display = VolumeDisplay.GiveBack(store);
                    if (display.Length > 0) Log.Info(display);
                    restoredForHang = true;
                }
            }
        }
        finally { pulse?.Dispose(); Native.CloseHandle(proc); Log.Flush(); }
    }

    static void OnOwnerGone(StateStore store, int ownerPid)
    {
        var st = store.Load();
        if (st.Owner != null && st.Owner.Pid != ownerPid) { Log.Info("owner replaced by another instance: nothing to do"); return; }
        Log.Info($"owner exited (clean={st.CleanExit}{(st.EndedBy != null ? ", " + st.EndedBy : "")}): " + RestoreTaskbar(store));
        if (st.AppBars.Count > 0) { AppBarHost.RemoveStale(st.AppBars); store.Update(s => s.AppBars.Clear()); }
        string display = VolumeDisplay.GiveBack(store);
        if (display.Length > 0) Log.Info(display);

        bool afterCrash = !st.CleanExit && st.EndedBy == null && SettingsStore.Load(AppPaths.SettingsFile).Settings.General.RelaunchAfterCrash;
        var now = DateTimeOffset.Now;
        var decision = RelaunchPlanner.Decide(st, afterCrash, Native.ShuttingDown(), now);
        switch (decision.Kind)
        {
            case RelaunchKind.AfterCrash:
                store.Update(s => s.Relaunches = CrashRelaunchPolicy.Record(s.Relaunches, now));
                StartAgain(st, "after a crash");
                return;
            case RelaunchKind.AfterCloseApp:
                Log.Info($"it closed for another program: starting it again in {RelaunchPlanner.CloseAppDelay.TotalSeconds:0} s");
                if (!WaitToStartAgain(store, ownerPid)) return;
                store.Update(s => s.CloseAppRelaunches = CloseAppRelaunchPolicy.Record(s.CloseAppRelaunches, DateTimeOffset.Now));
                StartAgain(st, "after closing for another program");
                return;
            default:
                if (!st.CleanExit || st.EndedBy != null) Log.Info($"not starting it again: {decision.Why}");
                if (st.CleanExit) KeepOnTop(store, ownerPid);
                return;
        }
    }

    static void StartAgain(ShellState st, string why)
    {
        // Without the owner's command line it would start on the default profile (a spec's shell on the user's state).
        if (st.Owner == null) { Log.Warn($"not starting it again {why}: its command line was not kept"); return; }
        string args = st.Owner.Args;
        int pid = Native.StartDetached(AppPaths.ExePath, (args + " --relaunched").Trim());
        Log.Info(pid != 0 ? $"started again {why} (pid {pid})" : "could not start it again");
    }

    /// <summary>
    /// The other program's installer goes on for a while after the shell has closed: wait, and give up when Windows
    /// starts shutting down meanwhile (the shell's own judgment of the query is not proof: EndSession) or another shell
    /// started.
    /// </summary>
    static bool WaitToStartAgain(StateStore store, int ownerPid)
    {
        long until = Environment.TickCount64 + (long)RelaunchPlanner.CloseAppDelay.TotalMilliseconds;
        while (Environment.TickCount64 < until)
        {
            Thread.Sleep(500);
            if (Native.ShuttingDown()) { Log.Info("Windows is shutting down: not starting it again"); return false; }
            var st = store.Load();
            if (st.Owner != null && st.Owner.Pid != ownerPid) { Log.Info("another shell started meanwhile: not starting it again"); return false; }
        }
        return true;
    }

    /// <summary>
    /// After the restore: Explorer can keep its taskbar below against HWND_TOPMOST for a minute or two (2026-10-15,
    /// after ManagedShell had read the icons out of Explorer's tray). Asked again every 2 s for up to 3 min, while no
    /// other shell has started and no fullscreen app is in front (Explorer keeps its taskbar below one itself).
    /// </summary>
    static void KeepOnTop(StateStore store, int ownerPid)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool asked = false;
        while (sw.Elapsed < TimeSpan.FromMinutes(3))
        {
            var st = store.Load();
            if (st.Owner != null && st.Owner.Pid != ownerPid) { if (asked) Log.Info("another shell started: it keeps the taskbar"); return; }
            if (st.Taskbar is not { } saved || Taskbar.Primary() == IntPtr.Zero) return;
            var now = Taskbar.Observe();
            var plan = TaskbarRecoveryPlanner.Plan(saved, Taskbar.GetState(), now);
            var topmost = plan.Steps.Where(s => s.Fix == TaskbarFix.MakeTopmost).ToList();
            if (topmost.Count == 0)
            {
                if (now.Any(w => w.UnderFullScreen && (w.ExStyle & TaskbarRecoveryPlanner.WS_EX_TOPMOST) == 0)) { if (asked) Log.Info("a fullscreen app is in front: always on top left to Explorer"); }
                else if (asked) Log.Info($"taskbar always on top again {sw.Elapsed.TotalSeconds:0.0} s after the shell left");
                return;
            }
            if (!asked) Log.Warn("Explorer keeps its taskbar below after the restore: asking again every 2 s for up to 3 min");
            asked = true;
            foreach (var step in topmost)
                foreach (var h in Taskbar.OfClass(step.Class)) Taskbar.MakeTopmost(h);
            Thread.Sleep(2000);
        }
        Log.Warn("Explorer still keeps its taskbar below 3 min after the shell left: giving up");
    }

    /// <summary>Puts the saved taskbar state back (also used by --restore-taskbar without a running shell).</summary>
    public static string RestoreTaskbar(StateStore store)
    {
        var st = store.Load();
        if (st.Taskbar is not { } saved) return "no saved taskbar state";
        if (Taskbar.Primary() == IntPtr.Zero) return "Explorer's taskbar is not there (restore when Explorer runs)";
        // Not answered (Explorer busy): the saved auto-hide set whatever it is (read as 0, an auto-hide left on was
        // taken as already off and never set back; 2026-11-06 review), and if that is not answered either, it stays
        // recorded as hidden by us (the next start puts it back).
        var plan = TaskbarRecoveryPlanner.Plan(saved, Taskbar.StateOrNull() ?? -1, Taskbar.Observe());
        if (!Taskbar.Apply(plan)) return "taskbar: " + plan + " (auto-hide not answered: kept as hidden by us)";
        store.Update(s => { if (s.Taskbar != null) s.Taskbar.HiddenByUs = false; });
        return "taskbar: " + plan;
    }
}
