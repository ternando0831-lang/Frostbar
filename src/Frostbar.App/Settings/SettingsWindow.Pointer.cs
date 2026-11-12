using System.Windows;
using System.Windows.Controls;
using Frostbar.App.Common;
using Frostbar.Core.Devices;
using Frostbar.Platform.Sys;

namespace Frostbar.App.Settings;

/// <summary>The Trackpad and Mouse pages and the Keyboard page's key repeat (stage 4-2), in place of Windows' own.</summary>
public sealed partial class SettingsWindow
{
    // Windows' settings through the public calls (PointerStore; a spec's shell has values of its own). Read as the page
    // is made (the calls only read: microseconds), written off the UI thread when a switch flips or a slider is let go
    // (each write tells every window). What the page does not show is written as it was read (a wheel of "one screen").

    void TrackpadPage()
    {
        var store = PointerStore.Current;
        var now = store.Touchpad();
        var failure = Failure();
        if (now == null) { Add(Note(_s["tp.none"])); AddFooter(_s["tp.windowsSettings"], "ms-settings:devices-touchpad"); return; }

        void Write(Func<TouchpadSettings, TouchpadSettings> change)
        {
            now = change(now!);
            var next = now;
            Written(() => store.SetTouchpad(next), failure);
        }
        PillSwitch S(string key, bool on, Func<TouchpadSettings, bool, TouchpadSettings> set) => Switch("trackpad." + key, on, v => Write(t => set(t, v)));

        var speed = new MacSlider(TouchpadSettings.SpeedMin, TouchpadSettings.SpeedMax, 1, now.CursorSpeed, _t);
        Controls["trackpad.speed"] = speed;
        speed.Committed += v => Write(t => t with { CursorSpeed = (int)Math.Round(v) });
        string[] levels = [_s["tp.most"], _s["tp.high"], _s["tp.medium"], _s["tp.low"]];
        var sensitivity = Choice("trackpad.sensitivity", levels, Math.Min(now.Sensitivity, TouchpadSettings.LeastShown), i => Write(t => t with { Sensitivity = i }));

        Add(Caption(_s["tp.pointClick"]));
        Add(Group(
            SliderRow(_s["tp.speed"], speed, _s["set.slow"], _s["set.fast"]),
            Row(_s["tp.sensitivity"], sensitivity),
            Row(_s["tp.tap"], S("tap", now.TapToClick, (t, v) => t with { TapToClick = v })),
            Row(_s["tp.tapDrag"], S("tapDrag", now.TapAndDrag, (t, v) => t with { TapAndDrag = v })),
            Row(_s["tp.twoFingerTap"], S("twoFingerTap", now.TwoFingerTap, (t, v) => t with { TwoFingerTap = v })),
            Row(_s["tp.corner"], S("corner", now.RightClickZone, (t, v) => t with { RightClickZone = v }))));
        Add(Caption(_s["tp.scrollZoom"]));
        Add(Group(
            Row(_s["tp.natural"], S("natural", now.NaturalScrolling, (t, v) => t with { NaturalScrolling = v }), _s["tp.naturalDetail"]),
            Row(_s["tp.pan"], S("pan", now.TwoFingerScroll, (t, v) => t with { TwoFingerScroll = v })),
            Row(_s["tp.zoom"], S("zoom", now.PinchZoom, (t, v) => t with { PinchZoom = v }))));
        Add(Group(
            Row(_s["tp.use"], S("enabled", now.Enabled, (t, v) => t with { Enabled = v })),
            Row(_s["tp.leaveOn"], S("leaveOn", now.LeaveOnWithMouse, (t, v) => t with { LeaveOnWithMouse = v }))));
        Add(Note(_s["tp.gestures"]));
        Add(failure);
        AddFooter(_s["tp.windowsSettings"], "ms-settings:devices-touchpad");
    }

