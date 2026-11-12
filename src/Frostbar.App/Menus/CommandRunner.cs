using System.Diagnostics;
using System.IO;
using Frostbar.App.Common;
using Frostbar.Core.Localization;
using Frostbar.Core.Menus;
using Frostbar.Core.Shell;
using Frostbar.Core.Wm;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Input;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Sys;
using Frostbar.Platform.Wm;

namespace Frostbar.App.Menus;

/// <summary>Carries out menu items for the app in front.</summary>
public sealed class CommandRunner
{
    readonly ForegroundTracker _front;
    readonly PowerActions _power;
    readonly Func<Strings> _strings;
    readonly Dialogs _dialogs;
    readonly List<IntPtr> _hidden = new();

    /// <summary>What the last command did (diagnostics pipe / E2E).</summary>
    public string LastResult { get; private set; } = "";

    /// <summary>Opens Frostbar's settings window (set by the shell; without it the item opens settings.json).</summary>
    public Action? OpenShellSettings { get; set; }

    /// <summary>Quits Frostbar itself, as <c>Frostbar.exe --quit</c> does (set by the shell).</summary>
    public Action? QuitShell { get; set; }

    /// <summary>Whether to animate (windows glide into their tiles); set by the shell from the appearance settings.</summary>
    public Func<bool> Animations { get; set; } = () => true;

    /// <summary>Whether tiled windows keep margins (macOS 15's setting, 2026-10-22); set by the shell from the settings.</summary>
    public Func<bool> TileMargins { get; set; } = () => false;

    /// <summary>
    /// Under an E2E spec only (<see cref="SpecGuard"/>): the folder of the programs the menus and the shortcuts may act
    /// on. With another app in front, its items do nothing (the system menu's still run). Null: every app.
    /// </summary>
    public string? OnlyAppsIn { get; set; }

    public CommandRunner(ForegroundTracker front, PowerActions power, Func<Strings> strings, Dialogs dialogs)
    {
        _front = front; _power = power; _strings = strings; _dialogs = dialogs;
    }

    /// <summary>The app in front is not one the spec may act on (<see cref="OnlyAppsIn"/>): <see cref="LastResult"/> says so.</summary>
    bool Guarded(FrontWindow? front)
    {
        if (OnlyAppsIn is not { } folder || front != null && SpecGuard.Allows(folder, front.ExePath, front.IsDesktop)) return false;
        LastResult = $"guarded: {(front == null ? "no app" : front.IsDesktop ? "the desktop" : front.ExeName)} in front is not a test target";
        return true;
    }

    /// <summary>The system menu's "Clear Menu" (Recent Items): set by the menu bar, which keeps them.</summary>
    public Action? ClearRecent { get; set; }

    /// <summary>
    /// A window of the app <see cref="Open"/> is asked to open, when it runs (zero when it does not): macOS brings an app
    /// that runs forward rather than starting it again. Set by the menu bar.
    /// </summary>
    public Func<string, IntPtr>? RunningWindow { get; set; }

    /// <summary>
    /// A command of the shell's own on the app in front (the input menu's mode, layout and emoji): false, and why in
    /// <see cref="LastResult"/>, when a spec's shell may not act on that app (<see cref="SpecGuard"/>).
    /// </summary>
    public bool MayAct(string what)
    {
        if (!Guarded(_front.Current)) return true;
        Log.Warn($"{what} → {LastResult}: nothing done");
        return false;
    }

    public void Run(MenuItem item)
    {
        var front = _front.Current;
        // The system menu's items act on no app in front; opening a recent item has a guard of its own (Open).
        if (item.Action.Kind is not (ActionKind.System or ActionKind.Open) && Guarded(front)) { Log.Warn($"menu: {item.Id} → {LastResult}: nothing done"); return; }
        try
        {
            switch (item.Action.Kind)
            {
                case ActionKind.Keys: SendKeys(front, item); break;
                case ActionKind.Window: WindowOp(front, item.Action.Name); break;
                case ActionKind.App: AppOp(front, item.Action.Name); break;
                case ActionKind.System: SystemOp(item.Action.Name); break;
                case ActionKind.Open: Open(item.Action.Name); break;
            }
        }
        catch (Exception ex)
        {
            LastResult = "error: " + ex.Message;
            Log.Error($"menu item {item.Id} failed", ex);
        }
        Log.Info($"menu: {item.Id} → {LastResult}");
    }

