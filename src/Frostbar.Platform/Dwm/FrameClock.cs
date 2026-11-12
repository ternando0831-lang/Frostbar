using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using Frostbar.Platform.Infrastructure;

namespace Frostbar.Platform.Dwm;

/// <summary>
/// Ticks on the UI thread once per display frame while started (ADR-007). WPF's CompositionTarget.Rendering only
/// ticks every frame while WPF itself has something to draw; the Dock's pictures are composition visuals, so with
/// nothing for WPF to draw it ticked every second frame or worse (2026-10-12: p95 33 ms). A background thread waits
/// for each DWM frame (DwmFlush, as in M0 S11) and asks the UI thread for one tick; ticks never queue up behind a busy
/// UI thread.
/// </summary>
public sealed class FrameClock : IDisposable
{
    [DllImport("dwmapi.dll")] static extern int DwmFlush();

    readonly Dispatcher _ui;
    readonly ManualResetEventSlim _run = new(false);
    readonly Thread _thread;
    readonly Action _onTick;
    volatile bool _disposed;
    int _pending;
    long _last;

    /// <summary>Seconds since the previous tick (the first tick after <see cref="Start"/> reports one frame of 60 Hz).</summary>
    public event Action<double>? Tick;

    public bool Running => _run.IsSet;

    public FrameClock(Dispatcher ui)
    {
        _ui = ui;
        _onTick = OnTick;
        _thread = new Thread(Loop) { IsBackground = true, Name = "frame clock" };
        _thread.Start();
    }

    public void Start()
    {
        if (_run.IsSet) return;
        _last = 0;
        _run.Set();
    }

    public void Stop() => _run.Reset();

    void Loop()
    {
        var idle = Stopwatch.StartNew();
        while (!_disposed)
        {
            _run.Wait();
            if (_disposed) return;
            idle.Restart();
            int hr = DwmFlush();
            // DWM had nothing to compose (or composition is off): do not spin, keep about 60 Hz.
            if (hr != 0 || idle.Elapsed.TotalMilliseconds < 2) Thread.Sleep(hr != 0 ? 16 : 4);
            if (!_run.IsSet) continue;
            if (Interlocked.Exchange(ref _pending, 1) == 0)
            {
                try { _ui.BeginInvoke(DispatcherPriority.Render, _onTick); }
                catch (Exception ex) { Log.Debug("frame clock: " + ex.Message); return; }
            }
        }
    }

    void OnTick()
    {
        Interlocked.Exchange(ref _pending, 0);
        if (!_run.IsSet) return;
        long now = Stopwatch.GetTimestamp();
        double dt = _last == 0 ? 1 / 60.0 : (now - _last) / (double)Stopwatch.Frequency;
        _last = now;
        Tick?.Invoke(dt);
    }

    public void Dispose()
    {
        _disposed = true;
        _run.Set(); // let the thread see it (the event is not disposed: the thread may still be on its way out)
        _thread.Join(500);
    }
}
