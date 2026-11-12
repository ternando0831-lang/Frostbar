using System.Runtime.InteropServices;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Sys;

/// <summary>
/// Sleep, restart, shut down, lock, log out. In development builds (DryRun) nothing happens except a log line: these must
/// never fire during tests (the user may be asleep; a lock would also stop GUI tests).
/// </summary>
public sealed class PowerActions(bool dryRun)
{
    /// <summary>What an action returns when a development build only logged it.</summary>
    public const string Skipped = "dry-run";

    public bool DryRun => dryRun;

    string Do(string what, Func<bool> act)
    {
        if (dryRun) { Log.Info($"power action '{what}' skipped (development build)"); return Skipped; }
        Log.Info($"power action '{what}'");
        bool ok = act();
        if (!ok) Log.Warn($"power action '{what}' failed: {Marshal.GetLastWin32Error()}");
        return ok ? "ok" : "failed";
    }

    /// <summary>
    /// Not yet verified on Modern Standby machines like the test PC (development builds only log it): SetSuspendState
    /// may hibernate there instead of entering standby. The test PC offers only S0 low-power idle and has hibernation
    /// switched off (powercfg /a, 2026-10-12), so it cannot hibernate there; whether it enters standby or fails is not
    /// seen (trying it puts the PC to sleep). Open item: the installed build went in without this check.
    /// </summary>
    public string Sleep() => Do("sleep", () => Native.SetSuspendState(false, false, false));
    public string Lock() => Do("lock", Native.LockWorkStation);
    public string Restart() => Do("restart", () => EnableShutdownPrivilege() && Native.ExitWindowsEx(0x2 /*EWX_REBOOT*/, 0x80000000 /*SHTDN_REASON_FLAG_PLANNED*/));
    public string Shutdown() => Do("shutdown", () => EnableShutdownPrivilege() && Native.ExitWindowsEx(0x1 | 0x8 /*EWX_SHUTDOWN|EWX_POWEROFF*/, 0x80000000));
    public string Logout() => Do("logout", () => Native.ExitWindowsEx(0 /*EWX_LOGOFF*/, 0));

    static bool EnableShutdownPrivilege()
    {
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 0x20 | 0x8 /*ADJUST_PRIVILEGES|QUERY*/, out var token)) return false;
        try
        {
            if (!Native.LookupPrivilegeValue(null, "SeShutdownPrivilege", out var luid)) return false;
            var tp = new TOKEN_PRIVILEGES { PrivilegeCount = 1, Luid = luid, Attributes = 0x2 /*SE_PRIVILEGE_ENABLED*/ };
            return Native.AdjustTokenPrivileges(token, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
        }
        finally { Native.CloseHandle(token); }
    }
}
