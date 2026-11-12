using System.Globalization;
using System.Windows.Threading;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;
using Frostbar.Platform.Shell;
using Windows.Networking.Connectivity;

namespace Frostbar.Platform.Sys;

/// <summary>Battery level and charging (hidden on machines without a battery).</summary>
public sealed class BatteryMonitor : IDisposable
{
    readonly MessageWindow _messages;
    readonly DispatcherTimer _timer;
    public bool Present { get; private set; }
    public int Percent { get; private set; }
    public bool OnAc { get; private set; }
    public bool Charging { get; private set; }
    public event Action? Changed;

    public BatteryMonitor(MessageWindow messages, Dispatcher ui)
    {
        _messages = messages;
        messages.PowerStatusChanged += Refresh;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(60), DispatcherPriority.Background, (_, _) => Refresh(), ui);
        _timer.Start();
        Refresh();
    }

    /// <summary>The message window lives as long as the shell: a menu bar made again must not leave this listening.</summary>
    public void Dispose()
    {
        _messages.PowerStatusChanged -= Refresh;
        _timer.Stop();
    }

    public void Refresh()
    {
        Stats.Inc("battery.refresh");
        if (!Native.GetSystemPowerStatus(out var s)) return;
        bool present = s.BatteryFlag != 128 && s.BatteryFlag != 255 && s.BatteryLifePercent != 255;
        int pct = present ? s.BatteryLifePercent : 0;
        bool ac = s.ACLineStatus == 1, charging = present && ((s.BatteryFlag & 8) != 0 || (ac && pct < 100));
        if (present == Present && pct == Percent && ac == OnAc && charging == Charging) return;
        (Present, Percent, OnAc, Charging) = (present, pct, ac, charging);
        Changed?.Invoke();
    }
}

public enum NetKind { None, Wifi, Ethernet, Other }

/// <summary>
/// Connection kind, name and Wi-Fi bars, from ConnectionProfile (the WLAN SSID APIs need location permission on 24H2+,
/// the profile name does not: M0 S10).
/// </summary>
public sealed class NetworkMonitor : IDisposable
{
    readonly Dispatcher _ui;
    readonly NetworkStatusChangedEventHandler _onStatus;
    public NetKind Kind { get; private set; }
    public string Name { get; private set; } = "";
    public int Bars { get; private set; }
    public bool Internet { get; private set; }
    public event Action? Changed;
    /// <summary>How many listen to <see cref="Changed"/> (diagnostics: a closed panel must not stay subscribed).</summary>
    public int Listeners => Changed?.GetInvocationList().Length ?? 0;

    /// <summary>
    /// A shell photographed for the public (FROSTBAR_E2E_NETWORK, with --diag): the name shown for the connection in
    /// place of the network's own, which stays out of the pictures. Null otherwise.
    /// </summary>
    public static string? SpecName { get; set; }

    /// <summary>Start-up warm-up, any thread: the same reads as the first <see cref="Refresh"/>, their answers dropped.</summary>
    public static void WarmUp()
    {
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            if (p != null) { _ = p.GetSignalBars(); _ = p.GetNetworkConnectivityLevel(); }
        }
        catch (Exception) { }
    }

    public NetworkMonitor(Dispatcher ui)
    {
        _ui = ui;
        _onStatus = _ => { Stats.Inc("network.event"); _ui.BeginInvoke(Refresh); };
        NetworkInformation.NetworkStatusChanged += _onStatus;
        Refresh();
    }

    /// <summary>The event is Windows' (static): a menu bar made again must not leave this listening.</summary>
    public void Dispose() => NetworkInformation.NetworkStatusChanged -= _onStatus;

    public void Refresh()
    {
        Stats.Inc("network.refresh");
        NetKind kind = NetKind.None; string name = ""; int bars = 0; bool internet = false;
        try
        {
            var p = NetworkInformation.GetInternetConnectionProfile();
            if (p != null)
            {
                kind = p.IsWlanConnectionProfile ? NetKind.Wifi : p.NetworkAdapter?.IanaInterfaceType == 6 ? NetKind.Ethernet : NetKind.Other;
                name = SpecName ?? p.ProfileName ?? "";
                bars = p.GetSignalBars() ?? (kind == NetKind.Wifi ? 3 : 0);
                internet = p.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
            }
        }
        catch (Exception) { }
        if (kind == Kind && name == Name && bars == Bars && internet == Internet) return;
        (Kind, Name, Bars, Internet) = (kind, name, bars, internet);
        Changed?.Invoke();
    }
}

