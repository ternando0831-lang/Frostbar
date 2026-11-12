using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.App.Menus;
using Frostbar.Core.Localization;
using Frostbar.Platform.Sys;
using Windows.Devices.Radios;

namespace Frostbar.App.ControlCenter;

/// <summary>
/// A view shown in a panel (a menu bar item's, or inside the Control Center). The services it shows live as long as
/// the shell, so it listens to them only while it is on screen (Loaded → Unloaded); the panel also detaches it for
/// good when it closes, in case Unloaded never comes. Detaching twice is harmless.
/// </summary>
public abstract class PanelView : StackPanel
{
    bool _listening, _detached;

    protected PanelView()
    {
        Loaded += (_, _) =>
        {
            if (_detached || _listening) return;
            _listening = true;
            Listen(true);
            Show(); // what changed before it was on screen
            _filled = true;
        };
        Unloaded += (_, _) => Stop();
    }

    /// <summary>Starts (true) or stops (false) listening to the services.</summary>
    protected abstract void Listen(bool on);
    protected abstract void Show();

    /// <summary>The back arrow (inside the Control Center), or null (diagnostics).</summary>
    public FrameworkElement? BackButton { get; protected set; }

    bool _waiting, _filled;

    /// <summary>
    /// Under a pressed button the rows are not made again (they would change under it, and its release fall on a row
    /// that saw no press: the click was lost). True: put off until the button is up (Show runs again then). Not the
    /// first rows: a status item opens its panel on the press, and put off then, it showed empty until the release
    /// (views: the volume item's Sound view read with no outputs).
    /// </summary>
    protected bool PutOffWhilePressed()
    {
        if (!_filled || Mouse.LeftButton != MouseButtonState.Pressed) return false;
        if (_waiting) return true;
        _waiting = true;
        Dispatcher.BeginInvoke(async () =>
        {
            while (Mouse.LeftButton == MouseButtonState.Pressed && IsLoaded) await Task.Delay(100);
            _waiting = false;
            if (IsLoaded) Show();
        });
        return true;
    }

    /// <summary>Stops listening for good (the panel closed).</summary>
    public void Detach() { _detached = true; Stop(); }

    void Stop()
    {
        if (!_listening) return;
        _listening = false;
        Listen(false);
    }
}

/// <summary>
/// Colours and pieces shared by the Wi-Fi, Bluetooth and Sound views (macOS's menus of the same name): a header with
/// an optional back arrow and switch, section titles, rows that light up under the pointer, a settings link at the
/// bottom.
/// </summary>
public sealed class ViewKit
{
    public readonly ThemeSnapshot Theme;
    public readonly Strings S;
    public readonly Brush Text, Dim, Hover, CircleOff, Accent, SwitchOff;

