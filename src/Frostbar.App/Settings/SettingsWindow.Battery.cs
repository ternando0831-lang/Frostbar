using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.Core.Devices;
using Frostbar.Platform.Sys;

namespace Frostbar.App.Settings;

/// <summary>The Battery page (macOS's System Settings → Battery, with Windows' Power &amp; battery), in place of Windows' settings.</summary>
public sealed partial class SettingsWindow
{
    // The charge and what feeds it; the power mode plugged in and on battery; when energy saver turns on; when the
    // screen goes off and the PC sleeps; what closing the lid does; the battery's wear. All of it is Windows' own
    // setting, changed as its Settings app changes it. Without a battery, only what applies plugged in shows.

    void BatteryPage()
    {
        if (_host.System is not { } sys) { Add(Note(_s["set.notReady"])); return; }
        var battery = sys.Battery;
        bool has = battery.Present;

        // ---- the charge
        if (has)
        {
            var level = new TextBlock { FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = _text };
            var state = new TextBlock { FontSize = 12, Foreground = _dim, Margin = new Thickness(0, 1, 0, 0), TextWrapping = TextWrapping.Wrap };
            var gauge = new Border { Height = 8, CornerRadius = new CornerRadius(4), Background = new SolidColorBrush(_t.Dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x14, 0, 0, 0)), Margin = new Thickness(0, 8, 0, 2) };
            var fill = new Border { Height = 8, CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left };
            var gaugeBox = new Grid();
            gaugeBox.Children.Add(gauge);
            gaugeBox.Children.Add(fill);
            fill.Margin = gauge.Margin;
            var words = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
            words.Children.Add(level);
            words.Children.Add(state);
            words.Children.Add(gaugeBox);
            void Charge()
            {
                level.Text = $"{battery.Percent}%";
                state.Text = battery.Charging ? _s["battery.charging"]
                    : battery.OnAc ? _s["battery.pluggedIn"]
                    : PowerSettings.SecondsLeft is int left ? _s.F("battery.onBatteryLeft", PowerChoices.Duration(TimeSpan.FromSeconds(left), _s)) : _s["battery.onBattery"];
                if (PowerSettings.EnergySaverOn) state.Text += "  ·  " + _s["battery.saverOn"];
                fill.Background = new SolidColorBrush(battery.Percent <= 20 && !battery.OnAc ? SystemTint.Red(_t.Dark) : SystemTint.Green(_t.Dark));
                gaugeBox.SizeChanged -= Resize;
                gaugeBox.SizeChanged += Resize;
                Resize(null, null);
            }
            void Resize(object? _, SizeChangedEventArgs? __) => fill.Width = Math.Max(8, gaugeBox.ActualWidth * battery.Percent / 100.0);
            Charge();
            Follow(h => battery.Changed += h, h => battery.Changed -= h, Charge);
            Every(30_000, Charge); // the estimate moves without a change of state
            Controls["battery.level"] = level;
            Add(Group(Cell(words)));
        }
        else Add(Note(_s["battery.none"]));

        // ---- power mode
        string[] modes = [_s["power.efficiency"], _s["power.balanced"], _s["power.performance"]];
        FrameworkElement ModeRow(string label, bool ac) => Row(label, Choice(ac ? "battery.modeAc" : "battery.modeDc", modes, (int)PowerSettings.GetMode(ac),
            i => { if (!PowerSettings.SetMode(ac, (PowerMode)i)) ShowAgain(); }));
        Add(Caption(_s["power.mode"]));
        Add(has ? Group(ModeRow(_s["power.pluggedIn"], true), ModeRow(_s["power.onBattery"], false)) : Group(ModeRow(_s["power.mode"], true)));
        Add(Note(_s["power.modeDetail"]));

        // ---- energy saver
        if (has)
        {
            var (values, index) = PowerChoices.With(PowerChoices.EnergySaver, PowerSettings.Get(PowerSetting.EnergySaverThreshold, pluggedIn: false));
            Add(Caption(_s["power.saver"]));
            Add(Group(Row(_s["power.saverAt"], PopUp("battery.saver", values.Select(v => PowerChoices.Threshold(v, _s)).ToArray(), index,
                i => { if (!PowerSettings.Set(PowerSetting.EnergySaverThreshold, false, values[i])) ShowAgain(); }), _s["power.saverDetail"])));
        }

        // ---- screen and sleep
        FrameworkElement Timeout(string key, string label, PowerSetting setting, bool ac)
        {
            var (values, index) = PowerChoices.With(PowerChoices.Timeouts, PowerSettings.Get(setting, ac));
            return Row(label, PopUp(key, values.Select(v => PowerChoices.Timeout(v, _s)).ToArray(), index,
                i => { if (!PowerSettings.Set(setting, ac, values[i])) ShowAgain(); }));
        }
        Add(Caption(_s["power.screenSleep"]));
        Add(has
            ? Group(Timeout("battery.screenAc", _s["power.screenAc"], PowerSetting.ScreenOff, true), Timeout("battery.screenDc", _s["power.screenDc"], PowerSetting.ScreenOff, false),
                    Timeout("battery.sleepAc", _s["power.sleepAc"], PowerSetting.Sleep, true), Timeout("battery.sleepDc", _s["power.sleepDc"], PowerSetting.Sleep, false))
            : Group(Timeout("battery.screenAc", _s["power.screen"], PowerSetting.ScreenOff, true), Timeout("battery.sleepAc", _s["power.sleep"], PowerSetting.Sleep, true)));

        // ---- the lid
        var (lid, hibernate) = PowerSettings.Capabilities();
        if (lid)
        {
            uint[] actions = hibernate ? [0, 1, 2, 3] : [0, 1, 3];
            FrameworkElement Lid(string key, string label, bool ac)
            {
                uint? now = PowerSettings.Get(PowerSetting.LidAction, ac);
                return Row(label, PopUp(key, actions.Select(a => _s["power.lid" + a]).ToArray(), now is uint n ? Array.IndexOf(actions, n) : -1,
                    i => { if (!PowerSettings.Set(PowerSetting.LidAction, ac, actions[i])) ShowAgain(); }));
            }
            Add(Caption(_s["power.lid"]));
            Add(has ? Group(Lid("battery.lidAc", _s["power.pluggedIn"], true), Lid("battery.lidDc", _s["power.onBattery"], false)) : Group(Lid("battery.lidAc", _s["power.lid"], true)));
        }

        // ---- the battery's wear (read at once: one query to the battery's driver, a millisecond or two)
        if (has && PowerSettings.Health() is { MaximumCapacity: > 0 } h)
        {
            var rows = new List<FrameworkElement> { Row(_s["battery.capacity"], Value($"{h.MaximumCapacity}%"), _s["battery.capacityDetail"]) };
            if (h.CycleCount > 0) rows.Add(Row(_s["battery.cycles"], Value(h.CycleCount.ToString())));
            Add(Caption(_s["battery.health"]));
            Add(Group([.. rows]));
        }
        AddFooter(_s["power.windowsSettings"], "ms-settings:powersleep");
    }

    MacControls.PopUp PopUp(string key, IReadOnlyList<string> labels, int selected, Action<int> chosen)
    {
        var p = new MacControls.PopUp(labels, selected, _t, chosen);
        Controls[key] = p;
        return p;
    }
}