    static bool InFront(FrontWindow f)
    {
        var fg = Native.GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        if (f.IsDesktop) return Native.ClassName(fg) is "Progman" or "WorkerW";
        var root = Native.GetAncestor(fg, Native.GA_ROOTOWNER);
        return root == f.Hwnd || fg == f.Hwnd || Native.ProcessId(fg) == f.Pid;
    }

    void SendKeys(FrontWindow? front, MenuItem item)
    {
        if (front == null || item.Action.Keys is not { } keys) { LastResult = "no front app"; return; }
        if (front.IsDesktop && item.Id == "new-window") { FileManager.NewWindow(); LastResult = "launched " + FileManager.Current; return; }
        if (front.Elevated) { LastResult = "elevated: not sent"; return; }
        if (!InFront(front))
        {
            WindowActions.ForceForeground(front.Hwnd);
            var sw = Stopwatch.StartNew();
            while (!InFront(front) && sw.ElapsedMilliseconds < 150) Thread.Sleep(10);
        }
        if (!InFront(front)) { LastResult = "target did not come to the front"; return; }
        bool ok = InputInjector.SendChord(keys, Native.GetForegroundWindow());
        LastResult = ok ? $"sent {keys.Format()}" : "SendInput failed";
    }

    /// <summary>Each window's frame from before Move &amp; Resize first moved it ("Return to Previous Size").</summary>
    public TileMemory Tiles { get; } = new();

    /// <summary>The window the Window menu's operations act on: the front app's (the desktop's: its first folder window).</summary>
    public IntPtr TargetWindow(FrontWindow? front) =>
        front == null ? IntPtr.Zero : front.IsDesktop ? _front.WindowsOf(front).FirstOrDefault() : front.Hwnd;

    /// <summary>A window operation on the window in front, as the Window menu runs it (a shortcut's, stage 3-2).</summary>
    public void RunWindowOp(string op)
    {
        var front = _front.Current;
        if (Guarded(front)) { Log.Warn($"window: {op} → {LastResult}: nothing done"); return; }
        try { WindowOp(front, op); }
        catch (Exception ex)
        {
            LastResult = "error: " + ex.Message;
            Log.Error($"window operation {op} failed", ex);
        }
        Log.Info($"window: {op} → {LastResult}");
    }

    void WindowOp(FrontWindow? front, string op)
    {
        if (op.StartsWith(WindowOps.ActivatePrefix, StringComparison.Ordinal))
        {
            var h = new IntPtr(long.Parse(op[WindowOps.ActivatePrefix.Length..]));
            WindowActions.Activate(h);
            LastResult = "activated";
            return;
        }
        if (front == null) { LastResult = "no front app"; return; }
        var target = TargetWindow(front);
        if (target == IntPtr.Zero) { LastResult = "no window"; return; }
        // As the menu disables them (a shortcut has no menu): an elevated app's windows refuse all but Minimize (UIPI).
        if (front.Elevated && !WindowOps.ReachesElevated(op)) { LastResult = "elevated: not done"; return; }
        Tiles.KeepOnly(w => Native.IsWindow(new IntPtr(w)));
        if (WindowOps.Place(op) is { } place)
        {
            // The first move keeps the frame from before; moving it again keeps that one (macOS returns to it).
            if (WindowActions.Tile(target, place, glide: Animations(), margins: TileMargins()) is { } before) Tiles.Moving(target.ToInt64(), before);
            LastResult = op;
            return;
        }
        switch (op)
        {
            case WindowOps.Minimize: WindowActions.Minimize(target); break;
            case WindowOps.Zoom: WindowActions.ToggleZoom(target); break;
            case WindowOps.ReturnFrame:
                if (Tiles.Take(target.ToInt64()) is not { } kept) { LastResult = "nothing kept"; return; }
                WindowActions.Return(target, kept, glide: Animations());
                break;
            case WindowOps.FrontAll: WindowActions.BringAllToFront(_front.WindowsOf(front)); break;
        }
        LastResult = op;
    }

