using System.Windows.Threading;
using Frostbar.Core.Shell;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Shell;

/// <summary>
/// Hides Explorer's taskbar while the shell runs (ADR-002) and puts it back exactly as saved.
/// The snapshot goes to state.json before anything changes, so the watchdog, the next start and the emergency script
/// can all undo it. A taskbar that comes back is hidden again at once (EVENT_OBJECT_SHOW) and by a 1 s safety check
/// on the ticker thread: is the hidden window visible (and is Explorer's the same). Auto-hide being turned off is
/// asked of Explorer (ABM_GETSTATE, a message to it that waits up to 5 s when Explorer hangs: on a thread of its own,
/// not the ticker's) on ABN_STATECHANGE, on WM_SETTINGCHANGE "TraySettings" and every 30 s. Until 2026-10-15 the 1 s
/// check ran on the UI thread (a DispatcherTimer) and asked all of it every second.
/// </summary>
public sealed class TaskbarHider : IDisposable
{
    const string KeepJob = "taskbar.keep", StateJob = "taskbar.state";

    readonly StateStore _store;
    readonly Dispatcher _ui;
    readonly BackgroundTicker _ticker;
    readonly SignalWorker _state;
    volatile string _strategy = "hide";
    volatile bool _hidden;
    bool _pending, _missing;
    IntPtr _hook;
    Native.WinEventProc? _proc;
    Action? _keepNow;

    public TaskbarHider(StateStore store, Dispatcher ui, BackgroundTicker ticker)
    {
        _store = store;
        _ui = ui;
        _ticker = ticker;
        _state = new SignalWorker("taskbar state", CheckState);
    }

    public bool IsHidden => _hidden;
    public string Strategy => _strategy;

    public void Hide(string strategy)
    {
        if (_hidden) return;
        _strategy = strategy;
        if (Taskbar.Primary() == IntPtr.Zero)
        {
            _pending = true;
            Log.Warn("taskbar not found (Explorer not running?): will hide it when Explorer starts");
            return;
        }
        _pending = false;
        var fresh = Taskbar.Snapshot(strategy);
        if (fresh.AbmState < 0)
        {
            // Explorer did not answer what its auto-hide is (busy, at sign-in): not hidden until it does, or the restore
            // would not know what to put back (read as off, a user's auto-hide on was turned off at the quit).
            _pending = true;
            if (!_askedAgain) Log.Warn("taskbar: Explorer did not say its auto-hide state: hiding it when it does (asked again every 2 s)");
            _askedAgain = true;
            System.Threading.Tasks.Task.Delay(2000).ContinueWith(_ => _ui.BeginInvoke(() => { if (_pending && !_hidden) Hide(_strategy); }));
            return;
        }
        _askedAgain = false;
        fresh.HiddenByUs = true;
        TaskbarSavedState snap = fresh;
        _store.Update(s =>
        {
            snap = TaskbarRecoveryPlanner.ChooseSnapshot(s.Taskbar, fresh);
            if (!ReferenceEquals(snap, fresh) && !_protected) Log.Warn("an earlier snapshot is still marked hidden: keeping it as the original state");
            snap.Strategy = strategy;
            snap.OnlyTopmost = false; // hidden now: all of it is put back
            s.Taskbar = snap;
        });
        Apply();
        _hidden = true;
        StartKeeper();
        Log.Info($"taskbar hidden ({strategy}); saved auto-hide state {snap.AbmState}, " +
                 string.Join(", ", snap.Windows.Select(w => $"{w.Class} visible={w.Visible} ex=0x{w.ExStyle:X}")));
    }

    /// <summary>
    /// Before something else changes the taskbar without hiding it (ManagedShell's tray takes WS_EX_TOPMOST off it):
    /// saves the original state marked as ours, so <see cref="Restore"/>, the watchdog and the next start put it back.
    /// A later <see cref="Hide"/> keeps this snapshot (it is the original).
    /// </summary>
    public void Protect()
    {
        if (Taskbar.Primary() == IntPtr.Zero) return;
        var fresh = Taskbar.Snapshot(_strategy);
        fresh.HiddenByUs = true;
        fresh.OnlyTopmost = true; // not hidden: only always-on-top is put back (a later Hide saves all of it)
        _store.Update(s => s.Taskbar = TaskbarRecoveryPlanner.ChooseSnapshot(s.Taskbar, fresh));
        _protected = true;
        Log.Info("taskbar state saved before the tray starts: " + string.Join(", ", fresh.Windows.Select(w => $"{w.Class} ex=0x{w.ExStyle:X}")));
    }

