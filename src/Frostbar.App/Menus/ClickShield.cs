using Frostbar.Platform.Interop;
using Frostbar.Platform.Shell;

namespace Frostbar.App.Menus;

/// <summary>
/// While a menu is open, invisible (alpha 1) windows over the desktop, just below the menu bar and the menus: a click
/// outside the menu lands here and closes it, and is not passed to the app below (like macOS and Win32 menus).
/// They never activate, so the app in front keeps focus (ADR-004).
/// One per monitor, and none covers its whole monitor: Explorer takes a topmost window covering a monitor for a
/// fullscreen app (2026-10-12). Under the bar the bar itself takes the clicks; elsewhere the bottom pixel row is left out.
/// </summary>
public sealed class ClickShields
{
    readonly List<Shield> _shields = new();
    string _layout = "";
    public event Action? Clicked;

    public IEnumerable<IntPtr> Handles => _shields.Select(s => s.Handle);

    public void Show(RECT bar)
    {
        var monitors = Monitors.All();
        string layout = string.Join(";", monitors.Select(m => m.Bounds.ToString())) + "|" + bar;
        if (layout != _layout)
        {
            foreach (var s in _shields) s.Close();
            _shields.Clear();
            foreach (var m in monitors)
            {
                var r = m.Bounds;
                if (r.Contains(new RECT(bar.Left, bar.Top, bar.Right, bar.Top + 1))) r.Top = Math.Max(r.Top, bar.Bottom);
                else r.Bottom -= 1;
                var s = new Shield(r);
                s.Clicked += () => Clicked?.Invoke();
                _shields.Add(s);
            }
            _layout = layout;
        }
        foreach (var s in _shields) s.ShowAtRect();
    }

    public void Hide()
    {
        foreach (var s in _shields) s.Hide();
    }

    /// <summary>
    /// Closed for good (their owner goes: a bar or a Dock made again). Only hidden, each kept a layered window the size
    /// of its monitor, and through its click the whole owner that was let go.
    /// </summary>
    public void Dispose()
    {
        foreach (var s in _shields) s.Close();
        _shields.Clear();
        _layout = "";
        Clicked = null;
    }

    /// <summary>
    /// A plain Win32 window with no redirection bitmap: it draws nothing and takes every press over its rectangle. It was
    /// a WPF window with a nearly clear background (AllowsTransparency), which kept a layered bitmap the size of its
    /// monitor for the life of its owner, shown or hidden (1920×1080×4 bytes, about 8 MB by count, one per monitor and
    /// per set: the menu bar's, the Dock's). Its 1/255 black over the desktop was not to be seen.
    /// </summary>
    sealed class Shield
    {
        const string ClassName = "Frostbar.ClickShield";
        const uint WM_LBUTTONDOWN = 0x201, WM_RBUTTONDOWN = 0x204, WM_MBUTTONDOWN = 0x207, WM_XBUTTONDOWN = 0x20B;
        static Native.WndProcDelegate? _proc;
        static readonly Dictionary<IntPtr, Shield> Live = new();
        readonly RECT _px;
        public IntPtr Handle { get; }
        public event Action? Clicked;

        public Shield(RECT px)
        {
            _px = px;
            if (_proc == null)
            {
                _proc = Proc;
                var wc = new Native.WNDCLASSEX
                {
                    cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.WNDCLASSEX>(),
                    lpfnWndProc = System.Runtime.InteropServices.Marshal.GetFunctionPointerForDelegate(_proc),
                    hInstance = Native.GetModuleHandle(null),
                    hCursor = Native.LoadCursor(IntPtr.Zero, Native.IDC_ARROW),
                    lpszClassName = ClassName,
                };
                if (Native.RegisterClassEx(ref wc) == 0)
                    throw new InvalidOperationException("RegisterClassEx failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            }
            long ex = Native.WS_EX_NOREDIRECTIONBITMAP | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST;
            // Placed in pixels when shown (monitors can have different scales).
            Handle = Native.CreateWindowEx((int)ex, ClassName, "Frostbar.ClickShield", Native.WS_POPUP, -32000, -32000, 1, 1,
                IntPtr.Zero, IntPtr.Zero, Native.GetModuleHandle(null), IntPtr.Zero);
            if (Handle == IntPtr.Zero)
                throw new InvalidOperationException("CreateWindowEx failed: " + System.Runtime.InteropServices.Marshal.GetLastWin32Error());
            Live[Handle] = this;
        }

        static IntPtr Proc(IntPtr h, uint msg, IntPtr w, IntPtr l)
        {
            switch (msg)
            {
                case Native.WM_MOUSEACTIVATE: return new IntPtr(Native.MA_NOACTIVATE);
                case WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN or WM_XBUTTONDOWN:
                    if (Live.TryGetValue(h, out var s)) s.Clicked?.Invoke();
                    return msg == WM_XBUTTONDOWN ? new IntPtr(1) : IntPtr.Zero;
            }
            return Native.DefWindowProc(h, msg, w, l);
        }

        public void ShowAtRect() =>
            Native.SetWindowPos(Handle, Native.HWND_TOPMOST, _px.Left, _px.Top, _px.Width, _px.Height, Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

        public void Hide() => Native.ShowWindow(Handle, Native.SW_HIDE);

        public void Close()
        {
            Live.Remove(Handle);
            Clicked = null;
            Native.DestroyWindow(Handle);
        }
    }
}
