using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Frostbar.App.Common;
using Frostbar.App.ControlCenter;
using Frostbar.App.Menus;
using Frostbar.Core.Localization;
using Frostbar.Core.MenuBar;
using Frostbar.Core.Menus;
using Frostbar.Core.Settings;
using Frostbar.Platform.Icons;
using Frostbar.Platform.Identity;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Input;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Shell;
using Frostbar.Platform.Sys;
using Frostbar.Platform.Tray;
using Frostbar.Platform.Wm;

namespace Frostbar.App.MenuBar;

/// <summary>
/// Everything behind the menu bar: the front app's menus, running their items, and the status items on the right.
/// </summary>
public sealed class MenuBarController : IDisposable
{
    readonly AppSettings _settings;
    readonly ThemeService _theme;
    readonly ForegroundTracker _front;
    readonly BatteryMonitor _battery;
    readonly NetworkMonitor _network;
    readonly AudioService _audio;
    readonly ImeMonitor _ime;
    readonly Dialogs _dialogs;
    readonly AppIdentity _identity;
    // The Control Center's (M6): started a moment after the bar, so the first opening shows real states.
    RadioService? _radios;
    BrightnessService? _brightness;
    MediaService? _media;
    WifiService? _wifi;
    BluetoothService? _bluetooth;
    Strings _s;

    public MenuBarWindow Window { get; }
    public MenuController Menus { get; }
    public CommandRunner Runner { get; }
    /// <summary>OS id of the thread that asks the app in front for its input mode (diagnostics).</summary>
    public uint ImeThreadId => _ime.ThreadId;
    public Strings Strings => _s;
    public Dialogs Dialogs => _dialogs;

