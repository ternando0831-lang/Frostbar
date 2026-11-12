namespace Frostbar.Core.Concurrency;

/// <summary>
/// Applies only the latest value asked for, one call at a time, away from the caller's thread: a slider dragged across
/// a slow setting (the screen's brightness through WMI) ends on the value it was let go at, without every step in
/// between queueing up behind it.
/// </summary>
public sealed class LatestValueWorker<T>
{
    readonly object _gate = new();
    readonly Action<T> _apply;
    readonly Action<Action> _start;
    readonly Action<Exception>? _failed;
    readonly ManualResetEventSlim _idle = new(true);
    T _next = default!;
    bool _hasNext, _running;

    /// <param name="apply">Makes the change (on the worker).</param>
    /// <param name="start">How the worker starts (default: the thread pool; tests run it at once).</param>
    /// <param name="failed">Told when <paramref name="apply"/> throws; the worker goes on with the next value.</param>
    public LatestValueWorker(Action<T> apply, Action<Action>? start = null, Action<Exception>? failed = null)
    {
        _apply = apply;
        _start = start ?? (work => Task.Run(work));
        _failed = failed;
    }

    public void Set(T value)
    {
        lock (_gate)
        {
            _next = value;
            _hasNext = true;
            if (_running) return; // the worker picks it up when its current call returns
            _running = true;
            _idle.Reset();
        }
        _start(Loop);
    }

    /// <summary>Waits until every value asked for has been applied: false when <paramref name="timeout"/> ran out first.</summary>
    public bool Wait(TimeSpan timeout) => _idle.Wait(timeout);

    void Loop()
    {
        while (true)
        {
            T value;
            lock (_gate)
            {
                if (!_hasNext) { _running = false; _idle.Set(); return; }
                value = _next;
                _next = default!;
                _hasNext = false;
            }
            try { _apply(value); }
            catch (Exception ex) { _failed?.Invoke(ex); }
        }
    }
}
