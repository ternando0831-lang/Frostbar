using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Frostbar.App.AppGrid;
using Frostbar.App.Common;
using Frostbar.App.Dock;
using Frostbar.App.MenuBar;
using Frostbar.App.Search;
using Frostbar.Core.Dock;
using Frostbar.Core.HotCorners;
using Frostbar.Core.Localization;
using Frostbar.Core.Menus;
using Frostbar.Core.Search;
using Frostbar.Core.Settings;
using Frostbar.Core.Shell;
using Frostbar.Platform.Apps;
using Frostbar.Platform.Identity;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Input;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Shell;
using Frostbar.Platform.Sys;
using Frostbar.Platform.Tray;
using Frostbar.Platform.Wm;

namespace Frostbar.App;

/// <summary>
/// Start and stop order of the shell.
/// Start: settings → undo what a crashed run left → owner record + watchdog → (Explorer ready) → our bars → hide taskbar.
/// Stop: taskbar back first (the one thing that must not go wrong) → app bars → windows → state marked clean.
/// </summary>
public sealed class ShellApp : Application, Settings.ISettingsHost
{
    /// <summary>
    /// The strip kept for the Dock: its icons, the plate on both sides of them, and the margin outside it (as high at the
    /// bottom as it is wide on a side).
    /// </summary>
    double DockReservationDip => _settings.Dock.IconSize + 2 * _settings.Dock.Padding + DockSurface.BottomMargin + 4;

    /// <summary>The screen's edge the Dock stands on (the settings').</summary>
    DockEdge DockEdgeWanted => DockFrame.Parse(_settings.Dock.Position);

    readonly StartupOptions _o;
    readonly Stopwatch _since = Stopwatch.StartNew();
    AppSettings _settings = new();
    StateStore? _store;
    MessageWindow? _messages;
    ThemeService? _theme;
    // Windows' dark mode by itself and the light/dark wallpapers (stage 4-3); a spec's own dark mode (null: Windows').
    AppearanceScheduler? _schedule;
    bool? _specDark;
    TaskbarHider? _hider;
    AppBarHost? _appBars;
    FullScreenDetector? _fullScreen;
    AppIdentity? _identity;
    ForegroundTracker? _front;
    RoomWatch? _room;
    HotkeyService? _hotkeys;
    PowerActions? _power;
    MenuBarController? _bar;
    MenuBar.MenuBarPeek? _peek;
    TrayHost? _tray;
    BackgroundTicker? _ticker;
    Heartbeat? _heartbeat;
    IntPtr _explorerTaskbar;
    Window? _dockReservation;
    /// <summary>The Dock's strip along its edge (the bottom, or a side); none with auto-hide.</summary>
    AppBarHost.Bar? _dockBar;
    /// <summary>The menu bar's strip at the top; none while the bar hides always (stage 5-1).</summary>
    AppBarHost.Bar? _topBar;
    /// <summary>A full-screen app is in front on the bar's monitor (the bar waits at the top edge meanwhile).</summary>
    bool _barOverFullScreen;
    DockController? _dock;
    SearchController? _search;
    AppGridController? _grid;
    Settings.SettingsWindow? _settingsWindow;
    DispatcherTimer? _saveLater, _dockSettled;
    /// <summary>settings.json's one writer (off the UI thread): waited for before the process ends or starts again.</summary>
    SettingsSaver _saver = null!;
    Overview.OverviewController? _overview;
    HotCorners.HotCornerWatcher? _corners;
    long _catalogAt = long.MinValue / 2;
    bool _catalogLoading;
    List<AppsFolder.Entry>? _catalog;
    CommandServer? _commands, _diag;
    MonitorInfo _monitor = null!;
    /// <summary>The displays the shell's windows were last placed for, and how often a change placed them again or made them again.</summary>
    List<Display> _displays = [];
    DispatcherTimer? _displaySettle;
    int _displaysReplaced, _displaysRebuilt, _displayMessages;
    bool _stopping, _taskbarRestoredOnRequest;
    long _readyMs = -1;

    /// <summary>
    /// Start-up's steps and when each ended (ms since the shell began), logged once at ready: what start-up spends its
    /// time on, and what could wait until after it (the user's choice of 2026-10-16: keep startup's 1500 ms, start faster).
    /// </summary>
    readonly List<(string Step, long Ms)> _startSteps = new();
    /// <summary>Windows made see-through by diagnostics "lens-presence … bare" (given back by "lens-presence off").</summary>
    readonly HashSet<Window> _bared = new();

    void Mark(string step)
    {
        if (_readyMs < 0) _startSteps.Add((step, _since.ElapsedMilliseconds));
    }

    /// <summary>How long the process ran before the shell began (the runtime's start, WPF's), in ms; -1: unknown.</summary>
    long _processBeganMs = -1;
    /// <summary>Under an E2E spec: the folder of the only programs the menus and the shortcuts act on (<see cref="SpecGuard"/>).</summary>
    string? _onlyAppsIn;
    RecentDocuments? _recentDocs;
    /// <summary>Recent Items' apps are saved within a minute of a change (every switch between apps changes them).</summary>
    DispatcherTimer? _recentSave;
    /// <summary>What uses the microphone and the camera (the privacy indicator): Windows' records, or a spec's own.</summary>
    IPrivacySource? _privacy;
    /// <summary>A spec's shell's own clipboard history (clipboard-set); null in the user's shell.</summary>
    Frostbar.Platform.Search.SpecClipboardHistory? _specClipboard;
    /// <summary>The flags of the "end the session" query being answered (WPF's SessionEnding drops them).</summary>
    EndSessionWatch? _endSession;

