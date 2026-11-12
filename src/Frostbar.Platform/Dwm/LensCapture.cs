using System.Runtime.InteropServices;
using System.Windows.Threading;
using Frostbar.Core.Shell;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;
using Windows.Foundation.Metadata;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.UI.Composition;
using WinRT;

namespace Frostbar.Platform.Dwm;

/// <summary>
/// One plate's lens (a <see cref="WindowBackdrop"/>'s): where the plate is on the screen, its look, and the surface the
/// lens is drawn on. Its fields belong to the UI thread, except <see cref="Pending"/>, which the capture's thread sets.
/// </summary>
public sealed class LensTarget
{
    internal readonly Plate Plate;
    /// <summary>Leaves the plate's windows out of screen capture, or puts them back.</summary>
    internal readonly Action<bool> Exclude;
    internal LensStyle Style;
    /// <summary>Dark glass (the bright behind it held down) or light (the dark raised).</summary>
    internal bool Dark = true;
    /// <summary>The plate on the screen (pixels).</summary>
    internal ScreenRect Rect;
    internal float Radius;
    internal double Scale = 1;
    internal bool Visible;
    internal CompositionDrawingSurface? Surface;
    internal CompositionSurfaceBrush? Brush;
    internal int SurfaceW, SurfaceH;
    internal bool Shown;
    /// <summary>Shown as the lens last heard (to know when it appears).</summary>
    internal bool Seen;
    /// <summary>When it last became visible (Stopwatch ticks): a lens drawn at once comes with its plate, not over 0.15 s.</summary>
    internal long VisibleSince;
    /// <summary>The picture shown while a larger one is made for the plate (let go once that is drawn).</summary>
    internal CompositionDrawingSurface? RetiredSurface;
    internal CompositionSurfaceBrush? RetiredBrush;
    internal int Pending;
    /// <summary>Whether this target has asked for its window to be left out of capture (each asks once: the window counts them).</summary>
    internal bool Excluded;
    /// <summary>Whether the plate's window is out of screen capture now (diagnostics: a lens read with it in reads itself).</summary>
    internal Func<bool>? WindowExcluded;

    /// <summary>
    /// Told what is behind the plate each time it is read (after a drawing, when the UI thread is free): the mean of
    /// its brightness, 0–1, and how far it varies over the plate (the standard deviation). What the plate's shadow
    /// goes by (Core's ShadowDepth, 2026-11-10); read for a plate without a tone too (the light appearance).
    /// </summary>
    internal Action<double, double>? Behind;
    /// <summary>The glass suits what is behind it (item 4): its tone, judged from what the lens reads; null: it keeps <see cref="Dark"/>.</summary>
    internal GlassTone? Tone;
    /// <summary>Told when <see cref="Tone"/> turns (true: dark now), UI thread; the lens is drawn again in it after.</summary>
    internal Action<bool>? Turned;
    /// <summary>Sampled for its tone and not read yet, and where (pixels on the monitor: it may have moved since).</summary>
    internal bool Sampled;
    internal ScreenRect SampledAt;

    internal LensTarget(Plate plate, Action<bool> exclude)
    {
        Plate = plate;
        Exclude = exclude;
    }

    // Fitted to the plate: a small piece (macOS 27's Control Center) is all dome, a menu has a rounded rim and more frost.
    internal LensLook ReadLook => LensCapture.RimOverride is { } rim
        ? Fitted with { Specular = rim.specular, Contact = rim.contact }
        : Fitted;

    LensLook Fitted
    {
        get
        {
            var look = LensLook.For(Style, Scale, Dark).Fit(Math.Min(Rect.Right - Rect.Left, Rect.Bottom - Rect.Top));
            return LensCapture.BendOverride is { } bend ? look with { RimBend = (float)(bend.bend * Scale), RimWidth = (float)(bend.width * Scale) } : look;
        }
    }

    /// <summary>
    /// How much of the glass is there (0–1): the plate materializing by its lens (the user's "A レンズの強さで実体化",
    /// 2026-10-23). What the lens reads (and what reaches it) is the whole look's: the look drawn is part of the way in.
    /// </summary>
    internal float Presence = 1;

    /// <summary>
    /// Joined with its neighbours (Core's LiquidJoin; WindowBackdrop sets them as its pieces come and go): how far
    /// their shapes run together, pixels (0: not joined), the neighbours' rectangles on the screen with their radii,
    /// and the plate's tint (the lens draws it while joined).
    /// </summary>
    internal float JoinBlend;
    internal (ScreenRect rect, float radius)[] Neighbours = [];
    internal (float r, float g, float b, float a) JoinTint;

    /// <summary>How far beyond the plate its joined drawing reaches, pixels (0: not joined).</summary>
    internal int JoinMargin => JoinBlend > 0 && Neighbours.Length > 0 ? LiquidJoin.Margin(JoinBlend) : 0;

    /// <summary>The join as the lens is drawn: the neighbours in the drawn area's pixels (the plate grown by the margin).</summary>
    internal LensJoin? Join()
    {
        int m = JoinMargin;
        if (m == 0) return null;
        int ox = Rect.Left - m, oy = Rect.Top - m;
        var others = new JoinBox[Math.Min(Neighbours.Length, LensJoin.Most)];
        for (int i = 0; i < others.Length; i++)
        {
            var (r, radius) = Neighbours[i];
            others[i] = new JoinBox((r.Left + r.Right) / 2f - ox, (r.Top + r.Bottom) / 2f - oy, (r.Right - r.Left) / 2f, (r.Bottom - r.Top) / 2f, radius);
        }
        return new LensJoin(m, JoinBlend, others, (JoinTint.r, JoinTint.g, JoinTint.b, JoinTint.a * Math.Clamp(Presence, 0, 1)));
    }

    /// <summary>The look drawn now: the plate's, part of the way in (<see cref="Presence"/>).</summary>
    internal LensLook Look => Presence >= 1 ? ReadLook : ReadLook.At(Presence);

