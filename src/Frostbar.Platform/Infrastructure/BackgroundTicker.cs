namespace Frostbar.Platform.Infrastructure;

/// <summary>
/// One plain background thread for cheap periodic checks. A WPF DispatcherTimer wakes the UI thread through WM_TIMER
/// and a posted queue message, about 0.35 ms each: a 300 ms timer alone was 0.25% of a core while idle (2026-10-12).
/// Checks here must be quick and thread-safe, and post to the UI thread only when something changed.
/// </summary>
public sealed class BackgroundTicker : IDisposable
{
    sealed class Job(string name, TimeSpan every, Action run)
    {
        public readonly string Name = name;
        public TimeSpan Every = every; // guarded by _jobs
        public readonly Action Run = run;
        public long Due;
        public volatile bool Now;
        public long Runs, Ticks; // diagnostics (written by the ticker thread only)
    }

    /// <summary>Diagnostics: each job's runs and the time they took (Stopwatch ticks → ms).</summary>
    public IReadOnlyList<(string name, long runs, double ms)> Costs()
    {
        lock (_jobs) return _jobs.Select(j => (j.Name, j.Runs, j.Ticks * 1000.0 / System.Diagnostics.Stopwatch.Frequency)).ToList();
    }

    readonly List<Job> _jobs = new();
    readonly AutoResetEvent _wake = new(false);
    readonly Thread _thread;
    volatile bool _stop;
    /// <summary>OS id of the ticker thread (diagnostics).</summary>
    public uint ThreadId { get; private set; }

    public BackgroundTicker()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "ticker", Priority = ThreadPriority.BelowNormal };
        _thread.Start();
    }

    /// <summary>Runs <paramref name="run"/> every <paramref name="every"/>; the returned action runs it as soon as possible.</summary>
    public Action Add(string name, TimeSpan every, Action run)
    {
        var job = new Job(name, every, run) { Due = NextDue(Environment.TickCount64, every) };
        lock (_jobs) _jobs.Add(job);
        _wake.Set();
        return () => { job.Now = true; _wake.Set(); };
    }

    public void Remove(string name)
    {
        lock (_jobs) _jobs.RemoveAll(j => j.Name == name);
    }

    /// <summary>Runs job <paramref name="name"/> every <paramref name="every"/> from now on (also from inside the job).</summary>
    public void Retime(string name, TimeSpan every)
    {
        lock (_jobs)
            foreach (var j in _jobs.Where(j => j.Name == name))
            {
                j.Every = every;
                j.Due = NextDue(Environment.TickCount64, every);
            }
        _wake.Set();
    }

    /// <summary>
    /// The next run of a job of period <paramref name="every"/>. Periods of 100 ms and more (all multiples of 100 ms)
    /// fall on multiples of their own period, so jobs added at different times wake the thread together. Each timed
    /// from its own start, the two 100 ms jobs and the 400 ms one woke it apart (2026-11-08 idle-cpu, 60 s: ime 546
    /// runs, hot-corners 546, foreground 148). Lined up, the ticker thread took 0.055% of a core instead of 0.114% (the
    /// same spec). The period stays; only the first run comes sooner, by less than a period.
    /// The quick 16 ms looks keep their own time.
    /// </summary>
    static long NextDue(long now, TimeSpan every)
    {
        long ms = (long)every.TotalMilliseconds;
        return ms >= 100 ? (now / ms + 1) * ms : now + ms;
    }

    void Loop()
    {
        ThreadId = Interop.Native.GetCurrentThreadId();
        while (!_stop)
        {
            long now = Environment.TickCount64, next = now + 60_000;
            Job[] due;
            lock (_jobs)
            {
                due = _jobs.Where(j => j.Now || j.Due <= now).ToArray();
                foreach (var j in due) { j.Now = false; j.Due = NextDue(now, j.Every); }
                foreach (var j in _jobs) next = Math.Min(next, j.Due);
            }
            foreach (var j in due)
            {
                long start = System.Diagnostics.Stopwatch.GetTimestamp();
                try { j.Run(); }
                catch (Exception ex) { Log.Warn($"ticker job {j.Name} failed", ex); }
                j.Runs++;
                j.Ticks += System.Diagnostics.Stopwatch.GetTimestamp() - start;
            }
            int wait = (int)Math.Clamp(next - Environment.TickCount64, 1, 60_000);
            _wake.WaitOne(wait);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        _thread.Join(1000);
    }
}
