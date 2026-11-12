using System.Runtime.InteropServices;
using Frostbar.Core.Devices;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Sys;

/// <summary>
/// Where the Trackpad, Mouse and Keyboard pages read and write (stage 4-2): Windows' own settings through the public
/// SystemParametersInfo calls (what Windows' settings app writes; saved and announced), or, under a spec, values of the
/// spec's own kept in memory, so that no spec changes the user's pointer unless it asks for Windows'.
/// The writes broadcast WM_SETTINGCHANGE to every window: make them off the UI thread, and once per change (a slider's
/// value when it is let go).
/// </summary>
public interface IPointerStore
{
    /// <summary>The precision touchpad's settings, or null when there is none (or the call failed).</summary>
    TouchpadSettings? Touchpad();
    /// <summary>Writes the touchpad's settings; what went wrong, or null.</summary>
    string? SetTouchpad(TouchpadSettings s);
    MouseSettings Mouse();
    string? SetMouse(MouseSettings s);
    KeyRepeatSettings Keys();
    string? SetKeys(KeyRepeatSettings s);
    /// <summary>Writes made (diagnostics: a spec counts them).</summary>
    int Writes { get; }
}

public static class PointerStore
{
    /// <summary>The store the pages use: Windows', unless a spec asked for its own at start.</summary>
    public static IPointerStore Current { get; set; } = new WindowsPointer();
}

/// <summary>Windows' own settings.</summary>
public sealed class WindowsPointer : IPointerStore
{
    const uint Save = Native.SPIF_UPDATEINIFILE | Native.SPIF_SENDCHANGE;
    int _writes;
    public int Writes => _writes;

    static bool ReadRaw(out Native.TOUCHPAD_PARAMETERS tp)
    {
        tp = new Native.TOUCHPAD_PARAMETERS { VersionNumber = 1 };
        return Native.SystemParametersInfo(Native.SPI_GETTOUCHPADPARAMETERS, (uint)Marshal.SizeOf<Native.TOUCHPAD_PARAMETERS>(), ref tp, 0);
    }

    public TouchpadSettings? Touchpad()
    {
        if (!ReadRaw(out var tp)) return null;
        var s = TouchpadBits.Read(tp.Present, tp.Settings, tp.SensitivityLevel, tp.CursorSpeed);
        return s.Present ? s : null;
    }

    public string? SetTouchpad(TouchpadSettings s)
    {
        // Read again first: only what the page has is written, the rest as Windows has it now.
        if (!ReadRaw(out var tp)) return $"could not read the touchpad ({Marshal.GetLastWin32Error()})";
        tp.VersionNumber = 1;
        tp.Settings = TouchpadBits.WriteSettings(tp.Settings, s);
        tp.Present = TouchpadBits.WritePresent(tp.Present, s.Enabled);
        tp.SensitivityLevel = Math.Clamp(s.Sensitivity, TouchpadSettings.MostSensitive, 4);
        tp.CursorSpeed = (uint)Math.Clamp(s.CursorSpeed, TouchpadSettings.SpeedMin, TouchpadSettings.SpeedMax);
        Interlocked.Increment(ref _writes);
        if (Native.SystemParametersInfo(Native.SPI_SETTOUCHPADPARAMETERS, (uint)Marshal.SizeOf<Native.TOUCHPAD_PARAMETERS>(), ref tp, Save)) return null;
        int error = Marshal.GetLastWin32Error();
        Log.Warn($"pointer: the touchpad's settings were not written ({error})");
        return $"error {error}";
    }

    public MouseSettings Mouse()
    {
        var m = new int[3];
        Native.SystemParametersInfo(Native.SPI_GETMOUSE, 0, m, 0);
        Native.SystemParametersInfo(Native.SPI_GETMOUSESPEED, 0, out int speed, 0);
        Native.SystemParametersInfo(Native.SPI_GETWHEELSCROLLLINES, 0, out int lines, 0);
        return new MouseSettings(speed, m[2] != 0, lines, Native.GetSystemMetrics(Native.SM_SWAPBUTTON) != 0, (int)Native.GetDoubleClickTime());
    }

