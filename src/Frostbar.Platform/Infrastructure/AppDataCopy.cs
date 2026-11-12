using System.Management;
using System.Text;
using Frostbar.Core.Shell;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Infrastructure;

/// <summary>
/// Whether the data folders are another app's copy of AppData (<see cref="PackageCopy"/>). Checked on 2026-10-19 from a
/// Claude session (in Claude's job, with no package of its own): a new file in %LOCALAPPDATA%\Frostbar and in
/// %APPDATA%\Frostbar went to Packages\Claude_pzs8sxrjxfjjc\LocalCache, while the folders themselves (and %TEMP%,
/// %LOCALAPPDATA%\Programs, the Start menu) read as the user's; from a process started through WMI, both went where
/// they were asked.
/// </summary>
public static class AppDataCopy
{
    /// <summary>
    /// The folder a new file in <paramref name="folder"/> really goes to: the final path of a hidden file made and deleted
    /// at once (nothing is left). Null when it cannot be made or read.
    /// </summary>
    public static string? WhereNewFilesGo(string folder)
    {
        const uint GenericWrite = 0x40000000, Delete = 0x00010000, ShareAll = 7, CreateNew = 1, Hidden = 0x2, DeleteOnClose = 0x04000000;
        string path = Path.Combine(folder, ".where-" + Guid.NewGuid().ToString("N"));
        using var h = Native.CreateFile(path, GenericWrite | Delete, ShareAll, IntPtr.Zero, CreateNew, Hidden | DeleteOnClose, IntPtr.Zero);
        if (h.IsInvalid) return null;
        var sb = new StringBuilder(1024);
        uint n = Native.GetFinalPathNameByHandle(h, sb, (uint)sb.Capacity, 0 /*FILE_NAME_NORMALIZED | VOLUME_NAME_DOS*/);
        return n == 0 || n >= sb.Capacity ? null : Path.GetDirectoryName(sb.ToString());
    }

    /// <summary>The package whose copy the first of <paramref name="folders"/> that is one is, and where its new files go.</summary>
    public static (string Family, string Where)? Of(params string[] folders)
    {
        foreach (var folder in folders)
            if (WhereNewFilesGo(folder) is { } went && PackageCopy.Family(folder, went) is { } family) return (family, went);
        return null;
    }
}

/// <summary>
/// Starting a process outside the caller's job. From a Claude session, CREATE_BREAKAWAY_FROM_JOB is refused (error 5,
/// 2026-10-19); a process WMI starts (Win32_Process.Create) has WmiPrvSE for a parent, is not in Claude's job, runs in the
/// user's session on WinSta0\Default, and gets the user's own environment (not the caller's).
/// </summary>
public static class OutsideStart
{
    /// <summary>Starts <paramref name="commandLine"/> in <paramref name="directory"/>: its process id, or null and why not.</summary>
    public static int? Start(string commandLine, string directory, out string why)
    {
        try
        {
            using var process = new ManagementClass(@"root\cimv2", "Win32_Process", null);
            using var input = process.GetMethodParameters("Create");
            input["CommandLine"] = commandLine;
            input["CurrentDirectory"] = directory;
            using var output = process.InvokeMethod("Create", input, null);
            uint rc = Convert.ToUInt32(output["ReturnValue"]);
            why = rc == 0 ? "" : "Win32_Process.Create returned " + rc;
            return rc == 0 ? Convert.ToInt32(output["ProcessId"]) : null;
        }
        catch (Exception e)
        {
            why = $"{e.GetType().Name}: {e.Message}";
            return null;
        }
    }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for a process started outside. Call it at once: a process gone (or not to be
    /// opened) before it is opened counts as finished, with an exit code nobody can read (Process.GetProcessById lost the
    /// code of a probe that ran 0.1 s on 2026-10-19: it opens the process only to read the code, after the exit).
    /// </summary>
    public static (bool Finished, int? Code) Wait(int pid, TimeSpan timeout)
    {
        IntPtr h = Native.OpenProcess(Native.SYNCHRONIZE | Native.PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (h == IntPtr.Zero) return (true, null);
        try
        {
            if (Native.WaitForSingleObject(h, (uint)timeout.TotalMilliseconds) != 0 /*WAIT_OBJECT_0*/) return (false, null);
            return (true, Native.GetExitCodeProcess(h, out uint code) ? (int)code : null);
        }
        finally { Native.CloseHandle(h); }
    }
}