    public ViewKit(ThemeSnapshot t, Strings s)
    {
        Theme = t;
        S = s;
        Text = Materials.Text(t);
        var c = ((SolidColorBrush)Text).Color;
        Dim = Frozen(new SolidColorBrush(Color.FromArgb((byte)(c.A * 0.6), c.R, c.G, c.B)));
        Hover = Frozen(new SolidColorBrush(t.Dark ? Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0)));
        CircleOff = Frozen(new SolidColorBrush(t.Dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0)));
        Accent = Frozen(new SolidColorBrush(Color.FromRgb(t.Accent.R, t.Accent.G, t.Accent.B)));
        SwitchOff = Frozen(new SolidColorBrush(t.Dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x26, 0, 0, 0)));
    }

    static Brush Frozen(Brush b) { b.Freeze(); return b; }

    /// <summary>
    /// The rows' radius: concentric with the glass the view is on (its radius less the rows' inset). A small panel's by
    /// default; the Control Center's views sit 4 DIPs in from a large piece (C5: they had the small panel's 8 there).
    /// </summary>
    public double RowRadius { get; init; } = GlassShape.PanelRow;

    /// <summary>Opens a page of Frostbar's settings ("wifi", "bluetooth", "sound"…); null in the diagnostics' views.</summary>
    public Action<string>? OpenSettings { get; init; }

    /// <summary>The link at the bottom of a view: Frostbar's own settings page, or Windows' (<paramref name="windowsPage"/>) when there is none to open.</summary>
    public void Settings(string page, string windowsPage)
    {
        if (OpenSettings != null) OpenSettings(page);
        else CommandRunner.Launch(windowsPage);
    }

    /// <summary>
    /// A view's list that scrolls once it is taller than the screen has room for (a long list of networks took the panel
    /// off the bottom of the screen), with the thin scroll bar of the other windows.
    /// </summary>
    public ScrollViewer Scrolling(StackPanel list)
    {
        var viewer = new ScrollViewer
        {
            Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, MaxHeight = Math.Max(200, SystemParameters.WorkArea.Height - 240), // the header, the links and the margins
        };
        MacScrollBar.Apply(viewer, () => Theme.Animations);
        SmoothScroll.Attach(viewer, () => Theme.Animations);
        return viewer;
    }

    /// <summary>The title row: a back arrow when <paramref name="back"/> is given, the title, a switch when <paramref name="toggle"/> is.</summary>
    public (DockPanel row, Border? back) Header(string title, Action? back, PillSwitch? toggle)
    {
        var row = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 6) };
        Border? arrow = null;
        if (back != null)
        {
            var b = arrow = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Brushes.Transparent, Margin = new Thickness(-4, 0, 4, 0), Child = Glyphs.Stroke(Glyphs.Back, 14, Text, 1.8) };
            b.MouseEnter += (_, _) => b.Background = Hover;
            b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
            b.MouseLeftButtonUp += (_, e) => { back(); e.Handled = true; };
            KeyNav.SetRun(b, back); // D2
            DockPanel.SetDock(b, System.Windows.Controls.Dock.Left);
            row.Children.Add(b);
        }
        if (toggle != null)
        {
            toggle.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(toggle, System.Windows.Controls.Dock.Right);
            row.Children.Add(toggle);
        }
        row.Children.Add(new TextBlock { Text = title, FontSize = 13, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        return (row, arrow);
    }

    public PillSwitch Switch(bool on) => new(Accent, SwitchOff, on, Theme.Animations);

    public Border Separator() => new() { Height = 1, Background = CircleOff, Margin = new Thickness(0, 6, 0, 6) };

    public TextBlock Section(string text) => new() { Text = text, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Dim, Margin = new Thickness(0, 2, 0, 4) };

    public TextBlock Note(string text) => new() { Text = text, FontSize = 12, Foreground = Dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 4) };

    /// <summary>A row: a round picture (accent when <paramref name="on"/>), a title, a dim line or nothing, things at the right end.</summary>
    public (Border row, TextBlock detail, Border circle) Row(UIElement picture, bool on, string title, string? detail, UIElement? right, Action? click)
    {
        var circle = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(13), Background = on ? Accent : CircleOff, Child = picture, Margin = new Thickness(0, 0, 10, 0) };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis });
        var line = new TextBlock { Text = detail ?? "", FontSize = 11.5, Foreground = Dim, TextTrimming = TextTrimming.CharacterEllipsis, Visibility = string.IsNullOrEmpty(detail) ? Visibility.Collapsed : Visibility.Visible };
        words.Children.Add(line);
        var dock = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(circle, System.Windows.Controls.Dock.Left);
        dock.Children.Add(circle);
        if (right != null)
        {
            if (right is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(right, System.Windows.Controls.Dock.Right);
            dock.Children.Add(right);
        }
        dock.Children.Add(words);
        var row = new Border { Child = dock, Padding = new Thickness(6, 4, 6, 4), Margin = new Thickness(-6, 0, -6, 0), CornerRadius = new CornerRadius(RowRadius), Background = Brushes.Transparent };
        if (click != null)
        {
            row.MouseEnter += (_, _) => row.Background = Hover;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
            OnClick(row, click);
        }
        return (row, line, circle);
    }

    /// <summary>
    /// A click on <paramref name="target"/>: pressed and let go on it. A row made again while the button was down (a
    /// Wi-Fi scan's answer) is a new element that saw no press, so the release over it does nothing; it took the
    /// release for a click on whatever row was under the pointer then, and joined that network (2026-10-14 review).
    /// </summary>
    public static void OnClick(UIElement target, Action click)
    {
        KeyNav.SetRun(target, click); // the keys reach it too (D2)
        bool pressed = false;
        target.MouseLeftButtonDown += (_, _) => pressed = true;
        target.MouseLeave += (_, _) => pressed = false;
        target.MouseLeftButtonUp += (_, e) =>
        {
            if (!pressed) return;
            pressed = false;
            e.Handled = true;
            click();
        };
    }

    /// <summary>A link row at the bottom ("Wi-Fi Settings…").</summary>
    public Border Link(string text, Action click)
    {
        var b = new Border { Child = new TextBlock { Text = text }, Padding = new Thickness(6, 5, 6, 5), Margin = new Thickness(-6, 0, -6, 0), CornerRadius = new CornerRadius(RowRadius), Background = Brushes.Transparent };
        b.MouseEnter += (_, _) => b.Background = Hover;
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        OnClick(b, click);
        return b;
    }

    /// <summary>A glyph on the 16 grid, stroked or filled, in the text colour (or white on an accent circle).</summary>
    public FrameworkElement Picture(Geometry g, bool stroke, bool onAccent, double size = 15) =>
        stroke ? Glyphs.Stroke(g, size, onAccent ? Brushes.White : Text, 1.5) : Glyphs.Fill(g, size, onAccent ? Brushes.White : Text);

    /// <summary>Wi-Fi's arcs with <paramref name="bars"/> (0–4) of them bright.</summary>
    public FrameworkElement WifiBars(int bars, bool onAccent, double size = 15)
    {
        var g = new Grid { Width = 16, Height = 16 };
        var bright = onAccent ? Brushes.White : Text;
        var faint = onAccent ? new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)) : Dim;
        int lit = Math.Clamp(bars, 0, 4);
        for (int i = 0; i < Glyphs.WifiArcs.Length; i++)
        {
            var brush = i < Math.Max(1, lit) ? bright : faint;
            g.Children.Add(i == 0 ? Glyphs.Fill(Glyphs.WifiArcs[i], 16, brush) : Glyphs.Stroke(Glyphs.WifiArcs[i], 16, brush, 1.6));
        }
        if (Math.Abs(size - 16) > 0.01) g.LayoutTransform = new ScaleTransform(size / 16, size / 16);
        return g;
    }
}

