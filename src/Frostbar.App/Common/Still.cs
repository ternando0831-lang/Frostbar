using System.Windows.Threading;

namespace Frostbar.App.Common;

/// <summary>
/// Work that can wait (the start-up's warm-ups, the settings index) waits until nothing moves: a page's 40–60 ms held a
/// list's glide (2026-10-17, frame-pace), and a warm-up's 100 ms or the settings index's window held a Dock launch's
/// landing (2026-10-18, dock-launch: 305 and 349 ms after the window, the limit 300).
/// </summary>
public static class Still
{
    /// <summary>Something moves now: a list glides, a window fades, a part tweens, a Dock icon bounces.</summary>
    public static bool Moving => SmoothScroll.Busy || Fades.Busy || Tween.Busy || Frostbar.App.Dock.DockSurface.AnyBouncing;

    /// <summary>Runs <paramref name="work"/> in an idle moment of the UI thread when nothing moves (looked at again every 100 ms).</summary>
    public static void Then(Dispatcher d, Action work) => d.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
    {
        if (!Moving) { work(); return; }
        var later = new DispatcherTimer(DispatcherPriority.ApplicationIdle, d) { Interval = TimeSpan.FromMilliseconds(100) };
        later.Tick += (_, _) => { later.Stop(); Then(d, work); };
        later.Start();
    });
}
