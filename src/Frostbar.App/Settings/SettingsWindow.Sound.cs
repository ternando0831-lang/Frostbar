using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Frostbar.App.Common;
using Frostbar.Platform.Sys;

namespace Frostbar.App.Settings;

/// <summary>The Sound page (macOS's System Settings → Sound, with Windows' volume mixer), in place of Windows' settings.</summary>
public sealed partial class SettingsWindow
{
    // Output: the devices (a click makes one the default), its volume, mute and balance. Input: the microphones and
    // the input volume. Apps: each app playing on the default output, its volume and mute (Windows' volume mixer),
    // read again every 2 seconds while the page is open. The rest (formats, enhancements, spatial sound) stays in
    // Windows' settings.

    void SoundPage()
    {
        SoundEffectsPart(); // first, as macOS's Sound page has them (2026-10-22); they need no service
        if (_host.System is not { } sys) { Add(Note(_s["set.notReady"])); return; }
        var audio = sys.Audio;

        // ---- output
        Add(Caption(_s["sound.output"]));
        var outputs = new StackPanel();
        Add(outputs);
        var volume = new MacSlider(0, 100, 1, Math.Round(audio.Volume * 100), _t);
        Controls["sound.volume"] = volume;
        volume.ValueChanged += v => audio.SetVolume((float)(v / 100));
        var mute = Switch("sound.mute", audio.Muted, on => audio.SetMute(on));
        var balance = new MacSlider(-100, 100, 5, Math.Round(audio.Balance * 100), _t);
        Controls["sound.balance"] = balance;
        balance.ValueChanged += v => audio.SetBalance((float)(v / 100));
        var balanceRow = SliderRow(_s["sound.balance"], balance, _s["sound.left"], _s["sound.right"]);
        Add(Group(SliderRow(_s["sound.outputVolume"], volume, _s["set.small"], _s["set.big"]), Row(_s["sound.mute"], mute), balanceRow));
        // The display for the volume and brightness keys (stage 2-2): Frostbar's at the top centre, Windows' own kept from showing.
        Add(Group(Row(_s["set.hud"], Switch("hud.enabled", _settings.Hud.Enabled, on => { _settings.Hud.Enabled = on; _host.Apply(SettingsArea.Hud); }))));
        Add(Note(_s["set.hudDetail"]));

        // ---- input
        Add(Caption(_s["sound.input"]));
        var inputs = new StackPanel();
        Add(inputs);
        var inputVolume = new MacSlider(0, 100, 1, Math.Round(audio.InputVolume * 100), _t);
        Controls["sound.inputVolume"] = inputVolume;
        inputVolume.ValueChanged += v => audio.SetInputVolume((float)(v / 100));
        var inputGroup = Group(SliderRow(_s["sound.inputVolume"], inputVolume, _s["set.small"], _s["set.big"]));
        Add(inputGroup);

        // Devices come and go moving (ListMotion, "B2").
        void Devices(StackPanel into, IReadOnlyList<AudioOutput> list, Geometry glyph, bool stroke, string keyPrefix, Func<string, bool> choose, string none) =>
            Refill(into, () => DevicesNow(into, list, glyph, stroke, keyPrefix, choose, none));

        void DevicesNow(StackPanel into, IReadOnlyList<AudioOutput> list, Geometry glyph, bool stroke, string keyPrefix, Func<string, bool> choose, string none)
        {
            into.Children.Clear();
            ForgetControls(keyPrefix);
            if (list.Count == 0) { into.Children.Add(ListMotion.Keyed(Note(none), "note:none")); return; }
            into.Children.Add(ListMotion.Keyed(Group([.. list.Select(d =>
            {
                var tick = Glyphs.Stroke(Glyphs.Check, 13, _accent, 1.8);
                tick.Visibility = d.IsDefault ? Visibility.Visible : Visibility.Hidden;
                var picture = stroke ? Glyphs.Stroke(glyph, 14, d.IsDefault ? Brushes.White : _text, 1.5) : Glyphs.Fill(glyph, 14, d.IsDefault ? Brushes.White : _text);
                var row = (Border)ItemRow(Circle(picture, on: d.IsDefault), d.Name, null, tick);
                if (!d.IsDefault) Clickable(row, () => choose(d.Id));
                Controls[keyPrefix + d.Name] = row;
                return ListMotion.Keyed((FrameworkElement)row, "dev:" + d.Id);
            })]), "card:devices"));
        }

        void Fill()
        {
            Devices(outputs, audio.Outputs, Glyphs.Speaker, stroke: false, "sound.output.", audio.SetDefaultOutput, _s["sound.noOutput"]);
            Devices(inputs, audio.Inputs, Glyphs.Microphone, stroke: true, "sound.input.", audio.SetDefaultInput, _s["sound.noInput"]);
            volume.Glide(Math.Round(audio.Volume * 100), _t.Animations); // the volume keys held: it slides as the volume display does
            mute.IsOn = audio.Muted;
            volume.Opacity = audio.Muted ? 0.4 : 1;
            balance.Glide(Math.Round(audio.Balance * 100), _t.Animations);
            balanceRow.Visibility = audio.Stereo ? Visibility.Visible : Visibility.Collapsed;
            inputVolume.Glide(Math.Round(audio.InputVolume * 100), _t.Animations);
            inputGroup.Visibility = audio.InputAvailable ? Visibility.Visible : Visibility.Collapsed;
        }
        Fill();
        Follow(h => audio.Changed += h, h => audio.Changed -= h, Fill);

        // ---- apps (the volume mixer)
        Add(Caption(_s["sound.apps"]));
        var apps = new StackPanel();
        Add(apps);
        var shown = new List<string>(); // the apps shown, by their key (rows are made again only when these change)
        int iconRetries = 0;
        var sliders = new Dictionary<string, (MacSlider slider, Border mute, System.Runtime.CompilerServices.StrongBox<string[]> sessions)>();

        void ShowApps(IReadOnlyList<AppAudio> sessions)
        {
            // One row per app: a browser's tabs are several sessions of one process.
            var byApp = sessions.GroupBy(a => a.SystemSounds ? "system" : a.ExePath.Length > 0 ? a.ExePath : a.Pid.ToString())
                .Select(g => (key: g.Key, items: g.ToList())).ToList();
            var keys = byApp.Select(a => a.key).ToList();
            if (keys.SequenceEqual(shown))
            {
                foreach (var (key, items) in byApp)
                    if (sliders.TryGetValue(key, out var r))
                    {
                        r.slider.Glide(Math.Round(items.Max(i => i.Volume) * 100), _t.Animations);
                        r.sessions.Value = items.Select(i => i.Key).ToArray(); // swapped whole: the worker reads it
                        SetMuteGlyph(r.mute, items.All(i => i.Muted));
                    }
                return;
            }
            shown = keys;
            bool missing = false;
            // Apps come and go moving (ListMotion, "B2"): the mixer was made again at once.
            Refill(apps, () => AppsNow(byApp, ref missing));
            // Icons still loading: the rows are made again at the next reading (a few times: one that cannot be read stays the speaker).
            if (missing && iconRetries++ < 3) shown = [];
        }

        void AppsNow(List<(string key, List<AppAudio> items)> byApp, ref bool missing)
        {
            sliders.Clear();
            apps.Children.Clear();
            ForgetControls("sound.app.", "sound.appMute.", "sound.appPid.", "sound.appMutePid.");
            if (byApp.Count == 0) { apps.Children.Add(ListMotion.Keyed(Note(_s["sound.noApps"]), "note:none")); return; }
            bool lacking = false;
            apps.Children.Add(ListMotion.Keyed(Group([.. byApp.Select(a =>
            {
                var first = a.items[0];
                string name = first.SystemSounds ? _s["sound.systemSounds"]
                    : first.ExePath.Length > 0 ? sys.AppName(first.ExePath, first.ExePath) : first.DisplayName.Length > 0 ? first.DisplayName : $"PID {first.Pid}";
                var sessionKeys = new System.Runtime.CompilerServices.StrongBox<string[]>(a.items.Select(i => i.Key).ToArray());
                var slider = new MacSlider(0, 100, 1, Math.Round(a.items.Max(i => i.Volume) * 100), _t) { MinWidth = 150 };
                // While dragging, only the latest value goes to Core Audio (each call walks the sessions).
                var worker = new Frostbar.Core.Concurrency.LatestValueWorker<float>(v => AudioSessions.Set(sessionKeys.Value!, v, null));
                slider.ValueChanged += v => worker.Set((float)(v / 100));
                var muteButton = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Brushes.Transparent, Margin = new Thickness(0, 0, 6, 0) };
                SetMuteGlyph(muteButton, a.items.All(i => i.Muted));
                Clickable(muteButton, () =>
                {
                    bool muted = muteButton.Tag is true;
                    SetMuteGlyph(muteButton, !muted);
                    var k = sessionKeys.Value!;
                    Task.Run(() => AudioSessions.Set(k, null, !muted));
                });
                sliders[a.key] = (slider, muteButton, sessionKeys);
                Controls["sound.app." + name] = slider;
                Controls["sound.appMute." + name] = muteButton;
                if (!first.SystemSounds) { Controls["sound.appPid." + first.Pid] = slider; Controls["sound.appMutePid." + first.Pid] = muteButton; }
                var right = new DockPanel { Width = 200 };
                DockPanel.SetDock(muteButton, System.Windows.Controls.Dock.Left);
                right.Children.Add(muteButton);
                right.Children.Add(slider);
                var img = first.SystemSounds || first.ExePath.Length == 0 ? null : sys.Icon(first.ExePath, first.ExePath);
                if (img == null && !first.SystemSounds && first.ExePath.Length > 0) lacking = true;
                FrameworkElement icon = first.SystemSounds ? Glyphs.Fill(Glyphs.Bell, 18, _text)
                    : img != null ? new Image { Source = img, Width = 22, Height = 22 }
                    : Glyphs.Fill(Glyphs.Speaker, 16, _text);
                var box = new Border { Width = 28, Height = 28, Child = icon };
                icon.HorizontalAlignment = HorizontalAlignment.Center;
                icon.VerticalAlignment = VerticalAlignment.Center;
                return ListMotion.Keyed(ItemRow(box, name, first.Playing ? null : _s["sound.idle"], right), "app:" + a.key);
            })]), "card:apps"));
            missing = lacking;
        }

