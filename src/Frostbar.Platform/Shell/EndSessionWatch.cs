using System.Runtime.InteropServices;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Shell;

/// <summary>
/// The flags of the WM_QUERYENDSESSION the UI thread is answering (Core's EndSession). WPF raises SessionEnding from a
/// window of its own and keeps only "log off or shut down" of them, so Restart Manager's "close this program" (an
/// installer, another program's update; the session goes on) looked like the user signing out (2026-10-16). A hook on
/// this thread's own windows (WH_CALLWNDPROC: no DLL, no other process) sees each sent message before its window does.
/// Made and disposed on the UI thread.
/// </summary>
public sealed class EndSessionWatch : IDisposable
{
    readonly Native.HookProc _proc; // the hook calls it: kept alive with this object
    IntPtr _hook;
    long _queryAt = long.MinValue;
    uint _query;

    public EndSessionWatch()
    {
        _proc = OnSent;
        _hook = Native.SetWindowsHookEx(Native.WH_CALLWNDPROC, _proc, IntPtr.Zero, Native.GetCurrentThreadId());
        if (_hook == IntPtr.Zero) Log.Warn($"end of session: no hook on the UI thread ({Marshal.GetLastWin32Error()}): Windows' shutting-down state decides alone");
    }

    /// <summary>The flags of the WM_QUERYENDSESSION sent to a window of this thread in the last 10 s, or null.</summary>
    // None seen: null (now minus long.MinValue overflowed to below 10 s, and the log said 0x00000000 for "not seen").
    public uint? QueryFlags => _queryAt != long.MinValue && Environment.TickCount64 - _queryAt < 10_000 ? _query : null;

    IntPtr OnSent(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && lParam != IntPtr.Zero)
        {
            try
            {
                // CWPSTRUCT: lParam, wParam, message, hwnd.
                if ((uint)Marshal.ReadInt32(lParam, 2 * IntPtr.Size) == Native.WM_QUERYENDSESSION)
                {
                    _query = unchecked((uint)Marshal.ReadIntPtr(lParam).ToInt64());
                    _queryAt = Environment.TickCount64;
                }
            }
            catch (Exception) { } // nothing may leave a hook
        }
        return Native.CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }
}
