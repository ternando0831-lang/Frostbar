using System.Runtime.InteropServices;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Shell;

public sealed record MonitorInfo(IntPtr Handle, RECT Bounds, RECT Work, bool Primary, uint Dpi, string Device)
{
    public double Scale => Dpi / 96.0;
    public int Px(double dip) => (int)Math.Round(dip * Scale);
}

public static class Monitors
{
    public static List<MonitorInfo> All()
    {
        var list = new List<MonitorInfo>();
        Native.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr _, ref RECT _, IntPtr _) =>
        {
            if (Get(m) is { } info) list.Add(info);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    public static MonitorInfo? Get(IntPtr m)
    {
        var mi = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
        if (!Native.GetMonitorInfo(m, ref mi)) return null;
        uint dpi = Native.GetDpiForMonitor(m, 0 /*MDT_EFFECTIVE_DPI*/, out uint x, out _) == 0 ? x : 96;
        return new MonitorInfo(m, mi.rcMonitor, mi.rcWork, (mi.dwFlags & 1) != 0, dpi, mi.szDevice);
    }

    public static MonitorInfo Primary() => All().FirstOrDefault(m => m.Primary) ?? All()[0];

    public static MonitorInfo? Of(IntPtr hwnd) => Get(Native.MonitorFromWindow(hwnd, Native.MONITOR_DEFAULTTONEAREST));
}