    public ShellApp(StartupOptions o)
    {
        _o = o;
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, ev) => { Log.Error("unhandled exception on the UI thread", ev.Exception); ev.Handled = true; Stop(1, clean: false); };
        if (_o.Diag) ComponentDispatcher.ThreadFilterMessage += (ref MSG m, ref bool _) => Stats.Inc($"ui.msg.0x{m.message:X4}");
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => { Log.Error("unhandled exception", ev.ExceptionObject as Exception); EmergencyRestore(); };
        TaskScheduler.UnobservedTaskException += (_, ev) => { Log.Warn("unobserved task exception", ev.Exception); ev.SetObserved(); };
        // What Windows asks for: Restart Manager's "close this program" (another program's installer or update; the
        // session goes on) is not the user signing out (2026-10-16: the Claude app's update closed the shell for good).
        _endSession = new EndSessionWatch();
        SessionEnding += (_, _) => OnSessionEnding();
        try { Start(); }
        catch (Exception ex) { Log.Error("start failed", ex); Stop(1, clean: false); }
    }

    bool HideTaskbarWanted =>
        _settings.General.HideWindowsTaskbar && !_o.NoHideTaskbar && !_taskbarRestoredOnRequest && !_welcomePending && (AppPaths.IsInstalledBuild || _o.HideTaskbar);

    /// <summary>The first start's welcome is still to be answered: the taskbar is not hidden before it is agreed to.</summary>
    bool _welcomePending;
    /// <summary>The tray setting the shell started with (a change applies at the next start).</summary>
    bool _trayAtStart = true;
    Settings.WelcomeWindow? _welcome;

    /// <summary>
    /// While WPF makes its first window (about 220 ms of the UI thread, 2026-11-08), another core loads what the next
    /// steps load on the UI thread: Windows' theme settings and the network and audio stacks. Each makes its objects and
    /// drops them: nothing is handed over, so the steps after run as before, only on loaded code. Measured with the
    /// Debug build against the build before, in turns (2026-11-08): the theme and styles 105–111 → 88–97 ms, the menu
    /// bar made 194–200 → 180–185 ms. A device made and dropped on the GPU, and a JIT profile, were tried in the same
    /// change and taken out without being measured apart.
    /// </summary>
    static void WarmUpOffTheUiThread()
    {
        Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            ThemeService.WarmUp(); long theme = sw.ElapsedMilliseconds;
            Frostbar.Platform.Sys.NetworkMonitor.WarmUp(); long network = sw.ElapsedMilliseconds;
            AudioService.WarmUp(); long audio = sw.ElapsedMilliseconds;
            Log.Info($"start-up warm-up off the UI thread: theme {theme}, network {network - theme}, audio {audio - network} ms");
        });
    }

    void Start()
    {
        bool fresh = !File.Exists(AppPaths.SettingsFile);
        // The settings are read off the UI thread while WPF makes its first window here (the message window, which needs
        // none of them): on their own the read took 81–85 ms and the window 179–190 ms (2026-10-16, a Release build).
        var reading = Task.Run(() => SettingsStore.Load(AppPaths.SettingsFile));
        WarmUpOffTheUiThread();
        _messages = new MessageWindow();
        Mark("message window");
        var load = reading.GetAwaiter().GetResult();
        Mark("settings read");
        _settingsLoad = load;
        _settings = load.Settings;
        _saver = new SettingsSaver(AppPaths.SettingsFile, (what, ex) => Log.Warn("settings: " + what, ex),
            (ms, tries) => Log.Info($"settings: written in {ms} ms{(tries > 1 ? $" ({tries} tries)" : "")}, off the UI thread"));
        FileManager.Choice = _settings.General.FileManager;
        FileManager.Path = _settings.General.FileManagerPath;
        // A first start asks (and writes welcomed:false, so that a start that dies before the answer asks again); settings
        // from before the welcome existed (or written by a spec: no "welcomed") count as answered.
        _welcomePending = WelcomeRule.Pending(!fresh, _settings.General.Welcomed, _o.Welcome);
        if (_welcomePending) _settings.General.Welcomed ??= false;
        else if (_settings.General.Welcomed == null) _settings.General.Welcomed = true;
        Log.Level = Log.Parse(_settings.Advanced.LogLevel);
        Log.Info($"Frostbar {AppPaths.Version} starting: pid {Environment.ProcessId}, {(AppPaths.IsInstalledBuild ? "installed" : "development")} build" +
                 (_o.Profile != null ? ", profile " + _o.Profile : "") + (_o.Relaunched ? ", started again by the watchdog" : ""));
        Mark("log");
        foreach (var f in load.Fixes) Log.Warn("settings: " + f);
        if (load.CorruptCopy != null) Log.Warn($"settings.json was unreadable: kept as {load.CorruptCopy}, using defaults");
        Mark("settings");
        // Under an E2E spec (with --diag only): the menus and the shortcuts act on its test target alone (SpecGuard).
        _onlyAppsIn = _o.Diag ? SpecGuard.Folder(Environment.GetEnvironmentVariable("FROSTBAR_E2E_ONLY")) : null;
        if (_onlyAppsIn != null) Log.Info($"spec guard: the menus and the shortcuts act only on the apps in {_onlyAppsIn}");
        // And a shell photographed for the public names its connection by the spec's word, not by the network's name.
        if (_o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_NETWORK") is { Length: > 0 } network) Frostbar.Platform.Sys.NetworkMonitor.SpecName = network;
        // And the login items page reads the spec's own key and folders, none of the PC's (only places of its own are taken).
        if (_o.Diag && StartupStore.ForSpec(Environment.GetEnvironmentVariable("FROSTBAR_E2E_STARTUP_KEY"), Environment.GetEnvironmentVariable("FROSTBAR_E2E_STARTUP_DIR")) is { } startup)
        {
            StartupStore.Current = startup;
            Log.Info($"spec: the login items come from HKCU\\{startup.UserRun} and {startup.UserFolder}");
        }
        // And the Trackpad, Mouse and Keyboard pages keep a spec's values in memory, unless it asked for Windows' (yes).
        if (_o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_POINTER") == "spec")
        {
            Frostbar.Platform.Sys.PointerStore.Current = new Frostbar.Platform.Sys.SpecPointer();
            Log.Info("spec: the pointer's and the keys' settings are the spec's own (in memory)");
        }
        if (!File.Exists(AppPaths.SettingsFile)) _saver.Save(_settings);

        _store = new StateStore(AppPaths.StateFile);
        RecoverFromPreviousRun(_store);
        Mark("previous run");

        _heartbeat = new Heartbeat(Dispatcher);
        Mark("heartbeat");
        int pid = Environment.ProcessId;
        long start = Native.ProcessStartTicks(pid);
        _processBeganMs = start == 0 ? -1 : (DateTime.UtcNow.Ticks - start) / TimeSpan.TicksPerMillisecond - _since.ElapsedMilliseconds;
        _store.Update(s =>
        {
            s.Owner = new OwnerInfo { Pid = pid, StartTimeUtcTicks = start, HeartbeatHwnd = _messages.Handle.ToInt64(), Args = _o.RelaunchArgs };
            s.CleanExit = false;
            s.EndedBy = null;
        });
        Mark("state");
        Watchdog.Spawn(pid, start, _o.WatchdogArgs);
        Mark("watchdog");

        _theme = new ThemeService(Dispatcher, _messages);
        ApplyThemeChoices();
        Mark("theme");
        ShellStyles.Install(this, _theme.Current); // tooltips and a field's right-click menu in the shell's look (C1)
        // The settings' pop-up lists and "…" menus on the menus' glass (C3).
        GlassList.Material = t => Materials.Menu(t, _settings.Appearance, (float)(GlassShape.Menu * (_monitor?.Scale ?? 1)));
        Mark("styles");
        _ticker = new BackgroundTicker();
        _identity = new AppIdentity();
        _hotkeys = new HotkeyService();
        _power = new PowerActions(dryRun: !AppPaths.IsInstalledBuild && !_o.RealPower);
        _front = new ForegroundTracker(_identity, h => Native.ProcessId(h) == (uint)Environment.ProcessId, _ticker);
        // The apps' windows open between the menu bar and the Dock (2026-10-24; afterwards Windows keeps them below the menu
        // bar itself). A spec's shell moves its test target's windows only.
        var roomGuard = _onlyAppsIn;
        _room = new RoomWatch(h => roomGuard == null || SpecGuard.Allows(roomGuard, Native.ProcessPath(Native.AppOrWindowProcessId(h)), isDesktop: false));
        Mark("trackers");
        // The system menu's Recent Items (stage 5-2): Windows' recent documents, read off the UI thread. A spec's shell
        // reads a folder of its own (with --diag only), never the user's.
        string? recentFolder = _o.Diag ? Environment.GetEnvironmentVariable("FROSTBAR_E2E_RECENT") : null;
        _recentDocs = new RecentDocuments(Dispatcher, string.IsNullOrWhiteSpace(recentFolder) ? null : recentFolder) { Wanted = _settings.MenuBar.RecentCount };
        if (_recentDocs.Folder == recentFolder) Log.Info($"spec: the recent documents come from {recentFolder}");
        // A spec's shell counts its sound effects without playing them (with --diag only): a test never beeps over a call.
        if (_o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_SOUNDS") == "spec")
        {
            UiSounds.Silent = true;
            Log.Info("spec: the sound effects are counted, not played");
        }
        // A spec's shell has a Snap setting of its own (with --diag only): the Dock page's switch never changes Windows'.
        if (_o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_SNAP") == "spec")
        {
            WindowSnap.UseSpec(true);
            Log.Info("spec: the Snap setting is the spec's own");
        }
        // And its desktop icons' switch keeps a value of its own: the user's icons are never hidden by a spec.
        if (_o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_DESKTOP_ICONS") == "spec")
        {
            DesktopItems.UseSpec(true);
            Log.Info("spec: the desktop icons' setting is the spec's own");
        }
        // The privacy indicator (stage 5-2): Windows' records of the apps' use (the user's choice, 2026-10-16). A spec's
        // shell (with --diag only) has its own state set through the diagnostics pipe, or reads records under a key of
        // its own: never the user's.
        if (_o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_PRIVACY") == "spec")
        {
            _privacy = new SpecPrivacy();
            Log.Info("spec: what uses the microphone and the camera is the spec's own (privacy-set)");
        }
        else if (_o.Diag && ConsentStorePrivacy.SpecStore(Environment.GetEnvironmentVariable("FROSTBAR_E2E_PRIVACY_STORE")) is { } store)
        {
            _privacy = new ConsentStorePrivacy(Dispatcher, store);
            Log.Info($"spec: the microphone's and the camera's records come from HKCU\\{store}");
        }
        else _privacy = new ConsentStorePrivacy(Dispatcher);
        Mark("recent documents");
        // The search's Clipboard filter (stage 6, E3c): Windows' clipboard history (the user's choice "a"); a spec's shell
        // (with --diag only) has its own, set through the diagnostics pipe: the user's clipboard is neither read nor changed.
        Frostbar.Platform.Search.IClipboardHistory clipboard = _o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_CLIPBOARD") == "spec"
            ? _specClipboard = new Frostbar.Platform.Search.SpecClipboardHistory()
            : new Frostbar.Platform.Search.WindowsClipboardHistory();
        if (_specClipboard != null) Log.Info("spec: the clipboard history is the spec's own (clipboard-set)");
        var strings = Strings.For(_settings.General.Language, System.Globalization.CultureInfo.CurrentUICulture);
        _search = new SearchController(_settings, _theme, () => _bar?.Strings ?? strings, Dispatcher, Monitors.Primary().Scale)
        {
            OpenSettings = page => OpenSettings(page),
            // Stage 6-1: the recent documents, the Dock's windows, the actions, and a spec's guard. Its keys are
            // registered with the other shortcuts (RegisterShortcuts: the settings page records them).
            Recent = _recentDocs,
            Windows = () => _dock?.Windows.Windows ?? [],
            ActivateWindow = h => _dock?.BringWindowForward(h),
            RunAction = RunSearchAction,
            ActionsAvailable = ActionsAvailableNow,
            OnlyAppsIn = _onlyAppsIn,
            // A spec's shell knows one vault, a folder of its own (with --diag only): never the user's notes.
            SpecVault = _o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_VAULT") is { Length: > 0 } vault ? vault : null,
            ClipboardHistory = clipboard,
        };
        if (_search.SpecVault != null) Log.Info($"spec: the notes come from {_search.SpecVault}");
        Mark("search");
        _grid = new AppGridController(Monitors.Primary(), _settings, _saver, _theme, () => _bar?.Strings ?? strings,
            () => _bar != null ? Native.FrameBounds(_bar.Window.Handle) : default, () => _dock?.Raise(), Dispatcher, _hotkeys!)
        {
            Shelf = () => _dock, // apps carried out of the grid are kept in the Dock (made again at times: asked each time)
            OnlyAppsIn = _onlyAppsIn, // a spec's grid opens the test target's programs only
        };
        _grid.Menu.Opening += () => _bar?.Menus.Close();
        // Apps installed since the list was read show up while the grid is open.
        _grid.Opened += () => { if (Environment.TickCount64 - _catalogAt > 30_000) _ = LoadAppCatalog(); };
        Mark("app grid");
        _overview = new Overview.OverviewController(Monitors.Primary(), _settings, _theme, _identity, () => _bar?.Strings ?? strings,
            () => _bar != null ? Native.FrameBounds(_bar.Window.Handle) : default, () => _dock?.Raise(), Dispatcher);
        if (_onlyAppsIn is { } only) _overview.MayActOn = h => SpecGuard.Allows(only, Native.ProcessPath(Native.AppOrWindowProcessId(h)), isDesktop: false);
        Mark("overview");
        _ = LoadAppCatalog();
        Mark("app catalog asked");
        _hider = new TaskbarHider(_store, Dispatcher, _ticker);
        _appBars = new AppBarHost(_store);
        _fullScreen = new FullScreenDetector(h => Native.ProcessId(h) == (uint)Environment.ProcessId, _ticker!);
        _appBars.FullScreenApp += _fullScreen.OnExplorerNotification;
        // Auto-hide turned off in Settings or by Explorer: asked of Explorer then, not every second (TaskbarHider).
        _appBars.TaskbarStateChanged += () => _hider?.CheckNow();
        _messages.SettingChanged += area => { if (area == "TraySettings") _hider?.CheckNow(); };
        _appBars.Placed += b => { if (b == _dockBar) _dock?.StandOn(DockEdgePx()); };
        _fullScreen.Changed += OnFullScreen;
        _theme.Changed += ApplyTheme;
        // Windows' dark mode by itself and the light/dark wallpapers (stage 4-3). A spec's shell switches a dark mode of
        // its own and keeps a wallpaper of its own (with --diag only), so that no spec changes the user's.
        if (_o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_THEME") == "spec") _specDark = false;
        if (_o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_WALLPAPER") == "spec") WallpaperStore.Current = new SpecWallpaper();
        // The Displays page's displays (stage 4-4): a spec's shell has displays of its own unless it asks for Windows'.
        if (_o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_DISPLAY") == "spec") DisplayStore.Current = new SpecDisplays();
        // The Notification Center of a spec's photos: its own notifications, not Windows' (the user's; diagnostics "nc-notices").
        if (_o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_NOTICES") == "spec") MenuBarController.SpecNotices = new();
        // Or Windows' own, of the one app a spec posts as (nc-clear: "Clear All" through Windows, never the user's).
        if (_o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_NOTICES") is { } noticesOf && noticesOf.StartsWith("app:", StringComparison.Ordinal))
            MenuBarController.SpecNoticesApp = noticesOf[4..];
        // Liquid Glass's lens (ADR-016) reads the screen with the shell's glass left out of capture, so a spec's photos
        // would not show it: a spec's shell goes without it unless the spec asks (FROSTBAR_E2E_LENS=on; the specs set off).
        bool lensWanted = !(_o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_LENS") == "off");
        Frostbar.Platform.Dwm.LensCapture.Available = lensWanted && Frostbar.Platform.Dwm.LensCapture.Supported();
        // A spec's rim ("<bright line>,<dark line>", 0–1 each): candidates photographed side by side.
        if (_o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_LENS_RIM") is { } rimOf && rimOf.Split(',') is [var s, var c]
            && float.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float spec)
            && float.TryParse(c, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float contact))
            Frostbar.Platform.Dwm.LensCapture.RimOverride = (Math.Clamp(spec, 0, 1), Math.Clamp(contact, 0, 1));
        // A spec's bend at the rim ("<DIPs read farther in at the edge>,<within this many DIPs of it>"): candidates photographed.
        if (_o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_LENS_BEND") is { } bendOf && bendOf.Split(',') is [var bendBy, var bendWithin]
            && float.TryParse(bendBy, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rimBend)
            && float.TryParse(bendWithin, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float rimWidth))
            Frostbar.Platform.Dwm.LensCapture.BendOverride = (Math.Clamp(rimBend, 0, 80), Math.Clamp(rimWidth, 0, 60));
        // A spec's pace of the lens's frames while what is behind a plate changes (ms): what following faster costs, measured.
        if (_o.Diag && int.TryParse(Environment.GetEnvironmentVariable("FROSTBAR_E2E_LENS_PACE"), out int paceMs))
            Frostbar.Platform.Dwm.LensCapture.FastMs = Math.Clamp(paceMs, 1, 500);
        if (_o.Diag && int.TryParse(Environment.GetEnvironmentVariable("FROSTBAR_E2E_LENS_SMOOTH"), out int smoothMs))
            Frostbar.Platform.Dwm.LensCapture.SmoothMs = Math.Clamp(smoothMs, 1, 500);
        Log.Info($"lens: {(Frostbar.Platform.Dwm.LensCapture.Available ? "available" : lensWanted ? "not available (no screen capture with dirty regions)" : "off for this spec")}");
        // Liquid Glass's knobs (glass while held), read at each press.
        GlassKnob.Enabled = () => _theme != null && _settings.Appearance.LiquidGlass && Materials.GlassPossible(_theme.Current, _settings.Appearance);
        GlassKnob.Animate = () => _theme?.Current.Animations ?? true;
        _schedule = new AppearanceScheduler(Dispatcher, () => _settings,
            dark => { if (_specDark != null) _specDark = dark; else ThemeService.SetDark(dark); },
            () => _specDark ?? ThemeService.WindowsDark());
        _theme.Changed += _ => _schedule?.Pair();
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => _schedule?.Check());
        _messages.ExplorerRestarted += OnExplorerRestarted;
        _messages.DisplayChanged += OnDisplayChanged;
        _messages.PowerResumed += () => OnResumed("power broadcast");
        _heartbeat.Gap += ms => OnResumed($"the heartbeat came {ms / 1000.0:0.0} s late");
        _messages.WorkAreaChanged += () =>
        {
            if (_stopping || _dock == null) return;
            // On a side the row is centred on the work area's height, which moves with the menu bar's strip or a taskbar.
            if (_dock.Window.Edge != DockEdge.Bottom) { _dock.StandOn(DockEdgePx()); return; }
            // Auto-hide: the Dock stands on the work area's bottom, which moves when our strip goes or Explorer's taskbar hides.
            if (_dockBar != null) return;
            int bottom = DockEdgePx();
            if (bottom != _dock.Window.EdgePx) _dock.StandOn(bottom);
        };

        Mark("bars and taskbar services");
        _commands = new CommandServer(AppPaths.PipeName, line => Dispatcher.InvokeAsync(() => HandleCommand(line)).Task);
        if (_o.Diag) _diag = new CommandServer(AppPaths.DiagPipeName, line => line == "settings-written" ? SettingsWritten() : Dispatcher.InvokeAsync(() => HandleDiag(line)).Task);
        Mark("pipes");

        WhenExplorerReady(() =>
        {
            Mark("Explorer ready");
            _explorerTaskbar = Taskbar.Primary();
            _trayAtStart = _settings.MenuBar.ShowTrayIcons;
            bool tray = _settings.MenuBar.ShowTrayIcons && !_o.NoTray;
            if (tray) _hider.Protect(); // the tray takes WS_EX_TOPMOST off the taskbar: save it first (ADR-001)
            Mark("taskbar saved");
            CreateShellWindows();
            CreateOverlays();
            Mark("overlays");
            if (HideTaskbarWanted) _hider.Hide(_settings.Advanced.TaskbarHideStrategy);
            else Log.Info(_settings.General.HideWindowsTaskbar
                ? "taskbar left visible (development build: start with --hide-taskbar to hide it)"
                : "taskbar left visible (setting off)");
            Mark("taskbar");
            _readyMs = _since.ElapsedMilliseconds;
            Log.Info($"ready in {_readyMs} ms");
            Log.Info("start-up steps (ms): " + StartSteps());
            if (_o.Command == "show-settings") OpenSettings(); // started again from the settings (a language change)
            if (_welcomePending) OpenWelcome();
            else ReportSettingsProblem();
            if (tray) StartTrayWhenSettled();
            // Each piece made ahead in its own idle moment, so input and the front app's changes get through in between
            // (all at once, they held the UI thread long enough for the menu bar to show the old app for 0.7 s: 2026-10-12),
            // and not while something moves (a warm-up's 100 ms held a Dock launch's landing: 2026-10-18).
            var warm = new DispatcherTimer(TimeSpan.FromSeconds(1.5), DispatcherPriority.ApplicationIdle, (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                foreach (var step in new Action[] { () => _bar?.Menus.Warm(), () => _search?.Warm(), () => _grid?.Warm() })
                    Still.Then(Dispatcher, step);
                // An older version's Run value moved to the sign-in task, and the switch's state read: off the UI thread,
                // and done before the index makes the Login Items page at 5 s (which would read it here otherwise).
                if (AppPaths.IsInstalledBuild)
                {
                    string text = LoginTaskText;
                    LoginWork(() => LoginItemStore.MoveFromRun(AppPaths.ExePath, text));
                }
            }, Dispatcher);
            warm.Start();
            var warmLater = new DispatcherTimer(TimeSpan.FromSeconds(5), DispatcherPriority.ApplicationIdle, (s, _) =>
            {
                ((DispatcherTimer)s!).Stop();
                Still.Then(Dispatcher, () => _overview?.Warm());
                Still.Then(Dispatcher, IndexShellPages);
            }, Dispatcher);
            warmLater.Start();
        });
    }

    /// <summary>
    /// Explorer switches its taskbar to auto-hide asynchronously (about a second); ManagedShell starting in the middle of
    /// that left the taskbar's space reserved (2026-10-12). The tray starts once the work area shows the space released
    /// (or after 3 s).
    /// </summary>
    void StartTrayWhenSettled()
    {
        if (_hider is not { IsHidden: true }) { _ = StartTray(); return; }
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(100) };
        timer.Tick += (_, _) =>
        {
            bool released = TaskbarSpaceReleased();
            if (!released && sw.Elapsed < TimeSpan.FromSeconds(3) && !_stopping) return;
            timer.Stop();
            if (_stopping) return;
            Log.Info(released ? $"taskbar space released after {sw.ElapsedMilliseconds} ms: starting the tray" : "taskbar space still reserved after 3 s: starting the tray anyway");
            _ = StartTray();
        };
        timer.Start();
    }

    /// <summary>The work area is the monitor minus our own bars only (Explorer's taskbar no longer reserves space, on any edge).</summary>
    bool TaskbarSpaceReleased()
    {
        var mon = Monitors.Primary();
        static ScreenRect S(RECT r) => new(r.Left, r.Top, r.Right, r.Bottom);
        var bars = (_appBars?.Bars ?? []).Select(b => new BarPlace(b.Edge, S(b.Rect))).ToList();
        return TaskbarSettle.Released(S(mon.Bounds), S(Native.WorkArea()), bars, (Taskbar.GetState() & Native.ABS_AUTOHIDE) != 0);
    }

    async Task StartTray()
    {
        var sw = Stopwatch.StartNew();
        _tray = new TrayHost(Dispatcher, _ticker!);
        bool ok = await _tray.StartAsync();
        if (_stopping) return;
        if (!ok) { Log.Warn("tray icons unavailable: Windows keeps them (Win+B)"); _tray.Dispose(); _tray = null; return; }
        Log.Info($"tray started in {sw.ElapsedMilliseconds} ms");
        _tray.BalloonShown += OnBalloon;
        _bar?.AttachTray(_tray);
        // The apps' taskbar progress and badges now come to the tray's window: the Dock shows them (macOS).
        _taskband = TaskbandListener.Start(Dispatcher);
        if (_dock != null) _dock.Taskband = _taskband;
    }

    TaskbandListener? _taskband;

    /// <summary>The tray apps' balloons for the notification centre (kept here: the menu bar is made again at times).</summary>
    readonly Frostbar.Core.Shell.BalloonList _balloons = new();

    /// <summary>An app's balloon (UI thread): into the notification centre, named after the app's executable, and a banner.</summary>
    void OnBalloon(TrayBalloon b)
    {
        string app = b.ExePath.Length > 0 ? _identity!.DisplayName(b.ExePath, b.ExePath) : b.Tooltip;
        var notice = _balloons.Add(b.Number, app, b.ExePath, b.Received, b.Title, b.Text);
        if (_bar?.NotificationCenter is { } open) _ = open.ReadAsync(); // open now: shown at once
        if (_settings.MenuBar.TrayBanners) _overlays?.Show(notice, BannersQuiet());
    }

    // ---- what comes over everything under the bar (stage 2): banners, the volume and brightness display ----------

    Overlays.OverlayHost? _overlays;
    VolumeDisplay? _volumeDisplay;

    void CreateOverlays()
    {
        if (_overlays != null) return;
        var strings = Strings.For(_settings.General.Language, System.Globalization.CultureInfo.CurrentUICulture);
        _overlays = new Overlays.OverlayHost(Dispatcher, () => _theme!.Current, () => _settings.Appearance, () => _bar?.Strings ?? strings,
            appId => _bar?.NoticeIcon(appId), OverlayFrame,
            key => _bar is { } b && b.Window.IsVisible && b.Window.StatusRect(key) is { IsEmpty: false } item ? item : null);
        _overlays.BannerClicked += n => { if (BalloonList.IsBalloon(n.Id)) _tray?.ClickBalloon(BalloonList.TrayNumberOf(n.Id)); };
        _overlays.HudItemLit += key => _bar?.Window.SetStatusLit(key);
        try
        {
            _volumeDisplay = new VolumeDisplay(_store!, Dispatcher, hide: _settings.Hud.Enabled);
            _volumeDisplay.VolumeKey += OnVolumeKey;
            _volumeDisplay.Shown += OnWindowsDisplay;
        }
        catch (Exception ex) { Log.Warn("volume display unavailable (Windows' own stays)", ex); }
    }

    /// <summary>DIPs: under the menu bar (the primary monitor's top while the bar is hidden), and the monitor's left and right.</summary>
    (double top, double left, double right) OverlayFrame()
    {
        var mon = Monitors.Primary();
        double s = mon.Scale;
        double top = _bar is { } b && b.Window.IsVisible ? b.Window.BottomDip : mon.Bounds.Top / s;
        return (top, mon.Bounds.Left / s, mon.Bounds.Right / s);
    }

    /// <summary>No banners now: do not disturb or a focus session, a full-screen app, or the notification centre open (it lists them).</summary>
    bool BannersQuiet() =>
        _bar?.NotificationCenter != null
        || (_fullScreen?.IsFullScreen(Monitors.Primary().Handle) ?? false)
        || DoNotDisturb.IsOn() == true || FocusSessions.Active;

    void OnVolumeKey(int command)
    {
        _volumeKeyAt = Environment.TickCount64; // the change it makes comes next (OnVolumeChanged): its feedback then
        if (_stopping || !_settings.Hud.Enabled || _bar is not { } bar || !bar.Audio.Available) return;
        _overlays?.ShowHud(HudKind.Volume, bar.Audio.Volume, bar.Audio.Muted); // the key's change follows (OnVolumeChanged)
    }

    // When a volume key was last pressed, and the feedback last played (TickCount64).
    long _volumeKeyAt = long.MinValue / 2, _feedbackAt = long.MinValue / 2;

    /// <summary>
    /// macOS's "Play feedback when volume is changed" (2026-10-22): a key's change, or the Control Center's slider let go,
    /// plays the default sound at the new volume (not when muted). At most one in 80 ms: a held key repeats.
    /// </summary>
    void VolumeFeedback(bool fromKey)
    {
        long now = Environment.TickCount64;
        if (_bar is not { } bar || !Frostbar.Core.Shell.VolumeFeedback.Plays(_settings.Sound.VolumeFeedback, bar.Audio.Muted, fromKey, now - _volumeKeyAt, now - _feedbackAt)) return;
        _feedbackAt = now;
        UiSounds.Play(UiSounds.Feedback);
    }

    /// <summary>Windows' display came out (kept from being drawn): ours for the same key.</summary>
    void OnWindowsDisplay(OsdCause cause)
    {
        if (_stopping || !_settings.Hud.Enabled || _bar is not { } bar) return;
        if (cause == OsdCause.Volume && bar.Audio.Available) _overlays?.ShowHud(HudKind.Volume, bar.Audio.Volume, bar.Audio.Muted);
        else if (cause == OsdCause.Brightness && bar.Brightness is { Available: true } br) _overlays?.ShowHud(HudKind.Brightness, br.Level / 100.0, false);
        else Log.Debug($"Windows' volume display came out ({cause}): nothing of ours for it");
    }

    void OnVolumeChanged()
    {
        if (_bar is { } bar) _overlays?.UpdateHud(HudKind.Volume, bar.Audio.Volume, bar.Audio.Muted);
        VolumeFeedback(fromKey: true);
    }

    void OnBrightnessChanged()
    {
        _volumeDisplay?.NoteBrightness(); // Windows' display out a moment ago was the brightness's
        if (_bar?.Brightness is { Available: true } br) HudForBrightness(br.Level);
    }

    /// <summary>
    /// The brightness moved (never the Control Center's slider: its changes do not come here). With Windows' display out it
    /// is the keys': ours comes or stays. No key is heard for the brightness as for the volume, and that display comes out
    /// only once in a run of keys (OsdJudge.BrightnessIsKeys). Otherwise only what shows follows it.
    /// </summary>
    void HudForBrightness(int level)
    {
        bool displayOut = _specDisplayOut ?? _volumeDisplay?.Out == true;
        if (!_stopping && _settings.Hud.Enabled && OsdJudge.BrightnessIsKeys(displayOut, Environment.TickCount64, _volumeKeyAt))
            _overlays?.ShowHud(HudKind.Brightness, level / 100.0, false);
        else _overlays?.UpdateHud(HudKind.Brightness, level / 100.0, false);
    }

    // Diagnostics "hud-brightness": Windows' display as out or not, for a spec (no key can be sent for the brightness).
    bool? _specDisplayOut;

    /// <summary>Start menu names for apps (a few hundred ms on its own STA thread), then refresh the bar's app name.</summary>
    async Task LoadAppCatalog()
    {
        if (_catalogLoading) return;
        _catalogLoading = true;
        try
        {
            var sw = Stopwatch.StartNew();
            var apps = await AppsFolder.EnumerateAsync();
            _catalogAt = Environment.TickCount64;
            if (_stopping) return;
            // Read again when the app grid opens: nothing to do when nothing changed. (The Dock reads its pins and the
            // windows again on the UI thread, right while the grid is in use; its pins took about 0.1 s at start-up.)
            if (_catalog != null && apps.SequenceEqual(_catalog)) { Log.Debug($"app catalog unchanged ({sw.ElapsedMilliseconds} ms)"); return; }
            _catalog = apps;
            _identity!.LoadCatalog(apps.Select(a => (a.Name, a.ParsingName)));
            _search?.SetApps(apps.Select(a => (a.Name, a.ParsingName)));
            _grid?.SetApps(apps.Select(a => (a.Name, a.ParsingName)));
            Log.Info($"app catalog: {apps.Count} entries in {sw.ElapsedMilliseconds} ms");
            _front?.Refresh();
            _bar?.UpdateTitles();
            _dock?.CatalogLoaded();
        }
        catch (Exception ex) { Log.Warn("app catalog failed", ex); }
        finally { _catalogLoading = false; }
    }

    static void RecoverFromPreviousRun(StateStore store)
    {
        var st = store.Load();
        bool OwnerAlive(OwnerInfo o) => o.Pid != Environment.ProcessId && Native.ProcessAlive(o.Pid, o.StartTimeUtcTicks);
        if (st.Owner != null && !st.CleanExit && !OwnerAlive(st.Owner)) Log.Warn("the previous run did not exit cleanly");
        if (TaskbarRecoveryPlanner.IsStale(st, OwnerAlive))
            Log.Warn("the previous run left the taskbar hidden: " + Watchdog.RestoreTaskbar(store));
        if (st.AppBars.Count > 0 && (st.Owner == null || !OwnerAlive(st.Owner)))
        {
            AppBarHost.RemoveStale(st.AppBars);
            store.Update(s => s.AppBars.Clear());
            Log.Info($"removed {st.AppBars.Count} app bar registration(s) left by the previous run");
        }
        if (st.VolumeDisplay is { Hidden: true } && (st.Owner == null || !OwnerAlive(st.Owner)))
            Log.Warn("the previous run left Windows' volume display hidden: " + VolumeDisplay.GiveBack(store));
    }

    /// <summary>At sign-in Explorer may not be up yet; our bars and the hiding need its taskbar.</summary>
    void WhenExplorerReady(Action next)
    {
        if (Taskbar.ExplorerReady()) { next(); return; }
        Log.Info("waiting for Explorer");
        var sw = Stopwatch.StartNew();
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(500) };
        bool done = false;
        void Go()
        {
            if (done) return;
            done = true;
            timer.Stop();
            _messages!.ExplorerRestarted -= Go;
            Log.Info($"Explorer ready after {sw.ElapsedMilliseconds} ms");
            next();
        }
        timer.Tick += (_, _) => { if (Taskbar.ExplorerReady() || sw.Elapsed > TimeSpan.FromSeconds(60)) Go(); };
        _messages!.ExplorerRestarted += Go;
        timer.Start();
    }

    void CreateShellWindows()
    {
        _monitor = Monitors.Primary();
        _displays = Displays();
        _front!.Refresh();
        _bar = new MenuBarController(_monitor, _settings, _theme!, _messages!, _hotkeys!, _identity!, _front, _power!, _ticker!, Dispatcher, _recentDocs, _privacy) { Balloons = _balloons };
        _bar.Runner.OnlyAppsIn = _onlyAppsIn;
        // A spec's bar does not read the user's wallpaper (C2): the spec gives it a brightness (bar-tone).
        if (_o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_BAR_TONE") == "spec") _bar.Picture.ForSpec(_ticker!);
        _bar.RecentChanged += cleared =>
        {
            if (cleared) { ((Settings.ISettingsHost)this).Apply(Settings.SettingsArea.Other); return; } // saved half a second later
            _recentSave ??= new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => SaveSettings(), Dispatcher);
            if (!_recentSave.IsEnabled) _recentSave.Start();
        };
        if (_tray is { Running: true }) _bar.AttachTray(_tray);
        _bar.VolumeChanged += OnVolumeChanged;
        _bar.VolumeSliderLetGo += () => VolumeFeedback(fromKey: false);
        _bar.BrightnessChanged += OnBrightnessChanged;
        _bar.NotificationCenterOpening += () => _overlays?.ClearBanners();
        _bar.Menus.Opening += () => { _dock?.Menu.Close(); _grid?.Menu.Close(); };
        _bar.AppearanceChanged = () => ((Settings.ISettingsHost)this).Apply(Settings.SettingsArea.Appearance);
        _bar.OpenSearch = () => { ClosePopups(); _search?.Toggle(); };
        _bar.Runner.OpenShellSettings = () => OpenSettings();
        _bar.Runner.QuitShell = () => Dispatcher.BeginInvoke(() => Stop(0, clean: true)); // as "--quit": the watchdog does not start it again
        _bar.OpenSettings = page => OpenSettings(page);
        _bar.Runner.Animations = () => _theme?.Current.Animations ?? true;
        _bar.Runner.TileMargins = () => _settings.Window.TileMargins;
        // The status items the user shows and their order (stage 5-1); a drag on the bar (Ctrl held) saves its new order.
        _bar.Window.ArrangeItems(_settings.MenuBar.Items);
        _bar.Window.ItemsReordered += order =>
        {
            _settings.MenuBar.Items = [.. order];
            ((Settings.ISettingsHost)this).Apply(Settings.SettingsArea.MenuBarItems);
        };
        _peek = new MenuBar.MenuBarPeek(_bar, _ticker!, Dispatcher, () => _theme?.Current.Animations ?? true);
        _peek.ShownChanged += () => _overlays?.Relayout(); // the banners and the volume display stand under the bar, or at the top
        Mark("menu bar made");
        _bar.Window.Show();
        Mark("menu bar shown");
        // Hiding on the desktop (always, or there only): no strip at the top (the windows get the whole height), the bar
        // waits at the top edge.
        PlaceBar(_monitor);
        Mark("menu bar placed");

        if (!_settings.Dock.AutoHide) ReserveDockStrip();
        Mark("dock strip");
        _dock = NewDock();
        Mark("dock");
        var edges = new Core.HotCorners.ScreenEdges(_monitor.Bounds.Left, _monitor.Bounds.Top, _monitor.Bounds.Right, _monitor.Bounds.Bottom);
        if (_corners == null) _corners = new HotCorners.HotCornerWatcher(_settings, _ticker!, Dispatcher, edges, RunCorner);
        else _corners.SetEdges(edges);
        RegisterShortcuts();
        Mark("corners and keys");
        // A full-screen app already in front (the bars made again while a game runs: a display change, the bar's
        // height; or one seen before the bar was made, at sign-in): the new bar and Dock take it. The new Dock stood
        // over the game until it left (2026-11-06 review).
        SyncFullScreen();
        // The Dynamic Island (2026-11-08, ADR-017): made at the first idle moment (the start-up's frames do not wait for it).
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
        {
            if (_bar == null || _stopping || _island != null) return;
            bool spec = _o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_ISLAND") == "spec";
            _island = new Overlays.IslandHost(Dispatcher, () => _bar?.Strings ?? Strings.For(_settings.General.Language, System.Globalization.CultureInfo.CurrentUICulture), spec);
            _island.Attach(_bar, _dock, _monitor!, _theme?.Current.Animations ?? true);
        });
    }

    Overlays.IslandHost? _island;

    /// <summary>The bar, the Dock and the corners follow whether a full-screen app is in front of the primary monitor now.</summary>
    void SyncFullScreen()
    {
        _fullScreen?.DisplaysChanged();
        bool fs = _fullScreen?.IsFullScreen(_monitor.Handle) ?? false;
        if (fs || _barOverFullScreen) OnFullScreen(_monitor.Handle, fs);
    }

    /// <summary>The start-up steps as a line: each step's own time, then the process's time before the shell began.</summary>
    string StartSteps()
    {
        var parts = new List<string>();
        long previous = 0;
        foreach (var (step, ms) in _startSteps)
        {
            parts.Add($"{step} {ms - previous}");
            previous = ms;
        }
        parts.Add($"ready {_readyMs - previous}");
        return string.Join(", ", parts) + (_processBeganMs >= 0 ? $"; the process ran {_processBeganMs} ms before the shell began" : "");
    }

    /// <summary>
    /// Closes the menu bar's and the Dock's open menus and panels, before another surface opens: their keys (arrows,
    /// Enter, Space, Esc) are registered while they are open, and Space typed into the search ran the menu item last
    /// pointed at (2026-10-12).
    /// </summary>
    void ClosePopups()
    {
        _bar?.Menus.Close();
        _dock?.Menu.Close();
        _grid?.Menu.Close();
    }

    /// <summary>A hot corner's action (M7), or a shortcut's (with the window operations of stage 3-2).</summary>
    void RunCorner(string action)
    {
        if (_stopping) return;
        ClosePopups();
        if (HotCornerActions.WindowOp(action) is { } op) { _bar?.Runner.RunWindowOp(op); return; }
        switch (action)
        {
            case "overview": _overview?.Toggle(); break;
            case "appWindows": if (_front?.Current is { } app) _overview?.Toggle(app.AppId); else _overview?.Toggle(); break; // macOS's App Exposé
            case "appGrid": _grid?.Toggle(); break;
            case "search": _search?.Toggle(); break;
            case "desktop": ToggleDesktop(); break;
            case "notifications": _bar?.ToggleNotificationCenter(); break;
            case "lock": Log.Info("hot corner: lock → " + _power?.Lock()); break;
        }
    }

    /// <summary>
    /// The search's actions (stage 6-1), each as its menu item, Dock item, hot corner or Control Center switch does it
    /// (restart, shut down, log out and emptying the bin ask first). A spec's shell does only what touches nothing of
    /// the user's: power actions while they only log (a development build), the bin when it is the spec's own folder,
    /// and the shell's own panels; not the desktop (it minimizes the user's windows), dark mode or do not disturb.
    /// </summary>
    void RunSearchAction(string id)
    {
        if (_stopping) return;
        string? refused = _onlyAppsIn == null ? null : id switch
        {
            SearchActions.Lock or SearchActions.Sleep or SearchActions.Restart or SearchActions.Shutdown or SearchActions.Logout
                when _bar?.Runner.PowerIsDryRun != true => "power actions that would run",
            SearchActions.EmptyTrash when _o.TrashFolder == null => "the user's recycle bin",
            SearchActions.Desktop => "the user's windows",
            SearchActions.DarkOn or SearchActions.DarkOff or SearchActions.DndOn or SearchActions.DndOff => "a setting of Windows",
            _ => null,
        };
        if (refused != null)
        {
            LastSearchAction = $"{id}: guarded ({refused})";
            Log.Warn($"search action {LastSearchAction}: nothing done");
            return;
        }
        LastSearchAction = id;
        Log.Info($"search action: {id}");
        ClosePopups();
        switch (id)
        {
            case SearchActions.Lock: _bar?.Runner.RunSystem(SystemOps.Lock); break;
            case SearchActions.Sleep: _bar?.Runner.RunSystem(SystemOps.Sleep); break;
            case SearchActions.Restart: _bar?.Runner.RunSystem(SystemOps.Restart); break;
            case SearchActions.Shutdown: _bar?.Runner.RunSystem(SystemOps.Shutdown); break;
            case SearchActions.Logout: _bar?.Runner.RunSystem(SystemOps.Logout); break;
            case SearchActions.ForceQuit: _bar?.Runner.RunSystem(SystemOps.ForceQuit); break;
            case SearchActions.AboutPc: _bar?.Runner.RunSystem(SystemOps.AboutPc); break;
            case SearchActions.EmptyTrash: _dock?.AskEmptyTrash(); break;
            case SearchActions.Desktop: ToggleDesktop(); break;
            case SearchActions.Overview: _overview?.Toggle(); break;
            case SearchActions.AppGrid: _grid?.Toggle(); break;
            case SearchActions.Notifications: _bar?.ToggleNotificationCenter(); break;
            case SearchActions.ControlCenter: _bar?.ToggleControlCenter(); break;
            case SearchActions.DarkOn or SearchActions.DarkOff: _bar?.SetDarkMode(id == SearchActions.DarkOn); break;
            case SearchActions.DndOn or SearchActions.DndOff: _bar?.SetDoNotDisturb(id == SearchActions.DndOn); break;
        }
    }

    /// <summary>
    /// Frostbar's settings pages read for the search (stage 6-1), once after start-up, a page an idle moment: each page is
    /// made in a settings window that is never shown, as the window's own search reads them.
    /// </summary>
    void IndexShellPages()
    {
        if (_stopping || _theme == null || _bar == null || _search == null) return;
        var sw = Stopwatch.StartNew();
        var s = _bar.Strings;
        var other = Strings.For(s.Language == "ja" ? "en" : "ja", System.Globalization.CultureInfo.InvariantCulture);
        Settings.SettingsWindow.SearchIndex(this, _theme.Current, s, other, index =>
        {
            if (index == null || _stopping || _search == null) return;
            _search.SetShellPages(index.Pages);
            _search.ShellPagesMs = index.BusyMs;
            Log.Info($"search: {index.Pages.Count} pages of Frostbar's settings read in {sw.ElapsedMilliseconds} ms " +
                     $"(the UI thread {index.BusyMs} ms in all, at most {index.LongestMs} ms at a time)");
        });
    }

    /// <summary>Diagnostics: the last action the search asked for, and whether a spec's guard stopped it.</summary>
    public string LastSearchAction { get; private set; } = "";

    /// <summary>
    /// Which actions can be done now, read once as the search opens: a switch's other way only, emptying the bin only
    /// when something is in it. Do not disturb is read only when an answer needs it (a COM call).
    /// </summary>
    Func<string, bool> ActionsAvailableNow()
    {
        bool dark = _theme?.Current.Dark ?? false;
        bool full = _dock?.TrashFull == true;
        var dnd = new Lazy<bool?>(DoNotDisturb.IsOn);
        return id => id switch
        {
            SearchActions.DarkOn => !dark,
            SearchActions.DarkOff => dark,
            SearchActions.DndOn => dnd.Value == false,
            SearchActions.DndOff => dnd.Value == true,
            SearchActions.EmptyTrash => full,
            _ => true,
        };
    }

    /// <summary>Shows the desktop, or the windows again (the taskbar's corner button).</summary>
    static void ToggleDesktop()
    {
        try
        {
            var type = Type.GetTypeFromProgID("Shell.Application");
            if (type == null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            try { shell.ToggleDesktop(); }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(shell); }
        }
        catch (Exception ex) { Log.Warn("show desktop failed", ex); }
    }

    /// <summary>
    /// The strip the Dock stands on, at the bottom or on its side (an app bar of a window never shown: M0 S3). Not with
    /// auto-hide.
    /// </summary>
    void ReserveDockStrip()
    {
        if (_dockBar != null || _appBars == null) return;
        uint edge = DockEdgeWanted switch { DockEdge.Left => Native.ABE_LEFT, DockEdge.Right => Native.ABE_RIGHT, _ => Native.ABE_BOTTOM };
        _dockReservation = new Window
        {
            Title = "Frostbar.DockReservation", WindowStyle = WindowStyle.None, ShowInTaskbar = false, ShowActivated = false,
            Width = 1, Height = 1, Left = -32000, Top = -32000,
        };
        var h = new WindowInteropHelper(_dockReservation).EnsureHandle();
        Native.AddExStyle(h, Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        _dockBar = _appBars.Register(h, edge, DockReservationDip, _monitor.Handle, positionWindow: true);
    }

    void ReleaseDockStrip()
    {
        if (_dockBar != null) _appBars?.Remove(_dockBar);
        _dockBar = null;
        _dockReservation?.Close();
        _dockReservation = null;
    }

    /// <summary>
    /// What the Dock stands on (pixels): its strip's outer side, or with auto-hide the work area's edge (the screen's,
    /// or Explorer's taskbar's): a y at the bottom, an x on a side.
    /// </summary>
    int DockEdgePx()
    {
        var work = (Monitors.Get(_monitor.Handle) ?? _monitor).Work;
        return DockEdgeWanted switch
        {
            DockEdge.Left => _dockBar?.Rect.Left ?? work.Left,
            DockEdge.Right => _dockBar?.Rect.Right ?? work.Right,
            _ => _dockBar?.Rect.Bottom ?? work.Bottom,
        };
    }

    DockController NewDock(bool comeIn = false)
    {
        // A spec's shell shows no pictures of the user's minimized windows (its pictures would carry them: stage 7-2),
        // from its first rows on: they are built in the constructor.
        var guarded = _onlyAppsIn;
        Func<RunningWindow, bool>? mayShowWindow = guarded == null ? null : w => SpecGuard.Allows(guarded, w.ExePath, isDesktop: false);
        var dock = new DockController(_monitor, _settings, _saver, _theme!, _identity!, _front!, _hotkeys!,
            () => _bar != null ? Native.FrameBounds(_bar.Window.Handle) : default, () => _bar!.Strings, Dispatcher,
            _o.Diag ? _o.TrashFolder : null, mayShowWindow,
            // And one photographed for the public lists the test target's windows alone: the user's open apps stay out.
            guarded != null && Environment.GetEnvironmentVariable("FROSTBAR_E2E_DOCK") == "spec" ? mayShowWindow : null);
        dock.OpenAppGrid = pressTime => { ClosePopups(); _grid?.Toggle(pressTime); };
        dock.ShowAllWindows = appId => { ClosePopups(); _overview?.Toggle(appId); };
        // Apps opened from the app grid, the search, a stack or the recent items bounce in the Dock (or come forward when
        // they run): 2026-10-23, the user's "A どこから開いても跳ねる" and "macOS と同じく前面に出す".
        Frostbar.App.Menus.CommandRunner.Opening ??= (target, source) => _dock?.Launching(target, source) ?? false;
        // A spec's shell starts, fronts, minimizes and quits the test target's apps only (its pins, a running one's windows).
        // An AppsFolder pin (an app carried from the app grid: a Start menu shortcut) by the program its link points to.
        if (_onlyAppsIn is { } only)
        {
            dock.MayActOn = item => item.Windows.Count > 0
                ? item.Windows.All(w => SpecGuard.Allows(only, w.ExePath, isDesktop: false))
                : SpecGuard.Allows(only, item.LaunchTarget is { } target && target.StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase)
                    ? ShellApi.LinkTarget(target) : item.LaunchTarget, isDesktop: false);
            // Its stacks: the spec's own folders only; what a stack opens: the test target (a shortcut by where it points).
            string own = Path.Combine(Path.GetTempPath(), "frostbar-e2e") + Path.DirectorySeparatorChar;
            dock.MayStack = folder => folder.StartsWith(own, StringComparison.OrdinalIgnoreCase);
            // A link file by IShellLink (as CommandRunner reads one): ShellApi.LinkTarget reads a Start menu entry's, and
            // a .lnk in a stack read as nothing (the spec's own shortcut to the test target was refused, dock-folder).
            dock.MayOpenFile = path => !Directory.Exists(path) && SpecGuard.Allows(only,
                string.Equals(Path.GetExtension(path), ".lnk", StringComparison.OrdinalIgnoreCase) ? Frostbar.Platform.Apps.OpenWith.Resolve(path)?.path : path, isDesktop: false);
        }
        _overview?.Follow(dock.Windows); // windows that come or go while the overview is open (stage 3-3)
        dock.Menu.Opening += () => _bar?.Menus.Close();
        dock.StandOn(DockEdgePx());
        dock.SetAutoHide(_settings.Dock.AutoHide, _ticker);
        if (comeIn) dock.WaitOutOfSight();
        dock.Window.Show();
        if (comeIn) dock.ComeIn();
        if (_taskband != null) dock.Taskband = _taskband;
        return dock;
    }

    /// <summary>The menu bar's hiding changed in the settings (stage 5-1; macOS's four choices since 2026-11-01).</summary>
    void SetBarAutoHide()
    {
        PlaceBar(_monitor);
        _overlays?.Relayout();
    }

    /// <summary>
    /// The menu bar's place with its hiding (macOS's "Automatically hide and show the menu bar", Core's MenuBarItems) and
    /// a full-screen app in front or not: the strip at the top kept or let go (then the bar stands at the top of
    /// <paramref name="mon"/>, no app bar places it), and the bar waiting at the top edge or standing, over a full-screen
    /// app too when it hides only on the desktop or never. Not <paramref name="again"/> (a full-screen app gone), a bar
    /// already waiting goes on waiting where it is.
    /// </summary>
    void PlaceBar(MonitorInfo mon, bool again = true)
    {
        if (_bar == null || _appBars == null || _peek == null) return;
        string mode = _settings.MenuBar.AutoHide;
        bool strip = Core.MenuBar.MenuBarItems.TakesStrip(mode);
        if (strip) _topBar ??= _appBars.Register(_bar.Window.Handle, Native.ABE_TOP, _settings.MenuBar.Height, mon.Handle, positionWindow: true);
        else
        {
            if (_topBar != null) { _appBars.Remove(_topBar); _topBar = null; }
            if (again)
            {
                var w = _bar.Window;
                w.Left = mon.Bounds.Left / mon.Scale;
                w.Top = mon.Bounds.Top / mon.Scale;
                w.Width = mon.Bounds.Width / mon.Scale;
            }
        }
        _bar.OverApps = _barOverFullScreen || !strip;
        if (Core.MenuBar.MenuBarItems.Hides(mode, _barOverFullScreen)) { if (again || !_peek.Waiting) _peek.Start(mon); }
        else if (_barOverFullScreen) { _peek.StandOver(); _island?.Raise(); } // StandOver raises the bar over the island
        else _peek.Stop();
    }

    void CloseShellWindows()
    {
        _island?.Dispose(); _island = null; // made again with the new bar
        _peek?.Stop();
        _peek = null;
        _appBars?.RemoveAll();
        _topBar = null;
        _dockBar = null;
        _bar?.Dispose(); _bar = null;
        _dockReservation?.Close(); _dockReservation = null;
        _dock?.Dispose(); _dock = null;
    }

    void ApplyTheme(ThemeSnapshot t)
    {
        ShellStyles.Paint(this, t);
        _bar?.ApplyTheme(t);
        _dock?.ApplyTheme(t);
        if (_island != null) _island.Animations = t.Animations;
        _search?.ApplyTheme(t);
        _grid?.ApplyTheme(t);
        _overview?.ApplyTheme(t);
        _settingsWindow?.Restyle(t);
    }

    // ---- settings window ---------------------------------------------------------------------------------------

    /// <summary>Opens the settings window, or brings it to the front (at <paramref name="page"/> when given).</summary>
    public void OpenSettings(string? page = null)
    {
        if (_stopping || _theme == null || _bar == null) return;
        if (_settingsWindow != null)
        {
            if (page != null) _settingsWindow.Show(page);
            if (_settingsWindow.WindowState == WindowState.Minimized) _settingsWindow.WindowState = WindowState.Normal;
            WindowActions.ForceForeground(new WindowInteropHelper(_settingsWindow).Handle);
            _settingsWindow.Activate();
            return;
        }
        var w = new Settings.SettingsWindow(this, _theme.Current, _bar.Strings, page);
        w.Closed += (_, _) => { if (_settingsWindow == w) _settingsWindow = null; };
        _settingsWindow = w;
        w.Show();
        WindowActions.ForceForeground(new WindowInteropHelper(w).Handle);
        w.Activate();
    }

    /// <summary>The settings' light/dark and animation choices over Windows' (at start, and when they change).</summary>
    void ApplyThemeChoices()
    {
        var a = _settings.Appearance;
        _theme!.DarkOverride = a.Theme switch { "dark" => true, "light" => false, _ => null };
        _theme.AnimationsOverride = a.ReduceMotion switch { "on" => false, "off" => true, _ => null };
        _theme.Accent = a.Accent;
        // The accessibility's motion and contrast (2026-10-22): read where the shell moves and draws its glass.
        ReduceMotion.On = _settings.Accessibility.ReduceMotion;
        Materials.HighContrastEdges = _settings.Accessibility.IncreaseContrast;
        MacScrollBar.Configure(a.ScrollBars == ScrollBarChoices.Always, a.ScrollBarClick == ScrollBarChoices.ClickedSpot);
        _theme.Refresh();
    }

    AppSettings Settings.ISettingsHost.Settings => _settings;
    bool Settings.ISettingsHost.TaskbarHidingAvailable => AppPaths.IsInstalledBuild || _o.HideTaskbar;
    bool Settings.ISettingsHost.TrayAtStart => _trayAtStart;
    string Settings.ISettingsHost.LogFolder => AppPaths.Logs;
    string Settings.ISettingsHost.SettingsFile => AppPaths.SettingsFile;
    (int auto, int fits) Settings.ISettingsHost.AppGridIconSizes(int columns, int rows) => _grid?.IconSizes(columns, rows) ?? (72, 84);
    // Starting at sign-in is for the installed copy only: a development build must not start at every sign-in.
    bool Settings.ISettingsHost.LoginItemAvailable => AppPaths.IsInstalledBuild;
    bool Settings.ISettingsHost.LoginItemOn
    {
        get
        {
            if (!AppPaths.IsInstalledBuild) return false;
            int known = Volatile.Read(ref _loginItem);
            return known >= 0 ? known == 1 : LoginItemStore.IsOn(AppPaths.ExePath);
        }
    }
    void Settings.ISettingsHost.SetLoginItem(bool on)
    {
        if (!AppPaths.IsInstalledBuild) return;
        Volatile.Write(ref _loginItem, on ? 1 : 0);
        string text = LoginTaskText;
        LoginWork(() => LoginItemStore.Set(on, AppPaths.ExePath, text));
    }

    // The sign-in start is a Task Scheduler task (LoginItemStore), and each call goes to the service: read and changed
    // off the UI thread, one change after another, the state read again after each (-1: not read yet).
    int _loginItem = -1;
    Task _loginWork = Task.CompletedTask;

    void LoginWork(Action change) =>
        _loginWork = _loginWork.ContinueWith(_ =>
        {
            change();
            Volatile.Write(ref _loginItem, LoginItemStore.IsOn(AppPaths.ExePath) ? 1 : 0);
        }, TaskScheduler.Default);

    /// <summary>The task's description in Task Scheduler, in the shell's language.</summary>
    string LoginTaskText => (_bar?.Strings ?? Strings.For(_settings.General.Language, System.Globalization.CultureInfo.CurrentUICulture))["login.task"];
    bool Settings.ISettingsHost.WindowsDark => _specDark ?? ThemeService.WindowsDark();
    Frostbar.Core.Time.AppearanceSchedule.State? Settings.ISettingsHost.Schedule => _schedule?.Last;
    event Action? Settings.ISettingsHost.ScheduleMoved
    {
        add { if (_schedule != null) _schedule.Moved += value; }
        remove { if (_schedule != null) _schedule.Moved -= value; }
    }
    Settings.SystemServices? Settings.ISettingsHost.System => _bar?.SystemServices();

    // The signed-in user (the settings' sidebar), read once. A spec's shell (with --diag only) has one of its own: its
    // pictures never show the user's name or face.
    Account? _account;
    Account Settings.ISettingsHost.Account => _account ??= _o.Diag && Environment.GetEnvironmentVariable("FROSTBAR_E2E_ACCOUNT") == "spec"
        ? new Account("Test User", null, Microsoft: false) : AccountInfo.Read();

    IReadOnlyList<Settings.SettingsTrayIcon> Settings.ISettingsHost.TrayIcons() =>
        [.. (_tray?.Last ?? []).GroupBy(i => i.SettingsKey).Select(g => g.First()).Select(i => new Settings.SettingsTrayIcon(i.SettingsKey,
            i.ExePath.Length > 0 ? _identity!.DisplayName(i.ExePath, i.ExePath) : i.Tooltip, i.Tooltip, i.Icon))];

    void Settings.ISettingsHost.Apply(Settings.SettingsArea area)
    {
        FileManager.Choice = _settings.General.FileManager; // read where it is used (opening a folder)
        FileManager.Path = _settings.General.FileManagerPath;
        switch (area)
        {
            case Settings.SettingsArea.TrayIcons:
                _bar?.RefreshTray(); // where each icon goes (the bar, the » panel, nowhere)
                break;
            case Settings.SettingsArea.Appearance:
                var before = _theme!.Current;
                ApplyThemeChoices();
                if (_theme.Current == before) ApplyTheme(_theme.Current); // the material is not part of the snapshot
                break;
            case Settings.SettingsArea.GlassTint:
                // The glass's tint only: the shell's glass made again in it, the settings window left as it is (made
                // again, it would drop the slider's knob being dragged). Menus, panels and stacks read it as they open.
                _bar?.ApplyTheme(_theme!.Current);
                _dock?.ApplyTheme(_theme!.Current);
                _search?.ApplyTheme(_theme!.Current);
                break;
            case Settings.SettingsArea.Clock:
                _bar?.Window.RefreshClock();
                break;
            case Settings.SettingsArea.Schedule:
                _schedule?.SettingsChanged();
                break;
            case Settings.SettingsArea.Wallpaper:
                _schedule?.PairChanged();
                break;
            case Settings.SettingsArea.Dock:
                // Another edge (stage 7): the strip goes there, and the Dock is made again for it.
                if (_dock != null && DockEdgeWanted != _dock.Window.Edge)
                {
                    Log.Info($"settings: the Dock moves to the {DockFrame.Name(DockEdgeWanted)} edge");
                    // Into its old edge and out of the new one (B14): the old one goes first (its pointer watch let go
                    // before the new one takes the same job), disposed once it is out of sight.
                    var old = _dock;
                    _dock = null;
                    old.GoAway(old.Dispose);
                    ReleaseDockStrip();
                    if (!_settings.Dock.AutoHide) ReserveDockStrip();
                    _dock = NewDock(comeIn: true);
                    _island?.UseDock(_dock);
                    break;
                }
                // Auto-hide: no strip, the work area reaches down to the screen's bottom, the Dock slides away.
                if (_settings.Dock.AutoHide != (_dock?.AutoHide ?? false))
                {
                    if (_settings.Dock.AutoHide) ReleaseDockStrip(); else ReserveDockStrip();
                    _dock?.StandOn(DockEdgePx()); // and again when the work area has followed (WorkAreaChanged)
                    _dock?.SetAutoHide(_settings.Dock.AutoHide, _ticker);
                }
                // The Dock follows the sliders at once; the reserved strip and larger icons once they stop moving.
                _dock?.ApplySettings();
                _dockSettled ??= new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => DockSettled(), Dispatcher);
                _dockSettled.Stop();
                _dockSettled.Start();
                break;
            case Settings.SettingsArea.MenuBarHeight:
                Log.Info($"settings: menu bar height {_settings.MenuBar.Height}: making the bars again");
                CloseShellWindows();
                CreateShellWindows();
                _overlays?.Relayout(barMadeAgain: true); // the banners and the volume display under the new bar's bottom
                break;
            case Settings.SettingsArea.Log:
                Log.Level = Log.Parse(_settings.Advanced.LogLevel);
                break;
            case Settings.SettingsArea.HotCorners:
                _corners?.SettingsChanged(); // the delay (the actions and the modifier are read at every look)
                RegisterShortcuts();
                break;
            case Settings.SettingsArea.AppGrid:
                _grid?.SettingsChanged(); // laid out again (made again for larger icons)
                break;
            case Settings.SettingsArea.Taskbar:
                if (_settings.General.HideWindowsTaskbar) _taskbarRestoredOnRequest = false; // switched on again after "bring it back now"
                if (HideTaskbarWanted) _hider?.Hide(_settings.Advanced.TaskbarHideStrategy);
                else _hider?.Restore();
                break;
            case Settings.SettingsArea.Hud:
                _volumeDisplay?.SetHide(_settings.Hud.Enabled); // off: Windows' own display again
                if (!_settings.Hud.Enabled) _overlays?.HideHud();
                break;
            case Settings.SettingsArea.Banners:
                if (!_settings.MenuBar.TrayBanners) _overlays?.ClearBanners();
                break;
            case Settings.SettingsArea.MenuBarItems:
                _bar?.Window.ArrangeItems(_settings.MenuBar.Items);
                break;
            case Settings.SettingsArea.MenuBarAutoHide:
                SetBarAutoHide();
                break;
            case Settings.SettingsArea.Battery:
                _bar?.RefreshBattery();
                break;
            case Settings.SettingsArea.ScrollBars:
                MacScrollBar.Configure(_settings.Appearance.ScrollBars == ScrollBarChoices.Always, _settings.Appearance.ScrollBarClick == ScrollBarChoices.ClickedSpot);
                break;
            case Settings.SettingsArea.Motion:
                ReduceMotion.On = _settings.Accessibility.ReduceMotion; // read where the shell moves things
                break;
            case Settings.SettingsArea.RecentItems:
                if (_recentDocs != null) _recentDocs.Wanted = _settings.MenuBar.RecentCount; // more read at once; fewer shown from what is read
                break;
            // Language and tray icons apply at the next start (the window says so); search reads its settings when used.
        }
        _saveLater ??= new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => SaveSettings(), Dispatcher);
        _saveLater.Stop();
        _saveLater.Start();
    }

    void DockSettled()
    {
        _dockSettled?.Stop();
        if (_stopping) return;
        if (_dockBar != null && Math.Abs(_dockBar.SizeDip - DockReservationDip) > 0.1)
        {
            _dockBar.SizeDip = DockReservationDip;
            _appBars!.Place(_dockBar); // Placed → the Dock stands on the new strip
        }
        if (_dock is { NeedsLargerIcons: true })
        {
            Log.Info("settings: the Dock's icons are read again at the larger size");
            _dock.Dispose();
            _dock = NewDock();
            _island?.UseDock(_dock);
        }
    }

    void SaveSettings()
    {
        _saveLater?.Stop();
        _recentSave?.Stop(); // Recent Items' apps go with it
        try { _saver.Save(_settings); } // serialised here, written off the UI thread (a failure is logged there)
        catch (Exception ex) { Log.Warn("settings: saving failed", ex); }
    }

    /// <summary>The saves asked for are written (before the process ends, or another instance reads settings.json).</summary>
    void WaitForSaves()
    {
        // (None yet when the shell is stopped before its settings are read.)
        if (_saver != null && !_saver.Wait(TimeSpan.FromSeconds(3))) Log.Warn("settings: still being written after 3 s; going on without waiting");
    }

    /// <summary>
    /// Diagnostics: answers once the saves asked for are in settings.json (a spec reads it next), after the input waiting
    /// on the UI thread (a pipe's request runs before it there, and it may ask for a save); the wait is not on the UI thread.
    /// </summary>
    async Task<string> SettingsWritten()
    {
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);
        return await Task.Run(() => _saver.Wait(TimeSpan.FromSeconds(3)) ? "ok" : "still writing");
    }

    /// <summary>
    /// settings.json was damaged or could not be read: said once the shell is up (the plan's "既定値＋.bak＋通知"; it was
    /// only in the log, and the defaults went on silently).
    /// </summary>
    void ReportSettingsProblem()
    {
        if (_settingsLoad is not { } load || (load.CorruptCopy == null && !load.Unreadable) || _bar == null) return;
        var s = _bar.Strings;
        string detail = load.CorruptCopy != null ? s.F("settings.corruptDetail", load.CorruptCopy) : s["settings.unreadableDetail"];
        Menus.Dialogs.Notice(_theme!.Current, s["settings.unreadableTitle"], detail, s["dlg.ok"]);
    }

    SettingsLoad? _settingsLoad;

    void Settings.ISettingsHost.RestoreTaskbarNow()
    {
        _taskbarRestoredOnRequest = true;
        Log.Info("settings: taskbar back on request: " + (_hider?.Restore() ?? "not started"));
    }

    void Settings.ISettingsHost.Relaunch() => Relaunch(openSettings: true);

    void Settings.ISettingsHost.HoldShortcuts(bool hold)
    {
        _shortcutsHeld = hold;
        RegisterShortcuts();
    }

    bool Settings.ISettingsHost.ShortcutTaken(string action) => _shortcutsTaken.Contains(action);

    // ---- keys for the hot corners' actions (macOS's Mission Control shortcuts; a touchpad's gestures can send them) ----

    readonly Dictionary<string, int> _shortcutIds = new();
    readonly HashSet<string> _shortcutsTaken = new();
    bool _shortcutsHeld;

    /// <summary>The action under which the search's keys are kept with the shortcuts' (the settings page asks whether they were taken).</summary>
    public const string SearchShortcut = "search";

    /// <summary>The settings' shortcuts and the search's keys registered again (none while the settings page records keys).</summary>
    void RegisterShortcuts()
    {
        if (_hotkeys == null) return;
        foreach (var id in _shortcutIds.Values) _hotkeys.Unregister(id);
        _shortcutIds.Clear();
        _shortcutsTaken.Clear();
        if (_shortcutsHeld || _stopping) return;
        // The search's keys (Alt+Space by default; stage 6-1: the settings page records others, or none).
        if (Shortcut.Parse(_settings.Search.Hotkey) is { } searchKeys)
        {
            if (_hotkeys.Register(searchKeys, () => { ClosePopups(); _search?.Toggle(_hotkeys.LastPressTime); }) is int searchId) _shortcutIds[SearchShortcut] = searchId;
            else
            {
                _shortcutsTaken.Add(SearchShortcut);
                Log.Warn($"search: {_settings.Search.Hotkey} is taken by Windows or another app; the menu bar's magnifier still opens the search");
            }
        }
        foreach (var (action, text) in _settings.HotCorners.Shortcuts)
        {
            if (Shortcut.Parse(text) is not { } keys) continue;
            string a = action;
            if (_hotkeys.Register(keys, () => RunShortcut(a)) is int id) _shortcutIds[action] = id;
            else
            {
                _shortcutsTaken.Add(action);
                Log.Warn($"shortcut {text} for {action}: taken by Windows or another app");
            }
        }
    }

    /// <summary>A shortcut's action, as its hot corner runs it; not over a full-screen app that holds the pointer (a game).</summary>
    void RunShortcut(string action)
    {
        if (_fullScreen?.IsFullScreen(_monitor.Handle) == true && !Native.PointerFree(_monitor.Bounds)) return;
        Log.Debug($"shortcut: {action}");
        RunCorner(action);
    }

    /// <summary>Starts the shell again (a new instance replaces this one), for a new language.</summary>
    void Relaunch(bool openSettings)
    {
        if (_saveLater?.IsEnabled == true) SaveSettings();
        WaitForSaves(); // the new instance reads them at once
        string exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "Frostbar.exe");
        string args = string.Join(' ', new[] { _o.RelaunchArgs, "--replace", openSettings ? "--settings" : "" }.Where(s => s.Length > 0));
        Log.Info("starting again for the new language");
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false }); }
        catch (Exception ex) { Log.Warn("could not start again", ex); }
    }

    // ---- the first start's welcome ---------------------------------------------------------------------------

    void OpenWelcome()
    {
        if (_stopping || _theme == null) return;
        if (_welcome != null) { _welcome.Activate(); return; }
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        var w = new Settings.WelcomeWindow(_theme.Current, _settings.General.Language, lang => Strings.For(lang, culture),
            AppPaths.IsInstalledBuild || _o.HideTaskbar, AppPaths.IsInstalledBuild, WelcomeDone);
        w.Closed += (_, _) => { if (_welcome == w) _welcome = null; };
        _welcome = w;
        w.Show();
        WindowActions.ForceForeground(new WindowInteropHelper(w).Handle);
        w.Activate();
    }

    /// <summary>The welcome's choices applied; closed before the end (null), nothing was agreed and the taskbar stays.</summary>
    void WelcomeDone(Settings.WelcomeChoices? c)
    {
        _welcomePending = false;
        var g = _settings.General;
        g.Welcomed = true;
        if (c == null)
        {
            g.HideWindowsTaskbar = false;
            SaveSettings();
            Log.Info("welcome: closed before the end; Windows' taskbar stays");
            return;
        }
        var d = _settings.Dock;
        // Resolved, as the shell runs: "system" on a Japanese Windows is the Japanese it already shows (no restart).
        var culture = System.Globalization.CultureInfo.CurrentUICulture;
        string running = _bar?.Strings.Language ?? Strings.For(g.Language, culture).Language;
        bool newLanguage = Strings.For(c.Language, culture).Language != running;
        g.Language = c.Language;
        g.HideWindowsTaskbar = c.HideTaskbar;
        if (!c.TaskbarPins) d.Pins.Clear();
        if (!c.Downloads && Native.KnownFolderPath(ShellFiles.Downloads) is { } downloads)
            d.Folders.RemoveAll(f => string.Equals(f, downloads, StringComparison.OrdinalIgnoreCase));
        d.AutoHide = c.AutoHide;
        d.ShowRecent = c.Recent;
        if (AppPaths.IsInstalledBuild)
        {
            g.LaunchAtLogin = c.LoginItem;
            // Here, not off the UI thread: a new language starts the shell again right after.
            LoginItemStore.Set(c.LoginItem, AppPaths.ExePath, LoginTaskText);
            Volatile.Write(ref _loginItem, c.LoginItem ? 1 : 0);
        }
        SaveSettings();
        Log.Info($"welcome: done: {c}");
        if (newLanguage) { Relaunch(openSettings: false); return; }
        _dock?.Reload();
        var host = (Settings.ISettingsHost)this;
        host.Apply(Settings.SettingsArea.Dock);
        host.Apply(Settings.SettingsArea.Taskbar);
    }

    void OnFullScreen(IntPtr monitor, bool fullScreen)
    {
        if (_bar == null || monitor != _monitor.Handle) return;
        _corners?.SetFullScreen(fullScreen);
        _barOverFullScreen = fullScreen;
        // macOS: over a full-screen app the menu bar waits at the top edge (unless it hides only on the desktop, or never)
        // and the Dock at the bottom edge; after it the bar stands or waits as its hiding has it.
        PlaceBar(_monitor, again: fullScreen);
        _dock?.SetFullScreen(fullScreen);
        if (fullScreen) _overlays?.ClearBanners(); // no banners over a full-screen app (they stay in the notification centre)
        _overlays?.Relayout(); // the volume display stands under the bar, or at the top while the bar is hidden
    }

    void OnExplorerRestarted()
    {
        if (_stopping || _appBars == null) return;
        var taskbar = Taskbar.Primary();
        if (taskbar != IntPtr.Zero && taskbar == _explorerTaskbar)
        {
            // Our own tray announces itself with the same message when it starts; Explorer has not restarted.
            Log.Debug("TaskbarCreated with the same Explorer taskbar: not a restart");
            return;
        }
        _explorerTaskbar = taskbar;
        Log.Info("Explorer restarted (TaskbarCreated)");
        if (_appBars.Bars.Count > 0) _appBars.RegisterAgain();
        _hider?.OnExplorerRestarted();
        _tray?.OnExplorerRestarted();
        _dock?.OnExplorerRestarted();
        _volumeDisplay?.ExplorerRestarted(); // Windows' volume display is a new window now
        _fullScreen?.ExplorerRestarted(); // the old Explorer's "fullscreen" is not taken back by the new one
    }

    DispatcherTimer? _resumeSettle;
    int _resumes;

    /// <summary>
    /// Back from sleep (PBT_APMRESUMEAUTOMATIC, or the heartbeat came late: the process did not run): a beat at once
    /// so the watchdog sees the UI thread, then once, 1 s later: the taskbar hidden again if Explorer showed it, the GPU
    /// device checked, the displays placed again if they changed, the front app and the clock read again.
    /// </summary>
    void OnResumed(string why)
    {
        if (_stopping) return;
        _heartbeat?.Beat();
        if (_resumeSettle == null)
        {
            _resumeSettle = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromSeconds(1) };
            _resumeSettle.Tick += (s, _) => { ((DispatcherTimer)s!).Stop(); AfterResume(); };
        }
        if (!_resumeSettle.IsEnabled) Log.Info($"resumed ({why})");
        _resumeSettle.Stop();
        _resumeSettle.Start();
    }

    void AfterResume()
    {
        if (_stopping) return;
        _resumes++;
        _hider?.CheckNow();
        Frostbar.Platform.Dwm.SurfaceFactory.CheckAll();
        OnDisplayChanged(); // placed again only if they changed (DisplayPlanner)
        _front?.Refresh();
        _bar?.Window.RefreshClock();
        _schedule?.Check(); // a sunset or a sunrise may have passed while asleep (the timer stops in a sleep)
    }

    static Display ToDisplay(MonitorInfo m) => new(m.Device, new ScreenRect(m.Bounds.Left, m.Bounds.Top, m.Bounds.Right, m.Bounds.Bottom), m.Dpi, m.Primary);

    static List<Display> Displays() => Monitors.All().Select(ToDisplay).ToList();

    /// <summary>
    /// One change of the displays (a monitor plugged in, a new resolution or scale, a new primary) sends several
    /// WM_DISPLAYCHANGE and WM_DPICHANGED: the shell waits until they stop for 400 ms, then places or makes its windows
    /// once (<see cref="ApplyDisplays"/>).
    /// </summary>
    void OnDisplayChanged()
    {
        if (_stopping || _bar == null) return;
        _displayMessages++;
        if (_displaySettle == null)
        {
            _displaySettle = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
            _displaySettle.Tick += (s, _) => { ((DispatcherTimer)s!).Stop(); ApplyDisplays(); };
        }
        _displaySettle.Stop();
        _displaySettle.Start();
    }

    void ApplyDisplays()
    {
        if (_stopping || _bar == null) return;
        // The movements' refresh read again whatever else changed (a refresh rate alone changes nothing below): read
        // once at the start, they ran 2.4 times too fast at 144 Hz after 60, and by 2 and 3 refreshes the other way.
        Common.DisplaySteps.Reread();
        var now = Displays();
        var change = DisplayPlanner.Plan(_displays, now);
        Log.Info("displays: " + string.Join(", ", now.Select(d => $"{d.Device} {d.Bounds.Right - d.Bounds.Left}x{d.Bounds.Bottom - d.Bounds.Top} {d.Dpi} dpi{(d.Primary ? " primary" : "")}")) + $": {change}");
        if (now.Count > 0) _displays = now; // none for a moment (a monitor going away): compared with the last ones next time
        if (change == DisplayChange.None) return;
        ClosePopups();
        var mon = Monitors.Primary();
        _monitor = mon;
        _grid?.MoveTo(mon);
        _overview?.MoveTo(mon);
        _corners?.SetEdges(new Core.HotCorners.ScreenEdges(mon.Bounds.Left, mon.Bounds.Top, mon.Bounds.Right, mon.Bounds.Bottom));
        Frostbar.Platform.Dwm.SurfaceFactory.CheckAll(); // a new display configuration can come with a reset GPU
        Frostbar.Platform.Dwm.LensCapture.Current?.Restart(); // the lens captures the primary monitor, at its size
        if (change == DisplayChange.Rebuild)
        {
            // Tool windows get no WM_DPICHANGED on Windows 11, and the bars are registered on their monitor: made again
            // on the primary at its scale (and the Dock on the GPU that drives it).
            _displaysRebuilt++;
            CloseShellWindows();
            CreateShellWindows();
            _search?.Rescaled(Monitors.Primary().Scale);
            _overlays?.Relayout(barMadeAgain: true);
            return;
        }
        _displaysReplaced++;
        foreach (var b in _appBars!.Bars) b.Monitor = mon.Handle; // all of them are on the primary; its handle can change with the layout
        _appBars.PlaceAll();
        _bar.MovedTo(mon);
        PlaceBar(mon); // with no strip, no app bar places it; waiting (over a full-screen app too), it looks at the new edges
        _dock?.MoveTo(mon);
        _island?.MoveTo(mon); // the primary's top middle, wherever it is now
        if (_dockBar == null) _dock?.StandOn(DockEdgePx()); // auto-hide: on the work area's edge
        SyncFullScreen(); // the primary may be another monitor now, or the same with a new handle
        _overlays?.Relayout();
    }

    int _alertsAnswered;

    string HandleCommand(string line)
    {
        Log.Info("command: " + line);
        switch (line)
        {
            case "ping": return "pong";
            case "activate":
            case "show-settings": OpenSettings(); return "ok";
            case "quit": Dispatcher.BeginInvoke(() => Stop(0, clean: true)); return "ok";
            case "restore-taskbar": _taskbarRestoredOnRequest = true; return _hider?.Restore() ?? "not started";
            default: return "unknown command";
        }
    }

    // Diagnostics "cc-trace": Focus's circle's scale and piece at each of Tween's frames.
    readonly List<object> _ccTrace = new();
    Action _ccTraceStep = () => { };
    // Diagnostics "tween-trace": values that must move as one (Tween.Together), at each of Tween's frames.
    readonly List<object> _tweenTrace = new();
    Action _tweenTraceStep = () => { };

    /// <summary>Diagnostics pipe (only with --diag): state for the E2E runner, and fault injection.</summary>
    string HandleDiag(string line)
    {
        var parts = line.Split(' ', 2);
        string arg = parts.Length > 1 ? parts[1] : "";
        switch (parts[0])
        {
            case "state": return JsonSerializer.Serialize(DiagState());
            case "hang": Thread.Sleep(int.Parse(arg)); return "woke";
            case "crash": Dispatcher.BeginInvoke(() => throw new InvalidOperationException("crash requested through the diagnostics pipe")); return "crashing";
            case "menu-open" when _bar != null:
                _bar.Menus.Open(int.Parse(arg));
                return JsonSerializer.Serialize(MenuState());
            case "menu-close" when _bar != null: _bar.Menus.Close(); return "ok";
            case "wifi-password-preview" when _theme != null && _bar != null:
            {
                // The password window as it looks, for screenshots: its Join tries nothing (it answers "wrong password").
                var w = new ControlCenter.WifiPasswordDialog(_theme.Current, _bar.Strings, arg.Length > 0 ? arg : "Example Network",
                    _ => Task.FromResult(global::Windows.Devices.WiFi.WiFiConnectionStatus.InvalidCredential));
                w.ShowInFront();
                var h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                return h.ToInt64().ToString();
            }
            case "menu-index" when _bar != null:
                // Cheap enough to poll every millisecond without slowing the UI thread (unlike "state").
                return _bar.Menus.OpenIndex == int.MinValue ? "none" : $"{_bar.Menus.OpenIndex} {_bar.Menus.LastOpenMs} {_bar.Menus.LastInputToFrameMs}";
            case "menu-run" when _bar != null:
            {
                // "menu-run <menu id> <item id> [for <hwnd>]": runs an item without the UI (dispatch tests). "system" is the
                // mark's menu. With "for", only while the app in front (as the shell follows it: the items act on that
                // app) is that window's: a spec's target had lost the foreground to the user's app, whose window was then
                // sent Ctrl+S, minimized and closed (2026-10-16, elevated). Under a spec the runner refuses other apps too.
                var a = arg.Split(' ');
                if (a.Length < 2) return "usage: menu-run <menu id> <item id> [for <hwnd>]";
                if (a.Length >= 4 && a[2] == "for" && long.TryParse(a[3], out long only))
                {
                    uint pid = Native.ProcessId(new IntPtr(only));
                    if (pid == 0 || _front?.Current is not { } inFront || inFront.Pid != pid)
                        return $"not in front: the front app is {_front?.Current?.ExeName ?? "none"}";
                }
                var menu = a[0] == "system" ? _bar.SystemMenu() : _bar.AppMenus().FirstOrDefault(m => m.Id == a[0]);
                var item = menu == null ? null : Flatten(menu.Items).FirstOrDefault(x => x.Id == a[1]);
                if (item == null) return "no such item";
                if (!item.Enabled) return "disabled";
                _bar.Runner.Run(item);
                return _bar.Runner.LastResult;
            }
            case "status" when _bar != null: _bar.OnStatus(arg, _bar.Window.StatusRect(arg)); return "ok";
            case "tray":
            {
                // Exe names only: tooltips can carry personal text (mail subjects, names).
                var rects = _bar?.Window.TrayRects() ?? new();
                var layout = _bar?.Window.Layout;
                var panel = _bar?.TrayOverflow;
                var cells = panel?.IconRects() ?? new();
                string Where(string key) => layout == null ? "unknown" : layout.Bar.Contains(key) ? "bar" : layout.Overflow.Contains(key) ? "overflow" : "hidden";
                return JsonSerializer.Serialize(new
                {
                    running = _tray?.Running ?? false,
                    items = (_tray?.Last ?? []).Select(i => new
                    {
                        i.ExeName, guid = i.Guid == Guid.Empty ? null : i.Guid.ToString(), hasIcon = i.Icon != null,
                        // The place key (the GUID or exe:<name>:<number>) and where the fit put it.
                        key = i.SettingsKey, where = Where(i.Key),
                        rect = rects.TryGetValue(i.Key, out var r) ? new { r.Left, r.Top, r.Right, r.Bottom } : null,
                        cell = cells.TryGetValue(i.Key, out var c) ? new { c.Left, c.Top, c.Right, c.Bottom } : null,
                    }),
                    shown = rects.Count,
                    // The bar's share-out (stage 0-8): the » button (its place in DIPs) and the titles shown of all.
                    layout = layout == null ? null : new
                    {
                        bar = layout.Bar.Count, overflow = layout.Overflow.Count, button = layout.OverflowButton,
                        // Pixels, as the icons' (and the menus' titles in "state").
                        buttonRect = _bar!.Window.StatusRect("tray-overflow") is { IsEmpty: false } b
                            ? new { left = (int)Math.Round(b.Left * _monitor.Scale), top = (int)Math.Round(b.Top * _monitor.Scale), right = (int)Math.Round(b.Right * _monitor.Scale), bottom = (int)Math.Round(b.Bottom * _monitor.Scale) }
                            : null,
                        titles = layout.Titles, titlesOf = _bar.Window.Titles.Count,
                    },
                    panelOpen = panel != null,
                    // Balloons: received from apps, listed for the notification centre, and kept by ManagedShell as missed (0: we take them).
                    balloons = new { received = _tray?.BalloonsReceived ?? 0, listed = _balloons.Items.Count, missed = _tray?.MissedNotifications() ?? -1 },
                    // Icons dropped because their app's window was gone (it ended without taking them away).
                    gone = new { onClick = _tray?.Dropped.onClick ?? 0, byCheck = _tray?.Dropped.byCheck ?? 0 },
                });
            }
            case "tray-click" when _tray != null:
            {
                // "tray-click <place key> [right]": a click on the icon as the bar sends it (press and release), without
                // the pointer: for an icon whose app has just been killed, which a real click could miss once it went.
                var a = arg.Split(' ');
                if (_tray.Last.FirstOrDefault(i => i.SettingsKey == a[0]) is not { } icon) return "no such icon";
                if (_onlyAppsIn is { } only && !SpecGuard.Allows(only, icon.ExePath, isDesktop: false)) return "guarded";
                var button = a.Length > 1 && a[1] == "right" ? System.Windows.Input.MouseButton.Right : System.Windows.Input.MouseButton.Left;
                _tray.Mouse(icon.Key, button, true);
                _tray.Mouse(icon.Key, button, false);
                return "ok";
            }
            case "dock" when _dock != null: return JsonSerializer.Serialize(DockState());
            case "dock-guarded" when _dock != null: return _dock.Guarded.ToString(); // clicks and menu commands a spec's shell's Dock did not carry out
            case "room" when _room != null: return JsonSerializer.Serialize(_room.State()); // the windows put between the bars (2026-10-24)
            // The Dock's menu or stack closed without input (as one closes after an item chosen has blinked).
            case "dock-menu-close" when _dock != null: _dock.Menu.Close(); return "ok";
            case "dock-zorder" when _dock != null:
            {
                // Where the Dock's windows stand among the top-level windows, from the top: the backdrop (plate, icons,
                // minimized windows' thumbnails), the content (the pointer) and the window above (the small icons).
                var dw = _dock.Window;
                var names = new Dictionary<IntPtr, string> { [dw.BackdropHandle] = "backdrop", [dw.Handle] = "content" };
                if (dw.AboveHandle != IntPtr.Zero) names[dw.AboveHandle] = "above";
                var order = new List<string>();
                int seen = 0;
                for (var h = Native.GetWindow(dw.Handle, 0 /*GW_HWNDFIRST*/); h != IntPtr.Zero && seen < 5000; h = Native.GetWindow(h, 2 /*GW_HWNDNEXT*/), seen++)
                    if (names.TryGetValue(h, out var name)) order.Add($"{name}@{seen}");
                return string.Join(" ", order);
            }
            case "gpu-lost" when _dock != null:
            {
                // As if the GPU had gone: its lost event fires (a real loss's path), or with "upload" an upload fails while
                // the Dock uploads its pictures again (a loss between two checks), in a dispatcher operation of its own as
                // the Dock's uploads are (an exception there ends the shell, not this command).
                var gpu = _dock.Window.Surfaces;
                bool upload = arg == "upload";
                gpu.SimulateLoss(duringUpload: upload);
                if (upload) Dispatcher.BeginInvoke(() => _dock?.Window.Surface.Scene?.RemakePictures());
                return $"generation {gpu.Generation}";
            }
            case "dock-menu" when _dock != null:
            {
                // The Dock's open right-click menu: its items and where they are (pixels), or "none".
                var p = _dock.Menu.Popup;
                if (p == null) return "none";
                static object Px(RECT r) => new { r.Left, r.Top, r.Right, r.Bottom };
                return JsonSerializer.Serialize(new
                {
                    hwnd = p.Handle.ToInt64(),
                    items = p.Items.Select((i, n) => new { i.Id, i.Label, i.Enabled, i.Checked, i.Hint, rect = i.IsSeparator ? null : Px(p.RowPx(n)) }),
                });
            }
            // A folder's open stack: its items (file names, newest first) and where they are (pixels), or "none".
            case "dock-stack" when _dock != null: return _dock.StackState() is { } stack ? JsonSerializer.Serialize(stack) : "none";
            // A folder kept in the Dock and taken out again (saved like the menu's): screenshots of a sample folder.
            case "dock-folder-add" when _dock != null: _dock.KeepFolder(arg); return "ok";
            case "dock-folder-remove" when _dock != null: _dock.RemoveFolder(arg); return "ok";
            case "search-open" when _search != null:
                // Opens as the shortcut would, timed from now.
                _search.Open(Environment.TickCount);
                return "ok";
            case "search-close" when _search != null: _search.Close(restore: arg == "restore"); return "ok";
            case "search-text" when _search != null: _search.SetText(arg); return "ok";
            case "search-render" when _search != null: _search.RenderAgain(); return JsonSerializer.Serialize(_search.State());
            case "search-state" when _search != null: return JsonSerializer.Serialize(_search.State());
            // Stage 6-1: a filter as Ctrl+1…4 sets it, the last action asked for, Frostbar's pages read again now.
            case "search-filter" when _search != null:
                if (!Enum.TryParse<SearchFilter>(arg, ignoreCase: true, out var filter)) return "apps, files, actions, clipboard or none";
                _search.SetFilter(filter);
                return JsonSerializer.Serialize(_search.State());
            case "search-action":
                return JsonSerializer.Serialize(new
                {
                    last = LastSearchAction, runner = _bar?.Runner.LastResult, trashFull = _dock?.TrashFull,
                    trashQuestion = _dock?.EmptyTrashQuestion is { } q ? new WindowInteropHelper(q).Handle.ToInt64() : 0,
                });
            // Read again a page an idle moment: search-state's shellPagesMs is the UI thread's time once they are read.
            case "search-index" when _search != null: IndexShellPages(); return "reading";
            case "grid-open" when _grid != null:
                // Opens as the Dock's item would, timed from now.
                _grid.Open(Environment.TickCount);
                return "ok";
            case "grid-close" when _grid != null: _grid.Close(restore: arg == "restore"); return "ok";
            case "grid-text" when _grid != null: _grid.SetText(arg); return "ok";
            case "grid-page" when _grid != null: _grid.ShowPage(int.Parse(arg)); return "ok";
            case "grid-state" when _grid != null: return JsonSerializer.Serialize(_grid.State());
            // The grid's open right-click menu: its items and where they are (pixels), or "none".
            case "grid-menu" when _grid != null: return _grid.MenuState() is { } gridMenu ? JsonSerializer.Serialize(gridMenu) : "none";
            case "grid-apps" when _grid != null: return JsonSerializer.Serialize(_grid.Apps());
            // A spec's setup: its own arrangement (its profile's settings), and a folder's new name typed without the IME.
            case "grid-arrange" when _grid != null: return _grid.SetArrangement(arg) is { } error ? "error: " + error : "ok";
            case "grid-folder-name" when _grid != null: return _grid.SetFolderName(arg) ? "ok" : "not renaming";
            case "cc-state" when _bar != null: return JsonSerializer.Serialize(ControlCenterState());
            case "cc-paint" when _bar?.ControlCenterPanel is { } ccp: // "cc-paint on|off": the dark mode's switch drawn so, nothing switched (its colours change over 0.15 s)
                ccp.PaintDarkSwitch(arg == "on");
                return "ok";
            case "cc-glide" when _bar?.ControlCenterPanel is { } ccg && double.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double level):
                // "cc-glide <0-1>": the volume slider told of a level from outside (as the volume keys do), drawn only:
                // nothing is set (B1: it slides there).
                ccg.VolumeSlider.Glide(level, _theme!.Current.Animations);
                return "ok";
            case "appearance-theme" when arg is "system" or "light" or "dark":
                // "appearance-theme system|light|dark": the shell's own appearance (Settings → Appearance; the spec's
                // profile), applied as the settings page does: Windows' mode is not changed (A4: a theme change).
                _settings.Appearance.Theme = arg;
                ((Settings.ISettingsHost)this).Apply(Settings.SettingsArea.Appearance);
                return "ok";
            // "scroll-trace on" starts recording every moving list's offset at each frame; "scroll-trace off" stops and
            // returns the record ([frame time ms, offset DIP] pairs): how evenly a list moves from frame to frame.
            case "scroll-trace":
                return JsonSerializer.Serialize(SmoothScroll.Trace(arg == "on").Select(p => new[] { Math.Round(p.ms, 2), Math.Round(p.offset, 2) }));
            case "view-state" when _bar != null: return JsonSerializer.Serialize(ViewPanelState());
            case "nc-state" when _bar != null: return JsonSerializer.Serialize(NotificationCenterState());
            // The banners and the volume display (stage 2), and Windows' own display as the shell keeps it.
            case "overlays" when _overlays != null:
                return JsonSerializer.Serialize(new
                {
                    overlays = _overlays.State(),
                    display = _volumeDisplay is { } v ? new { hwnd = v.Display.ToInt64(), hidden = v.Hidden, keys = v.Keys, showings = v.Showings } : null,
                    hudEnabled = _settings.Hud.Enabled, trayBanners = _settings.MenuBar.TrayBanners,
                });
            // The Dynamic Island (2026-11-08): its state; a spec's shell sets what it shows ("island-set …", IslandHost.SpecSet).
            case "island" when _island != null: return JsonSerializer.Serialize(_island.State());
            case "island-set" when _island != null: return _island.SpecSet(arg);
            // The volume display without a key (pictures, the window itself): "volume" or "brightness".
            case "hud-show" when _overlays != null && _bar != null:
                if (arg == "brightness" && _bar.Brightness is { Available: true } br) _overlays.ShowHud(HudKind.Brightness, br.Level / 100.0, false);
                else if (arg != "brightness") _overlays.ShowHud(HudKind.Volume, _bar.Audio.Volume, _bar.Audio.Muted);
                else return "no brightness here";
                return "ok";
            // A banner whose window's making lets another notice through, as the volume display's let a key through: two
            // notices of the spec's own from two apps (one app's would take the other's place), not balloons (no top bit).
            case "banner-nested" when _overlays != null:
            {
                var now = DateTimeOffset.Now;
                _overlays.WhileMakingBanner = () => _overlays.Show(new Notice(0x0E2E0002, "Frostbar E2E Two", "", now, "Nested two", "Came while the first was made"), quiet: false);
                _overlays.Show(new Notice(0x0E2E0001, "Frostbar E2E One", "", now, "Nested one", "Its window was being made"), quiet: false);
                return "ok";
            }
            // The brightness moved as the keys move it, with nothing written to the display: "hud-brightness <0-100> out|in"
            // (Windows' display out or not, as a spec cannot press a brightness key). "hud-brightness real" lets it be read again.
            case "hud-brightness" when _overlays != null:
            {
                var words = arg.Split(' ');
                if (words[0] == "real") { _specDisplayOut = null; return "ok"; }
                if (words.Length != 2 || !int.TryParse(words[0], out int level) || words[1] is not ("out" or "in")) return "error: <0-100> out|in, or real";
                _specDisplayOut = words[1] == "out";
                HudForBrightness(level);
                return "ok";
            }
            // The volume display called for again from inside its window's making, as a key let through by it would be.
            case "hud-nested" when _overlays != null && _bar != null:
                _overlays.WhileMakingHud = () => _overlays.ShowHud(HudKind.Volume, _bar.Audio.Volume, _bar.Audio.Muted);
                _overlays.ShowHud(HudKind.Volume, _bar.Audio.Volume, _bar.Audio.Muted);
                return "ok";
            case "nc-month" when _bar?.NotificationCenter != null: _bar.NotificationCenter.TurnMonth(int.Parse(arg)); return JsonSerializer.Serialize(NotificationCenterState());
            case "settings-state": return JsonSerializer.Serialize(SettingsState());
            case "appearance-state": // Windows' dark mode by itself (stage 4-3): the schedule's last look, the switches, the pair
                return JsonSerializer.Serialize(AppearanceState());
            case "appearance-clock" when _schedule != null: // appearance-clock <ISO time>|off: the schedule's "now" (a spec's), and a look at once
                if (arg == "off") _schedule.Clock = () => DateTimeOffset.Now;
                else if (DateTimeOffset.TryParse(arg, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var at)) _schedule.Clock = () => at;
                else return "a time like 2026-10-16T18:00:00+09:00, or off";
                _schedule.Check();
                return JsonSerializer.Serialize(AppearanceState());
            case "pointer-state": // what the Trackpad, Mouse and Keyboard pages read and write (a spec's own values, or Windows')
            {
                var p = Frostbar.Platform.Sys.PointerStore.Current;
                return JsonSerializer.Serialize(new { spec = p is Frostbar.Platform.Sys.SpecPointer, writes = p.Writes, touchpad = p.Touchpad(), mouse = p.Mouse(), keys = p.Keys() });
            }
            case "dialogs": // the open dialogs (About, Force Quit, power, the alerts, a display change) with their red close buttons, buttons and lines
                return JsonSerializer.Serialize(Windows.OfType<Menus.DialogWindow>().Where(d => d.IsVisible)
                    .Select(d => new
                    {
                        title = d.Title, hwnd = new WindowInteropHelper(d).Handle.ToInt64(), close = ScreenPx(d.CloseDot),
                        buttons = Descendants(d).OfType<System.Windows.Controls.Button>().Select(b => new { text = b.Content as string, rect = ScreenPx(b), primary = b.IsDefault }).ToList(),
                        lines =Descendants(d).OfType<System.Windows.Controls.TextBlock>().Where(x => !string.IsNullOrEmpty(x.Text) && x.TemplatedParent == null).Select(x => x.Text).ToList(),
                        lineRects = Descendants(d).OfType<System.Windows.Controls.TextBlock>().Where(x => !string.IsNullOrEmpty(x.Text) && x.TemplatedParent == null).Select(x => ScreenPx(x)).ToList(),
                        window = ScreenPx(d),
                    }).ToList());
            // An alert with the words and buttons of the real ones (pictures, the alert-look spec): what it asks does nothing here.
            case "alert-show" when _bar != null:
            {
                var s = _bar.Strings;
                var t = _theme!.Current;
                switch (arg)
                {
                    case "notice": Menus.Dialogs.Notice(t, s["settings.unreadableTitle"], s["dryRun"], s["dlg.ok"]); break;
                    case "confirm": Menus.Dialogs.Confirm(t, s.F("dlg.forceQuitQuestion", "Test Target"), s["dlg.forceQuitDetail"], s["dlg.cancel"], s["dlg.forceQuit"], () => _alertsAnswered++); break;
                    case "long": Menus.Dialogs.Confirm(t, s.F("dlg.forceQuitQuestion", "Test Target"), s["dlg.forceQuitDetail"], s["dlg.cancel"], "An action named with far too many words", () => _alertsAnswered++); break;
                    case "keep": Menus.Dialogs.KeepDisplay(t, s, 60, () => { }, () => { }); break;
                    case "power": _bar.Dialogs.ConfirmPower(s["dlg.restart"], s["dlg.restart"], () => "the alert-show diagnostic"); break;
                    default: return "error: notice|confirm|long|keep|power";
                }
                return "ok";
            }
            case "alert-answers": return _alertsAnswered.ToString(); // how many alerts' actions ran (alert-show)
            case "display-state": // the Displays page's displays (a spec's own, or Windows'), its writes, and a change waiting to be kept
            {
                var store = DisplayStore.Current;
                return JsonSerializer.Serialize(new { spec = store is SpecDisplays, writes = store.Writes, keepSeconds = Settings.SettingsWindow.KeepSeconds, asking = Settings.SettingsWindow.AskingToKeep, displays = store.Displays() });
            }
            case "recent" when _bar != null: // the system menu's Recent Items (stage 5-2) as the submenu would list them
                return JsonSerializer.Serialize(_bar.RecentState());
            // The Control Center's volume slider let go, without the slider (2026-10-22): the settings' volume feedback
            // plays then (counted, not played, in a spec's shell), and the volume itself is not touched.
            case "volume-letgo":
                VolumeFeedback(fromKey: false);
                return JsonSerializer.Serialize(new { played = UiSounds.Played, on = _settings.Sound.VolumeFeedback, muted = _bar?.Audio.Muted, available = _bar?.Audio.Available });
            case "input-state" when _bar != null: // the input mode as the bar reads it, and the input menu when it is open (stage 5-3)
            {
                var menu = _bar.InputMenu;
                return JsonSerializer.Serialize(new
                {
                    ime = _bar.ImeState(),
                    menu = menu == null ? null : new
                    {
                        ticked = menu.Ticked,
                        rows = menu.Rows.ToDictionary(kv => kv.Key, kv => kv.Value.IsVisible ? ScreenPx(kv.Value) : null),
                        emoji = menu.EmojiLink.IsVisible ? ScreenPx(menu.EmojiLink) : null,
                    },
                });
            }
            case "clipboard-set" when _specClipboard != null:
            {
                // "clipboard-set off|unavailable|<json>": a spec's own history; the JSON an array of
                // {"id", "kind": text|image|files|other, "text", "minutesAgo"}.
                if (arg is "off" or "unavailable")
                {
                    _specClipboard.State = arg == "off" ? Core.Search.ClipboardState.Off : Core.Search.ClipboardState.Unavailable;
                    return "ok";
                }
                var items = new List<Core.Search.ClipboardItem>();
                using (var doc = JsonDocument.Parse(arg))
                    foreach (var e in doc.RootElement.EnumerateArray())
                        items.Add(new Core.Search.ClipboardItem(e.GetProperty("id").GetString()!,
                            Enum.Parse<Core.Search.ClipboardKind>(e.GetProperty("kind").GetString()!, ignoreCase: true),
                            e.TryGetProperty("text", out var tx) ? tx.GetString() ?? "" : "",
                            DateTimeOffset.Now.AddMinutes(-(e.TryGetProperty("minutesAgo", out var m) ? m.GetDouble() : 0))));
                _specClipboard.State = Core.Search.ClipboardState.On;
                _specClipboard.Items = items;
                return "ok";
            }
            case "clipboard-state" when _specClipboard != null: // the spec's own history: what was put back, how often it was read
                return JsonSerializer.Serialize(new { state = _specClipboard.State.ToString(), items = _specClipboard.Items.Count, putBack = _specClipboard.PutBackIds, reads = _specClipboard.Reads });
            case "privacy-set" when _privacy is SpecPrivacy spec:
            {
                // "privacy-set <microphone apps|-> <camera apps|->": a spec's own state (names joined by "+"; - none).
                var a = arg.Split(' ', 2);
                static IReadOnlyList<string> Apps(string? x) => x is null or "-" ? [] : x.Split('+', StringSplitOptions.RemoveEmptyEntries);
                spec.Set(new Core.MenuBar.PrivacyState(Apps(a.ElementAtOrDefault(0)), Apps(a.ElementAtOrDefault(1))));
                return "ok";
            }
            case "privacy-state" when _bar != null: // the capsule (kind, pictures, where, tooltip) and an open Control Center's lines
            {
                var item = _bar.Window.StatusItems["privacy"];
                var (kind, pictures) = _bar.Window.PrivacyShown;
                return JsonSerializer.Serialize(new
                {
                    kind = kind.ToString(), pictures, rect = item.IsVisible ? ScreenPx(item) : null, tooltip = item.ToolTip as string,
                    cc = _bar.ControlCenterPanel?.PrivacyLines,
                    // Windows' records (or a spec's key of its own): how often read, the last read's time.
                    records = _privacy is ConsentStorePrivacy store ? new { reads = store.Reads, lastReadMs = store.LastReadMs } : null,
                });
            }
            case "now-playing" when _bar != null: // Now Playing (stage 5-2): its item, and its panel when it is open
            {
                var np = _bar.NowPlaying;
                return JsonSerializer.Serialize(new
                {
                    item = _bar.Window.StatusItems.TryGetValue("nowPlaying", out var it) && it.IsVisible ? ScreenPx(it) : null,
                    tooltip = it?.ToolTip as string,
                    panel = np == null ? null : new
                    {
                        title = np.Title, artist = np.Artist, app = np.AppName, button = np.ButtonShows,
                        playPause = np.PlayPauseButton.IsVisible ? ScreenPx(np.PlayPauseButton) : null,
                        open = np.OpenLink.IsVisible ? ScreenPx(np.OpenLink) : null,
                    },
                });
            }
            case "bar-items" when _bar != null: // the menu bar's movable status items (stage 5-1): the settings' order, the ones showing left to right, where each one is
            {
                var w = _bar.Window;
                var showing = w.StatusItems.Where(kv => Core.MenuBar.MenuBarItems.Movable.Contains(kv.Key) && kv.Value.IsVisible)
                    .OrderBy(kv => kv.Value.PointToScreen(new Point(0, 0)).X).Select(kv => kv.Key).ToList();
                return JsonSerializer.Serialize(new
                {
                    settings = _settings.MenuBar.Items, shown = w.ShownItems, showing,
                    rects = w.StatusItems.Where(kv => kv.Value.IsVisible).ToDictionary(kv => kv.Key, kv => ScreenPx(kv.Value)),
                });
            }
            case "bar-tone" when _bar != null:
            {
                // "bar-tone <0-1>|off": the brightness under the bar as a spec's own (C2: the words follow the wallpaper); its state.
                if (arg == "off") _bar.Picture.SetForSpec(null);
                else if (double.TryParse(arg, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double luma)) _bar.Picture.SetForSpec(luma);
                var look = _bar.BarLook(_theme!.Current);
                return JsonSerializer.Serialize(new
                {
                    luma = _bar.Picture.Luma, darkWords = _bar.Picture.DarkWords, reads = _bar.Picture.Reads, appearanceDark = _theme.Current.Dark, barDark = look.Dark,
                    words = (_bar.Window.Foreground as System.Windows.Media.SolidColorBrush)?.Color.ToString(),
                });
            }
            case "bar-autohide" when _bar != null: // the menu bar's hiding (stage 5-1): the setting, its strip at the top, waiting at the edge, down, on screen
                return JsonSerializer.Serialize(new
                {
                    autoHide = _settings.MenuBar.AutoHide, strip = _topBar != null, waiting = _peek?.Waiting ?? false, down = _peek?.Shown ?? false,
                    visible = _bar.Window.IsVisible, fullScreen = _barOverFullScreen,
                    material = _bar.Window.MaterialKind.ToString(), // a clear bar is frosted over the apps (2026-11-01)
                    // Where the bar's window stands (px) and its slides down (2026-10-19, "B9": it comes from past the top edge).
                    top = Native.FrameBounds(_bar.Window.Handle).Top, slides = _peek?.Slides ?? 0,
                });
            case "bar-autohide-set" when _bar != null: // bar-autohide-set <hiding>: chosen at once, no page to open (a spec catching the bar as it slides up); whether it was sliding
            {
                if (!Core.MenuBar.MenuBarItems.AutoHides.Contains(arg)) return "error: " + string.Join("|", Core.MenuBar.MenuBarItems.AutoHides);
                bool sliding = Common.Fades.IsGliding(_bar.Window);
                _settings.MenuBar.AutoHide = arg;
                SetBarAutoHide();
                return JsonSerializer.Serialize(new { sliding });
            }
            case "display-countdown": // display-countdown <seconds>: how long a display change waits to be kept (specs shorten it)
                if (!int.TryParse(arg, out int keep) || keep < 1 || keep > 60) return "error: 1-60 seconds";
                Settings.SettingsWindow.KeepSeconds = keep;
                return "ok";
            case "overview-state" when _overview != null: return JsonSerializer.Serialize(_overview.State());
            case "overview-open" when _overview != null:
            {
                // "overview-open [appId] [strip]": one app's windows only, and the strip of desktops with them (specs, shots).
                var a = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                string? app = a.FirstOrDefault(x => x != "strip");
                _overview.Open(app, desktops: a.Contains("strip") ? true : null);
                return JsonSerializer.Serialize(_overview.State());
            }
            case "overview-close" when _overview != null: _overview.Close(); return "ok";
            case "corners" when _corners != null:
                return JsonSerializer.Serialize(new { fired = _corners.Fired, last = _corners.LastFired.corner.ToString(), lastAt = _corners.LastFired.at, now = Environment.TickCount64 });
            case "corner-run":
            {
                // "corner-run <action> [hidden]": a hot corner's action as the watcher runs it; "hidden" with the menu bar
                // hidden first, as over a full-screen app (its status items then have no place on the screen).
                var a = arg.Split(' ');
                bool hide = a.Length > 1 && a[1] == "hidden" && _bar != null;
                if (hide) _bar!.Window.Hide();
                try { RunCorner(a[0]); return "ok"; }
                catch (Exception ex) { return "error: " + ex.GetType().Name + ": " + ex.Message; }
                finally { if (hide) _bar!.Window.Show(); }
            }
            case "settings-open": OpenSettings(arg.Length > 0 ? arg : null); return "ok";
            case "settings-search" when _settingsWindow != null: _settingsWindow.SetSearch(arg); return JsonSerializer.Serialize(SettingsState());
            case "settings-focus" when _settingsWindow != null:
            {
                // "settings-focus <control>": the keyboard's focus on a control of the page shown (D3: the spec then sends real
                // keys); its focus as the keyboard put it there, so the ring shows.
                if (!_settingsWindow.Controls.TryGetValue(arg, out var c)) return "no such control";
                System.Windows.Input.Keyboard.Focus(c);
                System.Windows.Input.KeyboardNavigation.SetTabNavigation(c, System.Windows.Input.KeyboardNavigationMode.Continue);
                return c.IsKeyboardFocused ? "ok" : "not focused";
            }
            case "uia" when _settingsWindow != null:
            {
                // "uia <control>": what a screen reader is told about a control of the page shown (D4): its type, name and patterns.
                if (!_settingsWindow.Controls.TryGetValue(arg, out var c)) return "no such control";
                var peer = System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(c);
                if (peer == null) return JsonSerializer.Serialize(new { peer = false });
                var patterns = Enum.GetValues<System.Windows.Automation.Peers.PatternInterface>().Where(i => peer.GetPattern(i) != null).Select(i => i.ToString()).ToArray();
                return JsonSerializer.Serialize(new
                {
                    peer = true, type = peer.GetAutomationControlType().ToString(), name = peer.GetName(), patterns,
                    toggle = peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Toggle) is System.Windows.Automation.Provider.IToggleProvider t ? t.ToggleState.ToString() : null,
                    value = peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Value) is System.Windows.Automation.Provider.IValueProvider vp ? vp.Value : null,
                });
            }
            case "liquid-glass" when _theme != null:
                // "liquid-glass on|off" as the settings' switch does (and saved like it): for screenshots; alone, what it is.
                if (arg is "on" or "off")
                {
                    _settings.Appearance.LiquidGlass = arg == "on";
                    ((Settings.ISettingsHost)this).Apply(Settings.SettingsArea.Appearance);
                }
                return _settings.Appearance.LiquidGlass ? "on" : "off";
            case "lens":
                // Liquid Glass's lens (ADR-016): the capture, its counts, the plates it draws and whether they are left out of capture.
                return JsonSerializer.Serialize(Frostbar.Platform.Dwm.LensCapture.Current?.State() ?? new { available = Frostbar.Platform.Dwm.LensCapture.Available });
            case "droplets":
                // For photos: "droplets <DIPs>" holds every shown panel's pieces running together that far; "droplets off".
                Frostbar.Platform.Dwm.WindowBackdrop.HoldDroplets(double.TryParse(arg, System.Globalization.CultureInfo.InvariantCulture, out double held) ? Math.Clamp(held, 0, 80) : null);
                return "ok";
            case "shadows":
                // Each shown glass's shadows: how deep (1: as made), and what was read behind the glass.
                return JsonSerializer.Serialize(Frostbar.Platform.Dwm.WindowBackdrop.ShadowStates());
            case "tones":
            {
                // The glass's tone (item 4): the open panel's parts (their rectangles in the panel's pixels, dark or not,
                // what is behind, a text colour on each, what is drawn in the wrong tone) and the panel's place, the
                // open menu's, the Dock's; the banners', the volume display's and the Dock's open stack's likewise.
                var panel = _bar?.Menus.Panel;
                var menu = _bar?.Menus.Popup is { IsVisible: true } mp ? mp : null;
                object? Of(Menus.PanelPopup? p) => p is { IsVisible: true } ? new { kind = p.GetType().Name, at = ScreenPx(p), parts = p.Tones() } : null;
                return JsonSerializer.Serialize(new
                {
                    panel = Of(panel),
                    banners = _overlays?.Banners.Select(Of).ToArray(),
                    hud = Of(_overlays?.HudShown),
                    stack = Of(_dock?.Menu.Panel),
                    menu = menu != null ? new { dark = menu.ToneDark, at = ScreenPx(menu), labels = Enumerable.Range(0, menu.Items.Count).Select(menu.LabelColor).ToArray() } : null,
                    dock = _dock != null ? new
                    {
                        dark = _dock.Window.Tone.dark,
                        luma = double.IsNaN(_dock.Window.Tone.luma) ? (double?)null : Math.Round(_dock.Window.Tone.luma, 3),
                        dots = _dock.Window.Surface.TextColor.ToString(),
                        // The apps' and folders' slots, and those with a picture drawn (none: the Dock stood empty).
                        items = _dock.Window.Surface.Items.Count(x => x.Kind is not (DockItemKind.Separator or DockItemKind.MinimizedWindow)),
                        drawn = _dock.Window.Surface.Items.Select((x, i) => (x, i)).Count(p => p.x.Kind is not (DockItemKind.Separator or DockItemKind.MinimizedWindow) && _dock.Window.Surface.Scene?.PictureDrawn(p.i) == true),
                    } : null,
                    mapped = Common.Inks.Mapped,
                });
            }
            case "lens-trace" when arg is "on" or "off" or "take":
            {
                // "lens-trace on": each plate's presence noted at each drawing (the glass coming by its lens, 2026-10-23);
                // "lens-trace take" returns them and clears; "off" stops.
                if (arg == "on") { Frostbar.Platform.Dwm.LensCapture.Tracing = true; Frostbar.Platform.Dwm.LensCapture.Current?.TakeTrace(); return "ok"; }
                var steps = Frostbar.Platform.Dwm.LensCapture.Current?.TakeTrace();
                if (arg == "off") Frostbar.Platform.Dwm.LensCapture.Tracing = false;
                return JsonSerializer.Serialize(steps ?? Array.Empty<object>());
            }
            case "lens-presence":
            {
                // "lens-presence <0..1> [bare]": every shown plate's lens drawn and held at that presence (bare: the content
                // windows see-through, for a photo of the glass alone); "lens-presence off".
                var lc = Frostbar.Platform.Dwm.LensCapture.Current;
                if (lc == null) return "no lens";
                var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bool bare = p.Contains("bare");
                if (p.Length == 0 || p[0] == "off")
                {
                    lc.HoldPresence(null);
                    Frostbar.Platform.Dwm.WindowBackdrop.HoldShadows(null);
                    foreach (var w in _bared) w.Opacity = 1;
                    _bared.Clear();
                    return "ok";
                }
                if (!float.TryParse(p[0], System.Globalization.CultureInfo.InvariantCulture, out float k) || k < 0 || k > 1) return "usage: lens-presence <0..1> [bare] | off";
                lc.HoldPresence(k);
                Frostbar.Platform.Dwm.WindowBackdrop.HoldShadows(k);
                // The window's own opacity, not its layered one: with that at 0 the first photo had the menu's rows over
                // the glass (2026-10-23; WPF drawing the window again with its own is the guess, not seen).
                if (bare)
                    foreach (Window w in Windows)
                        if (w.IsVisible && w.AllowsTransparency && PresentationSource.FromVisual(w) != null && _bared.Add(w)) w.Opacity = 0;
                return "ok";
            }
            case "lens-freeze" when arg is "on" or "off":
                // For photos: the glass back in screen capture, the lens keeping its last drawing; off: left out again.
                Frostbar.Platform.Dwm.LensCapture.Current?.Freeze(arg == "on");
                return "ok";
            case "nc-notices" when MenuBarController.SpecNotices is { } notices:
            {
                // "nc-notices [{"app":…,"title":…,"body":…,"minutes":…},…]": a spec's own notifications (FROSTBAR_E2E_NOTICES=spec).
                // The same notice (app and title) keeps its id from one call to the next, as Windows' do: numbered from 1
                // at each call, a notice come took the id of the one before it, and the list saw that one stay (nc-motion,
                // 2026-10-19).
                notices.Clear();
                foreach (var n in JsonDocument.Parse(arg.Length > 0 ? arg : "[]").RootElement.EnumerateArray())
                {
                    string Of(string key) => n.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
                    int minutes = n.TryGetProperty("minutes", out var m) ? m.GetInt32() : 0;
                    var key = (Of("app"), Of("title"));
                    if (!_specNoticeIds.TryGetValue(key, out uint id)) _specNoticeIds[key] = id = (uint)_specNoticeIds.Count + 1;
                    notices.Add(new Frostbar.Core.Shell.Notice(id, Of("app"), "", DateTimeOffset.Now.AddMinutes(-minutes), Of("title"), Of("body")));
                }
                return $"{notices.Count} notices";
            }
            case "glass-light":
                // Liquid Glass's lights and knobs (2026-10-18): plates lit by the pointer, presses' lights, knobs turned to glass.
                return JsonSerializer.Serialize(new
                {
                    pointLights = Frostbar.Platform.Dwm.Plate.PointLightsOn,
                    pressStarted = PressLight.Started, pressSpreading = PressLight.Spreading,
                    knobsPressed = GlassKnob.Pressed, knobsHeld = GlassKnob.Held, knobsForced = GlassKnob.Forced,
                    knobsStretched = GlassKnob.Stretched, knobStretchMost = Math.Round(GlassKnob.StretchMost, 3),
                    // A press's light spilling onto the glass near by (2026-10-23): pieces reached, composition spills started, WPF glows showing.
                    spilled = PressLight.Spilled, spills = Frostbar.Platform.Dwm.Plate.Spills, spillGlows = PressSpill.Showing,
                });
            case "glass-spill":
            {
                // "glass-spill [ms]": the pieces of glass a press's light reached within that many ms (default 1000), in
                // every window: its title, its piece, how strong, where (its pixels), how long ago.
                long within = long.TryParse(arg, out var ms) ? ms : 1000;
                return JsonSerializer.Serialize(Frostbar.Platform.Dwm.WindowBackdrop.Spilled(within)
                    .Select(x => new { window = x.window, key = x.key, strength = Math.Round(x.strength, 3), x = Math.Round(x.x), y = Math.Round(x.y), ago = x.ago }));
            }
            case "press-at":
            {
                // "press-at <x> <y> [holdMs]" (screen pixels): a press there as the shell shows one, no click (nothing runs):
                // the part's light, its light spilling onto the glass near by, and a part that gives sinking for holdMs
                // (default 300) and springing back. For tests and photos of parts a click would change (the Control Center's).
                var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2 || !double.TryParse(p[0], System.Globalization.CultureInfo.InvariantCulture, out double x)
                    || !double.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out double y)) return "usage: press-at <x> <y> [holdMs]";
                double hold = p.Length > 2 && double.TryParse(p[2], System.Globalization.CultureInfo.InvariantCulture, out var h) ? h : 300;
                if (PressLight.At(new Point(x, y)) is not { } pressed) return "no window of the shell there";
                var given = GlassBounce.Give(pressed.hit, hold);
                return JsonSerializer.Serialize(new
                {
                    window = pressed.window, lit = pressed.part != null,
                    gives = given == null ? (string?)null : given.GetType().Name + (System.Windows.Automation.AutomationProperties.GetName(given) is { Length: > 0 } n ? ":" + n : ""),
                    depth = given == null ? 1 : GlassBounce.GetDepth(given),
                });
            }
            case "press-scale":
            {
                // "press-scale <x> <y>": the scale of the part with a give under that point now (1: none, or at rest).
                var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 2 || !double.TryParse(p[0], System.Globalization.CultureInfo.InvariantCulture, out double x)
                    || !double.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out double y)) return "usage: press-scale <x> <y>";
                foreach (var w in PressLight.Under(new Point(x, y)))
                {
                    if (!w.IsVisible || PresentationSource.FromVisual(w) == null) continue;
                    var at = w.PointFromScreen(new Point(x, y));
                    if (at.X < 0 || at.Y < 0 || at.X >= w.ActualWidth || at.Y >= w.ActualHeight) continue;
                    // A window with nothing there to hit is passed by, as press-at passes it (the first answer was from one).
                    if (PressLight.HitAt(w, at) is not DependencyObject hit) continue;
                    var chain = new List<string>();
                    for (var d = hit; d != null; d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d)) // a run of text: its block
                    {
                        if (chain.Count < 12) chain.Add(d.GetType().Name);
                        if (d is FrameworkElement fe && GlassBounce.GetDepth(fe) < 1)
                            return JsonSerializer.Serialize(new
                            {
                                window = w.Title, scale = fe.RenderTransform is System.Windows.Media.ScaleTransform st ? Math.Round(st.ScaleX, 4) : 1, depth = GlassBounce.GetDepth(fe),
                                part = fe.GetType().Name, transform = fe.RenderTransform?.GetType().Name,
                            });
                    }
                    return JsonSerializer.Serialize(new { window = w.Title, scale = 1.0, depth = 1.0, hit = string.Join(" < ", chain) });
                }
                return "no window of the shell there";
            }
            case "bar-press" when _bar != null:
            {
                // The menu bar's plate behind the item pressed or open (2026-10-23): which item, shown, presses.
                var (item, shown, presses) = _bar.Window.Plate;
                return JsonSerializer.Serialize(new { item, shown, presses, glass = GlassKnob.Enabled() });
            }
            case "glass-tint":
            {
                // The glass's tint (macOS 27's slider, 2026-10-18): the setting, the Dock's plate as drawn now, and what a
                // menu opened now gets.
                var t = _theme!.Current;
                var menu = Materials.Menu(t, _settings.Appearance, 1);
                var dock = _dock?.Window.Material;
                return JsonSerializer.Serialize(new
                {
                    setting = _settings.Appearance.GlassTint, scale = Frostbar.Core.Shell.GlassTints.Scale(_settings.Appearance.GlassTint),
                    dockTint = dock?.Tint.A, dockEdge = dock?.Edge, menuTint = menu.Tint.A, menuEdge = menu.Edge,
                });
            }
            case "menu-rows" when _bar != null:
            {
                // The open menu's rows: label, enabled, rectangle on the screen (pixels).
                if (_bar.Menus.Popup is not { IsVisible: true } m) return "[]";
                double s = _monitor.Scale;
                return JsonSerializer.Serialize(m.Items.Select((item, i) =>
                {
                    var r = m.RowDip(i);
                    return new { label = item.Label, enabled = item.Enabled, Left = (int)Math.Round(r.Left * s), Top = (int)Math.Round(r.Top * s), Right = (int)Math.Round(r.Right * s), Bottom = (int)Math.Round(r.Bottom * s) };
                }));
            }
            case "hit":
            {
                // "hit <x> <y>" (screen pixels): the shell's window there (its title) and what WPF's hit test finds in it, from
                // the element hit up to the window: what a press at that point would go to.
                var xy = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (xy.Length < 2 || !int.TryParse(xy[0], out int hx) || !int.TryParse(xy[1], out int hy)) return "usage: hit <x> <y>";
                var root = Native.GetAncestor(Native.WindowFromPoint(new POINT { X = hx, Y = hy }), 2 /*GA_ROOT*/);
                var w = Windows.OfType<Window>().FirstOrDefault(x => new System.Windows.Interop.WindowInteropHelper(x).Handle == root);
                if (w == null) return JsonSerializer.Serialize(new { window = (string?)null, hwnd = root.ToInt64(), cls = Native.ClassName(root) });
                var at = w.PointFromScreen(new Point(hx, hy));
                var chain = new List<string>();
                for (var d = w.InputHitTest(at) as DependencyObject; d != null && chain.Count < 24; d = System.Windows.Media.VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
                {
                    string name = d.GetType().Name;
                    if (d is FrameworkElement { Name.Length: > 0 } fe) name += "#" + fe.Name;
                    if (d is UIElement { IsHitTestVisible: false }) name += "(no hit)";
                    if (KeyNav.GetRun(d) != null) name += "(runs)";
                    chain.Add(name);
                }
                return JsonSerializer.Serialize(new { window = w.Title, hwnd = root.ToInt64(), at = new { x = Math.Round(at.X, 1), y = Math.Round(at.Y, 1) }, chain });
            }
            case "press-light":
            {
                // For photos: "press-light <x> <y> <progress>" holds a press's light at that point of the screen (pixels), no
                // click; "press-light off" takes it away.
                if (arg == "off") { PressLight.Let(); return "ok"; }
                var p = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (p.Length < 3 || !double.TryParse(p[0], System.Globalization.CultureInfo.InvariantCulture, out double x)
                    || !double.TryParse(p[1], System.Globalization.CultureInfo.InvariantCulture, out double y)
                    || !double.TryParse(p[2], System.Globalization.CultureInfo.InvariantCulture, out double k)) return "usage: press-light <x> <y> <progress> | off";
                return PressLight.Show(new Point(x, y), k) ?? "nothing to light there";
            }
            case "cc-scale":
            {
                // "cc-scale <k> [<ky>]": Focus's circle held at that scale (as a press's bounce has it; across and down
                // apart with two), no press; "cc-scale off".
                if (_bar?.ControlCenterPanel is not { } cc) return "the Control Center is not open";
                var focus = cc.FocusTile;
                var ks = arg == "off" ? ["1"] : arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                double k = 1, ky = 1;
                if (ks.Length is < 1 or > 2 || !double.TryParse(ks[0], System.Globalization.CultureInfo.InvariantCulture, out k) || k < 0.5 || k > 1.5
                    || !double.TryParse(ks[^1], System.Globalization.CultureInfo.InvariantCulture, out ky) || ky < 0.5 || ky > 1.5) return "usage: cc-scale <0.5..1.5> [<0.5..1.5>] | off";
                focus.RenderTransformOrigin = new Point(0.5, 0.5);
                if (focus.RenderTransform is not System.Windows.Media.ScaleTransform st || st.IsFrozen) focus.RenderTransform = st = new System.Windows.Media.ScaleTransform(1, 1);
                // Moved by Tween in one step: its pieces are placed again at its steps (Tween.Stepped), as under a press.
                Tween.To(st, System.Windows.Media.ScaleTransform.ScaleXProperty, k, 1, Tween.Linear);
                Tween.To(st, System.Windows.Media.ScaleTransform.ScaleYProperty, ky, 1, Tween.Linear);
                return "ok";
            }
            case "cc-layers":
            {
                // "cc-layers <name>…": those layers left out of the Control Center's glass (Plate.LeaveOut); "cc-layers none".
                if (_bar?.ControlCenterPanel is not { } cc) return "the Control Center is not open";
                cc.LeaveOut(arg == "none" ? [] : arg.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                return "ok";
            }
            case "tween-skew":
            {
                // "tween-skew <ms>": every other movement begun before Tween's next frame begun that much earlier (two
                // begun apart, as GlassBounce's across and down were); "tween-skew off".
                double skew = 0;
                if (arg != "off" && (!double.TryParse(arg, System.Globalization.CultureInfo.InvariantCulture, out skew) || skew < 0 || skew > 50)) return "usage: tween-skew <0..50> | off";
                Tween.Skew = skew;
                return "ok";
            }
            case "cc-bounce":
            {
                // Focus's circle let go from a press's scale, as GlassBounce does, no press (nothing runs).
                if (_bar?.ControlCenterPanel is not { } cc) return "the Control Center is not open";
                var focus = cc.FocusTile;
                focus.RenderTransformOrigin = new Point(0.5, 0.5);
                if (focus.RenderTransform is not System.Windows.Media.ScaleTransform st || st.IsFrozen) focus.RenderTransform = st = new System.Windows.Media.ScaleTransform(1, 1);
                Tween.Stop(st, System.Windows.Media.ScaleTransform.ScaleXProperty);
                Tween.Stop(st, System.Windows.Media.ScaleTransform.ScaleYProperty);
                st.ScaleX = st.ScaleY = GlassBounce.Pressed;
                GlassBounce.Release(st);
                return "ok";
            }
            case "cc-trace":
            {
                // "cc-trace on": at each of Tween's frames, Focus's circle's scale across and down and its piece of glass
                // as placed (pixels); "cc-trace off" ends it and returns them.
                if (arg == "on")
                {
                    if (_bar?.ControlCenterPanel is not { } cc) return "the Control Center is not open";
                    _ccTrace.Clear();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Tween.Stepped -= _ccTraceStep;
                    _ccTraceStep = () =>
                    {
                        if (_ccTrace.Count > 2000) return;
                        var st = cc.FocusTile.RenderTransform as System.Windows.Media.ScaleTransform;
                        var g = cc.PlacedPieceOf(cc.FocusTile);
                        _ccTrace.Add(new { ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1), sx = st?.ScaleX, sy = st?.ScaleY, l = g?.L, t = g?.T, w = g?.W, h = g?.H, r = g?.Radius });
                    };
                    Tween.Stepped += _ccTraceStep;
                    return "ok";
                }
                Tween.Stepped -= _ccTraceStep;
                var trace = JsonSerializer.Serialize(_ccTrace);
                _ccTrace.Clear();
                return trace;
            }
            case "tween-trace":
            {
                // "tween-trace on": now and at each of Tween's frames, the settings window's segmented controls' raised
                // parts (place and width as set) and the notification centre's cards going (slide, opacity, margins,
                // padding, height); "tween-trace off" ends it and returns them (what must move as one: Tween.Together).
                if (arg == "on")
                {
                    _tweenTrace.Clear();
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Tween.Stepped -= _tweenTraceStep;
                    _tweenTraceStep = () =>
                    {
                        if (_tweenTrace.Count > 4000) return;
                        double ms = Math.Round(sw.Elapsed.TotalMilliseconds, 1);
                        if (_settingsWindow?.Controls is { } controls)
                            foreach (var (key, c) in controls)
                                if (c is MacControls.Segmented g) _tweenTrace.Add(new { ms, key, x = Math.Round(g.RaisedSet.X, 3), w = Math.Round(g.RaisedSet.Width, 3) });
                        if (_bar?.NotificationCenter is { } nc)
                            foreach (var (id, card) in nc.Leaving)
                                _tweenTrace.Add(new
                                {
                                    ms, card = id, h = Math.Round(card.Height, 3), mt = Math.Round(card.Margin.Top, 3), mb = Math.Round(card.Margin.Bottom, 3),
                                    pt = Math.Round(card.Padding.Top, 3), pb = Math.Round(card.Padding.Bottom, 3),
                                    x = card.RenderTransform is System.Windows.Media.TranslateTransform tt ? Math.Round(tt.X, 3) : 0, o = Math.Round(card.Opacity, 3),
                                });
                    };
                    _tweenTraceStep();
                    Tween.Stepped += _tweenTraceStep;
                    return "ok";
                }
                Tween.Stepped -= _tweenTraceStep;
                var traced = JsonSerializer.Serialize(_tweenTrace);
                _tweenTrace.Clear();
                return traced;
            }
            case "glass-knobs":
                // For photos: "glass-knobs <0..1> [stretch]" draws every knob that far turned to glass (and stretched
                // along its way that much, as its speed would), no press; "glass-knobs off".
                var knobArgs = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                GlassKnob.ForcedStretch = knobArgs.Length > 1 && double.TryParse(knobArgs[1], System.Globalization.CultureInfo.InvariantCulture, out double forcedStretch) ? forcedStretch : null;
                GlassKnob.Forced = arg == "off" || knobArgs.Length == 0 ? null : double.TryParse(knobArgs[0], System.Globalization.CultureInfo.InvariantCulture, out double forced) ? Math.Clamp(forced, 0, 1) : null;
                return "ok";
            case "welcome-open": OpenWelcome(); return "ok";
            case "welcome-state": return JsonSerializer.Serialize(WelcomeState());
            case "windows":
                // Our WPF windows still open, by type (a closed menu that lingers shows here).
                return string.Join(", ", Windows.Cast<Window>().GroupBy(w => w.GetType().Name).OrderBy(g => g.Key).Select(g => $"{g.Key} {g.Count()} ({g.Count(w => w.IsVisible)} shown)"));
            case "poof" when _theme != null:
            {
                // "poof <x> <y>" (pixels): the puff of an item dragged off the Dock, for screenshots.
                var a = arg.Split(' ');
                Dock.Poof.At(new Point(int.Parse(a[0]) / _monitor.Scale, int.Parse(a[1]) / _monitor.Scale), _theme.Current.Dark);
                return "ok";
            }
            case "animations" when _theme != null:
                // "animations on|off|system": the shell's animations for an experiment (not saved; the settings put it back at start).
                _theme.AnimationsOverride = arg switch { "on" => true, "off" => false, _ => null };
                _theme.Refresh();
                return _theme.Current.Animations ? "on" : "off";
            case "dock-autohide" when _dock != null: return _dock.AutoHideState();
            case "dock-autohide-set" when _dock != null:
                // As the settings window's switch does (and saved like it): for screenshots.
                _settings.Dock.AutoHide = arg == "on";
                ((Settings.ISettingsHost)this).Apply(Settings.SettingsArea.Dock);
                return _dock.AutoHideState();
            case "min-rect" when _dock != null: return _dock.MinRectState();
            case "dock-bouncing" when _dock != null:
                // Cheap enough to poll every few milliseconds while timing a bounce.
                return _dock.Window.Surface.Bouncing.Any() ? string.Join('|', _dock.Window.Surface.Bouncing) : "none";
            case "dock-light" when _dock != null:
            {
                // Cheap: render loop subscribed, magnification strength, hovered item, trash checks, trash full, and where
                // the pointer is along the row as the layout last used it: its screen x, its y on a side (-1: away).
                var sf = _dock.Window.Surface;
                double rowStart = _dock.Window.Edge == DockEdge.Bottom ? _dock.Window.Left : _dock.Window.Top;
                int pointerPx = sf.PointerX is double px ? (int)Math.Round((rowStart + px) * _monitor.Scale) : -1;
                // And (2026-10-23) the icon pressed: its scale and its shade as drawn, and the hops and landings that lit the plate.
                var (_, pressScale, _) = sf.PressState;
                var ic = System.Globalization.CultureInfo.InvariantCulture;
                return $"{(sf.Animating ? 1 : 0)} {sf.Strength.ToString("0.###", ic)} {sf.Hovered} {_dock.Bin.Checks} {(_dock.Bin.Full ? 1 : 0)} {pointerPx}"
                    + $" {pressScale.ToString("0.0000", ic)} {(sf.Scene?.ShadeShown ?? 0).ToString("0.000", ic)} {sf.Hopped} {sf.Landed}";
            }
            case "dock-launches" when _dock != null:
                // Apps opened from elsewhere (2026-10-23): told, brought forward, bounced, given an item, expired, mismatched,
                // the last one, the items starting, the bounces under way, the recent apps holding their place.
                return JsonSerializer.Serialize(_dock.LaunchState());
            case "dock-motion" when _dock != null:
            {
                // The finer movements (2026-10-17): the item flying into its slot after a drop (and whether its slot
                // shows it), the pictures of items gone shrinking with their rooms, the plate's length along the row (DIPs).
                var sf = _dock.Window.Surface;
                var scene = sf.Scene;
                int flying = sf.Flying is { } fk ? sf.Items.Select((x, i) => (x, i)).FirstOrDefault(q => q.x.Key == fk, (null!, -1)).i : -1;
                return JsonSerializer.Serialize(new
                {
                    animating = sf.Animating, strength = sf.Strength, flying = sf.Flying, flyingShown = flying >= 0 && scene?.IconShown(flying) == true,
                    leaving = scene?.LeavingState().Select(l => new { l.key, l.shown, l.size }).ToArray() ?? [],
                    plate = _dock.Window.Edge == DockEdge.Bottom ? sf.PlateRect.Width : sf.PlateRect.Height,
                    // The marks' small movements begun (B7, 2026-10-19): progress glides, badges popped, running dots faded in.
                    glides = scene?.MarkMoves.Glides, pops = scene?.MarkMoves.Pops, dotsIn = scene?.MarkMoves.DotsIn,
                    poofs = Dock.Poof.Puffs,
                    // Docks come out of a newly chosen edge (B14), and how far out of sight this one is now (DIPs).
                    cameIn = DockController.CameIn, slide = Math.Round(_dock.Window.Slide, 1),
                });
            }
            case "dock-press" when _dock != null:
                // "dock-press <index>": the item shown pressed (-1: none), for pictures; nothing is clicked.
                _dock.Window.Surface.ShowPressed(int.Parse(arg));
                return "ok";
            case "dock-click" when _dock != null:
            {
                // "dock-click <index> [left|middle]"
                var a = arg.Split(' ');
                _dock.Click(int.Parse(a[0]), a.Length > 1 && a[1] == "middle" ? System.Windows.Input.MouseButton.Middle : System.Windows.Input.MouseButton.Left);
                return "ok";
            }
            case "dock-frames" when _dock != null:
            {
                // "dock-frames start" keeps the Dock's render loop running and records frame intervals; "stop" reports them.
                var surface = _dock.Window.Surface;
                if (arg == "start") { _frames.Clear(); surface.Frame -= RecordFrame; surface.Frame += RecordFrame; surface.Measure(true); return "ok"; }
                surface.Frame -= RecordFrame;
                surface.Measure(false);
                var sorted = _frames.OrderBy(x => x).ToArray();
                double P(double q) => sorted.Length == 0 ? 0 : sorted[Math.Min(sorted.Length - 1, (int)Math.Ceiling(q * sorted.Length) - 1)];
                return JsonSerializer.Serialize(new { frames = sorted.Length, p50 = P(0.5), p95 = P(0.95), p99 = P(0.99), max = sorted.LastOrDefault(), slow = sorted.Count(x => x > 25) });
            }
            case "dock-trace" when _dock != null:
            {
                // "dock-trace on" records every layout the Dock makes; "off" returns them (2026-10-23, dock-shiver): when (ms),
                // the pointer and the magnification it used, the plate and each slot along the row in screen pixels.
                var surface = _dock.Window.Surface;
                surface.LaidOut -= TraceLayout;
                if (arg == "on") { _layouts.Clear(); _layoutClock.Restart(); surface.LaidOut += TraceLayout; return "ok"; }
                return JsonSerializer.Serialize(_layouts);
            }
            case "threads":
            {
                // Native ids of our threads, and of the runtime's: the finalizer (found by finalizing an object) and a
                // thread-pool worker.
                uint finalizer = 0, pool = 0;
                FinalizerProbe.Make(id => finalizer = id);
                GC.Collect();
                GC.WaitForPendingFinalizers();
                using (var done = new ManualResetEventSlim())
                {
                    ThreadPool.UnsafeQueueUserWorkItem(_ => { pool = Native.GetCurrentThreadId(); done.Set(); }, null);
                    done.Wait(1000);
                }
                return $"ui {Native.GetCurrentThreadId()} tray {_tray?.ThreadId ?? 0} ticker {_ticker?.ThreadId ?? 0} ime {_bar?.ImeThreadId ?? 0} log {Log.ThreadId} finalizer {finalizer} pool {pool}";
            }
            case "stats": return JsonSerializer.Serialize(Stats.Snapshot()); // event counters since start
            case "ticker-jobs" when _ticker != null: // each background job's runs and total time
                return string.Join("\n", _ticker.Costs().Select(c => $"{c.name} {c.runs} runs {c.ms:0.0} ms ({(c.runs > 0 ? c.ms * 1000 / c.runs : 0):0} us each)"));
            case "gc-stats":
            {
                // Allocation and collection counters (diagnostics: what a sweep costs the GC).
                var info = GC.GetGCMemoryInfo();
                return $"{GC.GetTotalAllocatedBytes(false)} {GC.CollectionCount(0)} {GC.CollectionCount(1)} {GC.CollectionCount(2)} " +
                       $"{info.PauseTimePercentage.ToString(System.Globalization.CultureInfo.InvariantCulture)} {GC.GetTotalPauseDuration().TotalMilliseconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}";
            }
            case "gc":
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                return GC.GetTotalMemory(false).ToString();
            case "launch":
            {
                // Tests: start a program the way the Dock will (right after a click on our window, so Windows lets the
                // new window take the foreground). "launch <exe>|<arguments>".
                var a = arg.Split('|', 2);
                Menus.CommandRunner.Launch(a[0], a.Length > 1 ? a[1] : null);
                return "ok";
            }
            case "launch-from":
            {
                // Tests: "launch-from <grid|search|stack|open> <target>|<arguments>": an app opened as that source opens one
                // (2026-10-23): the Dock told first (it bounces it, gives it an item, or brings it forward), then started
                // unless brought forward. A spec's shell: the test target's programs only (SpecGuard), as the sources are.
                var sp = arg.Split(' ', 2);
                if (sp.Length < 2) return "usage: launch-from <source> <target>|<arguments>";
                var a = sp[1].Split('|', 2);
                string program = a[0].StartsWith(@"shell:AppsFolder\", StringComparison.OrdinalIgnoreCase) ? ShellApi.LinkTarget(a[0]) ?? a[0]
                    : a[0].EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? Platform.Apps.OpenWith.Resolve(a[0])?.path ?? a[0] : a[0];
                if (_onlyAppsIn is { } only && !SpecGuard.Allows(only, program, isDesktop: false)) return "guarded";
                bool forward = Menus.CommandRunner.Opening?.Invoke(a[0], sp[0]) == true;
                if (!forward) Menus.CommandRunner.Launch(a[0], a.Length > 1 ? a[1] : null);
                return forward ? "forward" : "launched";
            }
            case "front-eval" when _front != null:
                // Tests: treat this window as the one in front (an elevated window cannot be brought forward by an
                // unelevated test runner: UIPI blocks its focus calls and injected clicks).
                _front.Evaluate(new IntPtr(long.Parse(arg)));
                goto case "front-show";
            case "front":
                _front?.Refresh();
                goto case "front-show";
            case "front-show":
                var f = _front?.Current;
                return JsonSerializer.Serialize(f == null ? null : new { f.DisplayName, f.ExeName, f.AppId, f.IsDesktop, f.Elevated, hwnd = f.Hwnd.ToInt64() });
            default: return HandleCommand(line);
        }
    }

    static IEnumerable<MenuItem> Flatten(IEnumerable<MenuItem> items) =>
        items.SelectMany(i => new[] { i }.Concat(Flatten(i.Children ?? [])));

    object? MenuState()
    {
        if (_bar == null) return null;
        static object R(RECT r) => new { r.Left, r.Top, r.Right, r.Bottom };
        static object? Popup(Menus.MenuPopup? p) => p == null ? null : new
        {
            hwnd = p.Handle.ToInt64(),
            rect = R(Native.FrameBounds(p.Handle)),
            menu = p.Menu.Id,
            highlight = p.Highlight,
            items = p.Items.Select((i, n) => new { i.Id, i.Label, i.Enabled, shortcut = i.Shortcut?.ToString(), i.Hint, submenu = i.Children != null, rect = i.IsSeparator ? null : R(p.RowPx(n)) }),
            icons = p.IconsShown, // the rows' icons that show (Recent Items)
        };
        var m = _bar.Menus;
        var p = m.Popup;
        double s = _monitor.Scale;
        return new
        {
            open = m.OpenIndex == int.MinValue ? (int?)null : m.OpenIndex,
            lastOpenMs = m.LastOpenMs,
            lastInputToFrameMs = m.LastInputToFrameMs,
            titles = _bar.Window.Titles.Select((t, i) =>
            {
                var r = _bar.Window.TitleRect(i);
                return new { title = t, left = (int)Math.Round(r.Left * s), top = (int)Math.Round(r.Top * s), right = (int)Math.Round(r.Right * s), bottom = (int)Math.Round(r.Bottom * s) };
            }),
            popup = Popup(p),
            // The open submenu (Window ▸ Move & Resize), and the row of the menu it belongs to.
            child = Popup(m.Child),
            childRow = p?.OpenChild ?? -1,
            panel = m.Panel == null ? null : new { hwnd = m.Panel.Handle.ToInt64(), rect = R(Native.FrameBounds(m.Panel.Handle)) },
        };
    }

    object? DockState()
    {
        if (_dock == null) return null;
        static object R(RECT r) => new { r.Left, r.Top, r.Right, r.Bottom };
        var w = _dock.Window;
        var s = w.Surface;
        double k = _monitor.Scale;
        RECT Px(Rect r) => r.IsEmpty ? default : new RECT((int)Math.Round(r.Left * k), (int)Math.Round(r.Top * k), (int)Math.Round(r.Right * k), (int)Math.Round(r.Bottom * k));
        Native.GetWindowRect(w.BackdropHandle, out var backdropRect);
        // Where the blur's plate really is: the backdrop window plus the plate inside it (pixels).
        var bp = w.BackdropPlate;
        object? backdropPlate = bp is { } b
            ? R(new RECT(backdropRect.Left + (int)b.l, backdropRect.Top + (int)b.t, backdropRect.Left + (int)(b.l + b.w), backdropRect.Top + (int)(b.t + b.h)))
            : null;
        var (pictures, stale) = s.Scene?.PictureCount() ?? (0, 0);
        return new
        {
            hwnd = w.Handle.ToInt64(),
            edge = DockFrame.Name(w.Edge), // the screen's edge it stands on (stage 7)
            label = s.Scene?.LabelBox is { } lb ? R(Px(w.ToScreen(lb))) : null, // the hovered item's name (pixels)
            dpi = System.Windows.Media.VisualTreeHelper.GetDpi(w).PixelsPerInchX, // the scale WPF draws the window at
            gpu = new { generation = w.Surfaces.Generation, software = w.Surfaces.Software, pictures, stale },
            rect = R(Native.FrameBounds(w.Handle)),
            visible = Native.IsWindowVisible(w.Handle),
            plate = R(Px(w.ToScreen(s.PlateRect))),
            backdrop = new { hwnd = w.BackdropHandle.ToInt64(), rect = R(backdropRect), visible = Native.IsWindowVisible(w.BackdropHandle), plate = backdropPlate },
            strength = s.Strength,
            hovered = s.Hovered,
            rendering = s.Animating,
            gap = s.GapAt,
            appDrag = s.AppDragOver,
            taskband = _taskband == null ? null : new { windows = _taskband.Windows, received = _taskband.Received },
            // The kept pins' AppIDs asked again after start-up (-1: not yet) and how many differed from those kept.
            pinIds = new { @checked = _dock.PinIdsChecked, differed = _dock.PinIdsDiffered },
            bouncing = s.Bouncing.ToList(),
            trashFull = _dock.Bin.Full,
            trashChecks = _dock.Bin.Checks,
            desktopSwitches = new { count = _dock.DesktopSwitches.count, last = _dock.DesktopSwitches.last }, // to another virtual desktop's window
            items = _dock.Items.Select((it, i) => new
            {
                it.Key, it.Name, kind = it.Kind.ToString(), it.AppId, it.Pinned, it.Running, it.Active, windows = it.Windows.Count,
                elsewhere = it.Windows.Count(x => x.Elsewhere), // on another virtual desktop
                hwnds = it.Windows.Select(x => x.Hwnd),
                icon = _dock.IconSourceOf(it.Key) is { } src && src.StartsWith("stock:", StringComparison.Ordinal) ? src : null,
                marks = s.MarkOf(it.Key) is { } mk ? new { state = mk.State.ToString(), progress = Math.Round(mk.Progress, 3), count = mk.Count, picture = mk.Picture != null } : null,
                rect = R(Px(w.ToScreen(s.SlotRect(i)))),
            }),
            // Minimized windows' pictures (stage 7-2): the thumbnail and the small icon on screen (pixels), and the window
            // above the pair that holds the small icons.
            minimized = s.Scene?.MinimizedState().Select(m => new
            {
                hwnd = m.Hwnd,
                registered = m.Registered,
                shows = m.Shows,
                source = new { width = m.Source.Width, height = m.Source.Height },
                picture = R(new RECT(backdropRect.Left + m.Picture.Left, backdropRect.Top + m.Picture.Top, backdropRect.Left + m.Picture.Right, backdropRect.Top + m.Picture.Bottom)),
                badge = R(new RECT(backdropRect.Left + m.Badge.Left, backdropRect.Top + m.Badge.Top, backdropRect.Left + m.Badge.Right, backdropRect.Top + m.Badge.Bottom)),
                badgeShows = m.BadgeShows,
            }),
            above = w.AboveHandle == IntPtr.Zero ? null : new { hwnd = w.AboveHandle.ToInt64(), visible = Native.IsWindowVisible(w.AboveHandle), rect = R(Native.FrameBounds(w.AboveHandle)) },
            pictured = s.Scene?.EverPictured, // every window a picture was made of since the Dock was made
            mayShowWindows = _dock.MayShowWindow == null ? "all" : "guarded",
        };
    }

    readonly List<double> _frames = new();
    void RecordFrame(double ms) => _frames.Add(ms);

    readonly List<object> _layouts = new();
    readonly System.Diagnostics.Stopwatch _layoutClock = new();
    void TraceLayout(Frostbar.Core.Dock.DockLayout l, double? pointer)
    {
        if (_dock == null || _layouts.Count >= 20_000) return;
        double s = _monitor.Scale, start = _dock.Window.Edge == DockEdge.Bottom ? _dock.Window.Left : _dock.Window.Top;
        int Px(double dip) => (int)Math.Round((start + dip) * s);
        _layouts.Add(new
        {
            ms = Math.Round(_layoutClock.Elapsed.TotalMilliseconds, 2),
            p = pointer is double at ? Px(at) : (int?)null,
            k = Math.Round(_dock.Window.Surface.Strength, 3),
            plate = new[] { Px(l.PlateLeft), (int)Math.Round(l.PlateWidth * s) },
            slots = l.Slots.Select(x => new[] { Px(x.Left), (int)Math.Round(x.Size * s) }).ToArray(),
        });
    }

    /// <summary>A WPF element's rectangle on screen in pixels, or null while it is not shown.</summary>
    static object? ScreenPx(FrameworkElement? e)
    {
        if (e == null || !e.IsVisible || PresentationSource.FromVisual(e) == null) return null;
        var p = e.PointToScreen(new Point(0, 0));
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(e);
        return new { Left = (int)Math.Round(p.X), Top = (int)Math.Round(p.Y), Right = (int)Math.Round(p.X + e.ActualWidth * dpi.DpiScaleX), Bottom = (int)Math.Round(p.Y + e.ActualHeight * dpi.DpiScaleY) };
    }

    /// <summary>
    /// <paramref name="e"/>'s rectangle as drawn (screen pixels), its own transforms and its parents' in: ScreenPx's
    /// size is the layout's, so a part shrunk under a press measured its full width (2026-10-18, cc-motion).
    /// </summary>
    static object? ScreenPxDrawn(FrameworkElement? e)
    {
        if (e == null || !e.IsVisible || PresentationSource.FromVisual(e) == null) return null;
        var a = e.PointToScreen(new Point(0, 0));
        var b = e.PointToScreen(new Point(e.ActualWidth, e.ActualHeight));
        return new { Left = (int)Math.Round(Math.Min(a.X, b.X)), Top = (int)Math.Round(Math.Min(a.Y, b.Y)), Right = (int)Math.Round(Math.Max(a.X, b.X)), Bottom = (int)Math.Round(Math.Max(a.Y, b.Y)) };
    }

    /// <summary>Every element under <paramref name="root"/> in the visual tree (diagnostics: a dialog's buttons and lines).</summary>
    static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var d in Descendants(child)) yield return d;
        }
    }

    /// <summary>Diagnostics: the Control Center's item, and its panel when open (values and where to click, in pixels).</summary>
    object ControlCenterState()
    {
        var bar = _bar!;
        double k = _monitor.Scale;
        var item = bar.Window.StatusRect("control-center");
        var p = bar.ControlCenterPanel;
        // "rect" is the round switch; "words" open the tile's view (a spec clicks only those for Wi-Fi: never the switch).
        // On is the switch's state (its Tag), not its colour: the colour takes 0.15 s to change (2026-10-17).
        object Toggle(ControlCenter.ControlCenterPanel.Toggle x) => new
        {
            subtitle = x.Subtitle.Text, on = x.Circle.Tag is true, rect = ScreenPx(x.Circle), words = ScreenPx(x.Subtitle),
            color = x.Circle.Background is System.Windows.Media.SolidColorBrush b ? $"#{b.Color.A:X2}{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}" : null,
        };
        return new
        {
            open = p != null,
            item = new { Left = (int)Math.Round(item.Left * k), Top = (int)Math.Round(item.Top * k), Right = (int)Math.Round(item.Right * k), Bottom = (int)Math.Round(item.Bottom * k) },
            lastOpenMs = bar.LastControlCenterOpenMs,
            panel = p == null ? null : ScreenPx(p),
            wifi = p == null ? null : Toggle(p.Wifi),
            bluetooth = p == null ? null : Toggle(p.Bluetooth),
            pieces = p?.PlacedPieces?.Count, // macOS 27's form: the glass pieces placed
            darkMode = p == null ? null : Toggle(p.DarkMode),
            focus = p == null ? null : ScreenPx(p.FocusTile),
            brightness = p == null ? null : new { shown = p.BrightnessSlider.IsVisible, value = p.BrightnessSlider.Value, track = ScreenPx(p.BrightnessSlider) },
            volume = p == null ? null : new { value = p.VolumeSlider.Value, track = ScreenPx(p.VolumeSlider) },
            output = p == null ? null : ScreenPx(p.OutputLink),
            // crossfades: songs changed while open, faded across; pops: the play button's new glyph popping in (B8).
            media = p == null ? null : new { shown = p.ShowsMedia, title = p.ShowsMedia ? p.MediaTitle : "", button = p.ShowsPause ? "pause" : "play", playPause = ScreenPx(p.PlayPauseButton), crossfades = p.MediaCrossfades, pops = p.GlyphPops },
            view = ViewState(p?.CurrentView),
            viewGlass = ViewGlass(p), // the open view's piece of glass (screen pixels): its rows must sit inside it
            // macOS 27's motion (2026-10-18): appearing (materializing), the content's own opacity, all the glass's
            // bounds (screen pixels), and the glass growing into a view or back (morph).
            appearing = p?.Appearing, contentOpacity = (p?.Content as UIElement)?.Opacity, glassBounds = GlassBounds(p),
            morphing = p?.Morphing, morph = p?.MorphGlass is { } mg ? ScreenPx(mg) : null,
            focusDrawn = p == null ? null : ScreenPxDrawn(p.FocusTile), // as drawn: a press shrinks it (GlassBounce)
            listeners = bar.Listeners(),
            // A theme change: the panel in the new colours took the old one's place (where it stood, its view open).
            swaps = bar.ControlCenterSwaps, viewKey = p?.CurrentViewKey, handle = p == null ? 0 : (long)p.Handle,
            // The keys (D2): the part they are on (its name), their moves and runs since the start.
            keyed = p?.KeyedPart is { } kp ? System.Windows.Automation.AutomationProperties.GetName(kp) : null, keyMoves = Menus.PanelPopup.KeyMoves, keyRuns = Menus.PanelPopup.KeyRuns,
            shellDark = _theme?.Current.Dark, // the shell's appearance now (a spec tells a theme change from none by it)
            // The sliders as drawn (B1: a value changed from outside slides there), and lists' rows moving (B2).
            volumeDrawn = p?.VolumeSlider.Drawn, rowsMoving = Common.ListMotion.Moving, listShows = Common.ListMotion.Shows,
        };
    }

    /// <summary>The bounds of all the pieces of glass a panel has placed (screen pixels), or null.</summary>
    static object? GlassBounds(Menus.PanelPopup? p)
    {
        if (p?.PlacedPieces is not { Count: > 0 } g || !Native.GetWindowRect(p.Handle, out var w)) return null;
        float l = g.Min(x => x.L), t = g.Min(x => x.T), r = g.Max(x => x.L + x.W), b = g.Max(x => x.T + x.H);
        return new { Left = w.Left + (int)l, Top = w.Top + (int)t, Right = w.Left + (int)r, Bottom = w.Top + (int)b };
    }

    static object? ViewGlass(ControlCenter.ControlCenterPanel? p)
    {
        if (p?.CurrentView == null || p.PlacedPieces is not { Count: > 0 } g || !Native.GetWindowRect(p.Handle, out var w)) return null;
        var x = g[0];
        return new { Left = w.Left + (int)x.L, Top = w.Top + (int)x.T, Right = w.Left + (int)(x.L + x.W), Bottom = w.Top + (int)(x.T + x.H) };
    }

    /// <summary>
    /// Diagnostics: the settings window (its page, where its sidebar items and controls are, their values), what the
    /// settings say now, and the Dock's sizes as it uses them.
    /// </summary>
    object WelcomeState()
    {
        var w = _welcome;
        return new
        {
            open = w != null,
            pending = _welcomePending,
            hwnd = w == null ? 0 : new WindowInteropHelper(w).Handle.ToInt64(),
            step = w?.Step,
            turns = w?.Turns ?? 0, // steps that came in moving from their side (B13)
            title = w?.Title,
            window = w == null ? null : ScreenPx(w),
            controls = w?.Controls.ToDictionary(x => x.Key, x => x.Value switch
            {
                PillSwitch p => (object)new { kind = "switch", on = p.IsOn, enabled = p.IsEnabled, rect = ScreenPx(p) },
                MacControls.Segmented g => new { kind = "segments", index = g.Index, parts = g.Parts.Select(y => ScreenPx(y)).ToArray() },
                _ => new { kind = x.Value.GetType().Name, rect = ScreenPx(x.Value) },
            }),
            welcomed = _settings.General.Welcomed,
            hideTaskbar = _settings.General.HideWindowsTaskbar,
            language = _settings.General.Language,
        };
    }

    object AppearanceState()
    {
        var a = _settings.Appearance;
        var last = _schedule?.Last;
        var store = WallpaperStore.Current;
        return new
        {
            schedule = a.DarkSchedule, city = a.City, darkFrom = a.DarkFrom, darkUntil = a.DarkUntil,
            now = _schedule?.Clock().ToString("o"),
            last = last is { } l ? new { dark = l.Dark, since = l.Since.ToString("o"), next = l.Next.ToString("o") } : null,
            switches = _schedule?.Switches ?? 0,
            specDark = _specDark,
            windowsDark = _specDark ?? ThemeService.WindowsDark(),
            wallpaper = new { spec = store is SpecWallpaper, current = store.Current(), fit = store.Fit().ToString(), writes = store.Writes, paired = _settings.Wallpaper.Paired, changes = _schedule?.WallpaperChanges ?? 0 },
        };
    }

    object SettingsState()
    {
        var w = _settingsWindow;
        object Control(FrameworkElement c) => c switch
        {
            MacSlider s => new { kind = "slider", value = s.Value, min = s.Minimum, max = s.Maximum, enabled = s.IsEnabled, rect = ScreenPx(s), knob = KnobPx(s) },
            PillSwitch p => new { kind = "switch", on = p.IsOn, enabled = p.IsEnabled, rect = ScreenPx(p) },
            MacControls.Segmented g => new { kind = "segments", index = g.Index, rect = ScreenPx(g), parts = g.Parts.Select(x => ScreenPx(x)).ToArray(), raised = Math.Round(g.RaisedAt.X, 1) },
            MacControls.PopUp u => new { kind = "popup", index = u.Index, open = u.IsOpen, rect = ScreenPx(u), rows = u.IsOpen ? u.Rows.Select(x => ScreenPx(x)).ToArray() : [], view = u.IsOpen ? ScreenPx(u.ListView) : null, labels = u.Labels, opacity = Math.Round(u.ListOpacity, 2), glass = u.OnGlass },
            MacControls.MenuButton m => new { kind = "menu", open = m.IsOpen, rect = ScreenPx(m), rows = m.IsOpen ? m.Rows.Select(x => ScreenPx(x)).ToArray() : [] },
            // Buttons, rows that open a page or take a click, texts the specs read.
            System.Windows.Controls.Button b => new { kind = "button", text = b.Content as string, enabled = b.IsEnabled, rect = ScreenPx(b) },
            System.Windows.Controls.TextBlock t => new { kind = "text", text = t.Text, rect = ScreenPx(t) },
            _ => new { kind = c.GetType().Name, rect = ScreenPx(c) },
        };
        static object? KnobPx(MacSlider s)
        {
            if (!s.IsVisible || PresentationSource.FromVisual(s) == null) return null;
            var p = s.PointToScreen(s.KnobCentre);
            return new { X = (int)Math.Round(p.X), Y = (int)Math.Round(p.Y) };
        }
        var d = _settings.Dock;
        return new
        {
            open = w != null,
            hwnd = w == null ? 0 : new WindowInteropHelper(w).Handle.ToInt64(),
            page = w?.Page,
            window = w == null ? null : ScreenPx(w),
            sidebar = w?.SidebarItems.ToDictionary(x => x.Key, x => ScreenPx(x.Value)),
            sidebarView = w == null ? null : ScreenPx(w.SidebarView),
            account = w?.AccountItem is { } accountRow ? ScreenPx(accountRow) : null, // the sidebar's account row (2026-10-22)
            // The window as glass (2026-10-22, the user's "C"): its look, the backdrop DWM has for it, whether it has a
            // system menu (DWM's caption buttons through the glass), the toolbar the page scrolls under, and where the
            // page's first part is (under the toolbar once scrolled).
            glass = w == null ? null : new { look = w.Look, backdrop = w.Backdrop, sysMenu = w.SystemMenu, toolbar = ScreenPx(w.Toolbar), band = w.BandOpacity, first = w.FirstPart is { } fp ? ScreenPx(fp) : null },
            scrolling = w?.Scrolling == true,
            // The page's list (2026-10-19, B4–B6): its offset, its stretch past an end, its scroll bar's opacity; the
            // glides the code asked for, stretches and bars shown since the start.
            pageScroll = w == null ? null : new
            {
                offset = Math.Round(w.PageScroll.Offset, 2), stretched = Math.Round(w.PageScroll.Stretched, 2), bar = Math.Round(w.PageScroll.Bar, 2), barTakes = w.PageScroll.BarTakes,
                // The bar and its thumb on the screen, and the page's length past its view (2026-10-22: the track's click).
                barRect = w.PageBar.Bar is { IsVisible: true } pb ? ScreenPx(pb) : null, thumb = w.PageBar.Thumb is { IsVisible: true } pt ? ScreenPx(pt) : null,
                scrollable = Math.Round(w.PageBar.Scrollable, 1),
            },
            codeGlides = SmoothScroll.CodeGlides, stretches = SmoothScroll.Stretches, barShows = MacScrollBar.Shows,
            // B12: pages faded in over the one before, the new page's opacity now; segment slides gone on across a page made again.
            pageFades = w?.PageFades ?? 0, pageOpacity = w == null ? 0 : Math.Round(w.PageOpacity, 2), segmentsContinued = MacControls.Segmented.Continued,
            search = w == null ? null : new { field = ScreenPx(w.SearchField), query = w.Query, found = w.Found.Select(x => ScreenPx(x)).ToArray() },
            controls = w?.Controls.ToDictionary(x => x.Key, x => Control(x.Value)),
            close = w == null ? null : ScreenPx(w.CloseButton),
            zoom = w == null ? null : ScreenPx(w.ZoomButton), zoomed = w?.Zoomed ?? false, // E4
            // The toolbar's arrows where they go somewhere (null: dim). 2026-10-22: before, only a sub-page's back arrow.
            back = w is { BackButton.IsHitTestVisible: true } ? ScreenPx(w.BackButton) : null,
            forward = w is { ForwardButton.IsHitTestVisible: true } ? ScreenPx(w.ForwardButton) : null,
            listeners = _bar?.Listeners(),
            settings = new
            {
                iconSize = d.IconSize, magnifiedSize = d.MagnifiedSize, magnification = d.Magnification, indicators = d.ShowIndicators, autoHide = d.AutoHide,
                spacing = d.Spacing, padding = d.Padding, liquidGlass = _settings.Appearance.LiquidGlass,
                theme = _settings.Appearance.Theme, reduceMotion = _settings.Appearance.ReduceMotion,
                corners = new { _settings.HotCorners.TopLeft, _settings.HotCorners.TopRight, _settings.HotCorners.BottomLeft, _settings.HotCorners.BottomRight, _settings.HotCorners.Modifier, _settings.HotCorners.DwellMs },
                shortcuts = _settings.HotCorners.Shortcuts,
                appGrid = new { _settings.AppGrid.IconSize, _settings.AppGrid.Columns, _settings.AppGrid.Rows },
            },
            dock = _dock == null ? null : new { baseSize = _dock.Window.Surface.BaseSize, maxSize = _dock.Window.Surface.MaxSize, magnify = _dock.Window.Surface.Magnify, animations = _dock.Window.Surface.Animations, fitted = _dock.Fitted },
            reservation = _dockBar == null ? 0 : _dockBar.SizeDip,
            dark = _theme?.Current.Dark,
            animations = _theme?.Current.Animations,
            // The foreground tracker's listeners: the menu bar and the Dock, however often they are made again.
            frontListeners = _front?.Listeners ?? 0,
            // The settings macOS has and Frostbar lacked (2026-10-22), as the shell has them now.
            added = new
            {
                accent = _theme == null ? null : $"#{_theme.Current.Accent.R:X2}{_theme.Current.Accent.G:X2}{_theme.Current.Accent.B:X2}",
                accentChoice = _settings.Appearance.Accent,
                reduceMotion = ReduceMotion.On, calmed = ReduceMotion.Calmed,
                contrast = Materials.HighContrastEdges, material = _settings.Appearance.Material,
                scrollAlways = MacScrollBar.AlwaysShown, jumpToSpot = MacScrollBar.JumpToSpot, trackPages = MacScrollBar.TrackClicks.Pages, trackSpots = MacScrollBar.TrackClicks.Spots,
                batteryPercent = _bar?.Window.BatteryPercentShown, colons = _bar?.Window.ColonsShown, clock = _bar?.Window.ClockText,
                recentCount = _settings.MenuBar.RecentCount, recentWanted = _recentDocs?.Wanted ?? 0,
                snap = WindowSnap.On(), snapSpec = WindowSnap.Spec, snapWrites = WindowSnap.Writes,
                desktopItems = DesktopItems.Shown(), desktopSpec = DesktopItems.Spec, desktopWrites = DesktopItems.Writes,
                sounds = UiSounds.Played, soundsSilent = UiSounds.Silent, soundEffects = _settings.Sound.InterfaceSounds, volumeFeedback = _settings.Sound.VolumeFeedback,
                searchHidden = _settings.Search.Hidden, tileMargins = _settings.Window.TileMargins,
                account = (_account ?? ((Settings.ISettingsHost)this).Account).Name,
            },
        };
    }

    /// <summary>Diagnostics: a menu bar item's panel holding a view (Wi-Fi, Sound), and where the items are.</summary>
    object ViewPanelState()
    {
        var bar = _bar!;
        double k = _monitor.Scale;
        object? Item(string key)
        {
            var r = bar.Window.StatusRect(key);
            return r.IsEmpty || r.Width <= 0 ? null : new { Left = (int)Math.Round(r.Left * k), Top = (int)Math.Round(r.Top * k), Right = (int)Math.Round(r.Right * k), Bottom = (int)Math.Round(r.Bottom * k) };
        }
        var panel = bar.Menus.Panel as ControlCenter.ViewPanel;
        return new
        {
            open = panel != null,
            anyPanel = bar.Menus.Panel != null,
            anyPanelRect = bar.Menus.Panel == null ? null : ScreenPx(bar.Menus.Panel),
            wifiItem = Item("wifi"),
            volumeItem = Item("volume"),
            batteryItem = Item("battery"),
            panel = panel == null ? null : ScreenPx(panel),
            view = ViewState(panel?.View),
            listeners = bar.Listeners(),
        };
    }

    /// <summary>A view's contents (counts, ids) and its back arrow. The joined network's name is for the specs to compare, not to print.</summary>
    /// <summary>Diagnostics: the notification centre (open or not, its notifications and where their cards and × are, the calendar's month).</summary>
    // A spec's own notices' ids, by app and title (diagnostics "nc-notices").
    readonly Dictionary<(string app, string title), uint> _specNoticeIds = new();

    object NotificationCenterState()
    {
        var bar = _bar!;
        var p = bar.NotificationCenter;
        double k = _monitor.Scale;
        var item = bar.Window.StatusRect("clock");
        return new
        {
            open = p != null,
            item = new { Left = (int)Math.Round(item.Left * k), Top = (int)Math.Round(item.Top * k), Right = (int)Math.Round(item.Right * k), Bottom = (int)Math.Round(item.Bottom * k) },
            panel = p == null ? null : ScreenPx(p),
            access = p?.AccessProblem,
            pieces = p?.PlacedPieces?.Count, // macOS 27's form: the glass pieces placed (cards, the calendar, the capsules)
            appearing = p?.Appearing, contentOpacity = (p?.Content as UIElement)?.Opacity, glassBounds = GlassBounds(p), // materializing (2026-10-18)
            listed = p?.ListedIds, // what the list shows (notices: what was read)
            notices = p?.Shown.Select(n => new
            {
                id = n.Id, app = n.App, title = n.Title,
                card = p.Cards.TryGetValue(n.Id, out var c) ? ScreenPx(c) : null,
                dismiss = p.DismissButtons.TryGetValue(n.Id, out var d) ? ScreenPx(d) : null,
            }).ToArray(),
            clearAll = p == null ? null : ScreenPx(p.ClearAllButton),
            // Cards dismissed, on their way out (2026-10-17): where each is (it shrinks) and how faint.
            leaving = p?.Leaving.Select(kv => new { id = kv.Key, rect = ScreenPx(kv.Value), opacity = Math.Round(kv.Value.Opacity, 2) }).ToArray(),
            month = p?.Month.ToString("yyyy-MM"),
            // 2026-10-19: what the list showed as the panel was made (A3: the last read at once), the apps' "N more", and
            // the lists' rows moving (B3: a notification grows in, an app's cards open and close).
            listedAtOpen = p?.ListedAtOpen, more = p?.MoreButtons.ToDictionary(kv => kv.Key, kv => ScreenPx(kv.Value)),
            listShows = Common.ListMotion.Shows, rowsMoving = Common.ListMotion.Moving,
            last = new { came = Common.ListMotion.Last.Came, went = Common.ListMotion.Last.Went, slid = Common.ListMotion.Last.Slid },
        };
    }

    static object? ViewState(ControlCenter.PanelView? v) => v switch
    {
        null => null,
        ControlCenter.FocusView f => new
        {
            kind = "focus", dnd = f.DoNotDisturbOn, session = f.SessionOn, dndRow = ScreenPx(f.DoNotDisturbRow), back = ScreenPx(f.BackButton),
            sessions = f.SessionRows.ToDictionary(x => x.Key, x => ScreenPx(x.Value)),
        },
        ControlCenter.WifiView w => new
        {
            kind = "wifi", on = w.SwitchOn, scanning = w.Scanning, networks = w.Shown.Count, connected = w.Shown.Count(n => n.Connected),
            secured = w.Shown.Count(n => n.Secured), joined = w.Joined, back = ScreenPx(w.BackButton),
        },
        ControlCenter.BluetoothView b => new
        {
            kind = "bluetooth", on = b.SwitchOn, devices = b.Shown.Select(d => d.Name).ToArray(), connected = b.Shown.Count(d => d.Connected), back = ScreenPx(b.BackButton),
            // Each device: whether it is connected, can be connected here, has a request in progress, and where its row is.
            rows = b.Shown.Select(d => new { name = d.Name, connected = d.Connected, canConnect = d.CanConnect, busy = b.Busy(d), rect = b.Rows.TryGetValue(d.Name, out var r) ? ScreenPx(r) : null }).ToArray(),
            failed = b.Failed,
        },
        ControlCenter.SoundView s => new
        {
            kind = "sound", outputs = s.Shown.Select(o => o.Id).ToArray(), current = s.Shown.FirstOrDefault(o => o.IsDefault)?.Id, volume = s.Volume.Value, back = ScreenPx(s.BackButton),
        },
        _ => new { kind = v.GetType().Name },
    };

    object DiagState()
    {
        static object R(RECT r) => new { r.Left, r.Top, r.Right, r.Bottom };
        var t = _theme?.Current;
        return new
        {
            pid = Environment.ProcessId,
            readyMs = _readyMs,
            profile = AppPaths.Profile,
            taskbar = new
            {
                hidden = _hider?.IsHidden ?? false, strategy = _hider?.Strategy, showing = Taskbar.AnyShowing(_hider?.Strategy ?? "hide"), abm = Taskbar.GetState(),
                position = Taskbar.Position() is { } p ? new { edge = p.Edge, rect = R(p.Rect) } : null, // ABE_*: 0 left, 1 top, 2 right, 3 bottom
            },
            workArea = R(Native.WorkArea()),
            // What the windows were placed for, and how often a change of the displays placed them (replaced) or made them (rebuilt) again.
            displays = new
            {
                messages = _displayMessages, replaced = _displaysReplaced, rebuilt = _displaysRebuilt, settling = _displaySettle?.IsEnabled ?? false, resumes = _resumes,
                monitors = _displays.Select(d => new { d.Device, d.Bounds.Left, d.Bounds.Top, d.Bounds.Right, d.Bounds.Bottom, d.Dpi, d.Primary }),
            },
            menuBar = _bar == null ? null : new
            {
                hwnd = _bar.Window.Handle.ToInt64(),
                dpi = System.Windows.Media.VisualTreeHelper.GetDpi(_bar.Window).PixelsPerInchX,
                rect = R(Native.FrameBounds(_bar.Window.Handle)),
                visible = Native.IsWindowVisible(_bar.Window.Handle),
                clock = _bar.Window.ClockText,
                ime = _bar.Window.ImeText,
                titles = _bar.Window.Titles,
                statusOpen = _bar.Window.StatusOpen,
                volume = _bar.Window.VolumePicture,
                focus = _bar.Window.FocusShown,
                front = _front?.Current is { } f ? new { f.DisplayName, f.ExeName, f.AppId, f.IsDesktop, f.Elevated, hwnd = f.Hwnd.ToInt64() } : null,
                menus = MenuState(),
            },
            dock = DockState(),
            appBars = _appBars?.Bars.Select(b => new { edge = b.Edge, rect = R(b.Rect) }),
            theme = t == null ? null : new { t.Dark, t.Transparency, t.HighContrast, t.EnergySaver, t.BlurAllowed, accent = $"#{t.Accent.R:X2}{t.Accent.G:X2}{t.Accent.B:X2}" },
            fullscreen = _fullScreen?.IsFullScreen(_monitor?.Handle ?? IntPtr.Zero) ?? false,
        };
    }

    /// <summary>
    /// Windows asks the shell to end (WPF's SessionEnding, from WM_QUERYENDSESSION). What it is goes into the state
    /// first: at each restart and shutdown Windows ended the process half-way through the stop, and the watchdog,
    /// reading "not clean", started it again as after a crash (2026-10-13/14). Closed for another program, the
    /// watchdog starts it again (RelaunchPlanner).
    /// </summary>
    void OnSessionEnding()
    {
        uint? flags = _endSession?.QueryFlags;
        bool down = Native.ShuttingDown();
        var kind = EndSession.Classify(flags, down);
        Try(() => _store?.Update(s =>
        {
            s.CleanExit = true;
            s.EndedBy = kind == EndSessionKind.CloseApp ? ShellState.ClosedForAnotherProgram : ShellState.SessionEnded;
        }));
        Log.Info($"session ending: {EndSession.Describe(flags)}, Windows shutting down: {(down ? "yes" : "no")}: " +
                 (kind == EndSessionKind.CloseApp ? "closing for another program (the watchdog starts it again)" : "the session ends"));
        Log.Flush();
        Stop(0, clean: true, keepOwner: kind == EndSessionKind.CloseApp);
    }

    /// <param name="keepOwner">The owner's command line stays for the watchdog to start the shell again with.</param>
    void Stop(int code, bool clean, bool keepOwner = false)
    {
        if (_stopping) return;
        _stopping = true;
        _stoppingClean = clean;
        Log.Info($"stopping (clean={clean})");
        // A quit asked for is recorded as one before anything is stopped (as OnSessionEnding writes its kind first): a
        // step that crashed the process on its way left it unclean, and the watchdog started again the shell just quit.
        if (clean) Try(() => _store?.Update(s => s.CleanExit = true));
        // Whatever step hangs, the taskbar still comes back and the process still ends (2026-10-12: a tray stopped in
        // the middle of its start hung the shutdown with the taskbar hidden).
        using var guard = new System.Threading.Timer(_ => StopTookTooLong(code, clean, keepOwner), null, 8000, Timeout.Infinite);
        Try(() => _diag?.Dispose());
        Try(() => _commands?.Dispose());
        if (_saveLater?.IsEnabled == true || _recentSave?.IsEnabled == true) Try(SaveSettings); // a change made a moment ago
        Try(() => _recentDocs?.Dispose());
        Try(() => _privacy?.Dispose());
        // The taskbar first: nothing pushes it down any more (TrayHost replaced ManagedShell's monitor), and a step
        // that hangs later must not leave it hidden. Again after the tray, in case ManagedShell's teardown moved it.
        Try(() => _hider?.Restore());
        Try(() => _taskband?.Dispose()); // off the tray's window while it still exists
        if (_tray != null)
        {
            Try(() => _tray.Dispose());
            Try(() => _hider?.Restore());
        }
        Try(() => _hider?.Dispose());
        Try(() => _volumeDisplay?.Dispose()); // Windows' volume display given back
        Try(() => _overlays?.Dispose());
        Try(() => _schedule?.Dispose());
        Try(CloseShellWindows);
        Try(() => _corners?.Dispose());
        Try(() => _overview?.Dispose());
        Try(() => _search?.Dispose());
        Try(() => _grid?.Dispose());
        Try(() => Frostbar.Platform.Dwm.LensCapture.Current?.Dispose()); // the screen capture ends with the glass
        Try(() => _front?.Dispose());
        Try(() => _room?.Dispose());
        Try(() => _hotkeys?.Dispose());
        Try(() => _ticker?.Dispose());
        Try(() => _fullScreen?.Dispose());
        Try(WaitForSaves); // the last of them, from the parts just stopped too, before the process ends (and the watchdog reads them)
        Try(() => _store?.Update(s =>
        {
            s.CleanExit = clean;
            s.AppBars.Clear();
            if (clean && !keepOwner) s.Owner = null; // after a crash the watchdog needs the owner's command line to start it again
        }));
        Try(() => _messages?.Dispose());
        Try(() => _endSession?.Dispose());
        Try(() => _heartbeat?.Dispose());
        Log.Info("stopped");
        Log.Flush();
        Shutdown(code);
    }

    /// <summary>Stopping hung (a thread-pool thread): put the taskbar back, record the exit, and leave.</summary>
    void StopTookTooLong(int code, bool clean, bool keepOwner)
    {
        Log.Error("stopping took more than 8 s: putting the taskbar back and leaving");
        try { _hider?.Restore(); } catch (Exception) { }
        try { if (_store != null) VolumeDisplay.GiveBack(_store); } catch (Exception) { }
        try
        {
            _store?.Update(s =>
            {
                s.CleanExit = clean;
                s.AppBars.Clear();
                if (clean && !keepOwner) s.Owner = null; // a requested quit must not be restarted by the watchdog
            });
        }
        catch (Exception) { }
        Log.Flush();
        Environment.Exit(code == 0 ? 3 : code);
    }

    /// <summary>Last resort from a non-UI thread crash: the taskbar must not stay hidden.</summary>
    void EmergencyRestore()
    {
        try { _hider?.Restore(); } catch (Exception) { }
        try { if (_store != null) VolumeDisplay.GiveBack(_store); } catch (Exception) { }
        // A crash while quitting as asked is still that quit (not started again).
        try { if (!(_stopping && _stoppingClean)) _store?.Update(s => s.CleanExit = false); } catch (Exception) { }
        Log.Flush();
    }

    bool _stoppingClean;

    static void Try(Action a)
    {
        try { a(); }
        catch (Exception ex) { Log.Warn("shutdown step failed", ex); }
    }
}

/// <summary>Diagnostics: an object whose finalizer reports the native id of the thread that runs it.</summary>
sealed class FinalizerProbe
{
    readonly Action<uint> _report;
    FinalizerProbe(Action<uint> report) => _report = report;
    ~FinalizerProbe() => _report(Native.GetCurrentThreadId());

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public static void Make(Action<uint> report) => _ = new FinalizerProbe(report);
}