        void SetMuteGlyph(Border b, bool muted)
        {
            b.Tag = muted;
            var g = new Grid { Width = 16, Height = 16 };
            g.Children.Add(Glyphs.Fill(Glyphs.Speaker, 16, _text));
            g.Children.Add(muted ? Glyphs.Stroke(Glyphs.Mute, 16, _text, 1.5) : Glyphs.Stroke(Glyphs.SpeakerWaves[1], 16, _text, 1.5));
            g.LayoutTransform = new ScaleTransform(13 / 16.0, 13 / 16.0);
            g.HorizontalAlignment = HorizontalAlignment.Center;
            g.VerticalAlignment = VerticalAlignment.Center;
            g.Opacity = muted ? 0.6 : 1;
            b.Child = g;
        }

        void ReadApps() => OffUi(AudioSessions.List, ShowApps);
        ReadApps();
        Every(2000, ReadApps);
        AddFooter(_s["sound.windowsSettings"], "ms-settings:sound");
    }

    /// <summary>A whole row (or a small button) that takes a click, lighter under the pointer.</summary>
    void Clickable(Border b, Action click)
    {
        b.Background = Brushes.Transparent; // takes the pointer everywhere, not only on its words
        b.MouseEnter += (_, _) => { if (!_found.Contains(b)) b.Background = _hover; };
        b.MouseLeave += (_, _) => { if (!_found.Contains(b)) b.Background = Brushes.Transparent; };
        b.MouseLeftButtonDown += (_, e) => e.Handled = true;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; click(); };
    }
}
