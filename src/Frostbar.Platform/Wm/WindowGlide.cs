using System.Diagnostics;
using System.Windows.Threading;
using Frostbar.Platform.Dwm;
using Frostbar.Platform.Interop;
using Motion = Frostbar.Core.Shell.MotionTable;

namespace Frostbar.Platform.Wm;

/// <summary>
/// Moves another app's window into place over a few display frames, easing out (macOS's tiling glides a window there;
/// the jump looked abrupt). Only its position glides, at its own size; the last step gives it the new size as well. A
/// window resized at every frame showed black where the app had not painted yet (2026-10-12, filmed: the test app's
/// new edges were black for most of the way); moved, it keeps what it drew. Each step is an asynchronous SetWindowPos,
/// so a slow app never holds up the UI thread. A window asked to glide again starts from where it is.
/// </summary>
public static class WindowGlide
{
    public const double Ms = Motion.WindowGlide;

    sealed record Glide(RECT From, RECT To, long Start);

    static readonly Dictionary<IntPtr, Glide> Gliding = new();
    static FrameClock? _clock;

    /// <summary>Glides <paramref name="h"/> to the window rectangle <paramref name="outer"/> (GetWindowRect's, invisible borders included). On the UI thread.</summary>
    public static void To(IntPtr h, RECT outer)
    {
        Native.GetWindowRect(h, out var from);
        if (from.Equals(outer)) return;
        Gliding[h] = new Glide(from, outer, Stopwatch.GetTimestamp());
        if (_clock == null)
        {
            _clock = new FrameClock(Dispatcher.CurrentDispatcher);
            _clock.Tick += _ => Step();
        }
        _clock.Start();
    }

    static void Step()
    {
        long now = Stopwatch.GetTimestamp();
        foreach (var (h, g) in Gliding.ToList())
        {
            double t = Math.Min(1, (now - g.Start) / (double)Stopwatch.Frequency * 1000 / Ms);
            double k = Motion.Out(t);
            int Lerp(int a, int b) => (int)Math.Round(a + (b - a) * k);
            if (!Native.IsWindow(h)) { Gliding.Remove(h); continue; }
            const uint flags = Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER | Native.SWP_ASYNCWINDOWPOS;
            if (t >= 1)
            {
                Native.SetWindowPos(h, IntPtr.Zero, g.To.Left, g.To.Top, g.To.Width, g.To.Height, flags);
                Gliding.Remove(h);
            }
            else Native.SetWindowPos(h, IntPtr.Zero, Lerp(g.From.Left, g.To.Left), Lerp(g.From.Top, g.To.Top), 0, 0, flags | Native.SWP_NOSIZE);
        }
        if (Gliding.Count == 0) _clock?.Stop();
    }
}
