using System.Runtime.InteropServices;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Shell;

/// <summary>
/// SHAppBarMessage, sent straight to Explorer's taskbar. SHAppBarMessage itself goes to the first "Shell_TrayWnd" in
/// z-order, which is our tray's window while it runs (ADR-001), and the answers that come back through shared memory
/// (QUERYPOS, SETPOS, GETTASKBARPOS) were lost on the way: the Dock's strip believed it sat where it had asked to while
/// Windows had put it above the taskbar (2026-10-12). Built like SHAppBarMessage does it: WM_COPYDATA carrying the
/// 32/64-bit neutral APPBARMSGDATA3264, the APPBARDATA3264 in shared memory for the messages that answer with a
/// rectangle. Falls back to SHAppBarMessage when Explorer's taskbar is not there.
/// </summary>
public static class AppBarMessenger
{
    [StructLayout(LayoutKind.Sequential)]
    struct APPBARDATA3264
    {
        public int cbSize;
        public int hWnd;
        public uint uCallbackMessage, uEdge;
        public RECT rc;
        public long lParam;
    }

    /// <summary>64 bytes: what Windows 10/11 send and check (the same layout as ManagedShell's APPBARMSGDATAV3).</summary>
    [StructLayout(LayoutKind.Sequential)]
    struct APPBARMSGDATA3264
    {
        public APPBARDATA3264 abd;
        public uint dwMessage;
        public uint dwPadding1;
        public long hSharedMemory;
        public uint dwSourceProcessId, dwPadding3;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct COPYDATASTRUCT { public IntPtr dwData; public int cbData; public IntPtr lpData; }

    [DllImport("shlwapi.dll", EntryPoint = "#7")] static extern IntPtr SHAllocShared(IntPtr data, uint size, uint processId);
    [DllImport("shlwapi.dll", EntryPoint = "#8")] static extern IntPtr SHLockShared(IntPtr shared, uint processId);
    [DllImport("shlwapi.dll", EntryPoint = "#9")] static extern bool SHUnlockShared(IntPtr view);
    [DllImport("shlwapi.dll", EntryPoint = "#10")] static extern bool SHFreeShared(IntPtr shared, uint processId);
    [DllImport("user32.dll")] static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, ref COPYDATASTRUCT l, uint flags, uint timeout, out IntPtr result);

    const uint WM_COPYDATA = 0x004A;

    public static IntPtr Send(uint message, ref APPBARDATA data) => Send(message, ref data, out _);

    /// <param name="answered">False when Explorer's taskbar was not there or did not answer in time: the 0 returned is no answer.</param>
    public static IntPtr Send(uint message, ref APPBARDATA data, out bool answered)
    {
        answered = false;
        IntPtr tray = Taskbar.Primary();
        if (tray == IntPtr.Zero) return Native.SHAppBarMessage(message, ref data);

        uint me = (uint)Environment.ProcessId;
        // The shared memory is made for the taskbar's process, which opens it as its own; we read the answer back
        // from there. (Through our tray window SHAppBarMessage made it for *our* process: Explorer could not open
        // it, so the answers were lost.)
        uint explorer = Native.ProcessId(tray);
        var abd = new APPBARDATA3264
        {
            cbSize = Marshal.SizeOf<APPBARDATA3264>(), hWnd = (int)data.hWnd.ToInt64(),
            uCallbackMessage = data.uCallbackMessage, uEdge = data.uEdge, rc = data.rc, lParam = data.lParam.ToInt64(),
        };
        var cmd = new APPBARMSGDATA3264 { abd = abd, dwMessage = message, dwSourceProcessId = me };
        bool answers = message is Native.ABM_QUERYPOS or Native.ABM_SETPOS or Native.ABM_GETTASKBARPOS;
        IntPtr shared = IntPtr.Zero;
        IntPtr abdBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<APPBARDATA3264>());
        IntPtr cmdBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<APPBARMSGDATA3264>());
        try
        {
            if (answers)
            {
                Marshal.StructureToPtr(abd, abdBuffer, false);
                shared = SHAllocShared(abdBuffer, (uint)Marshal.SizeOf<APPBARDATA3264>(), explorer);
                cmd.hSharedMemory = shared.ToInt64();
            }
            Marshal.StructureToPtr(cmd, cmdBuffer, false);
            var cds = new COPYDATASTRUCT { dwData = IntPtr.Zero, cbData = Marshal.SizeOf<APPBARMSGDATA3264>(), lpData = cmdBuffer };
            if (SendMessageTimeout(tray, WM_COPYDATA, data.hWnd, ref cds, Native.SMTO_ABORTIFHUNG, 5000, out IntPtr result) == IntPtr.Zero)
            {
                Log.Warn($"app bar message {message} to Explorer timed out");
                return IntPtr.Zero;
            }
            answered = true;
            if (shared != IntPtr.Zero)
            {
                IntPtr view = SHLockShared(shared, explorer);
                if (view != IntPtr.Zero)
                {
                    try
                    {
                        var answer = Marshal.PtrToStructure<APPBARDATA3264>(view);
                        data.rc = answer.rc;
                        // GETTASKBARPOS answers with the taskbar's edge too (it was dropped: nothing asked for it before 2026-10-15).
                        if (message == Native.ABM_GETTASKBARPOS) data.uEdge = answer.uEdge;
                    }
                    finally { SHUnlockShared(view); }
                }
            }
            return result;
        }
        finally
        {
            if (shared != IntPtr.Zero) SHFreeShared(shared, explorer);
            Marshal.FreeHGlobal(abdBuffer);
            Marshal.FreeHGlobal(cmdBuffer);
        }
    }
}
