using System.Runtime.InteropServices;
using System.Windows.Interop;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Shell;

/// <summary>
/// A hidden top-level window on the UI thread. Message-only windows miss broadcasts, so this is a real (never shown)
/// popup: it receives TaskbarCreated (Explorer restarted), setting and display changes, and power events.
/// The watchdog pings it with WM_NULL to tell a hung UI thread.
/// </summary>
public sealed class MessageWindow : IDisposable
{
    public static readonly uint TaskbarCreated = Native.RegisterWindowMessage("TaskbarCreated");
    readonly HwndSource _source;

    public IntPtr Handle => _source.Handle;
    public event Action? ExplorerRestarted;
    /// <summary>WM_SETTINGCHANGE with its string ("ImmersiveColorSet", "WindowMetrics", …) or null.</summary>
    public event Action<string?>? SettingChanged;
    /// <summary>A work area changed (WM_SETTINGCHANGE with SPI_SETWORKAREA): app bars came, went or moved.</summary>
    public event Action? WorkAreaChanged;
    public event Action? DisplayChanged;
    public event Action? PowerResumed;
    /// <summary>AC plugged/unplugged or battery level changed (PBT_APMPOWERSTATUSCHANGE).</summary>
    public event Action? PowerStatusChanged;

    public MessageWindow()
    {
        var p = new HwndSourceParameters("Frostbar.Message")
        {
            WindowStyle = Native.WS_POPUP,
            ExtendedWindowStyle = (int)(Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE),
            PositionX = -32000, PositionY = -32000, Width = 1, Height = 1,
        };
        _source = new HwndSource(p);
        _source.AddHook(Hook);
    }

    IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        Stats.Inc($"messageWindow.0x{msg:X4}");
        if ((uint)msg == TaskbarCreated) { ExplorerRestarted?.Invoke(); return IntPtr.Zero; }
        switch ((uint)msg)
        {
            case Native.WM_SETTINGCHANGE:
                // Work-area changes (SPI_SETWORKAREA) are not display changes: our own app bars cause them, and the
                // app-bar host already re-places bars on ABN_POSCHANGED. Treating them here would loop.
                SettingChanged?.Invoke(lParam != IntPtr.Zero ? Marshal.PtrToStringUni(lParam) : null);
                if (wParam.ToInt64() == 0x2F /*SPI_SETWORKAREA*/) WorkAreaChanged?.Invoke();
                break;
            case Native.WM_DISPLAYCHANGE:
            case Native.WM_DPICHANGED:
                DisplayChanged?.Invoke();
                break;
            case Native.WM_POWERBROADCAST:
                if (wParam.ToInt64() == 0x12 /*PBT_APMRESUMEAUTOMATIC*/) PowerResumed?.Invoke();
                if (wParam.ToInt64() == 0xA /*PBT_APMPOWERSTATUSCHANGE*/) PowerStatusChanged?.Invoke();
                break;
        }
        return IntPtr.Zero;
    }

    public void Dispose() => _source.Dispose();
}