/// <summary>
/// Wi-Fi (macOS's Wi-Fi menu): the switch, the network joined, the others nearby with their strength and a padlock
/// when secured; a click joins one (asking for the password when Windows has none saved). Looks for networks when it
/// opens.
/// </summary>
public sealed class WifiView : PanelView
{
    readonly ViewKit _k;
    readonly WifiService _wifi;
    readonly RadioService _radios;
    readonly Action _close;
    readonly Func<string, Task> _askPassword;
    readonly PillSwitch _switch;
    readonly StackPanel _list = new();
    readonly TextBlock _scanning;
    // The networks in the order shown: kept while the pointer is over the list (a scan's answers re-sorted the rows
    // by strength under the pointer, 2026-10-14 review), taken from the service (strongest first) otherwise.
    readonly List<string> _order = new();
    string? _joining, _failed;

    public WifiView(ViewKit k, WifiService wifi, RadioService radios, Action close, Action? back, Func<string, Task> askPassword)
    {
        _k = k;
        _wifi = wifi;
        _radios = radios;
        _close = close;
        _askPassword = askPassword;
        Width = 280;
        _switch = k.Switch(radios.Wifi == RadioState.On);
        _switch.Toggled += on => _ = radios.SetAsync(RadioKind.WiFi, on);
        var (header, arrow) = k.Header(k.S["cc.wifi"], back, radios.HasWifi ? _switch : null);
        BackButton = arrow;
        Children.Add(header);
        _scanning = new TextBlock { Text = k.S["wifi.scanning"], FontSize = 11.5, Foreground = k.Dim, HorizontalAlignment = HorizontalAlignment.Right, Visibility = Visibility.Collapsed };
        Children.Add(k.Scrolling(_list));
        Children.Add(k.Separator());
        Children.Add(k.Link(k.S["wifi.settings"], () => { close(); k.Settings("wifi", "ms-settings:network-wifi"); }));
        Show();
        _ = wifi.ScanAsync();
    }

    protected override void Listen(bool on)
    {
        if (on) { _wifi.Changed += Show; _radios.Changed += Show; }
        else { _wifi.Changed -= Show; _radios.Changed -= Show; }
    }

