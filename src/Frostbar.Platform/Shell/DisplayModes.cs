using System.Runtime.InteropServices;

namespace Frostbar.Platform.Shell;

/// <summary>A display mode: pixels, refresh rate and colour depth.</summary>
public sealed record DisplayMode(int Width, int Height, int Hz, int Bits)
{
    public override string ToString() => $"{Width}x{Height} {Hz} Hz {Bits} bit";
}

/// <summary>
/// The modes a display offers and the one it shows (EnumDisplaySettings), and a change for the moment only
/// (ChangeDisplaySettingsEx without CDS_UPDATEREGISTRY: nothing is saved, and <see cref="Reset"/> or the next sign-in
/// brings the saved mode back): the E2E's display-change test. The Displays page (stage 4-4) changes and saves at once
/// (<see cref="Apply"/>), as Windows' settings do, and puts the mode before back the same way when it is not kept.
/// </summary>
public static class DisplayModes
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields, dmPositionX, dmPositionY, dmDisplayOrientation, dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    const int ENUM_CURRENT_SETTINGS = -1;
    const int DM_BITSPERPEL = 0x40000, DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000;

    /// <summary>ChangeDisplaySettingsEx's answer for a change made.</summary>
    public const int DISP_CHANGE_SUCCESSFUL = 0;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplaySettings(string device, int mode, ref DEVMODE dm);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, ref DEVMODE dm, IntPtr hwnd, uint flags, IntPtr param);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int ChangeDisplaySettingsEx(string device, IntPtr dm, IntPtr hwnd, uint flags, IntPtr param);

    static DEVMODE New() => new() { dmDeviceName = "", dmFormName = "", dmSize = (short)Marshal.SizeOf<DEVMODE>() };

    static DisplayMode Of(in DEVMODE dm) => new(dm.dmPelsWidth, dm.dmPelsHeight, dm.dmDisplayFrequency, dm.dmBitsPerPel);

    /// <summary>The mode <paramref name="device"/> ("\\.\DISPLAY1") shows now, or null.</summary>
    public static DisplayMode? Current(string device)
    {
        var dm = New();
        return EnumDisplaySettings(device, ENUM_CURRENT_SETTINGS, ref dm) ? Of(dm) : null;
    }

    /// <summary>Every mode the display offers, each once (the driver lists some several times, for its scaling modes).</summary>
    public static List<DisplayMode> All(string device)
    {
        var list = new List<DisplayMode>();
        var dm = New();
        for (int i = 0; EnumDisplaySettings(device, i, ref dm); i++, dm = New())
        {
            var m = Of(dm);
            if (!list.Contains(m)) list.Add(m);
        }
        return list;
    }

    /// <summary>Shows <paramref name="mode"/> on <paramref name="device"/> for now, not saved. A DISP_CHANGE_* value (0: done).</summary>
    public static int Set(string device, DisplayMode mode)
    {
        var dm = New();
        dm.dmPelsWidth = mode.Width;
        dm.dmPelsHeight = mode.Height;
        dm.dmDisplayFrequency = mode.Hz;
        dm.dmBitsPerPel = mode.Bits;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_BITSPERPEL;
        return ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, 0, IntPtr.Zero);
    }

    /// <summary>Back to the mode saved for the display (the one Windows' settings show). A DISP_CHANGE_* value.</summary>
    public static int Reset(string device) => ChangeDisplaySettingsEx(device, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);

    const uint CDS_UPDATEREGISTRY = 1;
    const int ENUM_REGISTRY_SETTINGS = -2;

    /// <summary>Shows <paramref name="mode"/> on <paramref name="device"/> and saves it, as Windows' settings do (the Displays page). A DISP_CHANGE_* value.</summary>
    public static int Apply(string device, DisplayMode mode)
    {
        var dm = New();
        dm.dmPelsWidth = mode.Width;
        dm.dmPelsHeight = mode.Height;
        dm.dmDisplayFrequency = mode.Hz;
        dm.dmBitsPerPel = mode.Bits;
        dm.dmFields = DM_PELSWIDTH | DM_PELSHEIGHT | DM_DISPLAYFREQUENCY | DM_BITSPERPEL;
        return ChangeDisplaySettingsEx(device, ref dm, IntPtr.Zero, CDS_UPDATEREGISTRY, IntPtr.Zero);
    }

    /// <summary>The mode saved for <paramref name="device"/> (the next sign-in's), or null.</summary>
    public static DisplayMode? Saved(string device)
    {
        var dm = New();
        return EnumDisplaySettings(device, ENUM_REGISTRY_SETTINGS, ref dm) ? Of(dm) : null;
    }
}