    bool _protected, _askedAgain;

    void Apply()
    {
        // Auto-hide first so Explorer gives the screen space back, then hide or make transparent.
        if ((Taskbar.GetState() & Native.ABS_AUTOHIDE) == 0) Taskbar.SetState(Native.ABS_AUTOHIDE);
        if (_strategy == "transparent") Taskbar.MakeTransparent(); else Taskbar.HideWindows();
    }

    /// <summary>Puts the taskbar back as saved. Safe to call when nothing is hidden.</summary>
    public string Restore()
    {
        StopKeeper();
        _hidden = false;
        _pending = false;
        _protected = false;
        var st = _store.Load();
        if (st.Taskbar is not { } saved) return "nothing saved";
        // Explorer's taskbar not there (Explorer restarting at a quit): nothing can be put back, so it stays recorded as
        // hidden, and the next start or the watchdog puts it back (its auto-hide stays in Explorer's settings). It was
        // recorded as put back, and nothing ever turned the auto-hide off again.
        if (Taskbar.Primary() == IntPtr.Zero)
        {
            Log.Warn("taskbar: not restored, Explorer's taskbar is not there: kept as hidden by us");
            return "Explorer's taskbar not there";
        }
        // Not answered (Explorer busy): its auto-hide is set back as saved whatever it is (read as 0, it was left on).
        int abm = Taskbar.StateOrNull() ?? -1;
        var plan = TaskbarRecoveryPlanner.Plan(saved, abm, Taskbar.Observe());
        if (!Taskbar.Apply(plan))
        {
            // Its auto-hide not known to be set back (Explorer did not answer): still hidden by us, put back by the
            // watchdog or the next start. It was recorded as put back whatever happened (2026-11-06 review).
            Log.Warn("taskbar: Explorer did not answer the auto-hide state: kept as hidden by us (" + plan + ")");
            return plan + " (auto-hide not answered)";
        }
        _store.Update(s => { if (s.Taskbar != null) s.Taskbar.HiddenByUs = false; });
        Log.Info("taskbar restored: " + plan);
        return plan.ToString();
    }

    /// <summary>Explorer restarted (TaskbarCreated): the new taskbar windows are hidden again; the old snapshot stays.</summary>
    public void OnExplorerRestarted()
    {
        if (_pending) { Hide(_strategy); return; } // Explorer was missing when we started
        if (!_hidden) return;
        StopKeeper();
        Apply();
        _store.Update(s => { if (s.Taskbar != null) s.Taskbar.HiddenByUs = true; });
        StartKeeper();
        Log.Info("Explorer restarted: taskbar hidden again");
    }

