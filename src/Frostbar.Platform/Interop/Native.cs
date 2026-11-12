using System.Runtime.InteropServices;
using System.Text;

namespace Frostbar.Platform.Interop;

[StructLayout(LayoutKind.Sequential)]
public struct RECT
{
    public int Left, Top, Right, Bottom;
    public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
    public readonly int Width => Right - Left;
    public readonly int Height => Bottom - Top;
    public readonly bool Contains(RECT o) => o.Left >= Left && o.Top >= Top && o.Right <= Right && o.Bottom <= Bottom;
    public override readonly string ToString() => $"({Left},{Top})-({Right},{Bottom})";
}

[StructLayout(LayoutKind.Sequential)]
public struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
public struct MARGINS { public int Left, Right, Top, Bottom; }

[StructLayout(LayoutKind.Sequential)]
public struct DWM_THUMBNAIL_PROPERTIES
{
    public uint dwFlags;
    public RECT rcDestination;
    public RECT rcSource;
    public byte opacity;
    [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
    [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
}

[StructLayout(LayoutKind.Sequential)]
public struct APPBARDATA
{
    public int cbSize;
    public IntPtr hWnd;
    public uint uCallbackMessage;
    public uint uEdge;
    public RECT rc;
    public IntPtr lParam;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct MONITORINFOEX
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct STARTUPINFO
{
    public int cb;
    public string? lpReserved, lpDesktop, lpTitle;
    public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
    public short wShowWindow, cbReserved2;
    public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
}

[StructLayout(LayoutKind.Sequential)]
public struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

[StructLayout(LayoutKind.Sequential)]
public struct LASTINPUTINFO { public int cbSize; public uint dwTime; }

[StructLayout(LayoutKind.Sequential)]
public struct INPUT { public uint type; public INPUTUNION u; }

[StructLayout(LayoutKind.Explicit)]
public struct INPUTUNION
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

[StructLayout(LayoutKind.Sequential)]
public struct SYSTEM_POWER_STATUS { public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag; public int BatteryLifeTime, BatteryFullLifeTime; }

[StructLayout(LayoutKind.Sequential)]
public struct LUID { public uint Low; public int High; }

[StructLayout(LayoutKind.Sequential)]
public struct TOKEN_PRIVILEGES { public int PrivilegeCount; public LUID Luid; public int Attributes; }

[StructLayout(LayoutKind.Sequential)]
public struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth, biHeight;
    public ushort biPlanes, biBitCount;
    public uint biCompression, biSizeImage;
    public int biXPelsPerMeter, biYPelsPerMeter;
    public uint biClrUsed, biClrImportant;
}

[StructLayout(LayoutKind.Sequential)]
public struct BITMAPINFO { public BITMAPINFOHEADER bmiHeader; public uint bmiColors; }

/// <summary>Win32 declarations shared by the platform services (kept by hand: verified on 26H2 during M0).</summary>
public static class Native
{
    public const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
    public const long WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_APPWINDOW = 0x40000,
        WS_EX_LAYERED = 0x80000, WS_EX_NOREDIRECTIONBITMAP = 0x200000, WS_EX_NOACTIVATE = 0x8000000;
    public const int WS_POPUP = unchecked((int)0x80000000);
    public const long WS_SYSMENU = 0x80000;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, SWP_FRAMECHANGED = 0x20,
        SWP_SHOWWINDOW = 0x40, SWP_HIDEWINDOW = 0x80, SWP_NOOWNERZORDER = 0x200, SWP_ASYNCWINDOWPOS = 0x4000;
    public static readonly IntPtr HWND_TOPMOST = new(-1), HWND_NOTOPMOST = new(-2), HWND_TOP = IntPtr.Zero, HWND_BOTTOM = new(1);
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_SHOWNA = 8;
    public const uint WM_DESTROY = 0x2, WM_CLOSE = 0x10, WM_NULL = 0, WM_SETTINGCHANGE = 0x1A, WM_DISPLAYCHANGE = 0x7E,
        WM_NCACTIVATE = 0x86, WM_MOUSEACTIVATE = 0x21, WM_DPICHANGED = 0x2E0, WM_POWERBROADCAST = 0x218, WM_HOTKEY = 0x312,
        WM_WTSSESSION_CHANGE = 0x2B1, WM_ENDSESSION = 0x16, WM_QUERYENDSESSION = 0x11, WM_SYSCOMMAND = 0x112;
    public const int SC_RESTORE = 0xF120, SC_MINIMIZE = 0xF020, SC_CLOSE = 0xF060;
    public const int MA_NOACTIVATE = 3;
    public const uint SMTO_ABORTIFHUNG = 0x2;
    public const uint ABM_NEW = 0, ABM_REMOVE = 1, ABM_QUERYPOS = 2, ABM_SETPOS = 3, ABM_GETSTATE = 4, ABM_GETTASKBARPOS = 5, ABM_SETSTATE = 10;
    public const uint ABE_LEFT = 0, ABE_TOP = 1, ABE_RIGHT = 2, ABE_BOTTOM = 3;
    public const int ABN_STATECHANGE = 0, ABN_POSCHANGED = 1, ABN_FULLSCREENAPP = 2, ABN_WINDOWARRANGE = 3;
    public const int ABS_AUTOHIDE = 1;
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9, DWMWA_CLOAKED = 14, DWMWA_USE_HOSTBACKDROPBRUSH = 17,
        DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34;
    public const int DWMWCP_DONOTROUND = 1;
    public const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    public const uint EVENT_SYSTEM_FOREGROUND = 0x3, EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002,
        EVENT_OBJECT_HIDE = 0x8003, EVENT_OBJECT_LOCATIONCHANGE = 0x800B;
    public const uint WINEVENT_OUTOFCONTEXT = 0;
    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000, SYNCHRONIZE = 0x00100000;
    public const uint CREATE_BREAKAWAY_FROM_JOB = 0x01000000, CREATE_NEW_PROCESS_GROUP = 0x200, CREATE_NO_WINDOW = 0x08000000;
    public const uint MONITOR_DEFAULTTOPRIMARY = 1, MONITOR_DEFAULTTONEAREST = 2;

    // user32: windows
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindow(string? cls, string? title);
    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] public static extern int GetObject(IntPtr h, int size, out BITMAP bm);
    [DllImport("gdi32.dll")] public static extern int GetDIBits(IntPtr dc, IntPtr hbm, uint start, uint lines, [Out] byte[] bits, ref BITMAPINFO bmi, uint usage);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr dc);
    [DllImport("gdi32.dll")] public static extern int GetDeviceCaps(IntPtr dc, int index);

