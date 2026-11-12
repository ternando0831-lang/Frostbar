namespace Frostbar.Core.Devices;

/// <summary>
/// The precision touchpad's settings the Trackpad page shows (stage 4-2): those of winuser.h's TOUCHPAD_PARAMETERS
/// (Windows 11 24H2+, SPI_GETTOUCHPADPARAMETERS) that Windows' own touchpad settings have. Natural scrolling is macOS's
/// name for Windows' default, "down motion scrolls up" (scrollDirectionReversed off).
/// </summary>
public sealed record TouchpadSettings(
    bool Present, bool Enabled, bool LeaveOnWithMouse, bool TapToClick, bool TapAndDrag, bool TwoFingerTap, bool RightClickZone,
    bool TwoFingerScroll, bool PinchZoom, bool NaturalScrolling, int Sensitivity, int CursorSpeed)
{
    /// <summary>Sensitivity levels, most sensitive first (TOUCHPAD_SENSITIVITY_LEVEL); Windows' settings offer the first four.</summary>
    public const int MostSensitive = 0, LeastShown = 3;
    public const int SpeedMin = 1, SpeedMax = 20;
}

/// <summary>
/// The two words of bit fields in TOUCHPAD_PARAMETERS (the first declared is the lowest bit): which device is there,
/// and the settings. Kept here so that their order is tested, not only written down.
/// </summary>
public static class TouchpadBits
{
    // The first word (read only): touchpadPresent, legacyTouchpadPresent, externalMousePresent, touchpadEnabled,
    // touchpadActive, feedbackSupported, clickForceSupported.
    public const int Present = 0, Enabled = 3;
    // The second word: allowActiveWhenMousePresent, feedbackEnabled, tapEnabled, tapAndDragEnabled, twoFingerTapEnabled,
    // rightClickZoneEnabled, mouseAccelSettingHonored, panEnabled, zoomEnabled, scrollDirectionReversed.
    public const int LeaveOnWithMouse = 0, Tap = 2, TapAndDrag = 3, TwoFingerTap = 4, RightClickZone = 5, Pan = 7, Zoom = 8, ScrollReversed = 9;

    static bool Bit(uint word, int bit) => ((word >> bit) & 1) == 1;
    static uint With(uint word, int bit, bool on) => on ? word | (1u << bit) : word & ~(1u << bit);

    /// <summary>The settings in the two words and the two numbers (sensitivity, cursor speed).</summary>
    public static TouchpadSettings Read(uint present, uint settings, int sensitivity, uint cursorSpeed) => new(
        Bit(present, Present), Bit(present, Enabled), Bit(settings, LeaveOnWithMouse), Bit(settings, Tap), Bit(settings, TapAndDrag),
        Bit(settings, TwoFingerTap), Bit(settings, RightClickZone), Bit(settings, Pan), Bit(settings, Zoom), !Bit(settings, ScrollReversed),
        Math.Clamp(sensitivity, TouchpadSettings.MostSensitive, 4), (int)Math.Clamp(cursorSpeed, TouchpadSettings.SpeedMin, TouchpadSettings.SpeedMax));

    /// <summary>
    /// The settings word with <paramref name="s"/> written into it: the bits Windows' settings have are replaced, the
    /// others (feedback, the mouse's acceleration, the reserved ones) stay as read.
    /// </summary>
    public static uint WriteSettings(uint settings, TouchpadSettings s)
    {
        settings = With(settings, LeaveOnWithMouse, s.LeaveOnWithMouse);
        settings = With(settings, Tap, s.TapToClick);
        settings = With(settings, TapAndDrag, s.TapAndDrag);
        settings = With(settings, TwoFingerTap, s.TwoFingerTap);
        settings = With(settings, RightClickZone, s.RightClickZone);
        settings = With(settings, Pan, s.TwoFingerScroll);
        settings = With(settings, Zoom, s.PinchZoom);
        return With(settings, ScrollReversed, !s.NaturalScrolling);
    }

    /// <summary>The first word with the touchpad turned on or off (touchpadEnabled; the others are what Windows sees).</summary>
    public static uint WritePresent(uint present, bool enabled) => With(present, Enabled, enabled);
}

/// <summary>The mouse's settings the Mouse page shows: the pointer's speed and precision, the wheel, the buttons, double-click.</summary>
public sealed record MouseSettings(int Speed, bool EnhancePrecision, int WheelLines, bool SwapButtons, int DoubleClickMs)
{
    public const int SpeedMin = 1, SpeedMax = 20, WheelMin = 1, WheelMax = 20, DoubleClickMin = 200, DoubleClickMax = 900;

    /// <summary>The values in the ranges the page's sliders have (a wheel of "one screen" (-1) or more than 20 lines shows as 20).</summary>
    public MouseSettings Clamped() => this with
    {
        Speed = Math.Clamp(Speed, SpeedMin, SpeedMax),
        WheelLines = WheelLines < 0 ? WheelMax : Math.Clamp(WheelLines, WheelMin, WheelMax),
        DoubleClickMs = Math.Clamp(DoubleClickMs, DoubleClickMin, DoubleClickMax),
    };
}

/// <summary>The keys' repeat (SPI_GETKEYBOARDDELAY 0…3: 250…1000 ms; SPI_GETKEYBOARDSPEED 0…31: about 2.5…30 a second).</summary>
public sealed record KeyRepeatSettings(int Delay, int Rate)
{
    public const int DelayMin = 0, DelayMax = 3, RateMin = 0, RateMax = 31;

    public KeyRepeatSettings Clamped() => new(Math.Clamp(Delay, DelayMin, DelayMax), Math.Clamp(Rate, RateMin, RateMax));

    /// <summary>The delay before a held key repeats, in milliseconds (Windows' 0…3 are 250…1000 ms).</summary>
    public static int DelayMs(int delay) => 250 * (Math.Clamp(delay, DelayMin, DelayMax) + 1);
}