    void StartKeeper()
    {
        uint explorer = Native.ProcessId(Taskbar.Primary());
        _proc = (hook, ev, hwnd, obj, child, thread, time) =>
        {
            // A Windows event's callback: a failure in it is logged (it ended the process by the emergency path).
            try { OnShow(hook, ev, hwnd, obj, child, thread, time); }
            catch (Exception ex) { Log.Warn("taskbar: a window shown could not be looked at", ex); }
        };
        _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW, IntPtr.Zero, _proc, explorer, 0, Native.WINEVENT_OUTOFCONTEXT);
        _hookedExplorer = explorer;
        _missing = false;
        _kept = IntPtr.Zero;
        _keepNow = _ticker.Add(KeepJob, TimeSpan.FromSeconds(1), Keep);
        _ticker.Add(StateJob, TimeSpan.FromSeconds(30), _state.Signal);
    }

    void StopKeeper()
    {
        _ticker.Remove(KeepJob);
        _ticker.Remove(StateJob);
        _keepNow = null;
        if (_hook != IntPtr.Zero) { Native.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        _hookedExplorer = 0;
    }

    void OnShow(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (!_hidden || idObject != 0) return;
        if (TaskbarRecoveryPlanner.Hides(Native.ClassName(hwnd))) // a secondary monitor's taskbar stays
        {
            Log.Debug("taskbar shown by Explorer: hiding again");
            Apply();
        }
    }

    volatile uint _hookedExplorer;

    /// <summary>
    /// Both checks at once, off the UI thread: after a resume from sleep (Explorer may have shown its taskbar), on
    /// ABN_STATECHANGE and on WM_SETTINGCHANGE "TraySettings" (auto-hide may have been turned off).
    /// </summary>
    public void CheckNow()
    {
        _keepNow?.Invoke();
        if (_hidden) _state.Signal();
    }

    /// <summary>The hidden taskbar's window while it lives (the ticker thread's; zero: to be found).</summary>
    volatile IntPtr _kept;

    /// <summary>
    /// Ticker thread, every second: the hidden window shown again, or a new Explorer. Two quick questions while the
    /// window found lives (still there? shown?); finding it again (a new Explorer) walks the windows. Until 2026-10-15
    /// every run went through Taskbar.Primary() twice, some nine questions of 0.4 ms in all (cold calls are slow):
    /// 0.045% of a core with the taskbar hidden.
    /// </summary>
    void Keep()
    {
        if (!_hidden) return;
        var bar = _kept;
        if (bar == IntPtr.Zero || !Native.IsWindow(bar))
        {
            bar = Taskbar.Primary();
            _kept = bar;
            if (bar == IntPtr.Zero)
            {
                // Explorer is gone (restarting): look every 100 ms so its new taskbar is hidden almost at once.
                if (!_missing) { _missing = true; _ticker.Retime(KeepJob, TimeSpan.FromMilliseconds(100)); }
                return;
            }
            if (_missing) { _missing = false; _ticker.Retime(KeepJob, TimeSpan.FromSeconds(1)); }
            uint found = Native.ProcessId(bar);
            if (found != _hookedExplorer) { _ui.BeginInvoke(() => Back(found, "taskbar came back (a new Explorer): hiding again")); return; }
        }
        if (!Taskbar.Showing(bar, _strategy)) return;
        uint pid = Native.ProcessId(bar);
        if (pid != _hookedExplorer) { _kept = IntPtr.Zero; return; } // not the window we found (its number taken by another): found again next time
        _ui.BeginInvoke(() => Back(pid, "taskbar came back (watchdog restore, settings change or Explorer): hiding again"));
    }

    /// <summary>Its own thread, every 30 s and when told: auto-hide still on (ABM_GETSTATE, a message to Explorer).</summary>
    void CheckState()
    {
        if (!_hidden || Taskbar.Primary() == IntPtr.Zero) return;
        // Not answered (Explorer busy) is not "turned off": read as 0, it hid again on the UI thread, asking Explorer again.
        if (((Taskbar.StateOrNull() ?? Native.ABS_AUTOHIDE) & Native.ABS_AUTOHIDE) != 0) return;
        _ui.BeginInvoke(() => Back(_hookedExplorer, "taskbar auto-hide turned off (settings or Explorer): hiding again", autoHideOff: true));
    }

    /// <summary>
    /// UI thread: follow a new Explorer's show events, and hide again if something brought the taskbar back
    /// (<paramref name="autoHideOff"/>: already known, not asked of Explorer again here).
    /// </summary>
    void Back(uint pid, string why, bool autoHideOff = false)
    {
        if (!_hidden) return;
        if (pid != 0 && pid != _hookedExplorer)
        {
            // A new Explorer: follow its show events from now on (the hook belongs to this thread's message loop).
            if (_hook != IntPtr.Zero) Native.UnhookWinEvent(_hook);
            _hook = Native.SetWinEventHook(Native.EVENT_OBJECT_SHOW, Native.EVENT_OBJECT_SHOW, IntPtr.Zero, _proc!, pid, 0, Native.WINEVENT_OUTOFCONTEXT);
            _hookedExplorer = pid;
        }
        if (!autoHideOff && !Taskbar.AnyShowing(_strategy) && (Taskbar.GetState() & Native.ABS_AUTOHIDE) != 0) return;
        Log.Info(why);
        Apply();
        _store.Update(s => { if (s.Taskbar != null) s.Taskbar.HiddenByUs = true; });
    }

    public void Dispose()
    {
        StopKeeper();
        _state.Dispose();
    }
}
