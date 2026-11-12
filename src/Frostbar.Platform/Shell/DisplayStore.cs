using System.Runtime.InteropServices;
using Frostbar.Core.Devices;
using Frostbar.Platform.Infrastructure;

namespace Frostbar.Platform.Shell;

/// <summary>A display as the Displays page shows it (stage 4-4).</summary>
/// <param name="Device">Its GDI name ("\\.\DISPLAY1"), as EnumDisplaySettings and the monitors' info have it.</param>
/// <param name="Name">The monitor's own name (its EDID); empty for a built-in panel, which has none.</param>
/// <param name="Preferred">The monitor's own (native) mode, or null when it does not say.</param>
/// <param name="Scale">The scales it offers, or null when DisplayConfig's answer is not understood (the page leaves it to Windows' settings).</param>
public sealed record DisplayInfo(string Device, string Name, bool Internal, bool Primary, ScreenMode Mode, ScreenMode? Preferred,
    IReadOnlyList<ScreenMode> Modes, ScaleRange? Scale, bool HdrSupported, bool HdrOn);

/// <summary>
/// Where the Displays page reads and changes the displays (stage 4-4): Windows' own, or, under a spec, displays of the
/// spec's own kept in memory, so that no spec changes the user's screen unless it asks for Windows'.
/// </summary>
public interface IDisplayStore
{
    /// <summary>The active displays, the primary first.</summary>
    IReadOnlyList<DisplayInfo> Displays();
    /// <summary>Shows <paramref name="mode"/> on <paramref name="device"/> and saves it (as Windows' settings do; the page's countdown puts the one before back the same way); what went wrong, or null.</summary>
    string? SetMode(string device, ScreenMode mode);
    /// <summary>Sets the scale of <paramref name="device"/> (Windows saves it at once); what went wrong, or null.</summary>
    string? SetScale(string device, int percent);
    /// <summary>Changes made (diagnostics: a spec counts them).</summary>
    int Writes { get; }
}

public static class DisplayStore
{
    /// <summary>The store the page uses: Windows', unless a spec asked for its own at start.</summary>
    public static IDisplayStore Current { get; set; } = new WindowsDisplays();
}