/// <summary>
/// The input mode of the app in front, polled (no event exists for another app's IME): "あ" Japanese, "カ" katakana,
/// "A" direct input; other layouts show their language code. Cross-process IME queries go through the app's default IME
/// window (works for Microsoft IME: M0 S10).
/// When to ask is Core's <see cref="Core.Shell.ImeCheck"/>, looked at every 100 ms on the background ticker (a
/// GetLastInputInfo only; at 200 ms with a 150 ms settle the bar followed the 半角/全角 key in 290 ms, 2026-10-15). The question itself, two messages to another process that wait up to 100 ms each when the
/// app hangs, runs on a thread of its own: on the ticker it held up the hot corners' 16 ms checks. The UI thread
/// hears only about changes.
/// </summary>
public sealed class ImeMonitor : IDisposable
{
    readonly Func<IntPtr> _target;
    readonly Dispatcher _ui;
    readonly BackgroundTicker _ticker;
    readonly Action _checkNow;
    readonly Core.Shell.ImeCheck _rule = new(); // ticker thread
    readonly SignalWorker _asker;
    volatile bool _force;
    (string text, bool open, bool ja, long conversion, long layout) _last = ("", false, false, 0, 0); // asker thread
    public string Text { get; private set; } = "";
    public bool Japanese { get; private set; }
    public bool Open { get; private set; }
    /// <summary>The IME's conversion bits (IME_CMODE: Core's InputModes), read whether it is on or off.</summary>
    public long Conversion { get; private set; }
    /// <summary>The keyboard layout of the app in front (an HKL).</summary>
    public long Layout { get; private set; }
    public event Action? Changed;
    /// <summary>OS id of the thread that asks the app (diagnostics).</summary>
    public uint ThreadId => _asker.ThreadId;

    public ImeMonitor(Func<IntPtr> target, Dispatcher ui, BackgroundTicker ticker)
    {
        _target = target;
        _ui = ui;
        _ticker = ticker;
        _asker = new SignalWorker("ime", Query);
        _checkNow = ticker.Add("ime", TimeSpan.FromMilliseconds(100), Check);
    }

