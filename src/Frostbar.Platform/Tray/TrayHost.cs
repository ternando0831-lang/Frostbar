using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ManagedShell.WindowsTray;
using Frostbar.Core.MenuBar;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;
using MsRect = ManagedShell.Interop.NativeMethods.Rect;

namespace Frostbar.Platform.Tray;

/// <summary>
/// A tray icon as the UI sees it: frozen, safe to use on any thread. <see cref="Key"/> is ManagedShell's identifier
/// (clicks go back with it; it holds the window's handle, new with every start of the app); <see cref="SettingsKey"/>
/// stays the same across starts (TrayKeys: where the user put the icon is kept under it).
/// </summary>
public sealed record TrayItem(string Key, ImageSource? Icon, string Tooltip, string ExeName, Guid Guid, string SettingsKey, string ExePath);

/// <summary>A tray app's balloon notification (Shell_NotifyIcon with NIF_INFO) as the UI sees it; <see cref="Number"/> clicks it.</summary>
public sealed record TrayBalloon(int Number, string Title, string Text, string ExePath, string Tooltip, DateTimeOffset Received);

/// <summary>
/// Other apps' notification-area icons, through ManagedShell (ADR-001). Everything ManagedShell owns lives on one
/// dedicated STA thread; the UI only gets frozen snapshots and sends clicks back. That thread must stay light: while it
/// is busy, every app's Shell_NotifyIcon call waits (M0 S2: 4.8 s for a 5 s stall).
/// ManagedShell's start pushes Explorer's taskbar to HWND_BOTTOM; it is started here without that step
/// (<see cref="InitializeArea"/>). The shell still saves the taskbar state first and puts it back when it stops.
/// </summary>
public sealed class TrayHost : IDisposable
{
    readonly Dispatcher _ui;
    Dispatcher? _dispatcher; // the tray thread's; set once, never cleared
    Thread? _thread;
    Task<bool>? _start;
    volatile bool _disposed;
    TrayService? _service;
    NotificationArea? _area;
    DispatcherTimer? _publish;
    readonly HashSet<NotifyIcon> _watched = new();

    /// <summary>Raised on the UI thread with the visible icons, in the tray's order.</summary>
    public event Action<IReadOnlyList<TrayItem>>? Changed;
    /// <summary>Raised on the UI thread when an app shows a balloon notification.</summary>
    public event Action<TrayBalloon>? BalloonShown;
    /// <summary>Balloons received since the start (diagnostics).</summary>
    public int BalloonsReceived => Volatile.Read(ref _balloonCount);

    const int BalloonsKept = 50;
    readonly Dictionary<int, NotificationBalloon> _balloons = new(); // tray thread
    int _balloonCount;
    public bool Running { get; private set; }
    /// <summary>The tray's window, for the watchdog's responsiveness check.</summary>
    public IntPtr Handle { get; private set; }
    /// <summary>OS id of the tray thread (diagnostics: per-thread CPU).</summary>
    public uint ThreadId { get; private set; }

    readonly BackgroundTicker _ticker;

    public TrayHost(Dispatcher ui, BackgroundTicker ticker)
    {
        _ui = ui;
        _ticker = ticker;
    }

