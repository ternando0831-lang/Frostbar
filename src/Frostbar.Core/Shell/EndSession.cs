namespace Frostbar.Core.Shell;

/// <summary>What Windows asks for with WM_QUERYENDSESSION: the session ends, or one program is to close.</summary>
public enum EndSessionKind { SessionEnd, CloseApp }

/// <summary>
/// The flags (lParam) of WM_QUERYENDSESSION / WM_ENDSESSION. Restart Manager (an installer, another program's update)
/// asks the programs that use its files to close with ENDSESSION_CLOSEAPP, and the session goes on: on 2026-10-16 the
/// Claude app's update (MSIX, ForceApplicationShutdownOption) did so, and the shell quit as if the user signed out and
/// stayed away. WPF's SessionEnding keeps only "log off or shut down" of the flags.
/// </summary>
public static class EndSession
{
    public const uint CloseApp = 0x1, Critical = 0x40000000, Logoff = 0x80000000;

    /// <summary>
    /// "Close the app" closes it for another program; "sign out" and "critical" end the session. A shut down or restart
    /// (no flags) and a query not seen go by Windows' "shutting down" metric (SM_SHUTTINGDOWN): what sent the shell its
    /// "session ending" on 2026-10-16 was not recorded, and the metric's value during the query of a real shutdown is
    /// unverified (both are logged at every end, to be checked). Should it lag, the watchdog's wait and its own checks
    /// of the metric keep a real shutdown from being started into (<see cref="RelaunchPlanner"/>).
    /// </summary>
    public static EndSessionKind Classify(uint? flags, bool shuttingDown) => flags switch
    {
        uint f when (f & CloseApp) != 0 => EndSessionKind.CloseApp,
        uint f when (f & (Logoff | Critical)) != 0 => EndSessionKind.SessionEnd,
        _ => shuttingDown ? EndSessionKind.SessionEnd : EndSessionKind.CloseApp,
    };

    /// <summary>For the log: "close the app (0x00000001)", "sign out (0x80000000)", "shut down or restart (0x00000000)", "not seen".</summary>
    public static string Describe(uint? flags)
    {
        if (flags is not uint f) return "not seen";
        var parts = new List<string>();
        if ((f & CloseApp) != 0) parts.Add("close the app");
        if ((f & Logoff) != 0) parts.Add("sign out");
        if ((f & Critical) != 0) parts.Add("critical");
        if (parts.Count == 0) parts.Add("shut down or restart");
        return $"{string.Join(", ", parts)} (0x{f:X8})";
    }
}
