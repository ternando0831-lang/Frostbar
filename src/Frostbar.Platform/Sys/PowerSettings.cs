using System.Runtime.InteropServices;
using Frostbar.Platform.Infrastructure;

namespace Frostbar.Platform.Sys;

/// <summary>Windows 11's power mode (Settings → Power & battery → Power mode), plugged in or on battery.</summary>
public enum PowerMode { Unknown = -1, Efficiency = 0, Balanced = 1, Performance = 2 }

/// <summary>A setting of the active power plan that has a value plugged in (AC) and on battery (DC).</summary>
public enum PowerSetting { ScreenOff, Sleep, LidAction, EnergySaverThreshold }

/// <summary>The battery's wear: what it holds when full against what it was made to hold.</summary>
public sealed record BatteryHealth(uint DesignedMWh, uint FullChargeMWh, uint CycleCount)
{
    /// <summary>Full charge against design, in percent (0 when the battery does not say).</summary>
    public int MaximumCapacity => DesignedMWh == 0 ? 0 : (int)Math.Round(Math.Min(100.0, 100.0 * FullChargeMWh / DesignedMWh));
}

/// <summary>
/// The power settings Windows' Power &amp; battery page shows, read and written through the documented power APIs: the
/// power mode for each source (PowerGet/SetUserConfiguredAC/DCPowerMode, Windows 11 24H2), the active plan's values
/// (screen off, sleep, lid, energy saver's threshold), the battery's wear (IOCTL_BATTERY_QUERY_INFORMATION). A
/// standard user may change these, as the Settings app does. A changed value of the plan is applied by setting the
/// plan active again.
/// </summary>
public static class PowerSettings
{
    [DllImport("powrprof.dll")] static extern uint PowerGetUserConfiguredACPowerMode(out Guid mode);
    [DllImport("powrprof.dll")] static extern uint PowerGetUserConfiguredDCPowerMode(out Guid mode);
    [DllImport("powrprof.dll")] static extern uint PowerSetUserConfiguredACPowerMode(ref Guid mode);
    [DllImport("powrprof.dll")] static extern uint PowerSetUserConfiguredDCPowerMode(ref Guid mode);
    [DllImport("powrprof.dll")] static extern uint PowerGetEffectiveOverlayScheme(out Guid mode);
    [DllImport("powrprof.dll")] static extern uint PowerGetActiveScheme(IntPtr root, out IntPtr scheme);
    [DllImport("powrprof.dll")] static extern uint PowerSetActiveScheme(IntPtr root, ref Guid scheme);
    [DllImport("powrprof.dll")] static extern uint PowerReadACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerReadDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, out uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteACValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern uint PowerWriteDCValueIndex(IntPtr root, ref Guid scheme, ref Guid sub, ref Guid setting, uint value);
    [DllImport("powrprof.dll")] static extern bool GetPwrCapabilities(out SYSTEM_POWER_CAPABILITIES caps);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);

    [StructLayout(LayoutKind.Sequential)]
    struct SYSTEM_POWER_CAPABILITIES
    {
        [MarshalAs(UnmanagedType.U1)] public bool PowerButtonPresent, SleepButtonPresent, LidPresent, SystemS1, SystemS2, SystemS3, SystemS4, SystemS5, HiberFilePresent, FullWake, VideoDimPresent, ApmPresent, UpsPresent, ThermalControl, ProcessorThrottle;
        public byte ProcessorMinThrottle, ProcessorMaxThrottle;
        [MarshalAs(UnmanagedType.U1)] public bool FastSystemS4;
        [MarshalAs(UnmanagedType.U1)] public bool HiberBoot;
        [MarshalAs(UnmanagedType.U1)] public bool WakeAlarmPresent;
        [MarshalAs(UnmanagedType.U1)] public bool AoAc;
        [MarshalAs(UnmanagedType.U1)] public bool DiskSpinDown;
        public byte HiberFileType;
        [MarshalAs(UnmanagedType.U1)] public bool AoAcConnectivitySupported;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] spare3;
        [MarshalAs(UnmanagedType.U1)] public bool SystemBatteriesPresent, BatteriesAreShortTerm;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 24)] public byte[] BatteryScale; // BATTERY_REPORTING_SCALE[3]
        public int AcOnLineWake, SoftLidWake, RtcWake, MinDeviceWakeState, DefaultLowLatencyWake;
    }

    // Settings → Power mode: "Best power efficiency", "Balanced", "Best performance".
    static readonly Guid Efficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a"), Balanced = Guid.Empty,
        BetterPerformance = new("3af9b8d9-7c97-431d-ad78-34a8bfea439f"), Performance = new("ded574b5-45a0-4f42-8737-46345c09c238");

    static PowerMode ModeOf(Guid g) =>
        g == Efficiency ? PowerMode.Efficiency : g == Balanced || g == BetterPerformance ? PowerMode.Balanced : g == Performance ? PowerMode.Performance : PowerMode.Unknown;

    static Guid GuidOf(PowerMode m) => m switch { PowerMode.Efficiency => Efficiency, PowerMode.Performance => Performance, _ => Balanced };

    /// <summary>The power mode chosen for <paramref name="pluggedIn"/> (AC) or on battery.</summary>
    public static PowerMode GetMode(bool pluggedIn)
    {
        try
        {
            uint rc = pluggedIn ? PowerGetUserConfiguredACPowerMode(out var g) : PowerGetUserConfiguredDCPowerMode(out g);
            return rc == 0 ? ModeOf(g) : PowerMode.Unknown;
        }
        catch (EntryPointNotFoundException)
        {
            // Before 24H2 there is one mode, for the source in use.
            return PowerGetEffectiveOverlayScheme(out var g) == 0 ? ModeOf(g) : PowerMode.Unknown;
        }
    }

    /// <summary>False when Windows refused (or is older than 24H2, where the mode is not per source).</summary>
    public static bool SetMode(bool pluggedIn, PowerMode mode)
    {
        var g = GuidOf(mode);
        try
        {
            uint rc = pluggedIn ? PowerSetUserConfiguredACPowerMode(ref g) : PowerSetUserConfiguredDCPowerMode(ref g);
            Log.Info($"power: mode {(pluggedIn ? "plugged in" : "on battery")} → {mode}: {(rc == 0 ? "ok" : rc.ToString())}");
            return rc == 0;
        }
        catch (EntryPointNotFoundException) { return false; }
    }

    static (Guid sub, Guid setting) Key(PowerSetting s) => s switch
    {
        PowerSetting.ScreenOff => (new("7516b95f-f776-4464-8c53-06167f40cc99"), new("3c0bc021-c8a8-4e07-a973-6b14cbcb2b7e")),
        PowerSetting.Sleep => (new("238c9fa8-0aad-41ed-83f4-97be242c8f20"), new("29f6c1db-86da-48c5-9fdb-f2b67b1f44da")),
        PowerSetting.LidAction => (new("4f971e89-eebd-4455-a8de-9e59040e7347"), new("5ca83367-6e45-459f-a27b-476b1d01c936")),
        _ => (new("de830923-a562-41af-a086-e3a2c6bad2da"), new("e69653ca-cf7f-4f05-aa73-cb833fa90ad4")),
    };

    static Guid ActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var p) != 0) return Guid.Empty;
        try { return Marshal.PtrToStructure<Guid>(p); }
        finally { LocalFree(p); }
    }

    /// <summary>
    /// The active plan's value (seconds for the timeouts, 0 = never; the lid's action 0 nothing, 1 sleep, 2 hibernate,
    /// 3 shut down; energy saver's battery percent), or null when it cannot be read.
    /// </summary>
    public static uint? Get(PowerSetting s, bool pluggedIn)
    {
        var scheme = ActiveScheme();
        var (sub, setting) = Key(s);
        uint rc = pluggedIn ? PowerReadACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out uint v)
                            : PowerReadDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, out v);
        return rc == 0 ? v : null;
    }

    public static bool Set(PowerSetting s, bool pluggedIn, uint value)
    {
        var scheme = ActiveScheme();
        var (sub, setting) = Key(s);
        uint rc = pluggedIn ? PowerWriteACValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value)
                            : PowerWriteDCValueIndex(IntPtr.Zero, ref scheme, ref sub, ref setting, value);
        if (rc == 0) rc = PowerSetActiveScheme(IntPtr.Zero, ref scheme); // takes effect when the plan is set again
        Log.Info($"power: {s} {(pluggedIn ? "AC" : "DC")} → {value}: {(rc == 0 ? "ok" : rc.ToString())}");
        return rc == 0;
    }

    /// <summary>Whether the PC has a lid, and can hibernate (the lid's choices offer it only then).</summary>
    public static (bool lid, bool hibernate) Capabilities()
    {
        try { return GetPwrCapabilities(out var c) ? (c.LidPresent, c.SystemS4 && c.HiberFilePresent) : (false, false); }
        catch (Exception) { return (false, false); }
    }

    /// <summary>Energy saver is on now (Windows turns it on below its threshold, or the user did).</summary>
    public static bool EnergySaverOn => Interop.Native.GetSystemPowerStatus(out var s) && s.SystemStatusFlag == 1;

    /// <summary>Seconds of battery left as Windows estimates it, or null (plugged in, or not estimated yet).</summary>
    public static int? SecondsLeft => Interop.Native.GetSystemPowerStatus(out var s) && s.ACLineStatus != 1 && s.BatteryLifeTime > 0 ? s.BatteryLifeTime : null;

    // ---- the battery's wear -----------------------------------------------------------------------------------

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern IntPtr SetupDiGetClassDevs(ref Guid cls, IntPtr enumerator, IntPtr parent, int flags);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid cls, int index, ref SP_DEVICE_INTERFACE_DATA data);
    [DllImport("setupapi.dll", CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid cls; public int flags; public IntPtr reserved; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(IntPtr h, uint code, ref uint inBuf, int inSize, out uint outBuf, int outSize, out int returned, IntPtr ov);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(IntPtr h, uint code, ref BATTERY_QUERY_INFORMATION inBuf, int inSize, out BATTERY_INFORMATION outBuf, int outSize, out int returned, IntPtr ov);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    [StructLayout(LayoutKind.Sequential)] struct BATTERY_QUERY_INFORMATION { public uint BatteryTag; public int InformationLevel; public int AtRate; }
    [StructLayout(LayoutKind.Sequential)]
    struct BATTERY_INFORMATION { public uint Capabilities; public byte Technology, R1, R2, R3; public uint Chemistry; public uint DesignedCapacity, FullChargedCapacity, DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount; }

    /// <summary>The first battery's wear, or null (no battery, or it does not say).</summary>
    public static BatteryHealth? Health()
    {
        var cls = new Guid("72631e54-78a4-11d0-bcf7-00aa00b7b32a"); // GUID_DEVCLASS_BATTERY
        var set = SetupDiGetClassDevs(ref cls, IntPtr.Zero, IntPtr.Zero, 0x10 /*DEVICEINTERFACE*/ | 0x2 /*PRESENT*/);
        if (set == new IntPtr(-1)) return null;
        try
        {
            var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
            if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref cls, 0, ref d)) return null;
            SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out int need, IntPtr.Zero);
            if (need <= 0) return null;
            var buf = Marshal.AllocHGlobal(need);
            string path;
            try
            {
                Marshal.WriteInt32(buf, IntPtr.Size == 8 ? 8 : 6); // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W
                if (!SetupDiGetDeviceInterfaceDetail(set, ref d, buf, need, out _, IntPtr.Zero)) return null;
                path = Marshal.PtrToStringUni(buf + 4) ?? "";
            }
            finally { Marshal.FreeHGlobal(buf); }
            var h = CreateFile(path, 0x80000000 | 0x40000000, 3, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0x80, IntPtr.Zero);
            if (h == new IntPtr(-1)) return null;
            try
            {
                uint wait = 0;
                if (!DeviceIoControl(h, 0x294040 /*IOCTL_BATTERY_QUERY_TAG*/, ref wait, 4, out uint tag, 4, out _, IntPtr.Zero) || tag == 0) return null;
                var q = new BATTERY_QUERY_INFORMATION { BatteryTag = tag, InformationLevel = 0 /*BatteryInformation*/ };
                if (!DeviceIoControl(h, 0x294044 /*IOCTL_BATTERY_QUERY_INFORMATION*/, ref q, Marshal.SizeOf<BATTERY_QUERY_INFORMATION>(), out var info, Marshal.SizeOf<BATTERY_INFORMATION>(), out _, IntPtr.Zero)) return null;
                return new BatteryHealth(info.DesignedCapacity, info.FullChargedCapacity, info.CycleCount);
            }
            finally { CloseHandle(h); }
        }
        catch (Exception ex) { Log.Debug("battery health: " + ex.Message); return null; }
        finally { SetupDiDestroyDeviceInfoList(set); }
    }
}
