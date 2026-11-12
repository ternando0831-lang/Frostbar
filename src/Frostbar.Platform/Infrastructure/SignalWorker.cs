namespace Frostbar.Platform.Infrastructure;

/// <summary>
/// One background thread for work that may wait on another process (a message a hung app answers late): it runs the
/// work once per signal, and signals that come while it runs make one more run. Such waits stay off the
/// <see cref="BackgroundTicker"/>, whose quick checks (the hot corners' 16 ms) must not be held up (2026-10-15: the
/// IME's two 100 ms messages, the taskbar's state through Explorer).
/// </summary>
public sealed class SignalWorker : IDisposable
{
    readonly AutoResetEvent _go = new(false);
    volatile bool _stop;

    /// <summary>OS id of the thread (diagnostics).</summary>
    public uint ThreadId { get; private set; }

    public SignalWorker(string name, Action work)
    {
        var thread = new Thread(() =>
        {
            ThreadId = Interop.Native.GetCurrentThreadId();
            while (_go.WaitOne() && !_stop)
            {
                try { work(); }
                catch (Exception ex) { Log.Warn($"{name} failed", ex); }
            }
        }) { IsBackground = true, Name = name, Priority = ThreadPriority.BelowNormal };
        thread.Start();
    }

    public void Signal() => _go.Set();

    public void Dispose()
    {
        _stop = true;
        _go.Set();
    }
}
