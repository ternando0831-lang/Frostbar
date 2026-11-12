using System.Windows;
using System.Windows.Controls;
using Frostbar.App.Common;
using Frostbar.App.Menus;
using Frostbar.Core.Devices;
using Frostbar.Platform.Shell;

namespace Frostbar.App.Settings;

/// <summary>The Displays page (stage 4-4): each display's resolution, scale and refresh rate, the built-in one's brightness, HDR.</summary>
public sealed partial class SettingsWindow
{
    // Windows' displays (DisplayStore; a spec's shell has displays of its own). Read off the UI thread as the page opens
    // and again when the displays change (QueryDisplayConfig and the modes: a few milliseconds, S20). A change is made
    // off the UI thread too (a mode change blanks the screen for a moment) and saved, as Windows' settings do; then the
    // page asks for KeepSeconds whether to keep it, and puts the one before back the same way when it is not kept.

    /// <summary>The seconds a display change waits to be kept (a spec's shell shortens it through its diagnostics).</summary>
    public static int KeepSeconds { get; set; } = RevertCountdown.DefaultSeconds;
    /// <summary>Whether a change waits to be kept (diagnostics; and no second change starts meanwhile).</summary>
    public static bool AskingToKeep => _keepDialog != null;
    static DialogWindow? _keepDialog;
    string? _displayDevice; // the display the page shows when there are several (its GDI name)
    Action? _brightnessFollow; // the brightness slider shown follows the service (one at a time)

    void DisplayPage()
    {
        var body = new StackPanel();
        Add(body);
        var failure = Failure();
        Add(failure);
        Add(Note(_s["disp.note"]));
        AddFooter(_s["disp.windowsSettings"], "ms-settings:display");
        void Read() => OffUi(() => DisplayStore.Current.Displays(), all => FillDisplays(body, all, failure, Read));
        Read();
        if (_indexing) return;
        EventHandler changed = (_, _) => Dispatcher.BeginInvoke(Read);
        Microsoft.Win32.SystemEvents.DisplaySettingsChanged += changed;
        OnLeave(() => Microsoft.Win32.SystemEvents.DisplaySettingsChanged -= changed);
    }

    void FillDisplays(StackPanel body, IReadOnlyList<DisplayInfo> all, TextBlock failure, Action read)
    {
        body.Children.Clear();
        ForgetControls("display.");
        if (all.Count == 0) { body.Children.Add(Note(_s["disp.none"])); return; }
        var d = all.FirstOrDefault(x => x.Device == _displayDevice) ?? all[0];
        _displayDevice = d.Device;
        var store = DisplayStore.Current;
        string Name(DisplayInfo x) => x.Internal ? _s["disp.builtIn"] : x.Name.Length > 0 ? x.Name : string.Format(_s["disp.numbered"], all.ToList().IndexOf(x) + 1);

        if (all.Count > 1)
        {
            var which = Choice("display.which", [.. all.Select(Name)], all.ToList().IndexOf(d), i => { _displayDevice = all[i].Device; FillDisplays(body, all, failure, read); });
            body.Children.Add(Group(Row(_s["disp.which"], which)));
        }
        body.Children.Add(DisplayHeader(d, Name(d), all.Count > 1));

        var rows = new List<FrameworkElement>();
        // ---- resolution: the native one marked (the monitor's preferred mode)
        var sizes = DisplayChoices.Resolutions(d.Modes);
        string Size((int Width, int Height) r) => $"{r.Width} × {r.Height}" + (d.Preferred is { } p && p.Width == r.Width && p.Height == r.Height ? _s["disp.recommended"] : "");
        var resolution = new MacControls.PopUp([.. sizes.Select(Size)], Math.Max(0, sizes.ToList().IndexOf((d.Mode.Width, d.Mode.Height))), _t, i =>
        {
            if (DisplayChoices.ForResolution(d.Modes, sizes[i].Width, sizes[i].Height, d.Mode.Hz) is not { } to || to == d.Mode) return;
            var before = d.Mode;
            ChangeDisplay(() => store.SetMode(d.Device, to), () => store.SetMode(d.Device, before), failure, read);
        });
        Controls["display.resolution"] = resolution;
        rows.Add(Row(_s["disp.resolution"], resolution));

        // ---- scale: Windows' steps the display allows, the recommended one marked
        if (d.Scale is { } scale)
        {
            var percents = scale.Percents;
            var pick = new MacControls.PopUp([.. percents.Select(p => $"{p}%" + (p == scale.Recommended ? _s["disp.recommended"] : ""))], Math.Max(0, percents.ToList().IndexOf(scale.Now)), _t, i =>
            {
                int to = percents[i], before = scale.Now;
                if (to == before) return;
                ChangeDisplay(() => store.SetScale(d.Device, to), () => store.SetScale(d.Device, before), failure, read);
            });
            Controls["display.scale"] = pick;
            rows.Add(Row(_s["disp.scale"], pick, _s["disp.scaleDetail"]));
        }
        else rows.Add(Row(_s["disp.scale"], Value(_s["disp.scaleWindows"], false)));

        // ---- refresh rate: a choice only when the resolution has several
        var rates = DisplayChoices.Rates(d.Modes, d.Mode.Width, d.Mode.Height);
        if (rates.Count > 1)
        {
            var rate = new MacControls.PopUp([.. rates.Select(hz => $"{hz} Hz")], Math.Max(0, rates.ToList().IndexOf(d.Mode.Hz)), _t, i =>
            {
                var to = d.Mode with { Hz = rates[i] };
                if (to == d.Mode) return;
                var before = d.Mode;
                ChangeDisplay(() => store.SetMode(d.Device, to), () => store.SetMode(d.Device, before), failure, read);
            });
            Controls["display.rate"] = rate;
            rows.Add(Row(_s["disp.rate"], rate));
        }
        else
        {
            var only = Value($"{d.Mode.Hz} Hz", false);
            Controls["display.rate"] = only;
            rows.Add(Row(_s["disp.rate"], only));
        }
        body.Children.Add(Group([.. rows]));

        // ---- the built-in panel's brightness, when Windows has it (WMI; the Control Center's own service)
        if (d.Internal && _host.System?.Brightness is { } b)
        {
            var slider = new MacSlider(0, 100, 1, b.Level, _t);
            Controls["display.brightness"] = slider;
            slider.ValueChanged += v => b.Set((int)Math.Round(v));
            var group = Group(SliderRow(_s["disp.brightness"], slider, _s["disp.dim"], _s["disp.bright"]));
            group.Visibility = b.Available ? Visibility.Visible : Visibility.Collapsed;
            Action follow = () => { group.Visibility = b.Available ? Visibility.Visible : Visibility.Collapsed; if (!slider.IsMouseCaptured) slider.Glide(b.Level, _t.Animations); };
            // The slider shown now only: each fill (each read, each change of the displays) added one more, each moving a
            // slider no longer shown, until the page was left.
            if (_brightnessFollow != null) b.Changed -= _brightnessFollow;
            _brightnessFollow = follow;
            b.Changed += follow;
            OnLeave(() => { b.Changed -= follow; if (_brightnessFollow == follow) _brightnessFollow = null; });
            body.Children.Add(group);
        }

        // ---- HDR: only a display that has it; switched in Windows' settings (the colour profiles are Windows')
        if (d.HdrSupported)
        {
            var hdr = Button(_s["disp.hdrSettings"], () => CommandRunner.Launch("ms-settings:display-hdr"));
            Controls["display.hdr"] = hdr;
            body.Children.Add(Group(Row(_s["disp.hdr"], hdr, d.HdrOn ? _s["disp.hdrOn"] : _s["disp.hdrOff"])));
        }
    }