/// <summary>
/// Windows' displays: the active paths of the display configuration (QueryDisplayConfig) with their names, native modes,
/// scales and colour (DisplayConfigGetDeviceInfo), the modes EnumDisplaySettings lists, and the scale shown now from the
/// monitor's DPI. The scale is read and set through DisplayConfig's undocumented DPI-scale requests (-3 and -4, S20
/// 2026-10-16 on 26300: steps from the recommended one); when they do not answer, the page leaves the scale to Windows.
/// </summary>
public sealed class WindowsDisplays : IDisplayStore
{
    [StructLayout(LayoutKind.Sequential)] struct LUID { public uint Low; public int High; }
    [StructLayout(LayoutKind.Sequential)] struct RATIONAL { public uint Num, Den; }
    [StructLayout(LayoutKind.Sequential)] struct PATH_SOURCE { public LUID Adapter; public uint Id, ModeIdx, Status; }
    [StructLayout(LayoutKind.Sequential)] struct PATH_TARGET { public LUID Adapter; public uint Id, ModeIdx, Tech, Rotation, Scaling; public RATIONAL Refresh; public uint ScanLine; public int Available; public uint Status; }
    [StructLayout(LayoutKind.Sequential)] struct PATH { public PATH_SOURCE Source; public PATH_TARGET Target; public uint Flags; }
    [StructLayout(LayoutKind.Sequential)] struct MODE { public uint Type, Id; public LUID Adapter; public ulong D0, D1, D2, D3, D4, D5; }
    [StructLayout(LayoutKind.Sequential)] struct HEADER { public int Type; public uint Size; public LUID Adapter; public uint Id; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct SOURCE_NAME { public HEADER Header; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Gdi; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct TARGET_NAME
    {
        public HEADER Header; public uint Flags, Tech; public ushort Manufacturer, Product; public uint Connector;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string Friendly;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Path;
    }
    // The signal's pixel rate aligns the target mode to 8 bytes: 80 bytes in all (S20).
    [StructLayout(LayoutKind.Sequential)]
    struct PREFERRED_MODE { public HEADER Header; public uint Width, Height; public ulong PixelRate; public RATIONAL HSync, VSync; public uint ActiveCx, ActiveCy, TotalCx, TotalCy, VideoStandard, ScanLine; }
    [StructLayout(LayoutKind.Sequential)] struct DPI_GET { public HEADER Header; public int Min, Cur, Max; }
    [StructLayout(LayoutKind.Sequential)] struct DPI_SET { public HEADER Header; public int Rel; }
    [StructLayout(LayoutKind.Sequential)] struct COLOR_INFO_2 { public HEADER Header; public uint Value, Encoding, Bits, Mode; }

    const uint QDC_ONLY_ACTIVE_PATHS = 2;
    const int GET_SOURCE_NAME = 1, GET_TARGET_NAME = 2, GET_PREFERRED_MODE = 3, GET_ADVANCED_COLOR_INFO_2 = 15, GET_DPI_SCALE = -3, SET_DPI_SCALE = -4;
    const uint TECH_INTERNAL = 0x80000000, TECH_LVDS = 6, TECH_DISPLAYPORT_EMBEDDED = 11, TECH_UDI_EMBEDDED = 13;

    [DllImport("user32.dll")] static extern int GetDisplayConfigBufferSizes(uint flags, out uint paths, out uint modes);
    [DllImport("user32.dll")] static extern int QueryDisplayConfig(uint flags, ref uint paths, [Out] PATH[] pathArray, ref uint modes, [Out] MODE[] modeArray, IntPtr topology);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref SOURCE_NAME info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref TARGET_NAME info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref PREFERRED_MODE info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref DPI_GET info);
    [DllImport("user32.dll")] static extern int DisplayConfigGetDeviceInfo(ref COLOR_INFO_2 info);
    [DllImport("user32.dll")] static extern int DisplayConfigSetDeviceInfo(ref DPI_SET info);

    int _writes;
    public int Writes => _writes;

    static HEADER Header(int type, int size, LUID adapter, uint id) => new() { Type = type, Size = (uint)size, Adapter = adapter, Id = id };

    static PATH[] Paths()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            if (GetDisplayConfigBufferSizes(QDC_ONLY_ACTIVE_PATHS, out uint np, out uint nm) != 0) return [];
            var paths = new PATH[np];
            var modes = new MODE[nm];
            int r = QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero);
            if (r == 0) return paths[..(int)np];
            if (r != 122) { Log.Warn($"displays: QueryDisplayConfig {r}"); return []; } // 122: the paths changed meanwhile: ask again
        }
        return [];
    }

    static string Gdi(in PATH p)
    {
        var source = new SOURCE_NAME { Header = Header(GET_SOURCE_NAME, Marshal.SizeOf<SOURCE_NAME>(), p.Source.Adapter, p.Source.Id) };
        return DisplayConfigGetDeviceInfo(ref source) == 0 ? source.Gdi : "";
    }

    public IReadOnlyList<DisplayInfo> Displays()
    {
        var monitors = Monitors.All();
        var list = new List<DisplayInfo>();
        foreach (var p in Paths())
        {
            string device = Gdi(p);
            if (device.Length == 0) continue;
            var target = new TARGET_NAME { Header = Header(GET_TARGET_NAME, Marshal.SizeOf<TARGET_NAME>(), p.Target.Adapter, p.Target.Id) };
            bool named = DisplayConfigGetDeviceInfo(ref target) == 0;
            uint tech = named ? target.Tech : p.Target.Tech;
            bool builtIn = tech is TECH_INTERNAL or TECH_LVDS or TECH_DISPLAYPORT_EMBEDDED or TECH_UDI_EMBEDDED;

            var preferred = new PREFERRED_MODE { Header = Header(GET_PREFERRED_MODE, Marshal.SizeOf<PREFERRED_MODE>(), p.Target.Adapter, p.Target.Id) };
            ScreenMode? native = DisplayConfigGetDeviceInfo(ref preferred) == 0 && preferred.Width > 0
                ? new ScreenMode((int)preferred.Width, (int)preferred.Height, Hz(preferred.VSync)) : null;

            var monitor = monitors.FirstOrDefault(m => string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase));
            ScaleRange? scale = null;
            var dpi = new DPI_GET { Header = Header(GET_DPI_SCALE, Marshal.SizeOf<DPI_GET>(), p.Source.Adapter, p.Source.Id) };
            if (monitor != null && DisplayConfigGetDeviceInfo(ref dpi) == 0)
                scale = DisplayChoices.Scales((int)Math.Round(monitor.Dpi * 100 / 96.0), dpi.Min, dpi.Cur, dpi.Max);

            // Windows 11 24H2's second colour query tells HDR from wide colour (a built-in panel's colour management
            // answers "advanced colour on" to the first one: S20).
            var color = new COLOR_INFO_2 { Header = Header(GET_ADVANCED_COLOR_INFO_2, Marshal.SizeOf<COLOR_INFO_2>(), p.Target.Adapter, p.Target.Id) };
            bool colorRead = DisplayConfigGetDeviceInfo(ref color) == 0;
            bool hdrSupported = colorRead && (color.Value & 16) != 0, hdrOn = colorRead && (color.Value & 32) != 0;

            var modes = DisplayModes.All(device).Where(m => m.Bits == 32).Select(m => new ScreenMode(m.Width, m.Height, m.Hz)).Distinct().ToList();
            var now = DisplayModes.Current(device);
            if (now == null) continue;
            list.Add(new DisplayInfo(device, named ? target.Friendly : "", builtIn, monitor?.Primary ?? false, new ScreenMode(now.Width, now.Height, now.Hz),
                native, modes, scale, hdrSupported, hdrOn));
        }
        return [.. list.OrderByDescending(d => d.Primary)];
    }

    static int Hz(RATIONAL r) => r.Den == 0 ? 0 : (int)Math.Round(r.Num / (double)r.Den);

    public string? SetMode(string device, ScreenMode mode)
    {
        Interlocked.Increment(ref _writes);
        int r = DisplayModes.Apply(device, new DisplayMode(mode.Width, mode.Height, mode.Hz, 32));
        if (r == DisplayModes.DISP_CHANGE_SUCCESSFUL) { Log.Info($"displays: {device} at {mode}"); return null; }
        Log.Warn($"displays: {device} not set to {mode}: DISP_CHANGE {r}");
        return $"DISP_CHANGE {r}";
    }

    public string? SetScale(string device, int percent)
    {
        foreach (var p in Paths())
        {
            if (!string.Equals(Gdi(p), device, StringComparison.OrdinalIgnoreCase)) continue;
            var info = Displays().FirstOrDefault(d => d.Device == device);
            if (info?.Scale is not { } scale || !scale.Percents.Contains(percent)) return "the scale is not offered";
            Interlocked.Increment(ref _writes);
            var set = new DPI_SET { Header = Header(SET_DPI_SCALE, Marshal.SizeOf<DPI_SET>(), p.Source.Adapter, p.Source.Id), Rel = scale.Relative(percent) };
            int r = DisplayConfigSetDeviceInfo(ref set);
            if (r == 0) { Log.Info($"displays: {device} at {percent}%"); return null; }
            Log.Warn($"displays: {device} not set to {percent}%: {r}");
            return $"0x{r:X}";
        }
        return "the display is not there";
    }
}