    void AppOp(FrontWindow? front, string op)
    {
        if (front == null) { LastResult = "no front app"; return; }
        switch (op)
        {
            case AppOps.About: _dialogs.AboutApp(front); break;
            case AppOps.Hide:
                _hidden.AddRange(WindowActions.MinimizeAll(_front.WindowsOf(front)));
                break;
            case AppOps.HideOthers:
                var mine = _front.WindowsOf(front).ToHashSet();
                // A spec's shell hides the test targets' other windows only: the guard looked at the app in front, and
                // with the test target there every window of the user's was minimized.
                _hidden.AddRange(WindowActions.MinimizeAll(WindowList.AppWindows().Where(h => !mine.Contains(h)
                    && (OnlyAppsIn is not { } only || SpecGuard.Allows(only, Native.ProcessPath(Native.AppOrWindowProcessId(h)), isDesktop: false)))));
                break;
            case AppOps.ShowAll:
                foreach (var h in _hidden.Where(Native.IsWindow)) WindowActions.Restore(h); // an administrator's too
                _hidden.Clear();
                break;
            case AppOps.Quit:
                // Explorer: close its folder windows only, never the desktop or the taskbar.
                var windows = _front.WindowsOf(front);
                if (front.ExeName.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
                    windows = windows.Where(h => Native.ClassName(h) == "CabinetWClass").ToList();
                foreach (var h in windows) WindowActions.Close(h);
                break;
        }
        LastResult = op;
    }

    /// <summary>A system menu's command without its menu (the search's actions, stage 6-1): asks first where the menu does.</summary>
    public void RunSystem(string op)
    {
        try { SystemOp(op); }
        catch (Exception ex)
        {
            LastResult = "error: " + ex.Message;
            Log.Error($"system command {op} failed", ex);
        }
        Log.Info($"system: {op} → {LastResult}");
    }

    /// <summary>Power actions only log in a development build (a spec's shell is one): nothing to guard there.</summary>
    public bool PowerIsDryRun => _power.DryRun;

    void SystemOp(string op)
    {
        var s = _strings();
        switch (op)
        {
            case SystemOps.AboutPc: _dialogs.AboutPc(); LastResult = "about-pc"; break;
            // Windows' apps: not from a spec's shell (as the search's Windows pages are not).
            case SystemOps.Settings: if (OnlyAppsIn != null) { LastResult = "guarded: Windows' Settings"; break; } Launch("ms-settings:"); LastResult = "settings"; break;
            case SystemOps.Store: if (OnlyAppsIn != null) { LastResult = "guarded: the Store"; break; } Launch("ms-windows-store:"); LastResult = "store"; break;
            case SystemOps.ShellSettings:
                if (OpenShellSettings != null) { OpenShellSettings(); LastResult = "shell settings"; }
                else { Launch(AppPaths.SettingsFile); LastResult = "shell settings (file)"; }
                break;
            case SystemOps.QuitShell: QuitShell?.Invoke(); LastResult = QuitShell != null ? "quit shell" : "no quit here"; break;
            case SystemOps.ForceQuit: _dialogs.ForceQuit(); LastResult = "force-quit"; break;
            case SystemOps.Sleep:
                LastResult = _power.Sleep();
                if (LastResult == PowerActions.Skipped) _dialogs.Skipped(s["sys.sleep"]);
                break;
            case SystemOps.Lock:
                LastResult = _power.Lock();
                if (LastResult == PowerActions.Skipped) _dialogs.Skipped(s["sys.lock"]);
                break;
            case SystemOps.Restart: _dialogs.ConfirmPower(s["dlg.restartTitle"], s["dlg.restart"], () => _power.Restart()); LastResult = "confirm restart"; break;
            case SystemOps.Shutdown: _dialogs.ConfirmPower(s["dlg.shutdownTitle"], s["dlg.shutdown"], () => _power.Shutdown()); LastResult = "confirm shutdown"; break;
            case SystemOps.Logout: _dialogs.ConfirmPower(s["dlg.logoutTitle"], s["dlg.logout"], () => _power.Logout()); LastResult = "confirm logout"; break;
            case SystemOps.ClearRecent: ClearRecent?.Invoke(); LastResult = "recent items cleared"; break;
        }
    }

    /// <summary>
    /// Opens a program, a document or an AppsFolder entry: the system menu's Recent Items, the app that plays (Now
    /// Playing). A spec's shell opens only a program of the test target's folder: a document would open in whatever
    /// app owns its kind (<see cref="SpecGuard"/>).
    /// </summary>
    public void Open(string target)
    {
        if (OnlyAppsIn is { } folder && !(ProgramOf(target) is { } program && program.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                                          && SpecGuard.Allows(folder, program, isDesktop: false)))
        {
            LastResult = "guarded: not a test target's program";
            Log.Warn($"open: {Path.GetFileName(target)} → {LastResult}: nothing done");
            return;
        }
        // An app (not a document's link) that runs comes forward.
        if (!target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) && RunningWindow?.Invoke(target) is { } h && h != IntPtr.Zero)
        {
            WindowActions.Activate(h);
            LastResult = "brought forward";
            return;
        }
        if (Opening?.Invoke(target, "open") == true) { LastResult = "brought forward"; return; }
        Launch(target);
        LastResult = "opened";
    }