    /// <summary>The primary display's refresh rate in Hz (60 when it cannot be read).</summary>
    public static int RefreshHz()
    {
        var dc = GetDC(IntPtr.Zero);
        try { int hz = GetDeviceCaps(dc, 116 /*VREFRESH*/); return hz > 1 ? hz : 60; }
        finally { ReleaseDC(IntPtr.Zero, dc); }
    }
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr h);
    [DllImport("user32.dll")] public static extern bool RegisterShellHookWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool DeregisterShellHookWindow(IntPtr hwnd);
    public delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
    public const uint GW_HWNDPREV = 3;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] public static extern IntPtr SetWindowLongPtr(IntPtr h, int idx, IntPtr v);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr h);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    /// <summary>When the message being handled was posted (TickCount milliseconds).</summary>
    [DllImport("user32.dll")] public static extern int GetMessageTime();
    [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);
    /// <summary>The system owns <paramref name="rgn"/> once this succeeds (do not delete it); null takes the region away.</summary>
    [DllImport("user32.dll")] public static extern int SetWindowRgn(IntPtr h, IntPtr rgn, bool redraw);
    /// <summary>0 no region (or an error), 1 an empty region, 2 a rectangle, 3 more (copied into <paramref name="rgn"/>).</summary>
    [DllImport("user32.dll")] public static extern int GetWindowRgn(IntPtr h, IntPtr rgn);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] public static extern bool SetLayeredWindowAttributes(IntPtr h, uint key, byte alpha, uint flags);
    // Left out of screen capture (S31: Windows.Graphics.Capture and BitBlt alike show what is behind instead).
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll")] public static extern bool GetWindowDisplayAffinity(IntPtr h, out uint affinity);
    public const uint WDA_NONE = 0, WDA_EXCLUDEFROMCAPTURE = 0x11;
    [DllImport("user32.dll")] public static extern bool GetLayeredWindowAttributes(IntPtr h, out uint key, out byte alpha, out uint flags);
    [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern uint RegisterWindowMessage(string s);
    [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
    static extern IntPtr SendMessageTimeoutText(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);

    /// <summary>
    /// Tells every top-level window that a setting changed (WM_SETTINGCHANGE with the area's name), skipping hung
    /// ones. Can take a while with many windows: call it off the UI thread.
    /// </summary>
    public static void BroadcastSettingChange(string area) =>
        SendMessageTimeoutText(new IntPtr(0xFFFF) /*HWND_BROADCAST*/, WM_SETTINGCHANGE, IntPtr.Zero, area, 0x2 /*SMTO_ABORTIFHUNG*/, 1000, out _);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int idx);
    [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, ref RECT r, uint winIni);
    // The pointer's and the keys' settings (stage 4-2, S20 on 26300): a number out, three numbers in and out, a number
    // in the uiParam or as the pointer (SPI_SETMOUSESPEED), and the touchpad's parameters (Windows 11 24H2+).
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, int[] values, uint winIni);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool SystemParametersInfo(uint action, uint param, ref TOUCHPAD_PARAMETERS p, uint winIni);
    public const uint SPI_GETMOUSE = 0x0003, SPI_SETMOUSE = 0x0004, SPI_GETKEYBOARDSPEED = 0x000A, SPI_SETKEYBOARDSPEED = 0x000B,
        SPI_GETKEYBOARDDELAY = 0x0016, SPI_SETKEYBOARDDELAY = 0x0017, SPI_SETDOUBLECLICKTIME = 0x0020, SPI_SETMOUSEBUTTONSWAP = 0x0021,
        SPI_GETWHEELSCROLLLINES = 0x0068, SPI_SETWHEELSCROLLLINES = 0x0069, SPI_GETMOUSESPEED = 0x0070, SPI_SETMOUSESPEED = 0x0071,
        SPI_GETTOUCHPADPARAMETERS = 0x00AE, SPI_SETTOUCHPADPARAMETERS = 0x00AF, SPIF_UPDATEINIFILE = 1, SPIF_SENDCHANGE = 2;
    public const int SM_SWAPBUTTON = 23;
    /// <summary>Nonzero while the session shuts down (signing out, shutting down, restarting).</summary>
    public const int SM_SHUTTINGDOWN = 0x2000;
    public static bool ShuttingDown() => GetSystemMetrics(SM_SHUTTINGDOWN) != 0;

    // A hook on this thread's own windows (no DLL, no other process): WH_CALLWNDPROC sees each sent message before its
    // window does (EndSessionWatch).
    public const int WH_CALLWNDPROC = 4;
    public delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookEx(int idHook, HookProc proc, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    /// <summary>winuser.h's TOUCHPAD_PARAMETERS, version 1 (48 bytes): the two bit-field words are Frostbar.Core's TouchpadBits.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct TOUCHPAD_PARAMETERS
    {
        public uint VersionNumber, MaxSupportedContacts;
        public int LegacyTouchpadFeatures;
        public uint Present, Settings;
        public int SensitivityLevel;
        public uint CursorSpeed, FeedbackIntensity, ClickForce, ClickForceSensitivity, RightClickZoneWidth, RightClickZoneHeight;
    }
    [DllImport("user32.dll")] public static extern bool GetLastInputInfo(ref LASTINPUTINFO lii);
    [DllImport("user32.dll")] public static extern bool AllowSetForegroundWindow(int pid);

    // user32: our own raw windows
    public delegate IntPtr WndProcDelegate(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct WNDCLASSEX
    {
        public int cbSize;
        public int style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public IntPtr hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern ushort RegisterClassEx(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(int exStyle, string cls, string? name, int style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProc(IntPtr h, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern bool DestroyWindow(IntPtr h);
    [DllImport("user32.dll", EntryPoint = "LoadCursorW")] public static extern IntPtr LoadCursor(IntPtr inst, IntPtr name);
    public static readonly IntPtr IDC_ARROW = new(32512);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string? name);

    // user32: monitors
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr MonitorFromRect(ref RECT r, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool GetMonitorInfo(IntPtr mon, ref MONITORINFOEX mi);
    public delegate bool MonitorEnumProc(IntPtr mon, IntPtr hdc, ref RECT r, IntPtr data);
    [DllImport("user32.dll")] public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
    [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr mon, int type, out uint dpiX, out uint dpiY);

    // user32: events
    public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
    [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);

    // user32: foreground, input, hotkeys
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
    public const uint GA_ROOT = 2, GA_ROOTOWNER = 3;
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindowAsync(IntPtr h, int cmd);

    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public POINT ptScreenPos; }
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] static extern bool GetClipCursor(out RECT r);

    /// <summary>
    /// The pointer is drawn (a game playing hides it) and free over the whole of <paramref name="screen"/> (a game can
    /// also hold it inside its window).
    /// </summary>
    public static bool PointerFree(RECT screen)
    {
        var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        // Hidden either way games do it: ShowCursor(false) (not showing), or SetCursor(NULL) (showing, no shape).
        if (GetCursorInfo(ref ci) && ((ci.flags & 1 /*CURSOR_SHOWING*/) == 0 || ci.hCursor == IntPtr.Zero)) return false;
        return !GetClipCursor(out var clip) || clip.Left <= screen.Left && clip.Top <= screen.Top && clip.Right >= screen.Right && clip.Bottom >= screen.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [DllImport("user32.dll")]
    static extern bool UpdateLayeredWindow(IntPtr h, IntPtr hdcDst, IntPtr pptDst, IntPtr psize, IntPtr hdcSrc, IntPtr pptSrc, int crKey, ref BLENDFUNCTION blend, int flags);

    /// <summary>
    /// A per-pixel-alpha layered window's overall opacity (0–1), without new pixels: DWM blends what was drawn last
    /// (UpdateLayeredWindow with no source DC). The window's own next drawing sets its opacity again.
    /// </summary>
    public static bool SetLayeredOpacity(IntPtr h, double opacity)
    {
        var blend = new BLENDFUNCTION { BlendOp = 0 /*AC_SRC_OVER*/, SourceConstantAlpha = (byte)Math.Round(255 * Math.Clamp(opacity, 0, 1)), AlphaFormat = 1 /*AC_SRC_ALPHA*/ };
        return UpdateLayeredWindow(h, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref blend, 2 /*ULW_ALPHA*/);
    }
    public const int SW_SHOWNORMAL = 1, SW_SHOWMINIMIZED = 2, SW_MAXIMIZE = 3, SW_MINIMIZE = 6, SW_RESTORE = 9;
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
    [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
    [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr h, bool altTab);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] public static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr hkl);
    [DllImport("user32.dll")] public static extern IntPtr GetKeyboardLayout(uint thread);
    [DllImport("user32.dll")] public static extern short GetAsyncKeyState(int vk);
    public static bool KeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    // DWM thumbnails (M0 S11, M7's window overview)
    public const uint DWM_TNP_RECTDESTINATION = 0x1, DWM_TNP_RECTSOURCE = 0x2, DWM_TNP_OPACITY = 0x4, DWM_TNP_VISIBLE = 0x8, DWM_TNP_SOURCECLIENTAREAONLY = 0x10;
    [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr destination, IntPtr source, out IntPtr thumbnail);
    [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr thumbnail);
    [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr thumbnail, ref DWM_THUMBNAIL_PROPERTIES p);
    [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(IntPtr thumbnail, out SIZE size);
    [DllImport("dwmapi.dll")] public static extern int DwmFlush();
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetKeyNameText(int lParam, StringBuilder sb, int size);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);
    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("user32.dll")] public static extern bool ExitWindowsEx(uint flags, uint reason);
    [DllImport("user32.dll")] public static extern uint GetDoubleClickTime();
    [DllImport("imm32.dll")] public static extern IntPtr ImmGetDefaultIMEWnd(IntPtr h);
    public const uint WM_IME_CONTROL = 0x283;
    public const int IMC_GETCONVERSIONMODE = 1, IMC_SETCONVERSIONMODE = 2, IMC_GETOPENSTATUS = 5, IMC_SETOPENSTATUS = 6;
    [DllImport("user32.dll")] public static extern int GetKeyboardLayoutList(int n, IntPtr[]? list);
    public const uint WM_INPUTLANGCHANGEREQUEST = 0x50;

    // advapi32 / powrprof
    /// <summary>A key's change signalled on an event (asynchronous). With REG_NOTIFY_THREAD_AGNOSTIC the request outlives the thread that made it.</summary>
    [DllImport("advapi32.dll")] public static extern int RegNotifyChangeKeyValue(Microsoft.Win32.SafeHandles.SafeRegistryHandle key, bool subtree, uint filter, Microsoft.Win32.SafeHandles.SafeWaitHandle evt, bool asynchronous);
    public const uint REG_NOTIFY_CHANGE_NAME = 0x1, REG_NOTIFY_CHANGE_LAST_SET = 0x4, REG_NOTIFY_THREAD_AGNOSTIC = 0x10000000;
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr info, int len, out int needed);
    [DllImport("advapi32.dll")] public static extern IntPtr GetSidSubAuthority(IntPtr sid, uint n);
    [DllImport("advapi32.dll")] public static extern IntPtr GetSidSubAuthorityCount(IntPtr sid);
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] public static extern bool LookupPrivilegeValue(string? system, string name, out LUID luid);
    [DllImport("advapi32.dll", SetLastError = true)] public static extern bool AdjustTokenPrivileges(IntPtr token, bool disableAll, ref TOKEN_PRIVILEGES p, int len, IntPtr prev, IntPtr retLen);
    [DllImport("powrprof.dll")] public static extern bool SetSuspendState(bool hibernate, bool force, bool disableWakeEvent);
    [DllImport("kernel32.dll")] public static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS s);
    [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();

    // kernel32 / processes
    [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder sb, ref int size);
    [DllImport("kernel32.dll")] public static extern bool GetProcessTimes(IntPtr h, out long creation, out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
    [DllImport("kernel32.dll")] public static extern bool GetExitCodeProcess(IntPtr h, out uint code);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CreateProcess(string? app, StringBuilder cmd, IntPtr procAttr, IntPtr threadAttr, bool inherit, uint flags,
        IntPtr env, string? cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern int SetCurrentProcessExplicitAppUserModelID(string id);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle h, StringBuilder sb, uint size, uint flags);

    // shell32 / dwmapi
    [DllImport("shell32.dll")] public static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);
    [DllImport("ole32.dll")] public static extern void CoTaskMemFree(IntPtr p);
    [DllImport("shell32.dll")] public static extern IntPtr SHAppBarMessage(uint msg, ref APPBARDATA data);
    [DllImport("shell32.dll")] public static extern int SHQueryUserNotificationState(out int state);
    [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll")] public static extern int DwmExtendFrameIntoClientArea(IntPtr h, ref MARGINS m);

    // helpers
    public static string ClassName(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, sb.Capacity); return sb.ToString(); }
    public static string Title(IntPtr h)
    {
        int n = GetWindowTextLength(h);
        if (n <= 0) return "";
        var sb = new StringBuilder(n + 1);
        GetWindowText(h, sb, sb.Capacity);
        return sb.ToString();
    }

    public static string? KnownFolderPath(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) return null;
        try { return Marshal.PtrToStringUni(p); } finally { CoTaskMemFree(p); }
    }

    /// <summary>Mandatory integrity level RID of a process (0x2000 medium, 0x3000 high), or -1 when it cannot be read.</summary>
    public static int IntegrityLevel(IntPtr process)
    {
        if (!OpenProcessToken(process, 0x0008 /*TOKEN_QUERY*/, out var token)) return -1;
        try
        {
            GetTokenInformation(token, 25 /*TokenIntegrityLevel*/, IntPtr.Zero, 0, out int needed);
            if (needed <= 0) return -1;
            IntPtr buf = Marshal.AllocHGlobal(needed);
            try
            {
                if (!GetTokenInformation(token, 25, buf, needed, out _)) return -1;
                IntPtr sid = Marshal.ReadIntPtr(buf); // TOKEN_MANDATORY_LABEL.Label.Sid
                int count = Marshal.ReadByte(GetSidSubAuthorityCount(sid));
                return Marshal.ReadInt32(GetSidSubAuthority(sid, (uint)(count - 1)));
            }
            finally { Marshal.FreeHGlobal(buf); }
        }
        finally { CloseHandle(token); }
    }

    /// <summary>True when the process runs at a higher integrity level than us (or we may not even look): our input cannot reach it.</summary>
    public static bool IsMoreElevatedThanUs(uint pid)
    {
        IntPtr p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (p == IntPtr.Zero) return true;
        try
        {
            int theirs = IntegrityLevel(p);
            if (theirs < 0) return true;
            return theirs > IntegrityLevel(GetCurrentProcess());
        }
        finally { CloseHandle(p); }
    }
    public static long ExStyle(IntPtr h) => GetWindowLongPtr(h, GWL_EXSTYLE).ToInt64();
    public static void SetExStyle(IntPtr h, long ex) => SetWindowLongPtr(h, GWL_EXSTYLE, new IntPtr(ex));
    public static void AddExStyle(IntPtr h, long add) => SetExStyle(h, ExStyle(h) | add);
    public static void SetDwm(IntPtr h, int attr, int value) => DwmSetWindowAttribute(h, attr, ref value, 4);
    public static bool Cloaked(IntPtr h) => DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int c, 4) == 0 && c != 0;
    /// <summary>DWMWA_CLOAKED's flags (1 by the app, 2 by the shell: another virtual desktop, 4 inherited), 0 when not cloaked.</summary>
    public static int CloakedFlags(IntPtr h) => DwmGetWindowAttribute(h, DWMWA_CLOAKED, out int c, 4) == 0 ? c : 0;
    public static RECT FrameBounds(IntPtr h)
    {
        if (DwmGetWindowAttribute(h, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) == 0) return r;
        GetWindowRect(h, out r);
        return r;
    }
    public static uint ProcessId(IntPtr h) { GetWindowThreadProcessId(h, out uint pid); return pid; }

    /// <summary>
    /// The process of the app a window shows. A packaged app's window (Settings, Calculator, Store) belongs to
    /// ApplicationFrameHost.exe, which all of them share; the app's own process owns the CoreWindow inside the frame
    /// (2026-10-12: Force Quit killed the shared host, closing every such app at once). 0 for a frame with no app inside
    /// (suspended or minimized): the host is never the answer.
    /// </summary>
    public static uint AppProcessId(IntPtr h)
    {
        uint pid = ProcessId(h);
        if (ClassName(h) != "ApplicationFrameWindow") return pid;
        uint app = 0;
        EnumChildWindows(h, (c, _) =>
        {
            if (ClassName(c) != "Windows.UI.Core.CoreWindow") return true;
            uint p = ProcessId(c);
            if (p == pid) return true;
            app = p;
            return false;
        }, IntPtr.Zero);
        return app;
    }

    /// <summary><see cref="AppProcessId"/>, or the window's own process for a frame with no app inside (its exe, its name).</summary>
    public static uint AppOrWindowProcessId(IntPtr h) => AppProcessId(h) is var app && app != 0 ? app : ProcessId(h);

    public static RECT WorkArea()
    {
        var r = new RECT();
        SystemParametersInfo(0x30 /*SPI_GETWORKAREA*/, 0, ref r, 0);
        return r;
    }

    public static string? ProcessPath(uint pid)
    {
        IntPtr p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (p == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024); int size = sb.Capacity;
            return QueryFullProcessImageName(p, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(p); }
    }

    /// <summary>Process start time (UTC ticks), or 0 when the process is gone or not ours to open.</summary>
    public static long ProcessStartTicks(int pid)
    {
        IntPtr p = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (p == IntPtr.Zero) return 0;
        try { return GetProcessTimes(p, out long c, out _, out _, out _) ? DateTime.FromFileTimeUtc(c).Ticks : 0; }
        finally { CloseHandle(p); }
    }

    /// <summary>
    /// True when the process with this id is still running and is the one that started at <paramref name="startTicks"/>.
    /// A killed process stays openable (with its start time) while anyone holds a handle to it, so "running" is
    /// decided by waiting on it, not by OpenProcess succeeding (kill-both E2E, 2026-10-12).
    /// </summary>
    public static bool ProcessAlive(int pid, long startTicks)
    {
        IntPtr p = OpenProcess(SYNCHRONIZE | PROCESS_QUERY_LIMITED_INFORMATION, false, (uint)pid);
        if (p == IntPtr.Zero) return false;
        try
        {
            if (WaitForSingleObject(p, 0) != 0x102 /*WAIT_TIMEOUT: still running*/) return false;
            if (startTicks == 0) return true;
            return GetProcessTimes(p, out long c, out _, out _, out _) && Math.Abs(DateTime.FromFileTimeUtc(c).Ticks - startTicks) < TimeSpan.TicksPerSecond;
        }
        finally { CloseHandle(p); }
    }

    public static uint IdleMs() => (uint)Environment.TickCount - LastInputTick();

    /// <summary>When the last input came (GetTickCount ms). Cheap even on a cold CPU (about 15 µs, against 150 µs for GetCursorPos).</summary>
    public static uint LastInputTick()
    {
        var lii = new LASTINPUTINFO { cbSize = Marshal.SizeOf<LASTINPUTINFO>() };
        GetLastInputInfo(ref lii);
        return lii.dwTime;
    }

    /// <summary>
    /// Starts a process that inherits no handles and, when allowed, leaves our job object, so it survives
    /// "End task" on us and does not hold our stdout pipes.
    /// </summary>
    public static int StartDetached(string exe, string args)
    {
        var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
        foreach (uint flags in new[] { CREATE_BREAKAWAY_FROM_JOB | CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW, CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW })
        {
            var cmd = new StringBuilder($"\"{exe}\" {args}");
            if (CreateProcess(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, flags, IntPtr.Zero, null, ref si, out var pi))
            {
                CloseHandle(pi.hThread);
                CloseHandle(pi.hProcess);
                return pi.dwProcessId;
            }
        }
        return 0;
    }
}