    /// <summary>The networks shown now (diagnostics).</summary>
    public IReadOnlyList<WifiNetwork> Shown { get; private set; } = [];
    public bool SwitchOn => _switch.IsOn;
    public bool Scanning => _wifi.Scanning;
    public string Joined => _wifi.ConnectedSsid;

    protected override void Show()
    {
        if (PutOffWhilePressed()) return; // not made again under a pressed button: the rows would change under it
        _switch.IsOn = _radios.Wifi == RadioState.On;
        // Rows that came grow in, rows that went shrink out, the others slide (ListMotion): the list jumped at each scan.
        var before = ListMotion.Take(_list);
        Fill();
        ListMotion.Show(_list, before, _k.Theme.Animations);
    }

    void Fill()
    {
        bool keep = _list.IsMouseOver;
        _list.Children.Clear();
        if (_radios.Wifi != RadioState.On) { Shown = []; return; }
        if (_wifi.Problem != null && _wifi.Networks.Count == 0)
        {
            bool location = _wifi.Problem.Contains("Denied", StringComparison.OrdinalIgnoreCase);
            _list.Children.Add(ListMotion.Keyed(_k.Note(location ? _k.S["wifi.locationNeeded"] : _k.S["wifi.unavailable"]), "note:problem"));
            if (location) _list.Children.Add(ListMotion.Keyed(_k.Link(_k.S["wifi.locationSettings"], () => { _close(); CommandRunner.Launch("ms-settings:privacy-location"); }), "link:location"));
            Shown = [];
            return;
        }
        if (!keep) _order.Clear();
        foreach (var n in _wifi.Networks) if (!_order.Contains(n.Ssid)) _order.Add(n.Ssid);
        Shown = _wifi.Networks.OrderBy(n => _order.IndexOf(n.Ssid)).ToList();
        var joined = Shown.Where(n => n.Connected).ToList();
        var others = Shown.Where(n => !n.Connected).ToList();
        if (joined.Count > 0)
        {
            _list.Children.Add(ListMotion.Keyed(_k.Separator(), "line:joined"));
            foreach (var n in joined) _list.Children.Add(Row(n));
        }
        var head = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(_scanning, System.Windows.Controls.Dock.Right);
        if (_scanning.Parent is Panel p) p.Children.Remove(_scanning);
        head.Children.Add(_scanning);
        head.Children.Add(_k.Section(_k.S["wifi.others"]));
        _scanning.Visibility = _wifi.Scanning ? Visibility.Visible : Visibility.Collapsed;
        _list.Children.Add(ListMotion.Keyed(_k.Separator(), "line:others"));
        _list.Children.Add(ListMotion.Keyed(head, "head:others"));
        foreach (var n in others) _list.Children.Add(Row(n));
        if (others.Count == 0 && !_wifi.Scanning) _list.Children.Add(ListMotion.Keyed(_k.Note(_k.S["wifi.none"]), "note:none"));
        if (_failed != null) _list.Children.Add(ListMotion.Keyed(_k.Note(_k.S.F("wifi.failed", _failed)), "note:failed"));
    }

    Border Row(WifiNetwork n)
    {
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        if (n.Secured) right.Children.Add(_k.Picture(Glyphs.Lock, stroke: false, onAccent: false, size: 12));
        string? detail = n.Ssid == _joining ? _k.S["wifi.joining"] : null;
        var (row, _, _) = _k.Row(_k.WifiBars(n.Bars, n.Connected), n.Connected, n.Ssid, detail, right, n.Connected ? null : () => _ = JoinAsync(n));
        return ListMotion.Keyed(row, "net:" + n.Ssid);
    }

    async Task JoinAsync(WifiNetwork n)
    {
        _joining = n.Ssid;
        _failed = null;
        Show();
        var result = await _wifi.ConnectAsync(n.Ssid, null);
        _joining = null;
        // No saved profile (or a changed password): ask, as macOS does, in a window that can take the typing.
        if (result == Windows.Devices.WiFi.WiFiConnectionStatus.InvalidCredential && n.Secured)
        {
            _close();
            await _askPassword(n.Ssid);
            return;
        }
        if (result != Windows.Devices.WiFi.WiFiConnectionStatus.Success) _failed = n.Ssid;
        Show();
    }
}

