using System.IO.MemoryMappedFiles;
using System.Windows.Threading;

namespace Frostbar.Platform.Infrastructure;

/// <summary>
/// The UI thread's pulse for the watchdog: a tick count in shared memory, written by a dispatcher timer every 3 s.
/// WM_NULL pings alone miss a UI thread stuck in a CLR wait (Dispatcher.Invoke, a lock): an STA wait still answers
/// sent messages (2026-10-12: a shutdown hung for good while the watchdog saw the shell as alive). The timer only fires
/// when the dispatcher really works through its queue.
/// </summary>
public sealed class Heartbeat : IDisposable
{
    public const int PeriodMs = 3000;
    readonly MemoryMappedFile _map;
    readonly MemoryMappedViewAccessor _view;
    readonly DispatcherTimer _timer;

    public static string Name(int pid) => $"Local\\{AppPaths.Id}.heartbeat.{pid}";

    public Heartbeat(Dispatcher ui)
    {
        _map = MemoryMappedFile.CreateNew(Name(Environment.ProcessId), 8);
        _view = _map.CreateViewAccessor(0, 8);
        Beat();
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(PeriodMs), DispatcherPriority.Background, (_, _) => Beat(), ui);
        _timer.Start();
    }

    long _last;

    /// <summary>
    /// A beat came more than <see cref="GapMs"/> after the last (ms): the process did not run (the PC slept) or the UI
    /// thread was stuck. The clock goes on while the PC sleeps; the timer fires once it runs again. UI thread.
    /// </summary>
    public event Action<long>? Gap;

    public const int GapMs = 2 * PeriodMs + 1000;

    /// <summary>Beats now (after a resume, so the watchdog sees the UI thread at once).</summary>
    public void Beat()
    {
        long now = Environment.TickCount64, gap = _last == 0 ? 0 : now - _last;
        _view.Write(0, now);
        _last = now; // before Gap: its handler may beat again
        if (gap > GapMs) Gap?.Invoke(gap);
    }

    public void Dispose()
    {
        _timer.Stop();
        _view.Dispose();
        _map.Dispose();
    }

    /// <summary>The watchdog's side: the tick count (Environment.TickCount64, system-wide) of the last beat.</summary>
    public sealed class Reader : IDisposable
    {
        readonly MemoryMappedFile _map;
        readonly MemoryMappedViewAccessor _view;

        Reader(MemoryMappedFile map)
        {
            _map = map;
            _view = map.CreateViewAccessor(0, 8, MemoryMappedFileAccess.Read);
        }

        /// <summary>Null while the shell has not created it yet (or an older shell never does).</summary>
        public static Reader? Open(int pid)
        {
            try { return new Reader(MemoryMappedFile.OpenExisting(Name(pid), MemoryMappedFileRights.Read)); }
            catch (Exception) { return null; }
        }

        public long LastBeat => _view.ReadInt64(0);

        public void Dispose()
        {
            _view.Dispose();
            _map.Dispose();
        }
    }
}