    /// <param name="recent">The documents Windows lists as used lately (the shell keeps it: the bar is made again at times).</param>
    /// <param name="privacy">What uses the microphone and the camera (the shell keeps it); none: no indicator.</param>
    public MenuBarController(MonitorInfo mon, AppSettings settings, ThemeService theme, MessageWindow messages, HotkeyService hotkeys,
        AppIdentity identity, ForegroundTracker front, PowerActions power, BackgroundTicker ticker, Dispatcher ui, RecentDocuments? recent = null,
        IPrivacySource? privacy = null)
    {
        var took = System.Diagnostics.Stopwatch.StartNew();
        long Lap() { long ms = took.ElapsedMilliseconds; took.Restart(); return ms; }
        _settings = settings;
        _theme = theme;
        _front = front;
        _identity = identity;
        _recentDocs = recent;
        _privacy = privacy;
        _s = Strings.For(settings.General.Language, CultureInfo.CurrentUICulture);
        long strings = Lap();
        Window = new MenuBarWindow(mon, settings.MenuBar.Height, settings.MenuBar.Clock) { Lang = _s.Culture, BatteryPercent = settings.MenuBar.BatteryPercent };
        Window.RefreshClock();
        long window = Lap();
        _dialogs = new Dialogs(() => _theme.Current, () => _s, identity) { OpenSettings = page => OpenSettings?.Invoke(page) };
        Runner = new CommandRunner(front, power, () => _s, _dialogs);
        // Recent Items' "Clear Menu": the apps forgotten, the documents used until now left out (Windows' list stays).
        Runner.ClearRecent = () =>
        {
            _settings.MenuBar.RecentApps.Clear();
            _settings.MenuBar.RecentClearedAt = DateTime.UtcNow;
            RecentChanged?.Invoke(true);
        };
        // An app of Recent Items that runs comes forward: its window by its AppsFolder entry's AppID, or by its program.
        Runner.RunningWindow = target =>
        {
            const string apps = @"shell:AppsFolder\";
            string? appId = target.StartsWith(apps, StringComparison.OrdinalIgnoreCase) ? target[apps.Length..] : null;
            foreach (var h in WindowList.AppWindows())
                if (appId != null ? string.Equals(_identity.AppIdForWindow(h), appId, StringComparison.OrdinalIgnoreCase)
                                  : string.Equals(Native.ProcessPath(Native.ProcessId(h)), target, StringComparison.OrdinalIgnoreCase)) return h;
            return IntPtr.Zero;
        };
        Menus = new MenuController(Window, hotkeys, BuildAppMenus, SystemMenu, MakePopup, Runner.Run, ui);
        if (_recentDocs != null) _recentDocs.Changed += PrefetchRecentIcons;
        if (_privacy != null) _privacy.Changed += UpdatePrivacy;
        long menus = Lap();

        _battery = new BatteryMonitor(messages, ui);
        long battery = Lap();
        _network = new NetworkMonitor(ui);
        long network = Lap();
        _audio = new AudioService(ui);
        long audio = Lap();
        _ime = new ImeMonitor(() => _front.Current?.Hwnd ?? IntPtr.Zero, ui, ticker);
        // The bar's words dark or light by the wallpaper under it (C2): read again when the picture changes.
        _ticker = ticker;
        _screenNow = mon;
        Picture = new BarWallpaper(ui, () => { var m = _screenNow; return (m.Bounds.Width, m.Bounds.Height, (int)Math.Round(_settings.MenuBar.Height * m.Scale)); });
        Picture.Changed += () => { if (!_disposed) ApplyBarLook(_theme.Current); }; // a read under way when the bar was made again
        Picture.Watch(ticker);
        long ime = Lap();
        _battery.Changed += UpdateBattery;
        _network.Changed += UpdateNetwork;
        _audio.Changed += UpdateVolume;
        _ime.Changed += UpdateIme;
        _onFront = f =>
        {
            // Another app came to the front (Alt+Tab, a window popping up): an open menu belongs to the old one.
            Menus.Close();
            UpdateTitles();
            _ime.Poll();
            NoteRecentApp(f);
        };
        front.Changed += _onFront;
        Window.StatusClicked += OnStatus;
        _hotkeys = hotkeys;
        // The system menu's "Force Quit…" shortcut works everywhere, like macOS's Option-Command-Esc.
        if (Shortcut.Parse("Ctrl+Alt+Esc") is { } fq)
        {
            _forceQuitKey = hotkeys.Register(fq, () => { Menus.Close(); _dialogs.ForceQuit(); });
            if (_forceQuitKey == null) Log.Warn("Ctrl+Alt+Esc is taken by another app: Force Quit only from the menu");
        }
        long keys = Lap();
        ApplyTheme(theme.Current);
        var later = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.ApplicationIdle, (s, _) => { ((DispatcherTimer)s!).Stop(); StartControlCenterServices(); }, ui);
        later.Start();
        long look = Lap();
        Log.Debug($"menu bar: made in {strings + window + menus + battery + network + audio + ime + keys + look} ms (strings {strings}, window {window}, " +
                  $"menus {menus}, battery {battery}, network {network}, audio {audio}, IME {ime}, keys {keys}, look {look})");
    }

    readonly HotkeyService _hotkeys;
    readonly int? _forceQuitKey;
    /// <summary>Kept to stop listening: the tracker outlives a menu bar made again (a new height, a new DPI).</summary>
    readonly Action<FrontWindow> _onFront;

    // ---- the system menu's Recent Items (stage 5-2) ----------------------------------------------------------------

    readonly RecentDocuments? _recentDocs;
    /// <summary>The menus' icons (16 DIPs): Recent Items' apps and documents.</summary>
    IconService? _menuIcons;

    /// <summary>Recent Items changed: true when the user cleared them (saved at once), false for an app that came to the front (saved within a minute).</summary>
    public event Action<bool>? RecentChanged;

    /// <summary>The mark's menu as it opens now: Recent Items from the apps kept and the documents already read (nothing is read here).</summary>
    public Menu SystemMenu() => MenuBuilder.SystemMenu(_s, RecentSubmenu());

    // As many of each as the settings say (none: the system menu goes without Recent Items).
    MenuItem? RecentSubmenu() => RecentItems.Submenu(_settings.MenuBar.RecentApps,
        RecentItems.Documents(_recentDocs?.Items ?? [], _settings.MenuBar.RecentClearedAt, _settings.MenuBar.RecentCount), _s, _settings.MenuBar.RecentCount);

    IconService MenuIcons() => _menuIcons ??= new IconService(Window.Dispatcher, (int)Math.Ceiling(MenuPopup.IconDip * (Monitors.Of(Window.Handle)?.Scale ?? 1.25)));

    /// <summary>Asks for Recent Items' icons ahead, so that they are there when the submenu opens.</summary>
    void PrefetchRecentIcons()
    {
        foreach (var item in RecentSubmenu()?.Children ?? [])
            if (item.Icon is { } source) MenuIcons().Get(MenuPopup.IconKey(source), source);
    }

    /// <summary>
    /// The app in front joins Recent Items, first (macOS lists the apps opened lately; here the ones used lately), by its
    /// Start menu entry or its program. Not the desktop, nor an app with neither to open it by.
    /// </summary>
    void NoteRecentApp(FrontWindow f)
    {
        if (f.IsDesktop) return;
        DockPin? app = _identity.IsCatalogEntry(f.AppId) ? new DockPin { Kind = "appsFolder", Id = f.AppId, Name = f.DisplayName }
            : f.ExePath != null ? new DockPin { Kind = "path", Id = f.ExePath, Name = f.DisplayName } : null;
        if (app == null || !RecentItems.Note(_settings.MenuBar.RecentApps, app)) return;
        RecentChanged?.Invoke(false);
        string target = RecentItems.Target(app);
        MenuIcons().Get(MenuPopup.IconKey(target), target); // ahead of the submenu
    }

    /// <summary>Diagnostics: Recent Items as the submenu would list them, the folder the documents come from, how often it was read.</summary>
    public object RecentState() => new
    {
        folder = _recentDocs?.Folder, reads = _recentDocs?.Reads ?? -1, clearedAt = _settings.MenuBar.RecentClearedAt,
        items = (RecentSubmenu()?.Children ?? []).Select(i => new { i.Id, i.Label, i.Enabled, target = i.Action.Kind == ActionKind.Open ? i.Action.Name : null }),
        icons = _menuIcons?.Counts.cached ?? 0,
    };

    void StartControlCenterServices()
    {
        // Made again within its first 2 s (a new height, a display rebuilt): a gone bar's services were made and never let go.
        if (_radios != null || _disposed) return;
        var ui = Window.Dispatcher;
        _radios = new RadioService(ui);
        _brightness = new BrightnessService(ui);
        _brightness.Changed += () => BrightnessChanged?.Invoke();
        _media = new MediaService(ui);
        _media.Changed += UpdateNowPlaying;
        _wifi = new WifiService(ui);
        _bluetooth = new BluetoothService(ui);
        // A Bluetooth headset's battery in the volume item; Focus's moon in the bar (from now on: they wait for the
        // first idle moment after start-up, with the Control Center's services).
        _bluetooth.Changed += UpdateVolume;
        _unwatchFocus = FocusSessions.Watch(() => ui.BeginInvoke(RefreshFocus));
        Window.ClockTicked += OnClockTicked;
        RefreshFocus();
        UpdateVolume();
        PrefetchRecentIcons();
        ServicesStarted?.Invoke();
    }

    /// <summary>The Control Center's services are made (the first idle moment after start-up): what is played, Bluetooth.</summary>
    public event Action? ServicesStarted;
    /// <summary>What is played (null until <see cref="ServicesStarted"/>); Bluetooth (a headset's battery); the battery (the Dynamic Island hears them).</summary>
    public MediaService? Media => _media;
    public BluetoothService? Bluetooth => _bluetooth;
    public BatteryMonitor Battery => _battery;
    /// <summary>An app's name as the bar shows it.</summary>
    public string AppName(string appId) => _identity.DisplayName(appId, null);

    /// <summary>Now Playing's item: there while a session plays or is paused, its title, artist and app in the tooltip.</summary>
    void UpdateNowPlaying()
    {
        var now = _media?.Current;
        string tooltip = now == null ? _s["bar.item.nowPlaying"]
            : string.Join("  ·  ", new[] { now.Title, now.Artist, now.AppId.Length > 0 ? _identity.DisplayName(now.AppId, null) : "" }.Where(x => x.Length > 0).Distinct());
        Window.SetNowPlaying(now != null, tooltip.Length > 0 ? tooltip : _s["bar.item.nowPlaying"]);
    }

    /// <summary>Now Playing's panel when it is open (diagnostics).</summary>
    public NowPlayingView? NowPlaying => (Menus.Panel as ViewPanel)?.View as NowPlayingView;

    /// <summary>The input menu when it is open (diagnostics).</summary>
    public InputView? InputMenu => (Menus.Panel as ViewPanel)?.View as InputView;

    /// <summary>The input mode as the bar reads it (diagnostics): its text, on, Japanese, the conversion bits, the layout.</summary>
    public object ImeState() => new { text = _ime.Text, open = _ime.Open, japanese = _ime.Japanese, conversion = _ime.Conversion, layout = _ime.Layout };

    // ---- the privacy indicator (stage 5-2) --------------------------------------------------------------------------

    readonly IPrivacySource? _privacy;

    /// <summary>What uses the microphone and the camera now (none without a source).</summary>
    public PrivacyState Privacy => _privacy?.Current ?? PrivacyState.None;

    /// <summary>The capsule left of the Control Center, its tooltip naming the apps; an open Control Center lists them anew.</summary>
    void UpdatePrivacy()
    {
        var state = Privacy;
        Window.SetPrivacy(state, state.Describe(_s));
        ControlCenterPanel?.ShowPrivacy(state);
    }

    // ---- slow checks on the clock's minute: Focus's moon, a headset's battery ---------------------------------------
    // Do not disturb tells no one when it changes (ADR-013): it is read again on the clock's tick (at most every 30 s:
    // the clock may tick every second), after this shell's own switches, and when a focus session starts or ends.

    long _slowChecksAt, _batteryReadAt = -10 * 60_000;
    Action? _unwatchFocus;

    void OnClockTicked()
    {
        long now = Environment.TickCount64;
        if (now - _slowChecksAt < 30_000) return;
        _slowChecksAt = now;
        RefreshFocus();
        ReadOutputBattery();
    }

    /// <summary>The moon while do not disturb or a focus session is on.</summary>
    void RefreshFocus()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        bool on = DoNotDisturb.IsOn() == true || FocusSessions.Active;
        if (sw.ElapsedMilliseconds > 20) Log.Debug($"focus: read in {sw.ElapsedMilliseconds} ms");
        Window.SetFocus(on, _s["cc.focus"]);
    }

    /// <summary>
    /// A Bluetooth headset that is the output: its battery is read when it becomes the output (again 20 s later while
    /// none is known: the level comes after the connection) and every 5 minutes after.
    /// </summary>
    void ReadOutputBattery()
    {
        if (_bluetooth == null || !_audio.Headphones || _audio.DeviceContainer is not Guid c || !_bluetooth.Connects(c)) return;
        long now = Environment.TickCount64;
        if (now - _batteryReadAt < (_bluetooth.BatteryOfContainer(c) != null ? 5 * 60_000 : 20_000)) return;
        _batteryReadAt = now;
        _ = _bluetooth.ReadBatteriesAsync();
    }

    /// <summary>Do not disturb and focus sessions for the views, with the moon following this shell's own switches.</summary>
    FocusServices Focus => _focus ??= new(DoNotDisturb.IsOn,
        on => { bool ok = DoNotDisturb.Set(on); RefreshFocus(); return ok; },
        () => FocusSessions.Supported, () => FocusSessions.Active,
        length => { bool ok = FocusSessions.Start(length); RefreshFocus(); return ok; },
        () => { FocusSessions.Stop(); RefreshFocus(); });

    FocusServices? _focus;

    // ---- the search's actions (stage 6-1): what the Control Center's switches and the status items do ----

    /// <summary>Do not disturb from the search, as the Control Center's switch: the moon follows.</summary>
    public void SetDoNotDisturb(bool on)
    {
        if (!DoNotDisturb.Set(on)) Log.Warn($"search: do not disturb {(on ? "on" : "off")} refused");
        RefreshFocus();
    }

    /// <summary>Dark mode from the search, as the Control Center's switch (Frostbar's own mode when the settings fix one).</summary>
    public void SetDarkMode(bool dark) => SetDark(dark);

    /// <summary>Opens (or closes) the Control Center, as its status item does.</summary>
    public void ToggleControlCenter() => ToggleControlCenter(Window.StatusRect("control-center"));

    /// <summary>Diagnostics: how many listen to each service (closing the panels must bring these back to where they were; -1: not started).</summary>
    public object Listeners() => new
    {
        audio = _audio.Listeners, network = _network.Listeners, radios = _radios?.Listeners ?? -1, brightness = _brightness?.Listeners ?? -1,
        media = _media?.Listeners ?? -1, wifi = _wifi?.Listeners ?? -1, bluetooth = _bluetooth?.Listeners ?? -1,
    };

    /// <summary>The Control Center's panel when it is open (diagnostics).</summary>
    public ControlCenterPanel? ControlCenterPanel => Menus.Panel as ControlCenterPanel;

    /// <summary>Diagnostics: the status item's click to the first frame of the open Control Center, in ms (-1: not measured).</summary>
    public int LastControlCenterOpenMs { get; private set; } = -1;

    /// <summary>Settings → Appearance changed here (the Control Center's dark mode with a fixed mode): the shell applies and saves it.</summary>
    public Action? AppearanceChanged { get; set; }

    /// <summary>Dark mode from the Control Center: Frostbar's own mode when Settings → Appearance fixes one (that is what the switch shows), else Windows'.</summary>
    void SetDark(bool dark)
    {
        if (_settings.Appearance.Theme is "light" or "dark" && AppearanceChanged != null)
        {
            _settings.Appearance.Theme = dark ? "dark" : "light";
            AppearanceChanged();
        }
        else ThemeService.SetDark(dark);
    }

    void ToggleControlCenter(Rect anchor)
    {
        int input = Window.LastInputTime;
        Window.LastInputTime = 0;
        if (ControlCenterPanel != null) { Menus.Close(); return; }
        int queued = input != 0 ? Math.Max(0, Environment.TickCount - input) : 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Menus.OpenPanel(NewControlCenterPanel(), anchor);
        Window.SetStatusOpen("control-center");
        LastControlCenterOpenMs = -1;
        if (input == 0) return;
        EventHandler? frame = null;
        frame = (_, _) =>
        {
            System.Windows.Media.CompositionTarget.Rendering -= frame;
            LastControlCenterOpenMs = queued + (int)sw.ElapsedMilliseconds;
        };
        System.Windows.Media.CompositionTarget.Rendering += frame;
    }

    /// <summary>
    /// The password window for a network Windows has no saved password for (the Wi-Fi view closes its panel first:
    /// the panel never takes the keyboard). Completes when the window closes.
    /// </summary>
    Task AskWifiPassword(string ssid)
    {
        var done = new TaskCompletionSource();
        var w = new WifiPasswordDialog(_theme.Current, _s, ssid, password => _wifi!.ConnectAsync(ssid, password));
        w.Closed += (_, _) => done.TrySetResult();
        w.ShowInFront();
        return done.Task;
    }

    public void ApplyTheme(ThemeSnapshot t)
    {
        // An open Control Center is made again in the new colours (its dark mode switch changes them), standing where it
        // was with the same view open: it took the new one's place at once. It went back into its item and grew out
        // again, its view closed (2026-10-19, the user's "A4").
        var open = ControlCenterPanel;
        ApplyBarLook(t);
        if (open != null) Window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (!ReferenceEquals(ControlCenterPanel, open)) return; // closed meanwhile, or another opened
            var panel = NewControlCenterPanel();
            panel.OpenViewAtOnce(open.CurrentViewKey);
            if (Menus.SwapPanel(panel)) ControlCenterSwaps++;
            else panel.Close();
        });
    }

    /// <summary>The wallpaper under the bar, read (C2).</summary>
    public BarWallpaper Picture { get; }

    // The monitor the bar stands on, as the wallpaper's reader sees it (the ticker's thread reads it): kept from the bar's
    // making, a new resolution on the same monitor (the bar kept) read the wrong strip of the picture (2026-11-06 review).
    volatile MonitorInfo _screenNow;

    /// <summary>The primary monitor is the same one in a new form (another resolution, its place): the wallpaper is read for it.</summary>
    public void MovedTo(MonitorInfo mon) => _screenNow = mon;
    readonly BackgroundTicker _ticker;

    /// <summary>
    /// The bar's own look for theme <paramref name="t"/>: its words dark over a bright wallpaper and light over a dark one
    /// whatever the appearance (macOS's: C2, 2026-10-19), through its glass or with none; an opaque bar (and high
    /// contrast) keeps the appearance's.
    /// </summary>
    public ThemeSnapshot BarLook(ThemeSnapshot t)
    {
        if (t.HighContrast || Picture.Luma == null) return t;
        if (Frostbar.Core.Settings.BackdropRule.For(_settings.Appearance.Material, t.BlurAllowed, menuBar: true) == Frostbar.Core.Settings.Backdrop.Solid) return t;
        return t with { Dark = !Picture.DarkWords };
    }

    /// <summary>
    /// The bar stands over the apps' windows (a full-screen app in front, or no strip of its own when it hides): a clear
    /// bar is frosted then (<see cref="Materials.MenuBar"/>).
    /// </summary>
    public bool OverApps
    {
        get => _overApps;
        set
        {
            if (_overApps == value) return;
            _overApps = value;
            if (!_disposed) ApplyBarLook(_theme.Current);
        }
    }
    bool _overApps;

    /// <summary>
    /// What is behind the bar reads dark now (its words light: a dark wallpaper under a clear bar, or a dark bar): the
    /// Dynamic Island's key line shows then (HIG: "When the background is dark"). <see cref="LookChanged"/> tells it.
    /// </summary>
    public bool DarkBehind { get; private set; }
    public event Action? LookChanged;

    void ApplyBarLook(ThemeSnapshot appearance)
    {
        var t = BarLook(appearance);
        if (DarkBehind != t.Dark)
        {
            DarkBehind = t.Dark;
            LookChanged?.Invoke();
        }
        Window.ApplyTheme(t, Materials.MenuBar(t, _settings.Appearance, _overApps));
        Window.Describe("search", _s["status.search"]);
        Window.Describe("control-center", _s["status.controlCenter"]);
        Window.Describe("tray-overflow", _s["status.moreIcons"]);
        Window.DescribeLogo(_s["menu.system"]);
        UpdateTitles();
        UpdateBattery();
        UpdateNetwork();
        UpdateVolume();
        UpdateIme();
        UpdatePrivacy(); // the capsule's shades follow the theme
    }

    /// <summary>Control Centers that took the place of one in other colours (diagnostics: a theme change).</summary>
    public int ControlCenterSwaps { get; private set; }

    ControlCenterPanel NewControlCenterPanel()
    {
        StartControlCenterServices();
        var services = new ControlCenterServices(_audio, _network, _radios!, _brightness!, _media!, _wifi!, _bluetooth!, id => _identity.DisplayName(id, null), AskWifiPassword,
            Focus, OpenSettings, Privacy, () => VolumeSliderLetGo?.Invoke());
        return new ControlCenterPanel(_theme.Current, PanelMaterial(GlassShape.LargePanel), PieceMaterial(), _s, services, Chord, Menus.Close, SetDark);
    }

    FrontApp FrontAppInfo(bool withWindows)
    {
        var f = _front.Current;
        if (f == null) return new FrontApp(Explorer(), "explorer.exe", false, [], IsDesktop: true);
        var windows = withWindows
            ? _front.WindowsOf(f).Select(h => new AppWindowInfo(h.ToInt64(), Native.Title(h), Native.GetForegroundWindow() == h, Native.IsIconic(h))).ToList()
            : (IReadOnlyList<AppWindowInfo>)[];
        return new FrontApp(f.DisplayName, f.IsDesktop ? "explorer.exe" : f.ExeName, f.Elevated, windows, f.IsDesktop,
            FrameKept: withWindows && Runner.Tiles.Has(Runner.TargetWindow(f).ToInt64()));
    }

    string Explorer() => _s["app.explorer"];

    IReadOnlyList<Menu> BuildAppMenus() => MenuBuilder.ForApp(FrontAppInfo(withWindows: true), _s);

    /// <summary>The front app's menus as they would open now (diagnostics).</summary>
    public IReadOnlyList<Menu> AppMenus() => BuildAppMenus();

    public void UpdateTitles() => Window.SetTitles(MenuBuilder.ForApp(FrontAppInfo(withWindows: false), _s).Select(m => m.Title).ToList());

    MenuPopup MakePopup(Menu menu)
    {
        var t = _theme.Current;
        float radius = (float)(MenuPopup.RadiusDip * (Monitors.Of(Window.Handle)?.Scale ?? 1.25));
        // Only a menu with icons (the system menu's Recent Items) asks for the service: the others open as fast as before.
        return new MenuPopup(menu, t, Materials.Menu(t, _settings.Appearance, radius), FormatKeys, menu.Items.Any(i => i.Icon != null) ? MenuIcons() : null);
    }

    /// <summary>Key names of the keyboard layout for punctuation (Ctrl+= is Ctrl+; on a Japanese keyboard), built-in names otherwise.</summary>
    public static string FormatKeys(Shortcut s) => s.Format(vk =>
    {
        if (vk is < 0xBA or > 0xE2 || vk is > 0xC0 and < 0xDB) return null;
        uint sc = Native.MapVirtualKeyEx(vk, 0 /*MAPVK_VK_TO_VSC*/, Native.GetKeyboardLayout(0));
        var sb = new StringBuilder(32);
        return sc != 0 && Native.GetKeyNameText((int)(sc << 16), sb, sb.Capacity) > 0 ? sb.ToString() : null;
    });

    /// <summary>The battery's item drawn again as the settings have it now (its percentage shown or not).</summary>
    public void RefreshBattery()
    {
        Window.BatteryPercent = _settings.MenuBar.BatteryPercent;
        UpdateBattery();
    }

    void UpdateBattery() =>
        Window.SetBattery(_battery.Present, _battery.Percent, _battery.Charging, _s.F("status.battery", _battery.Percent) + (_battery.Charging ? " " + _s["status.charging"] : ""));

    void UpdateNetwork() =>
        Window.SetWifi(_network.Kind, _network.Bars, _network.Kind switch
        {
            NetKind.Wifi => _s.F("status.wifi", _network.Name),
            NetKind.Ethernet or NetKind.Other => _s.F("status.ethernet", _network.Name),
            _ => _s["status.offline"],
        });

    /// <summary>The volume item: headphones when the output is (macOS); its tooltip names the output and a headset's battery.</summary>
    void UpdateVolume()
    {
        string level = _audio.Muted ? _s["status.muted"] : _s.F("status.volume", (int)Math.Round(_audio.Volume * 100));
        string tooltip = _audio.DeviceName.Length > 0 ? _audio.DeviceName + "  ·  " + level : level;
        if (_audio.Headphones && _audio.DeviceContainer is Guid c && _bluetooth?.BatteryOfContainer(c) is int b) tooltip += "  ·  " + _s.F("bt.battery", b);
        Window.SetVolume(_audio.Volume, _audio.Muted, _audio.Headphones, tooltip);
        ReadOutputBattery();
        VolumeChanged?.Invoke();
    }

    void UpdateIme() => Window.SetIme(_ime.Text, _ime.Japanese ? (_ime.Open ? _s["status.imeOn"] : _s["status.imeOff"]) : "");

    /// <summary>Opens (or closes) the search panel: the magnifier's click.</summary>
    public Action? OpenSearch { get; set; }

    public void OnStatus(string key, Rect anchor)
    {
        switch (key)
        {
            case "clock": ToggleNotificationCenter(); break;
            case "search": if (OpenSearch != null) OpenSearch(); else Chord("Win+S"); break;
            case "control-center": ToggleControlCenter(anchor); break;
            // The capsule opens the Control Center, which names the apps at its top (macOS's indicator is part of it).
            case "privacy": ToggleControlCenter(Window.StatusRect("control-center")); break;
            case "wifi":
                if (ClosedOwnPanel(key)) break;
                StartControlCenterServices();
                OpenPanel(key, new ViewPanel(_theme.Current, PanelMaterial(), new WifiView(Kit(), _wifi!, _radios!, Menus.Close, null, AskWifiPassword)), anchor);
                break;
            case "ime": // the input menu (stage 5-3, macOS's): the modes and the layouts; the 半角/全角 key still toggles
                if (ClosedOwnPanel(key)) break;
                OpenPanel(key, new ViewPanel(_theme.Current, PanelMaterial(), new InputView(Kit(), _ime, Menus.Close,
                    mode => { if (Runner.MayAct("input mode " + mode)) _ime.SetMode(mode); },
                    layout => { if (Runner.MayAct("keyboard layout")) _ime.SwitchLayout(layout); },
                    () => { if (Runner.MayAct("emoji")) Chord("Win+."); })), anchor);
                break;
            case "volume":
                if (ClosedOwnPanel(key)) break;
                if (_audio.Available) OpenPanel(key, new ViewPanel(_theme.Current, PanelMaterial(), new SoundView(Kit(), _audio, Menus.Close, null)), anchor);
                break;
            case "battery":
                if (ClosedOwnPanel(key)) break;
                OpenPanel(key, new BatteryPanel(_theme.Current, PanelMaterial(), _s, _battery, Menus.Close, OpenSettings), anchor);
                break;
            case "focus": // the moon: the Focus view, as macOS's Focus menu
                if (ClosedOwnPanel(key)) break;
                OpenPanel(key, new ViewPanel(_theme.Current, PanelMaterial(), new FocusView(Kit(), Focus, Menus.Close, null)), anchor);
                break;
            case "nowPlaying": // what plays, larger than the Control Center's tile (macOS's Now Playing)
                if (ClosedOwnPanel(key) || _media == null) break;
                OpenPanel(key, new ViewPanel(_theme.Current, PanelMaterial(), new NowPlayingView(Kit(), _media, id => _identity.DisplayName(id, null), Runner.Open, Menus.Close)), anchor);
                break;
            case "tray-overflow":
                if (ClosedOwnPanel(key)) break;
                OpenTrayOverflow(anchor);
                break;
        }
    }

    /// <summary>The » panel when it is open (diagnostics).</summary>
    public TrayOverflowPanel? TrayOverflow => Menus.Panel as TrayOverflowPanel;

    /// <summary>
    /// The » button: the tray icons the bar has no room for, or that the user put there, as a grid under it. While it
    /// is open the apps hear where the cells are; when it closes, the » button again.
    /// </summary>
    void OpenTrayOverflow(Rect anchor)
    {
        var icons = Window.OverflowIcons;
        if (icons.Count == 0 || _trayHost is not { } tray) return;
        var panel = new TrayOverflowPanel(_theme.Current, PanelMaterial(), icons, tray.Mouse, Menus.Close);
        panel.Closed += (_, _) => tray.SetPlacements(Window.TrayRects());
        OpenPanel("tray-overflow", panel, anchor);
        tray.SetPlacements(panel.IconRects());
    }

    /// <summary>Opens a page of Frostbar's settings (the views' "… Settings" links); set by the shell.</summary>
    public Action<string>? OpenSettings { get; set; }

    ViewKit Kit() => new(_theme.Current, _s) { OpenSettings = OpenSettings };

    // ---- the notification centre (the clock's click, a hot corner) -------------------------------------------------

    /// <summary>Icons for the notification centre and the settings' volume mixer (one small set for both).</summary>
    IconService? _smallIcons;

    IconService SmallIcons() => _smallIcons ??= new IconService(Window.Dispatcher, (int)Math.Ceiling(28 * (Monitors.Of(Window.Handle)?.Scale ?? 1.25)));

    /// <summary>The notification centre when it is open (diagnostics).</summary>
    public NotificationCenterPanel? NotificationCenter => Menus.Panel as NotificationCenterPanel;

    /// <summary>The output's volume (the volume display reads it as a key changes it).</summary>
    public AudioService Audio => _audio;
    /// <summary>The display's brightness (null until the Control Center's services start, 2 s after the bar).</summary>
    public BrightnessService? Brightness => _brightness;
    /// <summary>The volume or the output changed (UI thread).</summary>
    public event Action? VolumeChanged;
    /// <summary>The Control Center's volume slider was let go (the settings' volume feedback plays then).</summary>
    public event Action? VolumeSliderLetGo;
    /// <summary>The notification centre is about to open.</summary>
    public event Action? NotificationCenterOpening;
    /// <summary>The brightness came or changed from outside the shell: the keys, Windows' slider, adaptive brightness (UI thread).</summary>
    public event Action? BrightnessChanged;

    /// <summary>An app's icon at the notification centre's size (28 DIPs; null while it loads or with no app).</summary>
    public System.Windows.Media.ImageSource? NoticeIcon(string appId) => appId.Length == 0 ? null : SmallIcons().Get("nc:" + appId, _identity.IconSource(appId, null));

    /// <summary>
    /// The tray apps' balloon notifications, kept by the shell (the bar is made again at times) and listed in the
    /// notification centre with Windows' own.
    /// </summary>
    public Frostbar.Core.Shell.BalloonList? Balloons { get; set; }

    /// <summary>Opens the notification centre under the clock, or closes it when it is open (macOS's clock does).</summary>
    public void ToggleNotificationCenter()
    {
        if (ClosedOwnPanel("clock")) return;
        NotificationCenterOpening?.Invoke(); // the banners go: the centre lists them (macOS)
        var balloons = Balloons;
        // A spec's photos (FROSTBAR_E2E_NOTICES=spec): Windows' notifications are the user's, neither read nor cleared; the
        // list is the spec's own (diagnostics "nc-notices") and the shell's balloons.
        bool spec = SpecNotices != null;
        string? only = SpecNoticesApp;
        var services = new NotificationCenterServices(
            async () =>
            {
                var list = spec ? [.. SpecNotices!]
                    : (await NotificationService.ReadAsync()).Where(n => only == null || n.AppId == only)
                        .Select(n => new Frostbar.Core.Shell.Notice(n.Id, n.App, n.AppId, n.Created, n.Title, n.Body)).ToList();
                if (balloons != null) list.AddRange(balloons.Items); // back on the UI thread, where the balloons are kept
                return list;
            },
            id => Frostbar.Core.Shell.BalloonList.IsBalloon(id) ? balloons?.Remove(id) == true : spec ? SpecNotices!.RemoveAll(n => n.Id == id) > 0 : NotificationService.Remove(id),
            () => { balloons?.Clear(); if (spec) { SpecNotices!.Clear(); return Task.FromResult(true); } return NotificationService.ClearAsync(only); },
            spec ? () => "Allowed" : NotificationService.Access, DoNotDisturb.IsOn,
            (appId, _) => appId.Length == 0 ? null : SmallIcons().Get("nc:" + appId, _identity.IconSource(appId, null)),
            n =>
            {
                if (Frostbar.Core.Shell.BalloonList.IsBalloon(n.Id)) _trayHost?.ClickBalloon(Frostbar.Core.Shell.BalloonList.TrayNumberOf(n.Id));
                // Through the runner: an app that runs comes forward (it was started again), and a spec's shell opens the
                // test target only (a card of PowerShell's opened PowerShell on the user's desktop).
                else Runner.Open(@"shell:AppsFolder\" + n.AppId);
            });
        OpenPanel("clock", new NotificationCenterPanel(_theme.Current, PanelMaterial(GlassShape.LargePanel), PieceMaterial(), _s, services, Menus.Close), Window.StatusRect("clock"));
    }


    /// <summary>What the settings' system pages show and change (the services start now if they had not yet).</summary>
    public Settings.SystemServices SystemServices()
    {
        StartControlCenterServices();
        return new Settings.SystemServices(_wifi!, _radios!, _bluetooth!, _audio, _battery, AskWifiPassword,
            (appId, exe) => _identity.DisplayName(appId, exe), (key, source) => SmallIcons().Get("mix:" + key, source), _brightness);
    }

    // The status item whose panel is open, and that panel: a second click on the item closes it (as macOS does; it
    // opened the panel again: 2026-10-12, the user's report).
    string? _panelOf;
    PanelPopup? _ownPanel;

    void OpenPanel(string key, PanelPopup panel, Rect anchor)
    {
        Menus.OpenPanel(panel, anchor);
        _panelOf = key;
        _ownPanel = panel;
        Window.SetStatusOpen(key); // its item keeps a plate while the panel is open (macOS); closing the menus takes it off
    }

    /// <summary>Closes <paramref name="key"/>'s own panel if that is the one open (true); another panel is left to be replaced.</summary>
    bool ClosedOwnPanel(string key)
    {
        if (Menus.Panel == null || Menus.Panel != _ownPanel || _panelOf != key) return false;
        Menus.Close();
        return true;
    }

    /// <summary>
    /// A spec's own notifications (FROSTBAR_E2E_NOTICES=spec with --diag: ShellApp sets it, diagnostics "nc-notices" fill
    /// it): the Notification Center lists them instead of Windows' (the user's), and clears only them. Null: Windows'.
    /// </summary>
    public static List<Frostbar.Core.Shell.Notice>? SpecNotices { get; set; }

    /// <summary>
    /// The one app whose Windows notifications a spec's shell lists, dismisses and clears (FROSTBAR_E2E_NOTICES=app:&lt;AppID&gt;
    /// with --diag: the app a spec posts its own as). The user's are neither shown nor cleared. Null: every app's.
    /// </summary>
    public static string? SpecNoticesApp { get; set; }

    /// <summary>The glass of each piece of the Control Center and the Notification Center (macOS 27: no panel under them).</summary>
    Platform.Dwm.Material PieceMaterial() =>
        Materials.Piece(_theme.Current, _settings.Appearance, (float)(GlassShape.Module * (Monitors.Of(Window.Handle)?.Scale ?? 1.25)));

    Platform.Dwm.Material PanelMaterial(double radiusDip = GlassShape.Panel) =>
        Materials.Menu(_theme.Current, _settings.Appearance, (float)(radiusDip * (Monitors.Of(Window.Handle)?.Scale ?? 1.25)));

    static void Chord(string text)
    {
        if (Shortcut.Parse(text) is { } s) InputInjector.SendChord(s, Native.GetForegroundWindow());
    }

    TrayHost? _trayHost;
    Action<IReadOnlyList<TrayItem>>? _trayChanged;

    /// <summary>Shows the tray's icons in the bar and forwards clicks on them to their apps.</summary>
    public void AttachTray(TrayHost tray)
    {
        if (_trayHost != null) return;
        _trayHost = tray;
        // Where the user put each icon (the settings' menu bar page): on the bar, in the » panel, or not at all.
        _trayChanged = items => Window.SetTray(items.Select(i => new TrayIconView(i.Key, i.Icon, i.Tooltip,
            TrayPlaces.Parse(_settings.MenuBar.TrayIcons.GetValueOrDefault(i.SettingsKey)))).ToList());
        tray.Changed += _trayChanged;
        Window.TrayIconMouse += (key, button, down) =>
        {
            if (down) Menus.Close(); // the app opens its own menu: ours must not stay over it
            tray.Mouse(key, button, down);
        };
        Window.TrayMoved += () => { if (TrayOverflow == null) tray.SetPlacements(Window.TrayRects()); }; // an open panel told its cells
        _trayChanged(tray.Last);
    }

    /// <summary>The settings moved a tray icon: shown again from the last snapshot (the panel closes: its icons may have changed).</summary>
    public void RefreshTray()
    {
        if (_trayHost == null || _trayChanged == null) return;
        if (TrayOverflow != null) Menus.Close();
        _trayChanged(_trayHost.Last);
    }

    bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        if (_trayHost != null && _trayChanged != null) _trayHost.Changed -= _trayChanged;
        if (_forceQuitKey is int id) _hotkeys.Unregister(id);
        _unwatchFocus?.Invoke();
        Picture.Unwatch(_ticker);
        Window.ClockTicked -= OnClockTicked;
        _front.Changed -= _onFront;
        _battery.Dispose();
        _network.Dispose();
        _ime.Dispose();
        Menus.Dispose(); // its click shields too
        _audio.Dispose();
        _radios?.Dispose();
        _brightness?.Dispose();
        if (_media != null) _media.Changed -= UpdateNowPlaying;
        _media?.Dispose();
        _wifi?.Dispose();
        _bluetooth?.Dispose();
        _smallIcons?.Dispose();
        if (_recentDocs != null) _recentDocs.Changed -= PrefetchRecentIcons; // the shell keeps it for the next bar
        if (_privacy != null) _privacy.Changed -= UpdatePrivacy; // so does it this
        _menuIcons?.Dispose();
        Window.Close();
    }
}