/// <summary>
/// Bluetooth (macOS's Bluetooth menu): the switch and the paired devices, connected ones first. A click connects or
/// disconnects an audio device (the others connect themselves, and their rows take no click); the row says so while
/// it happens, and a line under the list tells when the device did not follow.
/// </summary>
public sealed class BluetoothView : PanelView
{
    readonly ViewKit _k;
    readonly BluetoothService _bt;
    readonly RadioService _radios;
    readonly PillSwitch _switch;
    readonly StackPanel _list = new();
    readonly Dictionary<string, Border> _rows = new();
    // The order the devices were first shown in (connected first): rows keep their place while the view is open, or
    // the device just clicked jumps away from under the pointer as it connects (2026-10-13).
    readonly List<string> _order = new();
    (string Name, bool Connecting)? _failed;

    public BluetoothView(ViewKit k, BluetoothService bt, RadioService radios, Action close, Action? back)
    {
        _k = k;
        _bt = bt;
        _radios = radios;
        Width = 280;
        _switch = k.Switch(radios.Bluetooth == RadioState.On);
        _switch.Toggled += on => _ = radios.SetAsync(RadioKind.Bluetooth, on);
        var (header, arrow) = k.Header(k.S["cc.bluetooth"], back, radios.HasBluetooth ? _switch : null);
        BackButton = arrow;
        Children.Add(header);
        Children.Add(k.Scrolling(_list));
        Children.Add(k.Separator());
        Children.Add(k.Link(k.S["bt.settings"], () => { close(); k.Settings("bluetooth", "ms-settings:bluetooth"); }));
        Show();
        _ = bt.ReadBatteriesAsync();
    }

    protected override void Listen(bool on)
    {
        if (on) { _bt.Changed += Show; _radios.Changed += Show; }
        else { _bt.Changed -= Show; _radios.Changed -= Show; }
    }

    public IReadOnlyList<BluetoothItem> Shown { get; private set; } = [];
    public bool SwitchOn => _switch.IsOn;
    /// <summary>The devices' rows by name, and the device that did not follow the last click (diagnostics).</summary>
    public IReadOnlyDictionary<string, Border> Rows => _rows;
    public string? Failed => _failed?.Name;
    public bool Busy(BluetoothItem d) => _bt.IsBusy(d, out _);

    protected override void Show()
    {
        if (PutOffWhilePressed()) return; // a device row pressed as a battery read came in: its click was lost
        _switch.IsOn = _radios.Bluetooth == RadioState.On;
        var before = ListMotion.Take(_list); // rows come, go and slide (ListMotion)
        Fill();
        ListMotion.Show(_list, before, _k.Theme.Animations);
    }

    void Fill()
    {
        _list.Children.Clear();
        _rows.Clear();
        if (_radios.Bluetooth != RadioState.On) { Shown = []; return; }
        foreach (var d in _bt.Devices) if (!_order.Contains(d.Name)) _order.Add(d.Name);
        Shown = _bt.Devices.OrderBy(d => _order.IndexOf(d.Name)).ToList();
        _list.Children.Add(ListMotion.Keyed(_k.Separator(), "line:devices"));
        _list.Children.Add(ListMotion.Keyed(_k.Section(_k.S["bt.devices"]), "head:devices"));
        if (Shown.Count == 0) _list.Children.Add(ListMotion.Keyed(_k.Note(_k.S["bt.none"]), "note:none"));
        foreach (var d in Shown)
        {
            var g = d.Kind switch
            {
                BluetoothKind.Audio => Glyphs.Headphones,
                BluetoothKind.Mouse => Glyphs.Mouse,
                BluetoothKind.Keyboard => Glyphs.Keyboard,
                BluetoothKind.Phone => Glyphs.Phone,
                BluetoothKind.Computer => Glyphs.Computer,
                BluetoothKind.Gamepad => Glyphs.Gamepad,
                _ => Glyphs.Bluetooth,
            };
            bool busy = _bt.IsBusy(d, out bool connecting);
            string detail = busy ? _k.S[connecting ? "bt.connecting" : "bt.disconnecting"] : d.Connected ? _k.S["bt.connected"] : _k.S["bt.notConnected"];
            if (_bt.BatteryOf(d) is int level) detail += "  ·  " + _k.S.F("bt.battery", level); // as macOS's menu shows a connected device's battery
            var (row, _, _) = _k.Row(_k.Picture(g, stroke: true, onAccent: d.Connected), d.Connected, d.Name, detail, null, d.CanConnect && !busy ? () => _ = ToggleAsync(d) : null);
            _rows[d.Name] = row;
            _list.Children.Add(ListMotion.Keyed(row, "dev:" + d.Name));
        }
        if (_failed is { } f) _list.Children.Add(ListMotion.Keyed(_k.Note(_k.S.F(f.Connecting ? "bt.connectFailed" : "bt.disconnectFailed", f.Name)), "note:failed"));
    }