    public string? SetMouse(MouseSettings s)
    {
        // Only what changed: each write tells every window.
        var now = Mouse();
        var errors = new List<string>();
        void Check(bool ok, string what) { Interlocked.Increment(ref _writes); if (!ok) errors.Add($"{what} ({Marshal.GetLastWin32Error()})"); }
        if (s.Speed != now.Speed) Check(Native.SystemParametersInfo(Native.SPI_SETMOUSESPEED, 0, new IntPtr(Math.Clamp(s.Speed, MouseSettings.SpeedMin, MouseSettings.SpeedMax)), Save), "speed");
        if (s.EnhancePrecision != now.EnhancePrecision)
        {
            var m = new int[3];
            Native.SystemParametersInfo(Native.SPI_GETMOUSE, 0, m, 0);
            // Windows' own thresholds (6 and 10) when there were none: acceleration with 0 and 0 does nothing.
            if (s.EnhancePrecision && m[0] == 0 && m[1] == 0) { m[0] = 6; m[1] = 10; }
            m[2] = s.EnhancePrecision ? 1 : 0;
            Check(Native.SystemParametersInfo(Native.SPI_SETMOUSE, 0, m, Save), "precision");
        }
        // A wheel of "one screen" (-1) or more than 20 lines shows as 20 on the page: 20 does not overwrite it.
        if (s.WheelLines != now.WheelLines && !(now.WheelLines is < 0 or > MouseSettings.WheelMax && s.WheelLines == MouseSettings.WheelMax))
            Check(Native.SystemParametersInfo(Native.SPI_SETWHEELSCROLLLINES, (uint)Math.Clamp(s.WheelLines, MouseSettings.WheelMin, MouseSettings.WheelMax), IntPtr.Zero, Save), "wheel");
        if (s.SwapButtons != now.SwapButtons) Check(Native.SystemParametersInfo(Native.SPI_SETMOUSEBUTTONSWAP, s.SwapButtons ? 1u : 0u, IntPtr.Zero, Save), "buttons");
        if (s.DoubleClickMs != now.DoubleClickMs)
            Check(Native.SystemParametersInfo(Native.SPI_SETDOUBLECLICKTIME, (uint)Math.Clamp(s.DoubleClickMs, MouseSettings.DoubleClickMin, MouseSettings.DoubleClickMax), IntPtr.Zero, Save), "double-click");
        if (errors.Count == 0) return null;
        Log.Warn("pointer: the mouse's settings were not all written: " + string.Join(", ", errors));
        return string.Join(", ", errors);
    }

    public KeyRepeatSettings Keys()
    {
        Native.SystemParametersInfo(Native.SPI_GETKEYBOARDDELAY, 0, out int delay, 0);
        Native.SystemParametersInfo(Native.SPI_GETKEYBOARDSPEED, 0, out int rate, 0);
        return new KeyRepeatSettings(delay, rate);
    }

    public string? SetKeys(KeyRepeatSettings s)
    {
        var now = Keys();
        var c = s.Clamped();
        var errors = new List<string>();
        if (c.Delay != now.Delay)
        {
            Interlocked.Increment(ref _writes);
            if (!Native.SystemParametersInfo(Native.SPI_SETKEYBOARDDELAY, (uint)c.Delay, IntPtr.Zero, Save)) errors.Add($"delay ({Marshal.GetLastWin32Error()})");
        }
        if (c.Rate != now.Rate)
        {
            Interlocked.Increment(ref _writes);
            if (!Native.SystemParametersInfo(Native.SPI_SETKEYBOARDSPEED, (uint)c.Rate, IntPtr.Zero, Save)) errors.Add($"rate ({Marshal.GetLastWin32Error()})");
        }
        if (errors.Count == 0) return null;
        Log.Warn("pointer: the keys' repeat was not all written: " + string.Join(", ", errors));
        return string.Join(", ", errors);
    }
}

/// <summary>
/// A spec's own values, in memory (FROSTBAR_E2E_POINTER=spec with --diag): a touchpad like this PC's, and the mouse and
/// keys at Windows' defaults. Nothing of Windows' is read or written.
/// </summary>
public sealed class SpecPointer : IPointerStore
{
    readonly object _gate = new();
    TouchpadSettings _touchpad = new(true, true, true, true, true, true, true, true, true, true, 2, 10);
    MouseSettings _mouse = new(10, true, 3, false, 500);
    KeyRepeatSettings _keys = new(1, 31);
    int _writes;
    public int Writes => _writes;

    public TouchpadSettings? Touchpad() { lock (_gate) return _touchpad; }
    public string? SetTouchpad(TouchpadSettings s) { lock (_gate) { _touchpad = s; _writes++; } return null; }
    public MouseSettings Mouse() { lock (_gate) return _mouse; }
    public string? SetMouse(MouseSettings s) { lock (_gate) { _mouse = s.Clamped(); _writes++; } return null; }
    public KeyRepeatSettings Keys() { lock (_gate) return _keys; }
    public string? SetKeys(KeyRepeatSettings s) { lock (_gate) { _keys = s.Clamped(); _writes++; } return null; }
}
