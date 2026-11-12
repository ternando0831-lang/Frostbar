using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.App.Menus;
using Frostbar.Core.Devices;
using Frostbar.Platform.Sys;
using Windows.Devices.Radios;

namespace Frostbar.App.Settings;

/// <summary>The Wi-Fi and Bluetooth pages (macOS's System Settings → Wi-Fi, Bluetooth), in place of Windows' settings.</summary>
public sealed partial class SettingsWindow
{
    // ---- Wi-Fi ------------------------------------------------------------------------------------------------
    // The switch; the network joined with its details (System Settings' "Details…"); the networks Windows remembers,
    // each with auto-join and forget; the others nearby with Join. What the page cannot do (IP settings, proxies)
    // stays in Windows' settings, linked at the bottom.

    void WifiPage()
    {
        if (_host.System is not { } sys) { Add(Note(_s["set.notReady"])); return; }
        var radios = sys.Radios;
        var wifi = sys.Wifi;
        var power = Switch("wifi.power", radios.Wifi == RadioState.On, on => _ = radios.SetAsync(RadioKind.WiFi, on));
        if (!radios.HasWifi) { power.IsEnabled = false; power.Opacity = 0.4; }
        Add(Group(ItemRow(SquarePicture(WifiPicture(4, Brushes.White), Color.FromRgb(0x0A, 0x84, 0xFF)), _s["cc.wifi"], radios.HasWifi ? null : _s["cc.unavailable"], power)));

        var area = new StackPanel();
        Add(area);
        WlanConnection? joined = null;
        IReadOnlyList<WlanProfile> known = [];
        string? joining = null, failed = null;
        bool read = false;
        // Made for the search: the networks are there at once, so the details' labels ("IP アドレス"…) are among its
        // words. The search's index reads them off the UI thread first (SearchIndex); the window's own search reads them
        // here (read at once, the page took 57–64 ms against 1–5 ms for most pages: 2026-10-16).
        if (_indexing) { (joined, known) = _wlanForIndex ?? (WlanProfiles.Current(), WlanProfiles.List()); read = true; }

        // Networks come, go and move moving (ListMotion): the page was made again at each scan's answer, its rows and
        // its height jumping (2026-10-19, the user's "B2").
        void Fill() => Refill(area, FillNow);

        void FillNow()
        {
            power.IsOn = radios.Wifi == RadioState.On;
            area.Children.Clear();
            ForgetControls("wifi.joined", "wifi.known.", "wifi.join.");
            if (!radios.HasWifi) return; // no adapter: the row above says "unavailable" (it went on to "Wi-Fi is off")
            if (radios.Wifi != RadioState.On) { area.Children.Add(ListMotion.Keyed(Note(_s["wifi.off"]), "note:off")); return; }
            if (wifi.Problem != null && wifi.Networks.Count == 0)
            {
                bool location = wifi.Problem.Contains("Denied", StringComparison.OrdinalIgnoreCase);
                area.Children.Add(ListMotion.Keyed(Note(location ? _s["wifi.locationNeeded"] : _s["wifi.unavailable"]), "note:problem"));
                if (location) area.Children.Add(ListMotion.Keyed(Footer(_s["wifi.locationSettings"], () => CommandRunner.Launch("ms-settings:privacy-location")), "link:location"));
                return;
            }

            // The network joined, and its details.
            if (joined is { } c)
            {
                var current = known.FirstOrDefault(p => p.Name == c.Profile);
                var rows = new List<FrameworkElement>
                {
                    ListMotion.Keyed(ItemRow(Circle(WifiPicture(WifiDetails.Bars(c.Quality), Brushes.White), on: true), c.Ssid, _s["wifi.connected"],
                        current != null ? KnownMenu("wifi.joined", current, connected: true) : null), "net:" + c.Ssid),
                };
                void Detail(string label, string value) { if (value.Length > 0) rows.Add(ListMotion.Keyed(Row(label, Value(value, wrap: true)), "detail:" + label)); }
                Detail(_s["wifi.signal"], c.RssiDbm is int dbm ? _s.F("wifi.dbm", dbm, c.Quality) : $"{c.Quality}%"); // its brackets of the language (full width in English)
                Detail(_s["wifi.security"], _s[WifiDetails.SecurityKey(c.Auth, c.Cipher)]);
                Detail(_s["wifi.standard"], WifiDetails.Standard(c.PhyType, c.FrequencyMhz));
                string band = WifiDetails.Band(c.FrequencyMhz, c.Channel);
                Detail(_s["wifi.band"], band.Length > 0 && c.Channel > 0 ? _s.F("wifi.bandChannel", band, c.Channel) : band);
                Detail(_s["wifi.speed"], c.RxMbps > 0 ? _s.F("wifi.speedValue", c.RxMbps, c.TxMbps) : "");
                Detail(_s["wifi.ipv4"], string.Join("\n", c.IPv4));
                Detail(_s["wifi.subnet"], c.SubnetMask);
                Detail(_s["wifi.router"], string.Join("\n", c.Routers));
                Detail(_s["wifi.dns"], string.Join("\n", c.Dns));
                Detail(_s["wifi.ipv6"], string.Join("\n", c.IPv6));
                Detail(_s["wifi.hardware"], c.HardwareAddress);
                area.Children.Add(ListMotion.Keyed(Group([.. rows]), "card:joined"));
            }
            else if (read) area.Children.Add(ListMotion.Keyed(Note(_s["wifi.notJoined"]), "note:notJoined"));

            // The networks Windows remembers (the joined one is above).
            var others = known.Where(p => joined == null || p.Name != joined.Profile).ToList();
            if (others.Count > 0)
            {
                area.Children.Add(ListMotion.Keyed(Caption(_s["wifi.known"]), "head:known"));
                area.Children.Add(ListMotion.Keyed(Group([.. others.Select(p =>
                {
                    // Its strength when in range; faint when not.
                    var near = wifi.Networks.FirstOrDefault(n => n.Ssid == p.Name);
                    var picture = WifiPicture(near?.Bars ?? 4, _text);
                    if (near == null) picture.Opacity = 0.3;
                    return ListMotion.Keyed(ItemRow(Circle(picture, on: false), p.Name, p.AutoConnect ? _s["wifi.autoJoin"] : _s["wifi.manualJoin"], KnownMenu("wifi.known." + p.Name, p, connected: false)), "known:" + p.Name);
                })]), "card:known"));
            }

            // The others nearby.
            var nearby = wifi.Networks.Where(n => !n.Connected && (joined == null || n.Ssid != joined.Ssid)).ToList();
            var head = new DockPanel { LastChildFill = true };
            if (wifi.Scanning)
            {
                var scanning = new TextBlock { Text = _s["wifi.scanning"], FontSize = 11.5, Foreground = _dim, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 6, 6) };
                DockPanel.SetDock(scanning, System.Windows.Controls.Dock.Right);
                head.Children.Add(scanning);
            }
            head.Children.Add(Caption(_s["wifi.others"]));
            area.Children.Add(ListMotion.Keyed(head, "head:others"));
            if (nearby.Count == 0) area.Children.Add(ListMotion.Keyed(Note(wifi.Scanning ? _s["wifi.scanning"] : _s["wifi.none"]), "note:nearby"));
            else area.Children.Add(ListMotion.Keyed(Group([.. nearby.Select(n =>
            {
                var right = new StackPanel { Orientation = Orientation.Horizontal };
                if (n.Secured) right.Children.Add(new Border { Child = Glyphs.Fill(Glyphs.Lock, 12, _dim), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
                var join = Button(n.Ssid == joining ? _s["wifi.joining"] : _s["wifi.join"], () => _ = Join(n));
                join.IsEnabled = joining == null;
                Controls["wifi.join." + n.Ssid] = join;
                right.Children.Add(join);
                return ListMotion.Keyed(ItemRow(Circle(WifiPicture(n.Bars, _text), on: false), n.Ssid, null, right), "near:" + n.Ssid);
            })]), "card:nearby"));
            if (failed != null) area.Children.Add(ListMotion.Keyed(Note(_s.F("wifi.failed", failed)), "note:failed"));
        }

        async Task Join(WifiNetwork n)
        {
            joining = n.Ssid;
            failed = null;
            Fill();
            var result = await wifi.ConnectAsync(n.Ssid, null);
            joining = null;
            if (result == Windows.Devices.WiFi.WiFiConnectionStatus.InvalidCredential && n.Secured) await sys.AskWifiPassword(n.Ssid);
            else if (result != Windows.Devices.WiFi.WiFiConnectionStatus.Success) failed = n.Ssid;
            Reread();
        }

        // One read at a time; one asked for meanwhile runs after it (a read begun before a network was forgotten still
        // listed it, and the page kept the row: 2026-10-13).
        bool pending = false, again = false;
        void Reread()
        {
            if (pending) { again = true; return; }
            pending = true;
            again = false;
            OffUi(() => (WlanProfiles.Current(), WlanProfiles.List()), r =>
            {
                pending = false;
                (joined, known) = r;
                read = true;
                Fill();
                if (again) Reread();
            });
        }

        // "…": join automatically, forget (the joined network's warns that the connection ends).
        MacControls.MenuButton KnownMenu(string key, WlanProfile p, bool connected)
        {
            var menu = new MacControls.MenuButton(_t, () =>
            [
                new(_s["wifi.autoJoinItem"], p.AutoConnect, () =>
                {
                    bool auto = !p.AutoConnect;
                    OffUi(() => WlanProfiles.SetAutoConnect(p.Name, auto), ok => { if (!ok) failed = p.Name; Reread(); });
                }),
                new(_s["wifi.forgetItem"], false, () => Dialogs.Confirm(_t, _s.F("wifi.forgetQuestion", p.Name),
                    connected ? _s["wifi.forgetJoined"] : _s["wifi.forgetDetail"], _s["dlg.cancel"], _s["wifi.forget"],
                    () => OffUi(() => WlanProfiles.Delete(p.Name), _ => Reread()))),
            ]);
            Controls[key] = menu;
            return menu;
        }

        Fill();
        Follow(h => { wifi.Changed += h; radios.Changed += h; }, h => { wifi.Changed -= h; radios.Changed -= h; }, () => { Fill(); Reread(); });
        Reread();
        if (!_indexing) _ = wifi.ScanAsync();
        AddFooter(_s["wifi.windowsSettings"], "ms-settings:network-wifi");
    }

    FrameworkElement WifiPicture(int bars, Brush bright)
    {
        var g = new Grid { Width = 16, Height = 16 };
        var faint = new SolidColorBrush(Color.FromArgb(0x59, ((SolidColorBrush)bright).Color.R, ((SolidColorBrush)bright).Color.G, ((SolidColorBrush)bright).Color.B));
        int lit = Math.Clamp(bars, 1, 4);
        for (int i = 0; i < Glyphs.WifiArcs.Length; i++)
        {
            var brush = i < lit ? bright : faint;
            g.Children.Add(i == 0 ? Glyphs.Fill(Glyphs.WifiArcs[i], 16, brush) : Glyphs.Stroke(Glyphs.WifiArcs[i], 16, brush, 1.6));
        }
        g.LayoutTransform = new ScaleTransform(14 / 16.0, 14 / 16.0);
        return g;
    }

    // ---- Bluetooth --------------------------------------------------------------------------------------------
    // The switch and the paired devices with their state and battery; audio devices connect and disconnect here (as in
    // the Control Center). Pairing and removing stay with Windows' settings, linked at the bottom.

    void BluetoothPage()
    {
        if (_host.System is not { } sys) { Add(Note(_s["set.notReady"])); return; }
        var radios = sys.Radios;
        var bt = sys.Bluetooth;
        var power = Switch("bt.power", radios.Bluetooth == RadioState.On, on => _ = radios.SetAsync(RadioKind.Bluetooth, on));
        if (!radios.HasBluetooth) { power.IsEnabled = false; power.Opacity = 0.4; }
        Add(Group(ItemRow(SquarePicture(Glyphs.Stroke(Glyphs.Bluetooth, 14, Brushes.White, 1.5), Color.FromRgb(0x0A, 0x84, 0xFF)), _s["cc.bluetooth"], radios.HasBluetooth ? null : _s["cc.unavailable"], power)));
        var area = new StackPanel();
        Add(area);
        // Rows keep their places while the page is open (a device that connects would jump from under the pointer).
        var order = new List<string>();
        (string Name, bool Connecting)? failed = null;

        void Fill() => Refill(area, FillNow); // devices come and go moving (ListMotion, "B2")

        void FillNow()
        {
            power.IsOn = radios.Bluetooth == RadioState.On;
            area.Children.Clear();
            ForgetControls("bt.connect.");
            if (!radios.HasBluetooth) return; // no adapter: the row above says "unavailable" (it went on to "Bluetooth is off")
            if (radios.Bluetooth != RadioState.On) { area.Children.Add(ListMotion.Keyed(Note(_s["bt.off"]), "note:off")); return; }
            foreach (var d in bt.Devices) if (!order.Contains(d.Name)) order.Add(d.Name);
            var devices = bt.Devices.OrderBy(d => order.IndexOf(d.Name)).ToList();
            area.Children.Add(ListMotion.Keyed(Caption(_s["bt.myDevices"]), "head:devices"));
            if (devices.Count == 0) { area.Children.Add(ListMotion.Keyed(Note(_s["bt.none"]), "note:none")); return; }
            area.Children.Add(ListMotion.Keyed(Group([.. devices.Select(d =>
            {
                bool busy = bt.IsBusy(d, out bool connecting);
                string state = busy ? _s[connecting ? "bt.connecting" : "bt.disconnecting"] : d.Connected ? _s["bt.connected"] : _s["bt.notConnected"];
                if (bt.BatteryOf(d) is int level) state += "  ·  " + _s.F("bt.battery", level);
                Button? action = null;
                if (d.CanConnect)
                {
                    action = Button(d.Connected ? _s["bt.disconnect"] : _s["bt.connect"], () => _ = Toggle(d));
                    action.IsEnabled = !busy;
                    Controls["bt.connect." + d.Name] = action;
                }
                return ListMotion.Keyed(ItemRow(Circle(Glyphs.Stroke(BluetoothGlyph(d.Kind), 14, d.Connected ? Brushes.White : _text, 1.5), on: d.Connected), d.Name, state, action), "dev:" + d.Name);
            })]), "card:devices"));
            if (failed is { } f) area.Children.Add(ListMotion.Keyed(Note(_s.F(f.Connecting ? "bt.connectFailed" : "bt.disconnectFailed", f.Name)), "note:failed"));
            area.Children.Add(ListMotion.Keyed(Note(_s["bt.pairingNote"]), "note:pairing"));
        }

        async Task Toggle(BluetoothItem d)
        {
            bool connect = !d.Connected;
            failed = null;
            if (!await bt.SetConnectedAsync(d, connect)) failed = (d.Name, connect);
            // Up to 20 s later: another page shown meanwhile (or this one made anew), this one's rows are not the
            // window's any more (they took the live page's names in Controls, at no place on the screen).
            if (!area.IsLoaded) return;
            Fill();
        }

        Fill();
        Follow(h => { bt.Changed += h; radios.Changed += h; }, h => { bt.Changed -= h; radios.Changed -= h; }, Fill);
        if (!_indexing) _ = bt.ReadBatteriesAsync();
        AddFooter(_s["bt.windowsSettings"], "ms-settings:bluetooth");
    }

    static Geometry BluetoothGlyph(BluetoothKind kind) => kind switch
    {
        BluetoothKind.Audio => Glyphs.Headphones,
        BluetoothKind.Mouse => Glyphs.Mouse,
        BluetoothKind.Keyboard => Glyphs.Keyboard,
        BluetoothKind.Phone => Glyphs.Phone,
        BluetoothKind.Computer => Glyphs.Computer,
        BluetoothKind.Gamepad => Glyphs.Gamepad,
        _ => Glyphs.Bluetooth,
    };

    // ---- pieces of the system pages ------------------------------------------------------------------------------

    /// <summary>
    /// Fills <paramref name="area"/> again by <paramref name="fill"/>, moving (ListMotion): its keyed parts (cards, notes,
    /// headings) come, go and slide, and so do the keyed rows inside each card kept (by its key) as one movement. It was
    /// made again at once at each change its service told of (2026-10-19, the user's "B2").
    /// </summary>
    void Refill(StackPanel area, Action fill)
    {
        var before = ListMotion.Take(area);
        var cards = new Dictionary<string, ListMotion.Before>();
        foreach (var key in before.Keys)
            if (RowsOf(before.Row(key)) is { } rows) cards[key] = ListMotion.Take(rows);
        fill();
        var lists = new List<(Panel, ListMotion.Before, double)>();
        foreach (UIElement c in area.Children)
            if (c is FrameworkElement f && ListMotion.KeyOf(f) is { } key && cards.TryGetValue(key, out var b) && RowsOf(f) is { } rows)
                lists.Add((rows, b, 0));
        lists.Add((area, before, 0)); // the cards after their rows: a card's room follows them
        ListMotion.Run(lists, _t.Animations);
    }

    /// <summary>A card's rows (Group: a border round a stack), or null.</summary>
    static StackPanel? RowsOf(FrameworkElement? card) => card is Border { Child: StackPanel rows } ? rows : null;

    /// <summary>A row with a picture at the left, a title and a dim line under it, and a control at the right end.</summary>
    FrameworkElement ItemRow(FrameworkElement picture, string title, string? detail, FrameworkElement? right)
    {
        var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(8, 6, 8, 6), MinHeight = 28 };
        picture.Margin = new Thickness(0, 0, 10, 0);
        picture.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(picture, System.Windows.Controls.Dock.Left);
        dock.Children.Add(picture);
        if (right != null)
        {
            right.VerticalAlignment = VerticalAlignment.Center;
            right.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(right, System.Windows.Controls.Dock.Right);
            dock.Children.Add(right);
        }
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = title, Foreground = _text, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(detail)) words.Children.Add(new TextBlock { Text = detail, FontSize = 11.5, Foreground = _dim, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
        dock.Children.Add(words);
        return Cell(dock);
    }

    /// <summary>A round picture, in the accent colour when <paramref name="on"/> (a joined network, a connected device).</summary>
    Border Circle(FrameworkElement picture, bool on)
    {
        picture.HorizontalAlignment = HorizontalAlignment.Center;
        picture.VerticalAlignment = VerticalAlignment.Center;
        return new Border
        {
            Width = 28, Height = 28, CornerRadius = new CornerRadius(14), Child = picture,
            Background = on ? _accent : new SolidColorBrush(_t.Dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0)),
        };
    }

    /// <summary>A page's own coloured square (the sidebar's, larger) for the switch row at its top.</summary>
    static Border SquarePicture(FrameworkElement picture, Color color)
    {
        picture.HorizontalAlignment = HorizontalAlignment.Center;
        picture.VerticalAlignment = VerticalAlignment.Center;
        return new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Background = new SolidColorBrush(color), Child = picture };
    }

    TextBlock Note(string text) => new() { Text = text, FontSize = 12, Foreground = _dim, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4, 0, 4, 12) };

    TextBlock Value(string text, bool wrap) => new()
    {
        Text = text, Foreground = _dim, TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap, TextAlignment = TextAlignment.Right, MaxWidth = 280,
    };

    /// <summary>A button at the bottom right of the page (System Settings' "Advanced…"), for what stays in Windows' settings.</summary>
    FrameworkElement Footer(string text, Action click)
    {
        var b = Button(text, click);
        b.HorizontalAlignment = HorizontalAlignment.Right;
        b.Margin = new Thickness(0, 0, 0, 12);
        return b;
    }

    void AddFooter(string text, string windowsPage)
    {
        var b = Footer(text, () => CommandRunner.Launch(windowsPage));
        Controls["footer"] = b;
        Add(b);
    }
}