    async Task ToggleAsync(BluetoothItem d)
    {
        bool connect = !d.Connected;
        _failed = null;
        if (!await _bt.SetConnectedAsync(d, connect)) _failed = (d.Name, connect);
        Show();
    }
}

/// <summary>What the Focus view reads and changes (Windows' do not disturb and focus sessions underneath).</summary>
public sealed record FocusServices(Func<bool?> DoNotDisturb, Func<bool, bool> SetDoNotDisturb, Func<bool> SessionSupported, Func<bool> SessionActive,
    Func<TimeSpan, bool> StartSession, Action StopSession);

/// <summary>
/// Focus (macOS's Focus module): Do Not Disturb on and off (a click on its row, as macOS does), and Windows' timed focus
/// sessions (30 minutes, 1 hour, 2 hours; a running one can be ended). Windows' focus settings stay in Windows,
/// linked at the bottom.
/// </summary>
public sealed class FocusView : PanelView
{
    static readonly TimeSpan[] Lengths = [TimeSpan.FromMinutes(30), TimeSpan.FromHours(1), TimeSpan.FromHours(2)];
    readonly ViewKit _k;
    readonly FocusServices _sv;
    readonly StackPanel _list = new();
    string? _problem;

    public FocusView(ViewKit k, FocusServices services, Action close, Action? back)
    {
        _k = k;
        _sv = services;
        Width = 280;
        var (header, arrow) = k.Header(k.S["cc.focus"], back, null);
        BackButton = arrow;
        Children.Add(header);
        Children.Add(_list);
        Children.Add(k.Separator());
        Children.Add(k.Link(k.S["focus.settings"], () => { close(); CommandRunner.Launch("ms-settings:quiethours"); }));
        Show();
    }

    protected override void Listen(bool on) { } // read when shown and after each change: Windows tells no one

    /// <summary>The Do Not Disturb row and the sessions' rows (diagnostics: the specs click them).</summary>
    public Border? DoNotDisturbRow { get; private set; }
    public Dictionary<string, Border> SessionRows { get; } = new();
    public bool? DoNotDisturbOn { get; private set; }
    public bool SessionOn { get; private set; }

    protected override void Show()
    {
        if (PutOffWhilePressed()) return; // not made again under a pressed button
        var before = ListMotion.Take(_list); // a session's rows give way to its stop row, moving (ListMotion)
        Fill();
        ListMotion.Show(_list, before, _k.Theme.Animations);
    }

    void Fill()
    {
        _list.Children.Clear();
        SessionRows.Clear();
        _list.Children.Add(ListMotion.Keyed(_k.Separator(), "line:dnd"));
        bool? dnd = DoNotDisturbOn = _sv.DoNotDisturb();
        bool on = dnd == true;
        var (row, _, _) = _k.Row(_k.Picture(Glyphs.Moon, stroke: false, onAccent: on), on, _k.S["focus.dnd"], on ? _k.S["cc.on"] : _k.S["cc.off"], null,
            dnd == null ? null : () => Toggle(!on));
        DoNotDisturbRow = row;
        _list.Children.Add(ListMotion.Keyed(row, "dnd"));
        if (_sv.SessionSupported())
        {
            _list.Children.Add(ListMotion.Keyed(_k.Separator(), "line:session"));
            _list.Children.Add(ListMotion.Keyed(_k.Section(_k.S["focus.session"]), "head:session"));
            bool active = SessionOn = _sv.SessionActive();
            if (active)
            {
                var (r, _, _) = _k.Row(_k.Picture(Glyphs.Timer, stroke: true, onAccent: true), true, _k.S["focus.sessionOn"], _k.S["focus.endSession"], null, () => { _sv.StopSession(); Later(); });
                SessionRows["stop"] = r;
                _list.Children.Add(ListMotion.Keyed(r, "session:stop"));
            }
            else
                foreach (var length in Lengths)
                {
                    string label = length.TotalMinutes < 60 ? _k.S.F("dur.m", (int)length.TotalMinutes) : _k.S.F("dur.h", (int)length.TotalHours);
                    var (r, _, _) = _k.Row(_k.Picture(Glyphs.Timer, stroke: true, onAccent: false), false, label, null, null, () =>
                    {
                        _problem = _sv.StartSession(length) ? null : _k.S["focus.sessionFailed"]; // a later start that works takes the note away
                        Later();
                    });
                    SessionRows[((int)length.TotalMinutes).ToString()] = r;
                    _list.Children.Add(ListMotion.Keyed(r, "session:" + (int)length.TotalMinutes));
                }
        }
        if (_problem != null) _list.Children.Add(ListMotion.Keyed(_k.Note(_problem), "note:problem"));
    }