    public Task<bool> StartAsync()
    {
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _start = started.Task;
        _thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            _dispatcher = dispatcher;
            // An exception on this thread would end the whole process (and hide nothing less than the taskbar's state):
            // the tray logs it and carries on.
            dispatcher.UnhandledException += (_, e) => { Log.Error("tray: unhandled exception on the tray thread", e.Exception); e.Handled = true; };
            try
            {
                ThreadId = Native.GetCurrentThreadId();
                _service = new TrayService();
                _area = new NotificationArea(_service, new ExplorerTrayService());
                InitializeArea(_area);
                if (_area.IsFailed || _disposed)
                {
                    // Failed, or the shell began to stop while ManagedShell was starting (2026-10-12: a quit 5 ms
                    // into the start left the thread without a message loop and the shutdown hung).
                    if (_area.IsFailed) Log.Warn("tray: ManagedShell could not start the notification area");
                    CleanUp();
                    started.TrySetResult(false);
                    return;
                }
                Handle = _area.Handle;
                ReplaceMonitor();
                TaskbarOnTopAgain();
                _ticker.Add(CheckJob, TimeSpan.FromSeconds(5), Check);
                _checking = true;
                _publish = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => { _publish!.Stop(); Publish(); }, dispatcher);
                _area.TrayIcons.CollectionChanged += OnCollectionChanged;
                _area.NotificationBalloonShown += OnBalloon;
                foreach (var i in _area.TrayIcons) Watch(i);
                Running = true;
                Publish();
                started.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Log.Error("tray: start failed", ex);
                CleanUp();
                started.TrySetResult(false);
                return;
            }
            Dispatcher.Run();
        }) { IsBackground = true, Name = "tray" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return started.Task;
    }

    /// <summary>Lets go of ManagedShell (tray thread): also after a failed or cancelled start.</summary>
    void CleanUp()
    {
        Running = false;
        StopAskingOnTop();
        _publish?.Stop();
        if (_checking) { _ticker.Remove(CheckJob); _checking = false; }
        _keepingFirst = false;
        try
        {
            if (_area != null)
            {
                _area.TrayIcons.CollectionChanged -= OnCollectionChanged;
                _area.NotificationBalloonShown -= OnBalloon;
            }
            _area?.Dispose();
        }
        catch (Exception ex) { Log.Warn("tray: notification area dispose failed", ex); }
        try { _service?.Dispose(); }
        catch (Exception ex) { Log.Warn("tray: tray service dispose failed", ex); }
        _area = null;
        _service = null;
    }

    const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;

    /// <summary>
    /// ManagedShell's <c>NotificationArea.Initialize</c> (0.0.374, the same steps as its IL) without one: its
    /// <c>TrayService.Run</c> → <c>Resume</c> pushes Explorer's taskbar to HWND_BOTTOM so that its own window comes first,
    /// and after a fullscreen app had come and gone Explorer then refused HWND_TOPMOST on its taskbar for minutes
    /// (2026-10-15: Lab <c>trayexp bottomonly</c>, HWND_BOTTOM alone with no ManagedShell, not taken for 3 min). Ours is
    /// raised to the top instead (<c>MakeTrayTopmost</c>), the icons are asked for again (<c>SendTaskbarCreated</c>),
    /// and the 5 s check keeps it first. ManagedShell's 100 ms monitor is not started. Private members: if one is not
    /// there, ManagedShell's own Initialize runs (with the push).
    /// </summary>
    static void InitializeArea(NotificationArea area)
    {
        var a = typeof(NotificationArea);
        var t = typeof(TrayService);
        var e = typeof(ExplorerTrayService);
        var prepare = a.GetMethod("prepareCollections", Private);
        var sysTray = a.GetMethod("SysTrayCallback", Private);
        var iconData = a.GetMethod("IconDataCallback", Private);
        var trayField = a.GetField("trayDelegate", Private);
        var iconField = a.GetField("iconDataDelegate", Private);
        var explorerField = a.GetField("_explorerTrayService", Private);
        var serviceField = a.GetField("_trayService", Private);
        var objectField = a.GetField("shellServiceObject", Private);
        var setHandle = a.GetProperty("Handle")?.GetSetMethod(true);
        var setFailed = a.GetProperty("IsFailed")?.GetSetMethod(true);
        var explorerCallback = e.GetMethod("SetSystrayCallback", Private);
        var explorerRun = e.GetMethod("Run", Private);
        var serviceCallback = t.GetMethod("SetSystrayCallback", Private);
        var serviceIconData = t.GetMethod("SetIconDataCallback", Private);
        var serviceInit = t.GetMethod("Initialize", Private);
        var serviceTop = t.GetMethod("MakeTrayTopmost", Private);
        var serviceCreated = t.GetMethod("SendTaskbarCreated", Private);
        if (new object?[] { prepare, sysTray, iconData, trayField, iconField, explorerField, serviceField, objectField, setHandle, setFailed,
                explorerCallback, explorerRun, serviceCallback, serviceIconData, serviceInit, serviceTop, serviceCreated }.Any(m => m == null))
        {
            Log.Warn("tray: ManagedShell's start not found as expected: its own (which pushes Explorer's taskbar to the bottom)");
            area.Initialize();
            return;
        }
        try
        {
            prepare!.Invoke(area, null);
            var tray = Delegate.CreateDelegate(trayField!.FieldType, area, sysTray!);
            var icons = Delegate.CreateDelegate(iconField!.FieldType, area, iconData!);
            trayField.SetValue(area, tray);
            iconField.SetValue(area, icons);
            var explorer = explorerField!.GetValue(area);
            explorerCallback!.Invoke(explorer, [tray]);
            explorerRun!.Invoke(explorer, null);
            var service = serviceField!.GetValue(area);
            serviceCallback!.Invoke(service, [tray]);
            serviceIconData!.Invoke(service, [icons]);
            var handle = (IntPtr)serviceInit!.Invoke(service, null)!;
            setHandle!.Invoke(area, [handle]);
            if (handle != IntPtr.Zero)
            {
                serviceTop!.Invoke(service, null);
                serviceCreated!.Invoke(service, null);
            }
            var objects = new ShellServiceObject();
            objectField!.SetValue(area, objects);
            objects.Start();
        }
        catch (Exception ex)
        {
            Log.Warn("tray: ManagedShell's notification area did not start", ex);
            setFailed!.Invoke(area, [true]);
        }
    }
    volatile bool _keepingFirst; // ManagedShell's monitor replaced: the 5 s check keeps our window first
    volatile bool _checking;
    IntPtr _hwndTray;
    const string CheckJob = "tray.check";

    /// <summary>Ticker thread, every 5 s: our window first (when ManagedShell's monitor was replaced), icons whose app has gone.</summary>
    void Check()
    {
        if (_keepingFirst) KeepFirst();
        LookForGone();
    }

    /// <summary>
    /// Shell_NotifyIcon talks to the first "Shell_TrayWnd" in z-order, so ManagedShell keeps its window first with a
    /// private 100 ms timer that also pushes Explorer's taskbar to HWND_BOTTOM every time. That cost 0.3% of a core
    /// while idle, took WS_EX_TOPMOST off the taskbar again right after the watchdog put it back, and disturbed
    /// Explorer while it switched the taskbar to auto-hide (the space stayed reserved; 2026-10-12). Replaced by a 5 s
    /// check that only raises our window and never touches Explorer's. Private members of the pinned 0.0.374; if they
    /// are not there, ManagedShell's own timer is left running.
    /// </summary>
    void ReplaceMonitor()
    {
        try
        {
            var monitor = typeof(TrayService).GetField("trayMonitor", Private)?.GetValue(_service) as DispatcherTimer;
            _hwndTray = typeof(TrayService).GetField("HwndTray", Private)?.GetValue(_service) is IntPtr h ? h : IntPtr.Zero;
            if (monitor == null || _hwndTray == IntPtr.Zero)
            {
                Log.Warn("tray: ManagedShell's tray monitor not found: it keeps running (Explorer's taskbar loses TOPMOST until we stop)");
                return;
            }
            monitor.Stop();
            // Every 5 s (Check): FindWindow walks every top-level window (about 0.5 ms), and with the taskbar hidden
            // Explorer rarely raises its window (after a restart it is checked at once). FindWindow and SetWindowPos
            // work from any thread.
            _keepingFirst = true;
            Log.Info($"tray: ManagedShell's {monitor.Interval.TotalMilliseconds:0} ms tray monitor replaced by a 5 s check");
        }
        catch (Exception ex) { Log.Warn("tray: could not replace ManagedShell's tray monitor", ex); }
    }

    /// <summary>
    /// After the start: Explorer's taskbar always on top (it is no longer pushed to the bottom, but ManagedShell's own
    /// start may have run, or something else may have pushed it), then our tray window raised above it (ours stays
    /// first for Shell_NotifyIcon; the 5 s check keeps it so). Taken off and put back only when the shell stopped, it
    /// was not taken back three times (2026-10-15: after a fullscreen app had come and gone, Explorer refused
    /// HWND_TOPMOST for 1 to 3 min, and later runs saved that as the original); asked again every 2 s then.
    /// </summary>
    void TaskbarOnTopAgain()
    {
        const uint flags = Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE;
        var bars = Shell.Taskbar.Bars();
        int pushed = bars.Count(h => (Native.ExStyle(h) & Native.WS_EX_TOPMOST) == 0);
        foreach (var h in bars.Where(h => (Native.ExStyle(h) & Native.WS_EX_TOPMOST) == 0)) Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
        if (_hwndTray != IntPtr.Zero) Native.SetWindowPos(_hwndTray, Native.HWND_TOPMOST, 0, 0, 0, 0, flags);
        var below = bars.Where(h => (Native.ExStyle(h) & Native.WS_EX_TOPMOST) == 0).Select(Native.ClassName).ToList();
        if (below.Count == 0)
        {
            Log.Info($"tray: Explorer's taskbar always on top{(pushed > 0 ? " again" : "")} after the start; ours first: " + (Native.FindWindow("Shell_TrayWnd", null) == _hwndTray));
            return;
        }
        // Explorer kept it below for 1 to 2 minutes in the cases seen (2026-10-15, right after ManagedShell had read the
        // icons out of Explorer's tray): asked again every 2 s for up to 5 min.
        Log.Warn($"tray: Explorer kept {string.Join(", ", below)} below after ManagedShell's start (always on top not taken): asking again every 2 s");
        _onTopSince = Environment.TickCount64;
        _ticker.Add(OnTopJob, TimeSpan.FromSeconds(2), OnTopAgain);
        _askingOnTop = true;
    }

    const string OnTopJob = "tray.taskbarOnTop";
    volatile bool _askingOnTop;
    long _onTopSince;

    /// <summary>Ticker thread: asks again without waiting (a hung Explorer must not hold the ticker), and reads the answer at the next tick.</summary>
    void OnTopAgain()
    {
        if (!_askingOnTop) return;
        var bars = Shell.Taskbar.Bars();
        var below = bars.Where(h => (Native.ExStyle(h) & Native.WS_EX_TOPMOST) == 0).ToList();
        long waited = Environment.TickCount64 - _onTopSince;
        if (below.Count == 0)
        {
            StopAskingOnTop();
            Native.SetWindowPos(_hwndTray, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS);
            Log.Info($"tray: Explorer's taskbar always on top again {waited / 1000.0:0.0} s after ManagedShell's start");
            return;
        }
        if (waited > 5 * 60_000)
        {
            StopAskingOnTop();
            Log.Warn("tray: Explorer still keeps its taskbar below after 5 min: left to the restore at the stop");
            return;
        }
        // Not over a fullscreen app: Explorer keeps its taskbar below one itself.
        var covered = Shell.FullScreenDetector.FrontCovers(h => Native.ProcessId(h) == (uint)Environment.ProcessId, out _);
        foreach (var h in below)
            if (covered == IntPtr.Zero || Native.MonitorFromWindow(h, Native.MONITOR_DEFAULTTONEAREST) != covered)
                Native.SetWindowPos(h, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS);
    }

    void StopAskingOnTop()
    {
        if (!_askingOnTop) return;
        _askingOnTop = false;
        _ticker.Remove(OnTopJob);
    }

    /// <summary>Raises our tray window if another Shell_TrayWnd (Explorer's) would get the icons first.</summary>
    void KeepFirst()
    {
        if (_hwndTray == IntPtr.Zero || Native.FindWindow("Shell_TrayWnd", null) == _hwndTray) return;
        // Asynchronous: called from the ticker thread, it must not wait on a busy or hung tray thread.
        Native.SetWindowPos(_hwndTray, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_ASYNCWINDOWPOS);
        Log.Debug("tray: our tray window raised above Explorer's again");
    }

    /// <summary>The windows that own the icons (written on the tray thread, read by the 5 s check).</summary>
    volatile IntPtr[] _owners = [];
    int _droppedOnClick, _droppedByCheck;

    /// <summary>Icons dropped because their app's window was gone: at a click on them, and by the 5 s check (diagnostics).</summary>
    public (int onClick, int byCheck) Dropped => (Volatile.Read(ref _droppedOnClick), Volatile.Read(ref _droppedByCheck));

    IntPtr[] Owners() => _area == null ? [] : [.. _area.TrayIcons.Select(i => i.HWnd).Distinct()];

    /// <summary>
    /// Ticker thread: an app that ends without taking its icon away (killed, crashed, an exit that skips NIM_DELETE)
    /// leaves it in ManagedShell's list, and a click on it reaches no one (2026-10-22: Laragon's elephant stayed on the
    /// bar after Laragon had gone, and its right-click showed nothing). Explorer and ManagedShell drop such an icon when
    /// the pointer passes over it; the bar forwards no hover, so the check looks: IsWindow on each icon's window, the
    /// tray thread asked only when one is gone.
    /// </summary>
    void LookForGone()
    {
        var tray = _dispatcher;
        if (!Running || tray == null) return;
        foreach (var h in _owners)
            if (!Native.IsWindow(h)) { tray.BeginInvoke(DropGone); return; }
    }

    /// <summary>Tray thread: drops the icons whose window is gone.</summary>
    void DropGone()
    {
        if (_area == null) return;
        foreach (var i in _area.TrayIcons.ToList()) Gone(i, onClick: false);
        _owners = Owners(); // as they are now: a window an icon has moved to is not looked at every 5 s
    }

    /// <summary>Tray thread: drops the icon when its app's window is gone (true).</summary>
    bool Gone(NotifyIcon icon, bool onClick)
    {
        if (Native.IsWindow(icon.HWnd)) return false;
        _area?.TrayIcons.Remove(icon);
        if (onClick) Interlocked.Increment(ref _droppedOnClick);
        else Interlocked.Increment(ref _droppedByCheck);
        Log.Info($"tray: {Path.GetFileName(icon.Path ?? "?")}'s icon dropped {(onClick ? "at a click" : "by the check")}: its window is gone (the app ended without taking it away)");
        return true;
    }

    /// <summary>
    /// Explorer restarted: apps re-added their icons to its new taskbar, which was first in z-order for a moment.
    /// Raise ours and announce it again (ManagedShell's TaskbarCreated) so they add them here too.
    /// </summary>
    public void OnExplorerRestarted()
    {
        var tray = _dispatcher;
        if (!Running || tray == null) return;
        tray.BeginInvoke(() =>
        {
            KeepFirst();
            // (The constructor that takes a callback rejects null: 2026-10-12 this crashed the shell on Explorer restarts.)
            var again = new DispatcherTimer(DispatcherPriority.Background, tray) { Interval = TimeSpan.FromMilliseconds(500) };
            again.Tick += (_, _) =>
            {
                again.Stop();
                KeepFirst();
                try
                {
                    typeof(TrayService).GetMethod("SendTaskbarCreated", Private)?.Invoke(_service, null);
                    Log.Info("tray: announced again after Explorer's restart");
                }
                catch (Exception ex) { Log.Warn("tray: could not announce the tray again", ex); }
            };
            again.Start();
        });
    }

    void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null) foreach (NotifyIcon i in e.NewItems) Watch(i);
        if (e.OldItems != null) foreach (NotifyIcon i in e.OldItems) { i.PropertyChanged -= OnIconChanged; _watched.Remove(i); }
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var i in _watched) i.PropertyChanged -= OnIconChanged;
            _watched.Clear();
            foreach (var i in _area!.TrayIcons) Watch(i);
        }
        Schedule();
    }

    void Watch(NotifyIcon i)
    {
        if (_watched.Add(i)) i.PropertyChanged += OnIconChanged;
    }

    void OnIconChanged(object? sender, PropertyChangedEventArgs e)
    {
        Stats.Inc("tray.iconChanged." + (e.PropertyName ?? "all"));
        if (e.PropertyName is nameof(NotifyIcon.Icon) or nameof(NotifyIcon.Title) or nameof(NotifyIcon.IsHidden) or null or "") Schedule();
    }

    /// <summary>Coalesces bursts (apps that animate their icon, start-up re-registration) into one snapshot.</summary>
    void Schedule()
    {
        if (_publish is { IsEnabled: false }) _publish.Start();
    }

    /// <summary>
    /// Explorer's own volume, network and power icons: the bar has its own status items for these, so they are left
    /// out (macOS shows one of each). Other Explorer icons stay: Bluetooth, Safely Remove Hardware, the microphone
    /// and location in-use indicators.
    /// </summary>
    static readonly HashSet<Guid> ReplacedByStatusItems =
    [
        new("7820ae73-23e3-4229-82c1-e41cb67d5b9c"), // volume
        new("7820ae74-23e3-4229-82c1-e41cb67d5b9c"), // network
        new("7820ae75-23e3-4229-82c1-e41cb67d5b9c"), // power
    ];

    void Publish()
    {
        Stats.Inc("tray.publish");
        if (_area == null) return;
        _owners = Owners();
        var items = new List<TrayItem>();
        foreach (var i in _area.TrayIcons)
        {
            if (i.IsHidden || string.IsNullOrEmpty(i.Identifier) || ReplacedByStatusItems.Contains(i.GUID)) continue;
            string path = i.Path ?? "", exe = Path.GetFileName(path);
            items.Add(new TrayItem(i.Identifier, Frozen(i.Icon), i.Title ?? "", exe, i.GUID, TrayKeys.Of(i.GUID, exe, i.UID), path));
        }
        if (_firstPublish)
        {
            _firstPublish = false;
            Log.Info($"tray: {items.Count} icons: " + string.Join(", ", items.Select(i => i.ExeName)));
        }
        _ui.BeginInvoke(() => { Last = items; Changed?.Invoke(items); });
    }

    bool _firstPublish = true;

    /// <summary>The latest snapshot (UI thread).</summary>
    public IReadOnlyList<TrayItem> Last { get; private set; } = [];

    /// <summary>Icons are made on the tray thread; the UI needs a frozen copy.</summary>
    static ImageSource? Frozen(ImageSource? src)
    {
        try
        {
            if (src == null || src.IsFrozen) return src;
            if (src.CanFreeze)
            {
                var c = src.CloneCurrentValue();
                c.Freeze();
                return c;
            }
            if (src is BitmapSource bs)
            {
                var conv = new FormatConvertedBitmap(bs, PixelFormats.Pbgra32, null, 0);
                int stride = conv.PixelWidth * 4;
                var px = new byte[stride * conv.PixelHeight];
                conv.CopyPixels(px, stride, 0);
                var copy = BitmapSource.Create(conv.PixelWidth, conv.PixelHeight, 96, 96, PixelFormats.Pbgra32, null, px, stride);
                copy.Freeze();
                return copy;
            }
        }
        catch (Exception ex) { Log.Debug("tray: icon copy failed: " + ex.Message); }
        return null;
    }

    NotifyIcon? Find(string key) => _area?.TrayIcons.FirstOrDefault(i => i.Identifier == key);

    /// <summary>
    /// Tray thread: an app's balloon. Taken as ours (Handled: ManagedShell would otherwise keep it among the icon's
    /// missed notifications, and our tray window comes first, so Explorer never sees it: until 2026-10-15 every one was
    /// dropped) and told to the UI for the notification centre. The app hears what Windows would tell it: shown, then
    /// timed out after its time (an app may wait for that before its next balloon), and clicked when the card is.
    /// </summary>
    void OnBalloon(object? sender, NotificationBalloonEventArgs e)
    {
        e.Handled = true;
        var b = e.Balloon;
        if (b == null) return;
        int n = Interlocked.Increment(ref _balloonCount);
        _balloons[n] = b;
        foreach (var old in _balloons.Keys.Where(k => k <= n - BalloonsKept).ToList()) _balloons.Remove(old);
        try { b.SetVisibility(BalloonVisibility.Visible); }
        catch (Exception ex) { Log.Debug("tray: balloon shown not told: " + ex.Message); }
        var timedOut = new DispatcherTimer(DispatcherPriority.Background, _dispatcher!) { Interval = TimeSpan.FromMilliseconds(Math.Clamp(b.Timeout, 5000, 30_000)) };
        timedOut.Tick += (_, _) =>
        {
            timedOut.Stop();
            try { b.SetVisibility(BalloonVisibility.TimedOut); }
            catch (Exception ex) { Log.Debug("tray: balloon time-out not told: " + ex.Message); }
        };
        timedOut.Start();
        string exe = b.NotifyIcon?.Path ?? "";
        // (An unset time, DateTime.MinValue, cannot carry a local offset east of UTC: it would throw.)
        var received = b.Received == default ? DateTimeOffset.Now : new DateTimeOffset(b.Received);
        var item = new TrayBalloon(n, b.Title ?? "", b.Info ?? "", exe, b.NotifyIcon?.Title ?? "", received);
        Log.Info($"tray: balloon {n} from {Path.GetFileName(exe)} (title {item.Title.Length} chars, text {item.Text.Length})"); // not the words: they can be private
        _ui.BeginInvoke(() => BalloonShown?.Invoke(item));
    }

    /// <summary>The balloon's card was clicked: its app hears it (NIN_BALLOONUSERCLICK), as from Windows' notification.</summary>
    public void ClickBalloon(int number)
    {
        if (!Running) return;
        _dispatcher?.BeginInvoke(() =>
        {
            if (!_balloons.TryGetValue(number, out var b)) { Log.Debug($"tray: balloon {number} is no longer kept"); return; }
            try { b.Click(); }
            catch (Exception ex) { Log.Warn("tray: balloon click forwarding failed", ex); }
        });
    }

    /// <summary>Diagnostics: notifications ManagedShell kept as missed (none when the shell takes every balloon), or -1.</summary>
    public int MissedNotifications()
    {
        var tray = _dispatcher;
        if (!Running || tray == null) return -1;
        var op = tray.InvokeAsync(() => _area?.TrayIcons.Sum(i => i.MissedNotifications?.Count ?? 0) ?? -1);
        return op.Wait(TimeSpan.FromMilliseconds(500)) == DispatcherOperationStatus.Completed ? op.Result : -1;
    }

    /// <summary>
    /// Forwards a click on our copy of the icon to its app (the app opens its own menu or window). The cursor position
    /// travels with the message for apps that place their menu at it. An icon whose app has gone goes instead.
    /// </summary>
    public void Mouse(string key, MouseButton button, bool down)
    {
        Native.GetCursorPos(out var pt);
        uint mouse = (uint)((pt.Y << 16) | (pt.X & 0xFFFF));
        int dbl = (int)Native.GetDoubleClickTime();
        if (!Running) return;
        _dispatcher?.BeginInvoke(() =>
        {
            var icon = Find(key);
            if (icon == null || Gone(icon, onClick: true)) return;
            try
            {
                if (down) icon.IconMouseDown(button, mouse, dbl);
                else icon.IconMouseUp(button, mouse, dbl);
            }
            catch (Exception ex) { Log.Warn("tray: click forwarding failed", ex); }
        });
    }

    /// <summary>Where our copy of each icon is on screen (pixels): apps ask for it to place their pop-ups.</summary>
    public void SetPlacements(IReadOnlyDictionary<string, RECT> px)
    {
        if (!Running) return;
        _dispatcher?.BeginInvoke(() =>
        {
            foreach (var (key, r) in px)
                if (Find(key) is { } icon) icon.Placement = new MsRect { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        });
    }

    /// <summary>
    /// Stops the tray (from the UI thread). Bounded: waits up to 3 s for a start in progress (it sees the flag and
    /// cleans up by itself) and up to 3 s for ManagedShell to let go; never <c>InvokeShutdown</c>, which waits without
    /// a timeout, forever when the tray thread is stuck or already gone.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _start?.Wait(TimeSpan.FromSeconds(3)); } catch (Exception) { }
        var dispatcher = _dispatcher;
        if (dispatcher == null) return;
        bool loopRunning = _start is { IsCompletedSuccessfully: true, Result: true } && _thread is { IsAlive: true };
        if (loopRunning)
            try { dispatcher.Invoke(CleanUp, DispatcherPriority.Send, CancellationToken.None, TimeSpan.FromSeconds(3)); }
            catch (Exception ex) { Log.Warn("tray: stop failed", ex); }
        dispatcher.BeginInvokeShutdown(DispatcherPriority.Send);
    }
}