/// <summary>
/// A spec's own displays, in memory (FROSTBAR_E2E_DISPLAY=spec with --diag): a built-in panel like this PC's (one rate,
/// 125% of 100–175%) and a second monitor of several rates that says it has HDR, so the page's choices can be tried
/// without touching the user's screen.
/// </summary>
public sealed class SpecDisplays : IDisplayStore
{
    readonly object _gate = new();
    readonly List<DisplayInfo> _displays =
    [
        new(@"\\.\DISPLAY1", "", true, true, new(1920, 1080, 60), new(1920, 1080, 60),
            [new(1920, 1080, 60), new(1680, 1050, 60), new(1600, 900, 60), new(1280, 720, 60)], DisplayChoices.Scales(125, -2, -1, 1), false, false),
        new(@"\\.\DISPLAY2", "Frostbar E2E Monitor", false, false, new(2560, 1440, 144), new(2560, 1440, 144),
            [new(2560, 1440, 144), new(2560, 1440, 120), new(2560, 1440, 60), new(1920, 1080, 144), new(1920, 1080, 60)], DisplayChoices.Scales(100, 0, 0, 4), true, false),
    ];
    int _writes;
    public int Writes { get { lock (_gate) return _writes; } }

    public IReadOnlyList<DisplayInfo> Displays() { lock (_gate) return [.. _displays]; }

    public string? SetMode(string device, ScreenMode mode)
    {
        lock (_gate)
        {
            int i = _displays.FindIndex(d => d.Device == device);
            if (i < 0) return "the display is not there";
            if (!_displays[i].Modes.Contains(mode)) return "DISP_CHANGE -2";
            _displays[i] = _displays[i] with { Mode = mode };
            _writes++;
            return null;
        }
    }

    public string? SetScale(string device, int percent)
    {
        lock (_gate)
        {
            int i = _displays.FindIndex(d => d.Device == device);
            if (i < 0) return "the display is not there";
            if (_displays[i].Scale is not { } s || !s.Percents.Contains(percent)) return "the scale is not offered";
            _displays[i] = _displays[i] with { Scale = s with { Now = percent } };
            _writes++;
            return null;
        }
    }
}