    void Toggle(bool on)
    {
        _problem = _sv.SetDoNotDisturb(on) ? null : _k.S["focus.dndFailed"];
        Later();
    }

    /// <summary>Shows the state again once Windows has taken the change (it reports it a moment later).</summary>
    void Later()
    {
        Show();
        Dispatcher.BeginInvoke(async () => { await Task.Delay(400); Show(); });
    }
}

/// <summary>Sound (macOS's Sound menu): the volume, and the output devices with a tick on the one in use; a click switches to another.</summary>
public sealed class SoundView : PanelView
{
    readonly ViewKit _k;
    readonly AudioService _audio;
    readonly PillSlider _volume;
    readonly StackPanel _list = new();

    public SoundView(ViewKit k, AudioService audio, Action close, Action? back)
    {
        _k = k;
        _audio = audio;
        Width = 280;
        var (header, arrow) = k.Header(k.S["cc.sound"], back, null);
        BackButton = arrow;
        Children.Add(header);
        _volume = new PillSlider(Glyphs.Speaker) { Margin = new Thickness(0, 2, 0, 4) };
        var t = k.Theme;
        _volume.SetColors(
            new SolidColorBrush(t.Dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0, 0, 0)),
            new SolidColorBrush(t.Dark ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF) : Colors.White),
            new SolidColorBrush(Color.FromRgb(0x70, 0x70, 0x70)), new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0)));
        _volume.ValueChanged += v => audio.SetVolume((float)v);
        Children.Add(_volume);
        Children.Add(k.Scrolling(_list));
        Children.Add(k.Separator());
        Children.Add(k.Link(k.S["status.soundSettings"], () => { close(); k.Settings("sound", "ms-settings:sound"); }));
        Show();
    }

    protected override void Listen(bool on)
    {
        if (on) _audio.Changed += Show;
        else _audio.Changed -= Show;
    }

    public PillSlider Volume => _volume;
    public IReadOnlyList<AudioOutput> Shown { get; private set; } = [];

    protected override void Show()
    {
        // Not made again under a pressed button: an output row pressed as the volume changed lost its click, and a
        // dragged slider made the outputs again at each step (the slider follows itself).
        if (PutOffWhilePressed()) return;
        _volume.Glide(_audio.Muted ? 0 : _audio.Volume, _k.Theme.Animations);
        var before = ListMotion.Take(_list); // outputs come and go moving (ListMotion)
        _list.Children.Clear();
        Shown = _audio.Outputs;
        _list.Children.Add(ListMotion.Keyed(_k.Separator(), "line:outputs"));
        _list.Children.Add(ListMotion.Keyed(_k.Section(_k.S["cc.output"]), "head:outputs"));
        foreach (var o in _audio.Outputs)
        {
            var tick = o.IsDefault ? _k.Picture(Glyphs.Check, stroke: true, onAccent: false, size: 13) : null;
            var (row, _, _) = _k.Row(_k.Picture(Glyphs.Speaker, stroke: false, onAccent: o.IsDefault), o.IsDefault, o.Name, null, tick, o.IsDefault ? null : () => _audio.SetDefaultOutput(o.Id));
            _list.Children.Add(ListMotion.Keyed(row, "out:" + o.Id));
        }
        ListMotion.Show(_list, before, _k.Theme.Animations);
    }
}