    void MousePage()
    {
        var store = PointerStore.Current;
        var raw = store.Mouse(); // as Windows has it: written back unchanged where the page does not show it
        var shown = raw.Clamped();
        var failure = Failure();
        void Write(Func<MouseSettings, MouseSettings> change)
        {
            raw = change(raw);
            var next = raw;
            Written(() => store.SetMouse(next), failure);
        }

        var speed = new MacSlider(MouseSettings.SpeedMin, MouseSettings.SpeedMax, 1, shown.Speed, _t);
        Controls["mouse.speed"] = speed;
        speed.Committed += v => Write(m => m with { Speed = (int)Math.Round(v) });
        var wheel = new MacSlider(MouseSettings.WheelMin, MouseSettings.WheelMax, 1, shown.WheelLines, _t);
        Controls["mouse.wheel"] = wheel;
        wheel.Committed += v => Write(m => m with { WheelLines = (int)Math.Round(v) });
        // Double-click: slow (the longest wait) on the left, fast on the right, as macOS has it.
        int Flip(double v) => MouseSettings.DoubleClickMin + MouseSettings.DoubleClickMax - (int)Math.Round(v);
        var doubleClick = new MacSlider(MouseSettings.DoubleClickMin, MouseSettings.DoubleClickMax, 50, Flip(shown.DoubleClickMs), _t);
        Controls["mouse.doubleClick"] = doubleClick;
        doubleClick.Committed += v => Write(m => m with { DoubleClickMs = Flip(v) });

        Add(Group(
            SliderRow(_s["mouse.speed"], speed, _s["set.slow"], _s["set.fast"]),
            Row(_s["mouse.precision"], Switch("mouse.precision", shown.EnhancePrecision, on => Write(m => m with { EnhancePrecision = on })), _s["mouse.precisionDetail"]),
            SliderRow(_s["mouse.wheel"], wheel, _s["set.few"], _s["set.many"]),
            Row(_s["mouse.primary"], Choice("mouse.primary", [_s["sound.left"], _s["sound.right"]], shown.SwapButtons ? 1 : 0, i => Write(m => m with { SwapButtons = i == 1 }))),
            SliderRow(_s["mouse.doubleClick"], doubleClick, _s["set.slow"], _s["set.fast"])));
        Add(Note(_s["mouse.wheelDetail"]));
        Add(failure);
        AddFooter(_s["mouse.windowsSettings"], "ms-settings:mousetouchpad");
    }

    /// <summary>The Keyboard page's first group: how fast a held key repeats, and how long before it does.</summary>
    void KeyRepeat()
    {
        var store = PointerStore.Current;
        var keys = store.Keys().Clamped();
        var failure = Failure();
        void Write(Func<KeyRepeatSettings, KeyRepeatSettings> change)
        {
            keys = change(keys);
            var next = keys;
            Written(() => store.SetKeys(next), failure);
        }
        var rate = new MacSlider(KeyRepeatSettings.RateMin, KeyRepeatSettings.RateMax, 1, keys.Rate, _t);
        Controls["keys.rate"] = rate;
        rate.Committed += v => Write(k => k with { Rate = (int)Math.Round(v) });
        // Long on the left, short on the right (macOS): the slider runs against Windows' 0 (short) … 3 (long).
        var delay = new MacSlider(KeyRepeatSettings.DelayMin, KeyRepeatSettings.DelayMax, 1, KeyRepeatSettings.DelayMax - keys.Delay, _t);
        Controls["keys.delay"] = delay;
        delay.Committed += v => Write(k => k with { Delay = KeyRepeatSettings.DelayMax - (int)Math.Round(v) });
        Add(Caption(_s["keys.repeat"]));
        Add(Group(SliderRow(_s["keys.rate"], rate, _s["set.slow"], _s["set.fast"]), SliderRow(_s["keys.delay"], delay, _s["set.long"], _s["set.short"])));
        Add(failure);
    }

    /// <summary>A note for a write that did not go through (hidden until then).</summary>
    TextBlock Failure()
    {
        var note = Note("");
        note.Visibility = Visibility.Collapsed;
        return note;
    }

    /// <summary>Runs a write off the UI thread; what went wrong shows in <paramref name="failure"/>.</summary>
    void Written(Func<string?> write, TextBlock failure)
    {
        if (_indexing) return;
        failure.Visibility = Visibility.Collapsed;
        int page = _shown;
        Task.Run(write).ContinueWith(t =>
        {
            if (page != _shown) return;
            string? why = t.Exception?.InnerException?.Message ?? t.Result;
            if (why == null) return;
            failure.Text = string.Format(_s["set.changeFailed"], why);
            failure.Visibility = Visibility.Visible;
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
}