    /// <summary>Times it was drawn again for its presence alone (diagnostics).</summary>
    internal int PresenceDraws;

    /// <summary>The presence it was drawn at, last 64 times, with when (Stopwatch ms) and whether drawn (diagnostics "lens-trace").</summary>
    internal readonly Queue<(double ms, float presence, bool drawn)> Trace = new();
}

/// <summary>
/// Liquid Glass's lens (ADR-016, S31): what is behind the shell's glass bends at the plates' rims. The backdrop Windows
/// gives a composition plate is already blurred and cannot be transformed (2026-10-12), so the lens reads the screen
/// itself: a Windows.Graphics.Capture session of the primary monitor, with the shell's glass windows left out of it
/// (WDA_EXCLUDEFROMCAPTURE: so they are not in screenshots or screen sharing either, which the settings say). The
/// session runs while some plate with a lens is shown. The newest frame is kept (the one before goes back); a plate
/// copies what it reads from it into a picture of the screen (<see cref="LensGpu"/>) and is drawn again when a change reaches that,
/// except changes wholly inside a plate (the shell's own drawing there, the lens itself among it: drawing again on that
/// would never stop: <see cref="LensDamage"/>), and at once when the plate moves, changes size or appears. Drawing is on
/// the UI thread. Without dirty regions (before Windows 11 24H2) there is no lens: every frame would reach every plate.
/// </summary>
public sealed class LensCapture : IDisposable
{
    [DllImport("d3d11.dll")] static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("combase.dll")] static extern int RoGetActivationFactory(IntPtr classId, in Guid iid, out IntPtr factory);
    [DllImport("combase.dll", CharSet = CharSet.Unicode)] static extern int WindowsCreateString(string s, int length, out IntPtr hstring);
    [DllImport("combase.dll")] static extern int WindowsDeleteString(IntPtr hstring);

    static readonly Guid IID_IDXGIDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
    static readonly Guid IID_IGraphicsCaptureItemInterop = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
    static readonly Guid IID_IGraphicsCaptureItem = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid IID_IDirect3DDxgiInterfaceAccess = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    static readonly Guid IID_ID3D11Texture2D = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    /// <summary>Whether the lens may run in this process: set once at start-up (the PC can, and the shell wants it).</summary>
    public static bool Available { get; set; }

    /// <summary>
    /// A spec's rim in place of the look's (diagnostics: FROSTBAR_E2E_LENS_RIM, the candidates photographed side by side,
    /// 2026-11-06): the bright line's and the dark line's strength. Set once at start-up; null: the look's own.
    /// </summary>
    public static (float specular, float contact)? RimOverride { get; set; }

    /// <summary>
    /// A spec's bend at the rim (diagnostics: FROSTBAR_E2E_LENS_BEND, the candidates photographed side by side,
    /// 2026-11-10): how far in the rim reads at the edge and within how far of it, in DIPs (Core's LensLook.RimBend).
    /// Set once at start-up; null: the look's own.
    /// </summary>
    public static (float bend, float width)? BendOverride { get; set; }

    /// <summary>
    /// The least time between two frames while what is behind a plate changes, ms (33: 30 a second). A spec's shell can
    /// have another (diagnostics: FROSTBAR_E2E_LENS_PACE; `lens-pace` measures what following faster costs, 2026-11-10).
    /// </summary>
    public static int FastMs { get; set; } = 33;

    /// <summary>
    /// The least time between two frames while what is behind a plate moves on and on, ms (<see cref="Follow"/>): 8,
    /// under half a frame of a 60 Hz display, so that every composed frame comes (16 gave 50 a second). A spec's shell
    /// can have another, or none of it (FROSTBAR_E2E_LENS_SMOOTH; at or over <see cref="FastMs"/>: never faster than that).
    /// </summary>
    public static int SmoothMs { get; set; } = 8;

    /// <summary>The lens of this process, made the first time a plate asks for it (UI thread).</summary>
    public static LensCapture? Current { get; private set; }

    internal static LensCapture? For(Dispatcher ui) => Available ? Current ??= new LensCapture(ui) : null;

    /// <summary>Whether this Windows can capture the screen with dirty regions (Windows 11 24H2 and later).</summary>
    public static bool Supported()
    {
        try
        {
            return GraphicsCaptureSession.IsSupported()
                && ApiInformation.IsPropertyPresent("Windows.Graphics.Capture.GraphicsCaptureSession", "DirtyRegionMode");
        }
        catch (Exception ex) when (ex is COMException or TypeLoadException or InvalidCastException) { return false; }
    }

    sealed record Snapshot(ScreenRect[] Own, ScreenRect[] Read, LensTarget[] Targets);

    readonly Dispatcher _ui;
    readonly List<LensTarget> _targets = new();
    SurfaceFactory _f;
    Shell.MonitorInfo _mon;
    LensGpu? _gpu;
    IDirect3DDevice? _d3d;
    Direct3D11CaptureFramePool? _pool;
    GraphicsCaptureSession? _session;
    volatile Snapshot _snap = new([], [], []);
    int _whole, _drawQueued, _restartQueued;
    bool _frozen, _failed;
    DispatcherTimer? _stopLater, _quiet;
    bool _fast;
    // A start waits for the shaders and for the shell to be idle.
    bool _startQueued, _startNow;
    // The newest frame, kept until the next one (under the factory's Gpu lock): what a plate is drawn from.
    Direct3D11CaptureFrame? _latest;
    readonly List<ScreenRect> _changed = new(); // the capture's thread's own
    long _frames, _reached, _draws, _errors, _starts, _remakes, _hides, _appears, _selfReads;
    string? _lastError;

    LensCapture(Dispatcher ui)
    {
        _ui = ui;
        _mon = Shell.Monitors.Primary();
        _f = SurfaceFactory.Shared(_mon);
        _f.DeviceReplaced += Rebuild;
        _ = LensGpu.Compiled; // off the UI thread from now
    }

    bool Running => _session != null;

    internal void Add(LensTarget t)
    {
        if (_targets.Contains(t)) return;
        _targets.Add(t);
        // Not once the capture failed (no lens is drawn): every menu opened since was missing from screenshots and
        // screen sharing for the rest of the run (the failure gave back only the plates there then).
        if (!_frozen && !_failed) Exclude(t, true);
        Update(t);
    }

    /// <param name="withPlate">The plate goes with it (hidden or let go): nothing on the screen shows the lens going.</param>
    internal void Remove(LensTarget t, bool withPlate = false)
    {
        if (!_targets.Remove(t)) return;
        if (Tracing && t.Trace.Count > 0) _goneTraces.Add((new[] { t.Rect.Left, t.Rect.Top, t.Rect.Right, t.Rect.Bottom }, t.Trace.ToArray()));
        t.Trace.Clear();
        Exclude(t, false);
        ForgetSurface(t, counted: !withPlate);
        Publish();
        StopWhenUnused();
    }

    /// <summary>
    /// A target's say in its window's leaving screen capture, said once each way: a window with several plates (the
    /// Control Center's pieces) stays out while any of them has a lens. Each put the whole window back as it went, and
    /// the lenses left read the window's own glass (the parts fading out of the list as a view grows: 22 lenses drawn
    /// with the window in capture as Wi-Fi's view grew and 9 going back, the view's own the while it was open;
    /// 2026-10-18, lens-flicker).
    /// </summary>
    static void Exclude(LensTarget t, bool on)
    {
        if (t.Excluded == on) return;
        t.Excluded = on;
        t.Exclude(on);
    }

    /// <summary>The plate moved, changed size, look or visibility: it is drawn again at once (when shown). UI thread.</summary>
    internal void Update(LensTarget t)
    {
        bool appeared = t.Visible && !t.Seen;
        // Hidden: its tone waits for its next showing, and a reading still to come of what was behind it is dropped (it
        // made the hidden plate's "first reading", and it then kept its tone for 150 ms when it showed again).
        if (!t.Visible && t.Seen) { t.Tone?.Hidden(); t.Sampled = false; }
        t.Seen = t.Visible;
        if (appeared) t.VisibleSince = System.Diagnostics.Stopwatch.GetTimestamp();
        Publish();
        if (t.Visible) Start(); else StopWhenUnused();
        if (appeared) Busy(); // its picture may be up to a slow frame old: a fresh one soon
        if (t.Visible) Draw(t);
    }

    /// <summary>
    /// How often frames come (a running session takes a new MinUpdateInterval: S31 `lens interval`): each one costs the
    /// capture's thread 0.2–0.5 ms even when it reaches no plate, and something on the screen is nearly always changing
    /// (S31 `lens framecost`; 1.36% of a core at 30 a second, 2026-10-18 idle-cpu lens). So 2 a second while nothing
    /// reaches a plate, and 30 a second (what is behind followed smoothly) from a change that reaches one, or a plate
    /// appearing, until 3 s have passed quietly. (A maximized app that draws itself every few seconds behind the Dock
    /// keeps it fast nearly all the time: Claude's, 2026-10-19, seen in the diagnostics' "reaching". Not changed yet.) A plate that moves or
    /// changes size is drawn at once from the newest frame either way. UI thread.
    /// </summary>
    void Busy()
    {
        if (_pace == 0) Pace(1);
        _quiet ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) => { _quiet!.Stop(); Pace(0); }, _ui);
        _quiet.Stop();
        _quiet.Start();
    }

    /// <summary>
    /// What moves on and on behind a plate (a video, a page scrolling) is followed at the display's pace: at 33 ms the
    /// lens was drawn 25 times a second for 60 frames of it, two and three frames apart by turns (2026-11-10,
    /// `lens-pace`: 8 ms gives 60 a second; 16 only 50). It costs twice the share of a core (4% to 8% while a bar swept
    /// beside the Dock), so only while it lasts: from the fourth change to reach a plate within a quarter of a second
    /// (a change every 100 ms or slower, a pet's idle animation, a caret, never gets there), until none has for a
    /// quarter of a second. Called for each drawing a frame asked for. UI thread.
    /// </summary>
    void Follow()
    {
        bool onAndOn = _follow.Reached(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency);
        if (_pace == 1 && onAndOn) Pace(2);
        if (_pace != 2) return;
        _calm ??= new DispatcherTimer(TimeSpan.FromMilliseconds(LensFollow.CalmMs), DispatcherPriority.Background, (_, _) => { _calm!.Stop(); if (_pace == 2) Pace(1); }, _ui);
        _calm.Stop();
        _calm.Start();
    }

    readonly LensFollow _follow = new();
    DispatcherTimer? _calm;

    /// <summary>The capture's pace: 0 quiet (2 frames a second), 1 following (30), 2 following what moves on and on (the display's).</summary>
    int _pace;

    void Pace(int pace)
    {
        if (_session == null || _pace == pace) return;
        _pace = pace;
        _fast = pace > 0;
        if (pace == 2) _smooths++;
        try { _session.MinUpdateInterval = TimeSpan.FromMilliseconds(pace == 0 ? 500 : pace == 1 ? FastMs : Math.Min(FastMs, SmoothMs)); }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ObjectDisposedException) { }
    }

    int _smooths;

    void Publish()
    {
        var shown = _targets.Where(t => t.Visible && !LensDamage.Empty(t.Rect)).ToArray();
        int ox = _mon.Bounds.Left, oy = _mon.Bounds.Top;
        ScreenRect OnMonitor(ScreenRect r) => new(r.Left - ox, r.Top - oy, r.Right - ox, r.Bottom - oy);
        _snap = new Snapshot(
            [.. shown.Select(t => LensDamage.Grow(OnMonitor(t.Rect), 2 + t.JoinMargin))], // its own drawing: the plate (and its anti-aliased rim, its join)
            [.. shown.Select(t => LensDamage.Grow(OnMonitor(t.Rect), t.ReadLook.Reach + t.JoinMargin))],
            shown);
    }

    void Start()
    {
        _stopLater?.Stop();
        if (Running || _frozen || _failed || _startQueued || _targets.All(t => !t.Visible)) return;
        // Never in the middle of something: the shaders compile off the UI thread, and a capture starts once the shell is
        // idle (22–55 ms on the UI thread: at start-up, with the shaders, it made the installed shell's Dock 100 ms
        // slower to make, 2026-10-18 `startup lens`; and the Dock coming back over a full-screen app's end is moving).
        // Until then the plates show the blurred glass; the lens comes in when first drawn.
        var compiled = LensGpu.Compiled;
        if (!_startNow)
        {
            _startQueued = true;
            compiled.ContinueWith(_ => _ui.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                _startQueued = false;
                _startNow = true;
                try { Start(); }
                finally { _startNow = false; }
                foreach (var t in _targets.Where(t => t.Visible)) Draw(t);
            }), TaskScheduler.Default);
            return;
        }
        if (compiled.IsFaulted)
        {
            _failed = true;
            _lastError = compiled.Exception?.InnerException?.Message;
            Log.Warn("lens: the shaders did not compile; the glass goes without its lens", compiled.Exception?.InnerException);
            foreach (var t in _targets) { Exclude(t, false); ForgetSurface(t); }
            return;
        }
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            _gpu ??= new LensGpu(_f);
            _d3d ??= WinRtDevice(_f.Device);
            var item = MonitorItem(_mon.Handle);
            lock (_f.Gpu) _gpu.Size(item.Size.Width, item.Size.Height);
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(_d3d, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);
            _pool.FrameArrived += OnFrame;
            _session = _pool.CreateCaptureSession(item);
            _session.IsCursorCaptureEnabled = false;
            try { _session.IsBorderRequired = false; } catch (Exception ex) when (ex is COMException or UnauthorizedAccessException) { } // a yellow frame otherwise
            _session.DirtyRegionMode = GraphicsCaptureDirtyRegionMode.ReportOnly;
            Volatile.Write(ref _whole, 1); // the session's first frame is the whole screen
            _fast = false;
            _pace = 0;
            Busy();
            _session.StartCapture();
            _starts++;
            Log.Info($"lens: capturing the screen {item.Size.Width}x{item.Size.Height} for {_targets.Count(t => t.Visible)} plate(s) (started in {System.Diagnostics.Stopwatch.GetElapsedTime(t0).TotalMilliseconds:0} ms)");
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            // No lens then: the plates keep the blurred backdrop.
            _failed = true;
            _lastError = ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + ": " + ex.Message;
            Log.Warn("lens: no screen capture; the glass goes without its lens", ex);
            Stop();
            foreach (var t in _targets) { Exclude(t, false); ForgetSurface(t); }
        }
    }

    /// <summary>Stops the capture a moment after the last plate with a lens hides (a menu closing and the next opening keep it).</summary>
    void StopWhenUnused()
    {
        if (!Running || _targets.Any(t => t.Visible)) return;
        _stopLater ??= new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            _stopLater!.Stop();
            if (_targets.All(t => !t.Visible)) { Stop(); Log.Debug("lens: no plate shown; the capture stops"); }
        }, _ui);
        _stopLater.Stop();
        _stopLater.Start();
    }

    void Stop()
    {
        var session = _session;
        var pool = _pool;
        _session = null;
        _pool = null;
        if (pool != null) pool.FrameArrived -= OnFrame;
        session?.Dispose();
        lock (_f.Gpu)
        {
            _latest?.Dispose();
            _latest = null;
        }
        pool?.Dispose();
        // A hidden plate's picture is of what was behind it then: shown again before the next capture's first frame, it
        // showed that old screen at full strength (its presence dims only what lies over it). It goes back to the blurred
        // glass until it is drawn again.
        foreach (var t in _targets)
            if (!t.Visible && t.Shown) { t.Plate.SetLens(null); t.Shown = false; }
    }

    /// <summary>
    /// The capture's thread: keeps the frame as the newest (the one before goes back to the pool) and marks the plates
    /// its changes reached. Nothing is copied here: most frames are changes far from every plate (a window of the user's
    /// at work), and copying each one cost the shell 2.8% of a core while the screen kept changing (2026-10-18,
    /// idle-cpu lens: the capture's thread 2.1%, the GPU driver's 0.55%). A plate copies what it reads when it is drawn.
    /// </summary>
    void OnFrame(Direct3D11CaptureFramePool pool, object _)
    {
        Direct3D11CaptureFrame? frame = null;
        try
        {
            frame = pool.TryGetNextFrame();
            if (frame == null || _gpu is not { } gpu) return;
            Interlocked.Increment(ref _frames);
            if (frame.ContentSize.Width != gpu.Width || frame.ContentSize.Height != gpu.Height)
            {
                // The monitor's resolution changed: a session and a picture of the new size.
                if (Interlocked.Exchange(ref _restartQueued, 1) == 0) _ui.BeginInvoke(DispatcherPriority.Normal, Restart);
                return;
            }
            var snap = _snap;
            bool any = false;
            if (Interlocked.Exchange(ref _whole, 0) == 1)
            {
                // The session's first frame: every plate shown is drawn from it.
                foreach (var t in snap.Targets) Volatile.Write(ref t.Pending, 1);
                any = snap.Targets.Length > 0;
            }
            else if (snap.Targets.Length > 0)
            {
                _changed.Clear();
                foreach (var d in frame.DirtyRegions) _changed.Add(new ScreenRect(d.X, d.Y, d.X + d.Width, d.Y + d.Height));
                var reached = LensDamage.Reached(_changed, snap.Own, snap.Read);
                for (int i = 0; i < reached.Length; i++)
                    if (reached[i]) { Volatile.Write(ref snap.Targets[i].Pending, 1); any = true; }
                if (any) Remember(_changed, snap);
            }
            Direct3D11CaptureFrame? old;
            lock (_f.Gpu)
            {
                if (gpu != _gpu) return; // made again meanwhile (a lost device, other displays)
                if (pool != _pool) return; // stopped meanwhile: a frame of the session gone must not wait for the next one
                old = _latest;
                _latest = frame;
                frame = null; // kept
            }
            old?.Dispose();
            if (!any) return;
            Interlocked.Increment(ref _reached);
            if (Interlocked.Exchange(ref _drawQueued, 1) == 0) _ui.BeginInvoke(DispatcherPriority.Render, DrawPending);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _errors);
            if (_lastError == null) Log.Warn("lens: a frame could not be used", ex);
            _lastError = ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + ": " + ex.Message;
            if (DeviceRecovery.IsLost(ex.HResult)) _f.RequestCheck();
        }
        finally { frame?.Dispose(); }
    }

    // Diagnostics: the last changes that reached a plate (screen pixels), and when. Each keeps the capture at 30 frames
    // a second for 3 s (Busy): what keeps it so while nothing seems to move (idle-cpu lens, 2026-10-18: 17 a minute).
    readonly Queue<(long at, ScreenRect r)> _reaching = new();

    void Remember(List<ScreenRect> changed, Snapshot snap)
    {
        int ox = _mon.Bounds.Left, oy = _mon.Bounds.Top;
        lock (_reaching)
            foreach (var c in changed)
            {
                if (LensDamage.Empty(c) || snap.Own.Any(o => LensDamage.Inside(c, o)) || !snap.Read.Any(r => LensDamage.Meet(c, r))) continue;
                _reaching.Enqueue((System.Diagnostics.Stopwatch.GetTimestamp(), new ScreenRect(c.Left + ox, c.Top + oy, c.Right + ox, c.Bottom + oy)));
                while (_reaching.Count > 40) _reaching.Dequeue();
            }
    }

    /// <summary>
    /// The changes that reached a plate lately: how long ago, where, and the topmost window of another process under
    /// its middle (its process and class: the shell's own windows over it skipped, the Dock among them).
    /// </summary>
    object Reaching()
    {
        (long at, ScreenRect r)[] last;
        lock (_reaching) last = [.. _reaching];
        return last.Select(x =>
        {
            var (process, cls) = Under((x.r.Left + x.r.Right) / 2, (x.r.Top + x.r.Bottom) / 2);
            return new
            {
                ago = (int)System.Diagnostics.Stopwatch.GetElapsedTime(x.at).TotalMilliseconds,
                rect = new[] { x.r.Left, x.r.Top, x.r.Right, x.r.Bottom },
                process, cls,
            };
        }).ToArray();
    }

    static (string process, string cls) Under(int px, int py)
    {
        const uint GW_HWNDFIRST = 0, GW_HWNDNEXT = 2;
        var seed = Native.GetForegroundWindow();
        if (seed == IntPtr.Zero) return ("", "");
        for (var h = Native.GetWindow(seed, GW_HWNDFIRST); h != IntPtr.Zero; h = Native.GetWindow(h, GW_HWNDNEXT))
        {
            if (!Native.IsWindowVisible(h) || !Native.GetWindowRect(h, out var r) || px < r.Left || px >= r.Right || py < r.Top || py >= r.Bottom) continue;
            if (Native.DwmGetWindowAttribute(h, 14 /*DWMWA_CLOAKED*/, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            if (Native.GetWindowThreadProcessId(h, out uint pid) == 0 || pid == Environment.ProcessId) continue;
            var cls = new System.Text.StringBuilder(128);
            Native.GetClassName(h, cls, cls.Capacity);
            string process = "";
            try { process = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; } catch (Exception) { }
            return (process, cls.ToString());
        }
        return ("", "");
    }

    void DrawPending()
    {
        Volatile.Write(ref _drawQueued, 0);
        Busy(); // a change reached a plate: what is behind is followed smoothly for a while
        Follow(); // and at the display's pace while changes go on and on
        foreach (var t in _targets.ToArray())
            if (Interlocked.Exchange(ref t.Pending, 0) == 1 && t.Visible) Draw(t);
    }

    void Draw(LensTarget t)
    {
        if (_frozen || _gpu is not { } gpu || !Running || LensDamage.Empty(t.Rect)) return;
        int w = t.Rect.Right - t.Rect.Left, h = t.Rect.Bottom - t.Rect.Top;
        // Joined with its neighbours: drawn a margin larger all round (the joined shape reaches beyond the plate).
        var join = t.Join();
        int m = join?.Margin ?? 0, dw = w + 2 * m, dh = h + 2 * m;
        try
        {
            if (t.Surface == null || LensSurfaceSize.Remake(t.SurfaceW, dw) || LensSurfaceSize.Remake(t.SurfaceH, dh))
            {
                // A plate growing (the Control Center growing out of its item, a part's glass into its view) outgrows its
                // picture several times: the shown one stays until the new one is drawn, then they are swapped at once.
                // Taken off and shown again over 0.15 s each time, the plate's own rim lines came and went: a flicker
                // (the user's "コントロールセンター開くときチカチカしてる", 2026-10-18).
                if (t.Surface != null)
                {
                    _remakes++;
                    if (t.Shown && t.RetiredSurface == null) { t.RetiredSurface = t.Surface; t.RetiredBrush = t.Brush; }
                    else { t.Brush?.Dispose(); t.Surface.Dispose(); }
                }
                t.SurfaceW = LensSurfaceSize.Round(dw);
                t.SurfaceH = LensSurfaceSize.Round(dh);
                t.Surface = gpu.Surface(t.SurfaceW, t.SurfaceH);
                t.Brush = _f.Compositor.CreateSurfaceBrush(t.Surface);
                t.Brush.Stretch = CompositionStretch.None;
                t.Brush.HorizontalAlignmentRatio = 0;
                t.Brush.VerticalAlignmentRatio = 0;
            }
            int x = t.Rect.Left - _mon.Bounds.Left, y = t.Rect.Top - _mon.Bounds.Top;
            var read = LensDamage.Grow(new ScreenRect(x, y, x + w, y + h), t.ReadLook.Reach + m); // all it reads, whatever its presence
            bool drawn = false;
            lock (_f.Gpu)
            {
                // What the plate reads, from the newest frame; nothing captured yet: the first frame draws it.
                IntPtr texture = _latest != null ? Texture(_latest.Surface) : IntPtr.Zero;
                if (texture != IntPtr.Zero)
                {
                    if (t.WindowExcluded?.Invoke() == false) _selfReads++;
                    try { gpu.Copy(texture, [read]); }
                    finally { Marshal.Release(texture); }
                    drawn = gpu.Draw(t.Surface, dw, dh, x - m, y - m, t.Radius, t.Look, join);
                    if (drawn && (t.Tone != null || t.Behind != null))
                    {
                        t.SampledAt = new ScreenRect(x, y, x + w, y + h);
                        gpu.Sample(t.SampledAt);
                        t.Sampled = true;
                    }
                }
            }
            Traced(t, drawn);
            if (!drawn) { t.Pending = 1; return; }
            _draws++;
            // A lens drawn with its plate (within 50 ms of its showing: the capture already running, as for a panel that
            // opens) comes with it; only one that comes late (the capture starting) appears over 0.15 s, over the plate's
            // blurred backdrop and rim lines until then. Not a plate coming by its lens (its presence brings it in).
            bool late = !t.Shown && t.Presence >= 1 && System.Diagnostics.Stopwatch.GetElapsedTime(t.VisibleSince).TotalMilliseconds > 50;
            if (!t.Shown) _appears++;
            t.Plate.SetJoined(m); // with the drawing: the plate's own clip goes and comes with a joined one
            t.Plate.SetLens(t.Brush, appear: late);
            t.Shown = true;
            if (t.RetiredSurface != null)
            {
                t.RetiredBrush?.Dispose();
                t.RetiredSurface.Dispose();
                t.RetiredBrush = null;
                t.RetiredSurface = null;
            }
            // Its tone: read at once when it has none yet (it is just showing: it opens in its tone), else a moment
            // later, when the GPU has done the copy (reading now would hold the UI thread until it has).
            if (t.Tone != null) ReadTones(now: double.IsNaN(t.Tone.Luma));
            else if (t.Behind != null) ReadTones(now: false);
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            _errors++;
            if (_lastError == null) Log.Warn("lens: a plate could not be drawn", ex);
            _lastError = ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + ": " + ex.Message;
            if (DeviceRecovery.IsLost(ex.HResult)) _f.RequestCheck();
        }
    }

    // ---- a plate coming by its lens (the user's "A レンズの強さで実体化", 2026-10-23) --------------------------------------

    /// <summary>Plates' presences traced (diagnostics "lens-trace"): off by default.</summary>
    public static bool Tracing { get; set; }

    static void Traced(LensTarget t, bool drawn)
    {
        if (!Tracing) return;
        if (t.Trace.Count >= 64) t.Trace.Dequeue();
        t.Trace.Enqueue((System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0 / System.Diagnostics.Stopwatch.Frequency, t.Presence, drawn));
    }

    /// <summary>
    /// The plate drawn again at its new presence from the picture of the screen it read last (no new copy, no new
    /// mips: a presence's step changes only how the lens is drawn). A plate not drawn yet is drawn as usual. UI thread.
    /// </summary>
    internal void Repaint(LensTarget t)
    {
        if (!t.Shown || t.Surface == null) { if (t.Visible) Draw(t); return; }
        if (_frozen || _gpu is not { } gpu || !Running || LensDamage.Empty(t.Rect)) return;
        int w = t.Rect.Right - t.Rect.Left, h = t.Rect.Bottom - t.Rect.Top;
        var join = t.Join();
        int m = join?.Margin ?? 0, dw = w + 2 * m, dh = h + 2 * m;
        if (dw > t.SurfaceW || dh > t.SurfaceH || m != (int)t.Plate.Joined) { Draw(t); return; } // grown past its picture, or into or out of a join: made again
        int x = t.Rect.Left - _mon.Bounds.Left, y = t.Rect.Top - _mon.Bounds.Top;
        try
        {
            bool drawn;
            lock (_f.Gpu) drawn = gpu.Draw(t.Surface, dw, dh, x - m, y - m, t.Radius, t.Look, join);
            Traced(t, drawn);
            if (drawn) { _draws++; t.PresenceDraws++; }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException or ArgumentException)
        {
            _errors++;
            _lastError = ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + ": " + ex.Message;
            if (DeviceRecovery.IsLost(ex.HResult)) _f.RequestCheck();
        }
    }

    /// <summary>
    /// Whether the targets of one window (<paramref name="mine"/>) stand clear of every other shown plate with a lens:
    /// a plate over another (a submenu over its menu) comes by its fade instead (Core's LensPresence.Allowed: the capture
    /// leaves the shell's glass out, so a plate part of the way in over another would show a hole through it).
    /// </summary>
    internal bool Clear(IReadOnlyCollection<LensTarget> mine) =>
        mine.All(t => LensPresence.Allowed(t.Rect, _targets.Where(o => o.Visible && !mine.Contains(o) && !LensDamage.Empty(o.Rect)).Select(o => o.Rect)));

    // ---- the glass's tone (item 4, the user's "A", 2026-10-19) ---------------------------------------------------------

    bool _tonesQueued;
    int _toneRetries;
    DispatcherTimer? _toneLater, _toneDue;
    long _toneReads, _turns;

    /// <summary>
    /// Reads the tones of the plates sampled since the last reading: <paramref name="now"/>, waiting for the GPU (a plate
    /// showing: it opens in its tone), or once the UI thread is idle, when the copy is done. UI thread.
    /// </summary>
    void ReadTones(bool now)
    {
        if (now) { ReadTonesNow(wait: true); return; }
        if (_tonesQueued) return;
        _tonesQueued = true;
        _ui.BeginInvoke(DispatcherPriority.Background, () => { _tonesQueued = false; ReadTonesNow(wait: false); });
    }

    void ReadTonesNow(bool wait)
    {
        if (_gpu is not { } gpu) return;
        var sampled = _targets.Where(t => t.Sampled && (t.Tone != null || t.Behind != null)).ToArray();
        if (sampled.Length == 0) return;
        var luma = new double[sampled.Length];
        var deviation = new double[sampled.Length];
        bool done;
        try
        {
            lock (_f.Gpu) done = gpu.Brightness([.. sampled.Select(t => t.SampledAt)], luma, deviation, wait);
        }
        catch (COMException ex)
        {
            _errors++;
            _lastError = ex.GetType().Name + " 0x" + ex.HResult.ToString("X8") + ": " + ex.Message;
            if (DeviceRecovery.IsLost(ex.HResult)) _f.RequestCheck();
            return;
        }
        if (!done)
        {
            // Still copying: a moment later (and waiting for it, the third time).
            _toneLater ??= new DispatcherTimer(TimeSpan.FromMilliseconds(15), DispatcherPriority.Background, (_, _) =>
            {
                _toneLater!.Stop();
                ReadTonesNow(wait: ++_toneRetries >= 3);
            }, _ui);
            _toneLater.Start();
            return;
        }
        _toneRetries = 0;
        _toneReads++;
        long ms = Environment.TickCount64;
        for (int i = 0; i < sampled.Length; i++)
        {
            var t = sampled[i];
            t.Sampled = false;
            if (double.IsNaN(luma[i])) continue;
            t.Behind?.Invoke(luma[i], deviation[i]);
            if (t.Tone != null && t.Tone.Read(luma[i], ms)) Turn(t);
        }
        Due();
    }

    /// <summary>Checks the plates waiting to turn when the first of them is due (nothing behind may change meanwhile).</summary>
    void Due()
    {
        long? due = null;
        foreach (var t in _targets)
            if (t.Tone?.DueAt is { } at && (due == null || at < due)) due = at;
        if (due == null) { _toneDue?.Stop(); return; }
        _toneDue ??= new DispatcherTimer(DispatcherPriority.Background, _ui);
        _toneDue.Stop();
        _toneDue.Tick -= OnDue;
        _toneDue.Tick += OnDue;
        _toneDue.Interval = TimeSpan.FromMilliseconds(Math.Max(1, due.Value - Environment.TickCount64 + 1));
        _toneDue.Start();
    }

    void OnDue(object? sender, EventArgs e)
    {
        _toneDue!.Stop();
        long now = Environment.TickCount64;
        foreach (var t in _targets.ToArray())
            if (t.Visible && t.Tone != null && t.Tone.Tick(now)) Turn(t);
        Due();
    }

    /// <summary>A plate's tone turned: its window takes the glass of that tone and colours what is on it; the lens is drawn in it.</summary>
    void Turn(LensTarget t)
    {
        _turns++;
        t.Dark = t.Tone!.Dark;
        t.Turned?.Invoke(t.Dark);
        if (t.Visible) Draw(t);
    }

    /// <summary>Lets the GPU's things go, under the same lock as the capture's thread copies under (it may be in the middle of a frame).</summary>
    void DropGpu()
    {
        lock (_f.Gpu)
        {
            _gpu?.Dispose();
            _gpu = null;
        }
    }

    /// <param name="counted">Counted as a lens taken off a plate that stays (its rim lines back: a flicker).</param>
    void ForgetSurface(LensTarget t, bool counted = true)
    {
        if (t.Shown && counted) _hides++;
        t.Plate.SetLens(null);
        t.Shown = false;
        t.Brush?.Dispose();
        t.Surface?.Dispose();
        t.Brush = null;
        t.Surface = null;
        t.SurfaceW = t.SurfaceH = 0;
        t.RetiredBrush?.Dispose();
        t.RetiredSurface?.Dispose();
        t.RetiredBrush = null;
        t.RetiredSurface = null;
    }

    /// <summary>The GPU device was lost and replaced: everything on it is made again.</summary>
    void Rebuild()
    {
        Stop();
        DropGpu();
        _d3d = null;
        foreach (var t in _targets) ForgetSurface(t);
        _failed = false;
        if (!_frozen) foreach (var t in _targets) Exclude(t, true); // given back when it failed: kept out of the capture again
        Start();
        foreach (var t in _targets.Where(t => t.Visible)) Draw(t);
    }

    /// <summary>The displays changed (the primary monitor, its size): the capture starts again on the primary one. UI thread.</summary>
    public void Restart()
    {
        Volatile.Write(ref _restartQueued, 0);
        Stop();
        var mon = Shell.Monitors.Primary();
        if (mon.Device != _mon.Device)
        {
            _f.DeviceReplaced -= Rebuild;
            DropGpu();
            _d3d = null;
            foreach (var t in _targets) ForgetSurface(t);
            _f = SurfaceFactory.Shared(mon);
            _f.DeviceReplaced += Rebuild;
        }
        _mon = mon;
        Publish();
        Start();
    }

    /// <summary>
    /// Diagnostics (lens-freeze): the plates are put back into screen capture and the lens stops following the screen
    /// (it keeps its last drawing), so that a spec can photograph them; off: left out again, a new session.
    /// </summary>
    public void Freeze(bool on)
    {
        if (on == _frozen) return;
        _frozen = on;
        if (on) Stop();
        foreach (var t in _targets) Exclude(t, !on);
        if (!on) Start();
    }

    /// <summary>What the lens is doing, for diagnostics.</summary>
    public object State() => new
    {
        available = Available,
        running = Running,
        frozen = _frozen,
        fast = _fast,
        pace = _pace,
        smooths = _smooths,
        failed = _failed,
        starts = _starts,
        frames = Interlocked.Read(ref _frames),
        reached = Interlocked.Read(ref _reached),
        draws = _draws,
        // Pictures made again (the plate grew past its surface), shown lenses taken off plates that stay, lenses appearing
        // (each with the plate's own rim lines going), and lenses drawn while their window was in screen capture (reading
        // their own glass): what flickers as a plate grows (2026-10-18).
        remakes = _remakes,
        hides = _hides,
        appears = _appears,
        selfReads = _selfReads,
        overlays = Plate.OverlayChanges,
        reaching = Reaching(),
        // The glass's tone (item 4): readings of what is behind the plates that suit it, and the turns they made.
        toneReads = _toneReads,
        turns = _turns,
        errors = Interlocked.Read(ref _errors),
        lastError = _lastError,
        screen = _gpu is { } g ? $"{g.Width}x{g.Height}" : null,
        plates = _targets.Select(t => new
        {
            style = t.Style.ToString(),
            visible = t.Visible,
            shown = t.Shown,
            excluded = t.WindowExcluded?.Invoke(),
            rect = new[] { t.Rect.Left, t.Rect.Top, t.Rect.Right, t.Rect.Bottom },
            surface = new[] { t.SurfaceW, t.SurfaceH },
            dark = t.Dark,
            adapts = t.Tone != null,
            luma = t.Tone is { } tone && !double.IsNaN(tone.Luma) ? Math.Round(tone.Luma, 3) : (double?)null,
            // Coming by its lens (2026-10-23): its presence, its plate's (what lies over the lens), and its draws for presence alone.
            presence = Math.Round(t.Presence, 3),
            join = t.JoinMargin,
            platePresence = Math.Round(t.Plate.Presence, 3),
            presenceDraws = t.PresenceDraws,
        }).ToArray(),
    };

    /// <summary>Diagnostics "lens-trace": each plate's presences drawn while tracing (ms, presence, drawn), then cleared.</summary>
    public object TakeTrace()
    {
        // Plates let go while tracing (a menu closed) keep theirs: they went with the window hiding, their last steps with them.
        var all = _targets.Select(t => (rect: new[] { t.Rect.Left, t.Rect.Top, t.Rect.Right, t.Rect.Bottom }, steps: t.Trace.ToArray()))
            .Concat(_goneTraces)
            .Where(x => x.steps.Length > 0)
            .Select(x => new { x.rect, steps = x.steps.Select(s => new { ms = Math.Round(s.ms, 1), p = Math.Round(s.presence, 3), drawn = s.drawn }).ToArray() })
            .ToArray();
        foreach (var t in _targets) t.Trace.Clear();
        _goneTraces.Clear();
        return all;
    }

    readonly List<(int[] rect, (double ms, float presence, bool drawn)[] steps)> _goneTraces = new();

    /// <summary>
    /// Diagnostics "lens-presence": every shown plate's lens drawn at <paramref name="presence"/> and held there (null:
    /// let go; drawn at their own again). For photos with lens-freeze.
    /// </summary>
    public void HoldPresence(float? presence)
    {
        _held = presence;
        foreach (var t in _targets.Where(t => t.Visible))
        {
            if (presence is { } p) { t.Presence = p; t.Plate.SetPresence(p); }
            else { t.Presence = 1; t.Plate.SetPresence(1); }
            Repaint(t);
        }
    }

    float? _held;

    /// <summary>A presence held for photos (diagnostics), or null.</summary>
    public float? HeldPresence => _held;

    static unsafe GraphicsCaptureItem MonitorItem(IntPtr monitor)
    {
        const string cls = "Windows.Graphics.Capture.GraphicsCaptureItem";
        Marshal.ThrowExceptionForHR(WindowsCreateString(cls, cls.Length, out var h));
        IntPtr factory;
        try { Marshal.ThrowExceptionForHR(RoGetActivationFactory(h, in IID_IGraphicsCaptureItemInterop, out factory)); }
        finally { WindowsDeleteString(h); }
        try
        {
            IntPtr raw;
            Guid iid = IID_IGraphicsCaptureItem;
            Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(void***)factory)[4])(factory, monitor, &iid, &raw)); // CreateForMonitor
            var item = GraphicsCaptureItem.FromAbi(raw);
            Marshal.Release(raw);
            return item;
        }
        finally { Marshal.Release(factory); }
    }

    static IDirect3DDevice WinRtDevice(IntPtr device)
    {
        Marshal.ThrowExceptionForHR(Marshal.QueryInterface(device, in IID_IDXGIDevice, out var dxgi));
        try
        {
            Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
            var d3d = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
            Marshal.Release(inspectable);
            return d3d;
        }
        finally { Marshal.Release(dxgi); }
    }

    static unsafe IntPtr Texture(IDirect3DSurface surface)
    {
        var unknown = ((IWinRTObject)surface).NativeObject.ThisPtr;
        if (Marshal.QueryInterface(unknown, in IID_IDirect3DDxgiInterfaceAccess, out var access) < 0) return IntPtr.Zero;
        IntPtr texture;
        Guid tid = IID_ID3D11Texture2D;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)(*(void***)access)[3])(access, &tid, &texture); // GetInterface
        Marshal.Release(access);
        return hr < 0 ? IntPtr.Zero : texture;
    }

    public void Dispose()
    {
        _stopLater?.Stop();
        _toneLater?.Stop();
        _toneDue?.Stop();
        Stop();
        foreach (var t in _targets) ForgetSurface(t);
        DropGpu();
        if (Current == this) Current = null;
    }
}
