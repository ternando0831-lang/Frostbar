using System.Diagnostics;
using Frostbar.Core.Time;
using Frostbar.Platform.Interop;

namespace Frostbar.App.Common;

/// <summary>
/// A movement's progress on the display's refreshes, ticked by a display-paced clock (FrameClock): from
/// <see cref="Begin"/>, each tick is placed by the refresh it will be shown at, one more than the last at least, so
/// that a movement goes the same way every refresh while the UI thread's own moments are uneven (the thumbnails'
/// way, ThumbnailAnimator). WPF's own animations run on its frame clock, which comes out of its rest unevenly: as a
/// list began to move, its frames came 19, 15, 0 and 37 ms apart (2026-10-17, frame-pace). The steps are Core's
/// <see cref="RefreshSteps"/>: what must agree (a scale across and down) is moved by one of them.
/// </summary>
public sealed class DisplaySteps
{
    long _start;
    RefreshSteps _steps = new();
    public bool Begun { get; private set; }

    public void Begin() => Begin(0);

    /// <summary>Begins <paramref name="earlierMs"/> before now (diagnostics "tween-skew").</summary>
    public void Begin(double earlierMs)
    {
        _start = Stopwatch.GetTimestamp() - (long)(earlierMs * Stopwatch.Frequency / 1000);
        _steps = new();
        Begun = true;
    }

    /// <summary>0 to 1 over <paramref name="ms"/>, for the refresh this tick will be shown at.</summary>
    public double Next(double ms) => _steps.Next(Stopwatch.GetElapsedTime(_start).TotalMilliseconds, Period, ms);

    static double? _period;

    /// <summary>A refresh of the display, in ms.</summary>
    public static double Period => _period ??= 1000.0 / Math.Max(24, Native.RefreshHz());

    /// <summary>The displays changed (a refresh rate too): <see cref="Period"/> is read again at its next use.</summary>
    public static void Reread() => _period = null;
}
