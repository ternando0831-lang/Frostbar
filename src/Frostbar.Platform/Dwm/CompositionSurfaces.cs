using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Frostbar.Core.Shell;
using Frostbar.Platform.Infrastructure;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using WinRT;

namespace Frostbar.Platform.Dwm;

/// <summary>
/// Bitmaps for Windows.UI.Composition (ADR-007): a D3D11 device handed to the compositor, and pixels copied into its
/// surfaces with UpdateSubresource. Everything runs on the UI thread, the compositor's thread.
/// If the GPU is reset (driver update, TDR) the surfaces lose their pixels: the device signals it (and an upload on it
/// fails), <see cref="CheckDevice"/> makes a new one and raises <see cref="DeviceReplaced"/>, and owners upload again.
/// A GPU that keeps failing gets the software device after a few tries (<see cref="DeviceRecovery"/>).
/// </summary>
public sealed unsafe class SurfaceFactory : IDisposable
{
    [DllImport("d3d11.dll")]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels,
        uint featureLevelCount, uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr context);

    [StructLayout(LayoutKind.Sequential)]
    struct D3D11_BOX { public uint left, top, front, right, bottom, back; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int x, y; }

    const int D3D_DRIVER_TYPE_HARDWARE = 1, D3D_DRIVER_TYPE_WARP = 5;
    const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20, D3D11_SDK_VERSION = 7;
    static readonly Guid IID_ICompositorInterop = new("25297D5C-3AD4-4C9C-B5CF-E36A38512330");
    static readonly Guid IID_ICompositionDrawingSurfaceInterop = new("FD04E6E3-FE0C-4C3C-AB19-A07601A576EE");
    static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(in Guid iid, out IntPtr factory);
    static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    const int D3D_DRIVER_TYPE_UNKNOWN = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAY_DEVICE
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string? device, uint index, ref DISPLAY_DEVICE dd, uint flags);

    static readonly Guid IID_ID3D11Device4 = new("8992ab71-02e6-4b8d-ba48-b056dcda42c4");

    readonly Shell.MonitorInfo _monitor;
    readonly Dispatcher _ui;
    readonly DeviceRecovery _recovery = new();
    IntPtr _device, _context;
    CompositionGraphicsDevice? _graphics;

    /// <summary>
    /// The device, its immediate context and the compositor's graphics device on it, for the lens (ADR-016), which also
    /// uses them from the capture's thread: every use of the context holds <see cref="Gpu"/> (and the device is
    /// multithread-protected besides, for the compositor's own use of it).
    /// </summary>
    internal IntPtr Device => _device;
    internal IntPtr Context => _context;
    internal CompositionGraphicsDevice Graphics => _graphics!;
    internal readonly object Gpu = new();
    public Shell.MonitorInfo Monitor => _monitor;
    AutoResetEvent? _removed;
    RegisteredWaitHandle? _removedWait;
    uint _removedCookie;
    bool _watching, _simulatedLoss, _failNextUpload;
    int _checkQueued;

    public Compositor Compositor { get; } = Composition.Compositor;

    /// <summary>The GPU device was lost and replaced: every surface has to be filled again.</summary>
    public event Action? DeviceReplaced;

    /// <summary>The devices made so far: 1 at first, one more after each loss. Pictures made before the last one are gone.</summary>
    public int Generation { get; private set; }

    /// <summary>On the software device (WARP): the GPU kept failing, or there was no hardware device.</summary>
    public bool Software { get; private set; }

    /// <param name="monitor">The monitor the pictures are shown on: the device is made on the GPU that drives it.</param>
    public SurfaceFactory(Shell.MonitorInfo monitor)
    {
        _monitor = monitor;
        _ui = Dispatcher.CurrentDispatcher;
        CreateDevice(software: false);
    }

    static readonly Dictionary<string, SurfaceFactory> _shared = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The factory for a monitor's display, shared by everything shown there (the Dock, the app grid) for as long as
    /// the process runs: each D3D11 device costs this process 20–35 MB (2026-10-12: 37 MB for the first, 22 MB for a
    /// second). UI thread only.
    /// </summary>
    public static SurfaceFactory Shared(Shell.MonitorInfo monitor)
    {
        if (!_shared.TryGetValue(monitor.Device, out var f)) _shared[monitor.Device] = f = new SurfaceFactory(monitor);
        return f;
    }

    /// <summary>Checks every shared factory's device (after a resume from sleep or a change of the displays). UI thread.</summary>
    public static void CheckAll()
    {
        foreach (var f in _shared.Values.ToList()) f.CheckDevice();
    }

    void CreateDevice(bool software)
    {
        Generation++;
        Software = software;
        if (software)
        {
            Log.Warn("composition: the GPU device was lost again and again: using the software device (WARP) until the shell starts again");
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION, out _device, out _, out _context));
        }
        else CreateHardwareDevice();
        Protect();
        CreateGraphics();
        WatchRemoval();
    }

    static readonly Guid IID_ID3D10Multithread = new("9B7E4E00-342C-4106-A19F-4F2704F689F0");

    /// <summary>
    /// The device's context guards itself (ID3D10Multithread::SetMultithreadProtected, slot 5): the lens copies into
    /// its picture of the screen on the capture's thread (ADR-016), and the compositor may use the device too.
    /// </summary>
    void Protect()
    {
        if (Marshal.QueryInterface(_device, in IID_ID3D10Multithread, out IntPtr mt) < 0) return;
        ((delegate* unmanaged[Stdcall]<IntPtr, int, int>)(*(IntPtr**)mt)[5])(mt, 1);
        Marshal.Release(mt);
    }

    void CreateHardwareDevice()
    {
        // On the GPU that drives the monitor, where DWM composes it. With hybrid graphics, DXGI lists the screen's output
        // under the GPU the app prefers (this laptop: under the RTX 4050, though the Intel UHD drives the screen), and a
        // device on the discrete GPU kept its driver busy while idle (a thread at 0.15% of a core, 2026-10-12) and keeps
        // that GPU powered. The display device's PCI ids name the right one.
        IntPtr adapter = AdapterFor(_monitor);
        int hr;
        try
        {
            if (adapter != IntPtr.Zero)
            {
                // IDXGIAdapter::GetDesc: slot 8; DXGI_ADAPTER_DESC starts with the name (WCHAR[128])
                byte* desc = stackalloc byte[512];
                if (((delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)(*(IntPtr**)adapter)[8])(adapter, desc) >= 0)
                    Log.Info("composition: D3D11 device on " + new string((char*)desc).TrimEnd());
            }
            else Log.Info("composition: no adapter found for the monitor; D3D11 device on the default adapter");
            hr = adapter != IntPtr.Zero
                ? D3D11CreateDevice(adapter, D3D_DRIVER_TYPE_UNKNOWN, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION, out _device, out _, out _context)
                : D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_HARDWARE, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION, out _device, out _, out _context);
        }
        finally { if (adapter != IntPtr.Zero) Marshal.Release(adapter); }
        if (hr < 0)
        {
            Log.Warn($"composition: no hardware D3D11 device (0x{hr:X8}), using WARP");
            Marshal.ThrowExceptionForHR(D3D11CreateDevice(IntPtr.Zero, D3D_DRIVER_TYPE_WARP, IntPtr.Zero, D3D11_CREATE_DEVICE_BGRA_SUPPORT, IntPtr.Zero, 0, D3D11_SDK_VERSION, out _device, out _, out _context));
            Software = true;
        }
    }

    /// <summary>
    /// Asks the device to signal an event when it is lost (ID3D11Device4::RegisterDeviceRemovedEvent, slot 65, and
    /// UnregisterDeviceRemoved, slot 66: checked in Lab `probe gpu`, 2026-10-15), and then checks it on the UI thread.
    /// A thread-pool wait: nothing runs until it fires. Before, only opening the app grid checked the device, and the
    /// Dock uploading a picture to a lost one threw on the UI thread, which ends the shell.
    /// </summary>
    void WatchRemoval()
    {
        if (Marshal.QueryInterface(_device, in IID_ID3D11Device4, out IntPtr d4) < 0)
        {
            Log.Info("composition: no ID3D11Device4: a lost device is noticed when an upload fails");
            return;
        }
        try
        {
            var removed = new AutoResetEvent(false);
            uint cookie;
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)(*(IntPtr**)d4)[65])(d4, removed.SafeWaitHandle.DangerousGetHandle(), &cookie);
            if (hr < 0)
            {
                Log.Warn($"composition: RegisterDeviceRemovedEvent failed (0x{hr:X8}): a lost device is noticed when an upload fails");
                removed.Dispose();
                return;
            }
            _removed = removed;
            _removedCookie = cookie;
            _watching = true;
            // An auto-reset event: the wait re-arms itself after each signal.
            _removedWait = ThreadPool.RegisterWaitForSingleObject(removed, (_, _) => RequestCheck(), null, Timeout.Infinite, executeOnlyOnce: false);
        }
        finally { Marshal.Release(d4); }
    }

    void UnwatchRemoval()
    {
        _removedWait?.Unregister(null);
        _removedWait = null;
        if (_watching && _device != IntPtr.Zero && Marshal.QueryInterface(_device, in IID_ID3D11Device4, out IntPtr d4) >= 0)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, void>)(*(IntPtr**)d4)[66])(d4, _removedCookie);
            Marshal.Release(d4);
        }
        _watching = false;
        _removed?.Dispose();
        _removed = null;
    }

    /// <summary>Checks the device on the UI thread soon. Any thread; requests made before it runs make one check.</summary>
    public void RequestCheck()
    {
        if (Interlocked.Exchange(ref _checkQueued, 1) == 1) return;
        _ui.BeginInvoke(DispatcherPriority.Normal, () =>
        {
            Volatile.Write(ref _checkQueued, 0);
            CheckDevice();
        });
    }

    /// <summary>
    /// Diagnostics (gpu-lost): as if the GPU had gone. Without <paramref name="duringUpload"/> the device's lost event
    /// fires, the path of a real loss (with no event, nothing notices until the next check, as with a real loss); with
    /// it the next upload fails as it does on a lost device.
    /// </summary>
    public void SimulateLoss(bool duringUpload)
    {
        _simulatedLoss = true;
        if (duringUpload) _failNextUpload = true;
        else _removed?.Set();
    }

    int RemovedReason() => _device == IntPtr.Zero ? 0
        : ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(IntPtr**)_device)[39])(_device); // ID3D11Device::GetDeviceRemovedReason

    void CreateGraphics()
    {
        IntPtr compositor = ((IWinRTObject)Compositor).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(compositor, in IID_ICompositorInterop, out IntPtr interop));
        try
        {
            IntPtr raw;
            // ICompositorInterop::CreateGraphicsDevice(IUnknown* renderingDevice, ICompositionGraphicsDevice** result): slot 5
            var fn = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr*, int>)(*(IntPtr**)interop)[5];
            Marshal.ThrowExceptionForHR(fn(interop, _device, &raw));
            _graphics = CompositionGraphicsDevice.FromAbi(raw);
            Marshal.Release(raw);
        }
        finally { Marshal.Release(interop); }
    }

    /// <summary>
    /// The DXGI adapter that drives <paramref name="monitor"/> (AddRef'd), or zero: the one whose PCI vendor and device
    /// ids are those of the display device behind the monitor (EnumDisplayDevices), else the one DXGI lists the
    /// monitor's output under. IDXGIFactory::EnumAdapters is slot 7; IDXGIAdapter::EnumOutputs slot 7, GetDesc slot 8
    /// (DXGI_ADAPTER_DESC: VendorId at byte 256, DeviceId at 260); IDXGIOutput::GetDesc slot 7 (the HMONITOR at byte 88).
    /// </summary>
    static IntPtr AdapterFor(Shell.MonitorInfo monitor)
    {
        if (CreateDXGIFactory1(IID_IDXGIFactory1, out IntPtr factory) < 0) return IntPtr.Zero;
        var (vendor, deviceId) = PciIds(monitor.Device);
        try
        {
            var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)factory)[7];
            byte* adapterDesc = stackalloc byte[304];
            byte* outputDesc = stackalloc byte[96];
            IntPtr byOutput = IntPtr.Zero;
            for (uint a = 0; ; a++)
            {
                IntPtr adapter;
                if (enumAdapters(factory, a, &adapter) < 0) break; // DXGI_ERROR_NOT_FOUND: no more
                var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)(*(IntPtr**)adapter)[8];
                if (vendor != 0 && getDesc(adapter, adapterDesc) >= 0 && *(uint*)(adapterDesc + 256) == vendor && *(uint*)(adapterDesc + 260) == deviceId)
                {
                    if (byOutput != IntPtr.Zero) Marshal.Release(byOutput);
                    return adapter;
                }
                bool hasOutput = false;
                var enumOutputs = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)adapter)[7];
                for (uint o = 0; !hasOutput; o++)
                {
                    IntPtr output;
                    if (enumOutputs(adapter, o, &output) < 0) break;
                    var getOutputDesc = (delegate* unmanaged[Stdcall]<IntPtr, byte*, int>)(*(IntPtr**)output)[7];
                    if (getOutputDesc(output, outputDesc) >= 0 && *(IntPtr*)(outputDesc + 88) == monitor.Handle) hasOutput = true;
                    Marshal.Release(output);
                }
                if (hasOutput && byOutput == IntPtr.Zero) byOutput = adapter;
                else Marshal.Release(adapter);
            }
            return byOutput;
        }
        finally { Marshal.Release(factory); }
    }

    /// <summary>The PCI vendor and device ids of the display adapter behind a GDI display name ("\\.\DISPLAY1"), or zeros.</summary>
    static (uint vendor, uint device) PciIds(string displayName)
    {
        var dd = new DISPLAY_DEVICE { cb = Marshal.SizeOf<DISPLAY_DEVICE>() };
        for (uint i = 0; EnumDisplayDevices(null, i, ref dd, 0); i++, dd.cb = Marshal.SizeOf<DISPLAY_DEVICE>())
        {
            if (!string.Equals(dd.DeviceName, displayName, StringComparison.OrdinalIgnoreCase)) continue;
            // "PCI\VEN_8086&DEV_A788&SUBSYS_…"
            uint Hex(string key)
            {
                int at = dd.DeviceID.IndexOf(key, StringComparison.OrdinalIgnoreCase);
                return at >= 0 && at + key.Length + 4 <= dd.DeviceID.Length
                    && uint.TryParse(dd.DeviceID.AsSpan(at + key.Length, 4), System.Globalization.NumberStyles.HexNumber, null, out uint v) ? v : 0;
            }
            Log.Debug($"composition: {displayName} is driven by {dd.DeviceString}");
            return (Hex("VEN_"), Hex("DEV_"));
        }
        return (0, 0);
    }

    /// <summary>
    /// Checks that the GPU device still works; if not, makes a new one (the software one after
    /// <see cref="DeviceRecovery.Max"/> within a minute) and raises <see cref="DeviceReplaced"/>. UI thread.
    /// </summary>
    public void CheckDevice()
    {
        if (_device == IntPtr.Zero) return;
        int reason = RemovedReason();
        if (reason == 0 && !_simulatedLoss) return;
        if (reason == 0) reason = unchecked((int)0x887A0005); // simulated: DXGI_ERROR_DEVICE_REMOVED
        _simulatedLoss = _failNextUpload = false;
        bool hardware = !Software && _recovery.Hardware(Environment.TickCount64);
        Log.Warn($"composition: the GPU device was removed (0x{reason:X8}); creating a new one");
        UnwatchRemoval();
        ReleaseDevice();
        CreateDevice(software: !hardware);
        DeviceReplaced?.Invoke();
    }

    /// <summary>
    /// A brush showing <paramref name="image"/> (any WPF bitmap) stretched over its visual with bilinear filtering.
    /// (A mipmap surface cannot feed a surface brush: "MipmapSurface is not supported as input for
    /// CompositionSurfaceBrush", 2026-10-12. Callers that shrink pictures a lot upload them at several sizes.)
    /// </summary>
    public CompositionSurfaceBrush Brush(BitmapSource image)
    {
        var px = Pixels(image, out int w, out int h);
        CompositionSurfaceBrush brush;
        try { brush = Compositor.CreateSurfaceBrush(Surface(px, w, h)); }
        catch (Exception ex) when (Lost(ex))
        {
            // The device went since the last check: an empty brush for now; the new device's DeviceReplaced fills it.
            Log.Warn($"composition: a picture could not be uploaded (0x{ex.HResult:X8}): the GPU device is checked");
            RequestCheck();
            brush = Compositor.CreateSurfaceBrush();
        }
        brush.Stretch = CompositionStretch.Fill;
        brush.BitmapInterpolationMode = CompositionBitmapInterpolationMode.Linear;
        return brush;
    }

    /// <summary>Gives a brush's surface back to the compositor.</summary>
    public static void Release(CompositionSurfaceBrush brush)
    {
        (brush.Surface as IDisposable)?.Dispose();
        brush.Dispose();
    }

    /// <summary>Premultiplied BGRA pixels, top row first.</summary>
    static byte[] Pixels(BitmapSource image, out int w, out int h)
    {
        BitmapSource src = image.Format == PixelFormats.Pbgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        w = src.PixelWidth;
        h = src.PixelHeight;
        var px = new byte[w * h * 4];
        src.CopyPixels(px, w * 4, 0);
        return px;
    }

    CompositionDrawingSurface Surface(byte[] px, int w, int h)
    {
        var surface = _graphics!.CreateDrawingSurface(new Windows.Foundation.Size(w, h), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        try { Fill(surface, px, w, h); }
        catch
        {
            surface.Dispose();
            throw;
        }
        return surface;
    }

    /// <summary>A failed upload because the device is lost (its HRESULT, or the device says so), not a bad picture.</summary>
    bool Lost(Exception ex) => DeviceRecovery.IsLost(ex.HResult) || _simulatedLoss || RemovedReason() != 0;

    /// <summary><paramref name="image"/> scaled by WPF to exactly <paramref name="w"/> × <paramref name="h"/> pixels, high quality.</summary>
    public static BitmapSource Scaled(BitmapSource image, int w, int h)
    {
        if (image.PixelWidth == w && image.PixelHeight == h) return image;
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) dc.DrawImage(image, new System.Windows.Rect(0, 0, w, h));
        var rtb = new RenderTargetBitmap(Math.Max(1, w), Math.Max(1, h), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    /// <summary>Copies pixels into a drawing surface (ICompositionDrawingSurfaceInterop::BeginDraw with a D3D11 texture).</summary>
    void Fill(CompositionDrawingSurface surface, byte[] px, int w, int h)
    {
        if (_failNextUpload)
        {
            _failNextUpload = false;
            throw new COMException("simulated lost device (diagnostics)", unchecked((int)0x887A0005));
        }
        IntPtr unk = ((IWinRTObject)surface).NativeObject.ThisPtr;
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unk, in IID_ICompositionDrawingSurfaceInterop, out IntPtr interop));
        try
        {
            var vtbl = *(IntPtr**)interop;
            var begin = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, POINT*, int>)vtbl[3];
            var end = (delegate* unmanaged[Stdcall]<IntPtr, int>)vtbl[4];
            IntPtr texture;
            POINT offset;
            Guid iid = IID_ID3D11Texture2D;
            Marshal.ThrowExceptionForHR(begin(interop, IntPtr.Zero, &iid, &texture, &offset));
            try
            {
                var box = new D3D11_BOX { left = (uint)offset.x, top = (uint)offset.y, front = 0, right = (uint)(offset.x + w), bottom = (uint)(offset.y + h), back = 1 };
                // ID3D11DeviceContext::UpdateSubresource: slot 48
                var update = (delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, D3D11_BOX*, void*, uint, uint, void>)(*(IntPtr**)_context)[48];
                lock (Gpu)
                    fixed (byte* p = px) update(_context, texture, 0, &box, p, (uint)(w * 4), 0);
            }
            finally
            {
                Marshal.Release(texture);
                Marshal.ThrowExceptionForHR(end(interop));
            }
        }
        finally { Marshal.Release(interop); }
    }

    void ReleaseDevice()
    {
        _graphics?.Dispose();
        _graphics = null;
        if (_context != IntPtr.Zero) { Marshal.Release(_context); _context = IntPtr.Zero; }
        if (_device != IntPtr.Zero) { Marshal.Release(_device); _device = IntPtr.Zero; }
    }

    public void Dispose()
    {
        UnwatchRemoval();
        ReleaseDevice();
    }
}