    void Check()
    {
        var lii = new LASTINPUTINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<LASTINPUTINFO>() };
        Native.GetLastInputInfo(ref lii);
        bool force = _force;
        _force = false;
        long since = unchecked((uint)Environment.TickCount - lii.dwTime);
        if (_rule.Due(Environment.TickCount64, lii.dwTime, since, force)) _asker.Signal();
    }

    /// <summary>Look again now (the app in front changed).</summary>
    public void Poll()
    {
        _force = true;
        _checkNow();
    }

    void Query()
    {
        Stats.Inc("ime.poll");
        var h = _target();
        if (h == IntPtr.Zero || !Native.IsWindow(h)) return;
        uint tid = Native.GetWindowThreadProcessId(h, out _);
        long layout = Native.GetKeyboardLayout(tid).ToInt64();
        ushort lang = (ushort)(layout & 0xFFFF);
        var ime = Native.ImmGetDefaultIMEWnd(h);
        bool open = false; long conv = 0;
        if (ime != IntPtr.Zero)
        {
            if (Native.SendMessageTimeout(ime, Native.WM_IME_CONTROL, new IntPtr(Native.IMC_GETOPENSTATUS), IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 100, out var o) != IntPtr.Zero) open = o != IntPtr.Zero;
            // Read when off too: the input menu keeps romaji or kana input when it turns the IME on (stage 5-3).
            if (Native.SendMessageTimeout(ime, Native.WM_IME_CONTROL, new IntPtr(Native.IMC_GETCONVERSIONMODE), IntPtr.Zero, Native.SMTO_ABORTIFHUNG, 100, out var c) != IntPtr.Zero) conv = c.ToInt64();
        }
        bool ja = lang == 0x0411;
        string text = Core.MenuBar.InputModes.BarText(ja, open, conv, ShortName(lang));
        var now = (text, open, ja, conv, layout);
        if (now == _last) return;
        _last = now;
        _ui.BeginInvoke(() =>
        {
            (Text, Open, Japanese, Conversion, Layout) = now;
            Changed?.Invoke();
        });
    }

    public void Dispose()
    {
        _ticker.Remove("ime");
        _asker.Dispose();
    }

    static string ShortName(ushort lang)
    {
        try { return CultureInfo.GetCultureInfo(lang).TwoLetterISOLanguageName.ToUpperInvariant(); }
        catch (CultureNotFoundException) { return "?"; }
    }

    /// <summary>Turns the Japanese IME of the app in front on or off.</summary>
    public void Toggle()
    {
        var h = _target();
        var ime = h == IntPtr.Zero ? IntPtr.Zero : Native.ImmGetDefaultIMEWnd(h);
        if (ime == IntPtr.Zero) return;
        Native.SendMessageTimeout(ime, Native.WM_IME_CONTROL, new IntPtr(Native.IMC_SETOPENSTATUS), new IntPtr(Open ? 0 : 1), Native.SMTO_ABORTIFHUNG, 200, out _);
        Poll();
    }

    /// <summary>
    /// Puts the Japanese IME of the app in front in <paramref name="mode"/> (Core's InputModes: the input menu, stage
    /// 5-3): on or off, then its conversion bits with romaji or kana input kept. False when the app has no IME window.
    /// </summary>
    public bool SetMode(string mode)
    {
        var h = _target();
        var ime = h == IntPtr.Zero ? IntPtr.Zero : Native.ImmGetDefaultIMEWnd(h);
        if (ime == IntPtr.Zero || Core.MenuBar.InputModes.For(mode, Conversion) is not { } set) return false;
        Native.SendMessageTimeout(ime, Native.WM_IME_CONTROL, new IntPtr(Native.IMC_SETOPENSTATUS), new IntPtr(set.Open ? 1 : 0), Native.SMTO_ABORTIFHUNG, 200, out _);
        if (set.Open) Native.SendMessageTimeout(ime, Native.WM_IME_CONTROL, new IntPtr(Native.IMC_SETCONVERSIONMODE), new IntPtr(set.Conversion), Native.SMTO_ABORTIFHUNG, 200, out _);
        Poll();
        return true;
    }

    /// <summary>The keyboard layouts the user has (HKLs, in Windows' order), each with its language's name.</summary>
    public static List<(long Layout, string Name)> Layouts()
    {
        int n = Native.GetKeyboardLayoutList(0, null);
        var list = new IntPtr[Math.Max(n, 0)];
        if (n > 0) Native.GetKeyboardLayoutList(n, list);
        return list.Select(l =>
        {
            int lang = (int)(l.ToInt64() & 0xFFFF);
            string name;
            try { name = CultureInfo.GetCultureInfo(lang).DisplayName; } catch (CultureNotFoundException) { name = $"0x{lang:X4}"; }
            return (l.ToInt64(), name);
        }).ToList();
    }

    /// <summary>Asks the app in front to change to <paramref name="layout"/> (WM_INPUTLANGCHANGEREQUEST; the app may refuse).</summary>
    public void SwitchLayout(long layout)
    {
        var h = _target();
        if (h == IntPtr.Zero) return;
        Native.PostMessage(h, Native.WM_INPUTLANGCHANGEREQUEST, IntPtr.Zero, new IntPtr(layout));
        Poll();
    }
}