    /// <summary>The display's picture, its name and what it shows (and, of several, whether it is the main one).</summary>
    FrameworkElement DisplayHeader(DisplayInfo d, string name, bool several)
    {
        var picture = new Border { Width = 52, Height = 44, Margin = new Thickness(0, 0, 12, 0), Child = Glyphs.Stroke(Glyphs.DisplayPicture, 44, _text, 1.2) };
        var words = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        words.Children.Add(new TextBlock { Text = name, FontWeight = FontWeights.SemiBold, FontSize = 13, Foreground = _text });
        string summary = string.Format(_s["disp.summary"], d.Mode.Width, d.Mode.Height, d.Mode.Hz) + (d.Scale is { } s ? string.Format(_s["disp.summaryScale"], s.Now) : "");
        var detail = new TextBlock { Text = summary, FontSize = 11.5, Foreground = _dim, Margin = new Thickness(0, 2, 0, 0) };
        Controls["display.header"] = detail;
        words.Children.Add(detail);
        if (several && d.Primary) words.Children.Add(new TextBlock { Text = _s["disp.main"], FontSize = 11.5, Foreground = _dim });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 10, 12, 10) };
        row.Children.Add(picture);
        row.Children.Add(words);
        return Group(row);
    }

    /// <summary>
    /// A display change made off the UI thread, then the question whether to keep it (after half a second: the screen
    /// settles, and the shell places its bars again after 400 ms); not kept, <paramref name="putBack"/> runs the same way.
    /// </summary>
    void ChangeDisplay(Func<string?> change, Func<string?> putBack, TextBlock failure, Action read)
    {
        if (_indexing || _keepDialog != null) return;
        failure.Visibility = Visibility.Collapsed;
        int page = _shown;
        // The page is read again and the failure written only while it is still the one shown; the question is asked
        // whatever is shown (the change is made: it must be kept or put back).
        void Failed(string why) { if (page != _shown) return; failure.Text = string.Format(_s["set.changeFailed"], why); failure.Visibility = Visibility.Visible; }
        void Again() { if (page == _shown) read(); }
        Task.Run(change).ContinueWith(async t =>
        {
            string? why = t.Exception?.InnerException?.Message ?? t.Result;
            if (why != null) { Failed(why); Again(); return; }
            await Task.Delay(500);
            Again();
            _keepDialog = Dialogs.KeepDisplay(_t, _s, KeepSeconds,
                putBack: () =>
                {
                    _keepDialog = null;
                    Task.Run(putBack).ContinueWith(back => { if ((back.Exception?.InnerException?.Message ?? back.Result) is { } no) Failed(no); Again(); },
                        TaskScheduler.FromCurrentSynchronizationContext());
                },
                kept: () => { _keepDialog = null; Again(); });
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