    /// <summary>
    /// An app being opened from elsewhere than the Dock (the app grid, the search, a stack, the recent items: the target
    /// and where from), told to the Dock once the caller's own guard let it through: it bounces the app's item (or gives
    /// it one), or brings the app forward when it runs (true: the caller does not start it). 2026-10-23, the user's "A
    /// どこから開いても跳ねる" and "macOS と同じく前面に出す".
    /// </summary>
    public static Func<string, string, bool>? Opening { get; set; }

    /// <summary>
    /// The program behind <paramref name="target"/>: an AppsFolder entry's (its Start menu shortcut's), a link file's
    /// (IShellLink: the shell item of a link file has no System.Link.TargetParsingPath), else the target itself.
    /// </summary>
    static string? ProgramOf(string target) =>
        target.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? Platform.Apps.OpenWith.Resolve(target)?.path
        : target.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase) ? ShellApi.LinkTarget(target)
        : target;

    /// <summary>Opens a URI, file or program; the started app may take the foreground.</summary>
    public static void Launch(string target, string? args = null)
    {
        Native.AllowSetForegroundWindow(-1 /*ASFW_ANY*/);
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Arguments = args ?? "" }); }
        catch (Exception ex) { Log.Warn($"could not open {target}", ex); }
    }

    /// <summary>
    /// Opens <paramref name="target"/> (an AppsFolder entry of a Start menu shortcut) with its "runas" verb: as administrator
    /// (the app grid's menu, 2026-10-24), the shortcut's arguments with it. On a thread of its own: the call returns once
    /// the app has started elevated, and on the UI thread it held the shell up 16 s once on this PC, which asks nothing (a
    /// PC that asks would wait for the answer too: not seen here). <paramref name="done"/> is told on that thread whether it started
    /// (false: the question declined, or it failed).
    /// </summary>
    public static void LaunchAsAdmin(string target, Action<bool>? done = null)
    {
        Native.AllowSetForegroundWindow(-1 /*ASFW_ANY*/);
        var run = new Thread(() =>
        {
            bool started;
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true, Verb = "runas" })?.Dispose(); started = true; }
            catch (Exception ex) { Log.Warn($"could not open {target} as administrator", ex); started = false; }
            done?.Invoke(started);
        }) { IsBackground = true, Name = "run-as-administrator" };
        run.SetApartmentState(ApartmentState.STA); // the shell's verbs want COM
        run.Start();
    }
}
