using System.Diagnostics;
using System.Windows.Threading;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.Platform.Dwm;

/// <summary>
/// A live picture of one window, drawn by DWM inside another window (the window overview, M7). DWM draws it above the
/// destination window's own content, in the destination's client pixels; layered destinations work too (M0 S11,
/// checked again 2026-10-12). Only the window's visible frame is shown (not its invisible resize borders), so a
/// thumbnail placed on the window's frame bounds covers the window exactly. Updating it is cheap and thread-safe.
/// </summary>
public sealed class Thumbnail : IDisposable
{
    readonly IntPtr _handle;
    readonly RECT? _source;
    bool _disposed;

    public IntPtr Source { get; }

    Thumbnail(IntPtr handle, IntPtr source, RECT? visible)
    {
        _handle = handle;
        Source = source;
        _source = visible;
    }

    public static Thumbnail? Create(IntPtr destination, IntPtr source)
    {
        int hr = Native.DwmRegisterThumbnail(destination, source, out var h);
        if (hr != 0) { Log.Debug($"thumbnail: register failed 0x{hr:X8}"); return null; }
        // The visible frame inside the window rectangle (the resize borders around it are invisible on Windows 11).
        Native.GetWindowRect(source, out var wr);
        var fb = Native.FrameBounds(source);
        var visible = new RECT(fb.Left - wr.Left, fb.Top - wr.Top, fb.Right - wr.Left, fb.Bottom - wr.Top);
        if (visible.Width <= 0 || visible.Height <= 0) visible = new RECT(0, 0, wr.Width, wr.Height);
        return new Thumbnail(h, source, visible);
    }

    /// <summary>
    /// The whole window, not cut to its frame: a minimized window has no frame to read (it stands at -32000), and its
    /// thumbnail still shows it as it was, at its size from before (M0 S19).
    /// </summary>
    public static Thumbnail? CreateWhole(IntPtr destination, IntPtr source)
    {
        int hr = Native.DwmRegisterThumbnail(destination, source, out var h);
        if (hr != 0) { Log.Debug($"thumbnail: register failed 0x{hr:X8}"); return null; }
        return new Thumbnail(h, source, null);
    }

    /// <summary>The size DWM gives the source (pixels; a minimized window's from before it was minimized), or 0×0.</summary>
    public (int Width, int Height) SourceSize =>
        !_disposed && Native.DwmQueryThumbnailSourceSize(_handle, out var s) == 0 ? (s.cx, s.cy) : (0, 0);

    /// <summary>Shows it at <paramref name="dest"/> (pixels in the destination window).</summary>
    public void Show(RECT dest, byte opacity = 255)
    {
        if (_disposed) return;
        uint cut = _source != null ? Native.DWM_TNP_RECTSOURCE : 0;
        var p = new DWM_THUMBNAIL_PROPERTIES
        {
            dwFlags = Native.DWM_TNP_RECTDESTINATION | cut | Native.DWM_TNP_VISIBLE | Native.DWM_TNP_OPACITY | Native.DWM_TNP_SOURCECLIENTAREAONLY,
            rcDestination = dest, rcSource = _source ?? default, opacity = opacity, fVisible = true, fSourceClientAreaOnly = false,
        };
        Native.DwmUpdateThumbnailProperties(_handle, ref p);
    }

    public void Hide()
    {
        if (_disposed) return;
        var p = new DWM_THUMBNAIL_PROPERTIES { dwFlags = Native.DWM_TNP_VISIBLE, fVisible = false };
        Native.DwmUpdateThumbnailProperties(_handle, ref p);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Native.DwmUnregisterThumbnail(_handle);
    }
}

/// <summary>
/// Moves thumbnails between two sets of rectangles, one step per DWM frame on a thread of its own paced by DwmFlush:
/// driven from WPF's render loop they moved at 30 fps, from here at 60 (M0 S11). A new run takes over from the one
/// going on; <c>done</c> runs on the UI thread when a run ends without being taken over.
/// </summary>
public sealed class ThumbnailAnimator
{
    readonly Dispatcher _ui;
    int _generation;

    public ThumbnailAnimator(Dispatcher ui) => _ui = ui;

    public bool Running { get; private set; }
    /// <summary>
    /// The last finished run's steps in ms, on the refresh grid: one refresh per frame shown, two where a refresh went
    /// by without an update (diagnostics: the specs judge on these).
    /// </summary>
    public IReadOnlyList<double> LastIntervals { get; private set; } = [];
    /// <summary>The same run's times between DwmFlush returns, as measured (diagnostics).</summary>
    public IReadOnlyList<double> LastRawIntervals { get; private set; } = [];

    /// <param name="ease">0–1 → 0–1 (the share of the way at that point of the time).</param>
    /// <param name="fade">
    /// Accessibility's reduced motion (2026-10-22): the thumbnails stand at their <c>to</c> rectangles and fade in (1) or
    /// out (-1) instead of moving; 0 moves them.
    /// </param>
    public void Run(IReadOnlyList<(Thumbnail thumb, RECT from, RECT to)> items, double ms, Func<double, double> ease, Action? done, int fade = 0)
    {
        int gen = Interlocked.Increment(ref _generation);
        Running = true;
        double period = 1000.0 / Native.RefreshHz();
        var thread = new Thread(() =>
        {
            var steps = new List<double>();
            var raw = new List<double>();
            var sw = Stopwatch.StartNew();
            double last = -1;
            long lastFrame = -1;
            while (Volatile.Read(ref _generation) == gen)
            {
                double now = sw.Elapsed.TotalMilliseconds;
                if (last >= 0) raw.Add(now - last);
                last = now;
                // Positions for the refresh this update will be shown at, not for the moment this thread woke: DwmFlush
                // returns 21 and 12 ms apart in turn while DWM still shows one update per refresh (2026-10-12), so
                // positions taken at the wake-up time were up to ±4.5 ms of movement off (most early in the move).
                long frame = (long)Math.Round(now / period);
                if (lastFrame >= 0 && frame <= lastFrame) frame = lastFrame + 1; // woken twice within a refresh
                if (lastFrame >= 0) steps.Add((frame - lastFrame) * period);
                lastFrame = frame;
                double t = ms <= 0 ? 1 : Math.Min(1, frame * period / ms);
                double k = ease(t);
                if (fade != 0)
                {
                    byte alpha = (byte)Math.Round(255 * (fade > 0 ? k : 1 - k));
                    foreach (var (thumb, _, to) in items) thumb.Show(to, alpha);
                }
                else foreach (var (thumb, from, to) in items) thumb.Show(Lerp(from, to, k));
                if (t >= 1) break;
                if (Native.DwmFlush() != 0) Thread.Sleep(16); // composition off: keep a pace anyway
            }
            _ui.BeginInvoke(() =>
            {
                if (Volatile.Read(ref _generation) != gen) return;
                Running = false;
                LastIntervals = steps;
                LastRawIntervals = raw;
                done?.Invoke();
            });
        }) { IsBackground = true, Name = "thumbnails", Priority = ThreadPriority.AboveNormal };
        thread.Start();
    }

    /// <summary>Stops the run going on where it is (its <c>done</c> does not run).</summary>
    public void Stop()
    {
        Interlocked.Increment(ref _generation);
        Running = false;
    }

    public static RECT Lerp(RECT a, RECT b, double k) => new(
        (int)Math.Round(a.Left + (b.Left - a.Left) * k), (int)Math.Round(a.Top + (b.Top - a.Top) * k),
        (int)Math.Round(a.Right + (b.Right - a.Right) * k), (int)Math.Round(a.Bottom + (b.Bottom - a.Bottom) * k));
}
