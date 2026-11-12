using System.Management;
using System.Windows.Threading;
using Frostbar.Core.Concurrency;
using Frostbar.Platform.Infrastructure;

namespace Frostbar.Platform.Sys;

/// <summary>
/// The built-in display's brightness (WMI WmiMonitorBrightness, M0 S10), 0–100. Read and written on the thread pool:
/// WMI calls take tens of milliseconds and must not run on the UI thread. Changes from elsewhere (the brightness keys,
/// the quick settings, adaptive brightness) arrive through WmiMonitorBrightnessEvent. External monitors without DDC/CI
/// support through WMI are not covered: <see cref="Available"/> stays false then.
/// </summary>
public sealed class BrightnessService : IDisposable
{
    const int EchoMs = 600;
    readonly Dispatcher _ui;
    readonly LatestValueWorker<int> _setter;
    ManagementEventWatcher? _watcher;
    long _lastSet;
    bool _disposed;

    public bool Available { get; private set; }
    public int Level { get; private set; }
    /// <summary>The level arrived or changed (UI thread).</summary>
    public event Action? Changed;
    /// <summary>How many listen to <see cref="Changed"/> (diagnostics: a closed panel must not stay subscribed).</summary>
    public int Listeners => Changed?.GetInvocationList().Length ?? 0;

    public BrightnessService(Dispatcher ui)
    {
        _ui = ui;
        _setter = new LatestValueWorker<int>(Apply, failed: ex => Log.Warn("brightness could not be set", ex));
        Task.Run(Start);
    }

    void Start()
    {
        try
        {
            bool found = false;
            using (var q = new ManagementObjectSearcher(@"root\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness WHERE Active = TRUE"))
                foreach (ManagementObject m in q.Get())
                    using (m)
                    {
                        int level = Convert.ToInt32(m["CurrentBrightness"]);
                        _ui.BeginInvoke(() => { Available = true; Level = level; Changed?.Invoke(); });
                        found = true;
                        break;
                    }
            // No display with a brightness here: its events would change a level nobody shows (Available stays false),
            // and the watcher kept a WMI connection and its threads for the life of the shell.
            if (!found) { Log.Info("brightness unavailable: no display with a brightness"); return; }
            if (_disposed) return;
            _watcher = new ManagementEventWatcher(new ManagementScope(@"root\WMI"), new EventQuery("SELECT * FROM WmiMonitorBrightnessEvent"));
            _watcher.EventArrived += (_, e) =>
            {
                int level = Convert.ToInt32(e.NewEvent["Brightness"]);
                // Our own changes come back as events too, a step or two behind a dragged slider: those are left out.
                if (Environment.TickCount64 - Interlocked.Read(ref _lastSet) < EchoMs) return;
                _ui.BeginInvoke(() => { Level = level; Changed?.Invoke(); });
            };
            _watcher.Start();
            // Let go meanwhile (its bar made again): Dispose found no watcher yet, so this one would go on for a dead service.
            if (_disposed) { _watcher.Stop(); _watcher.Dispose(); }
        }
        catch (Exception ex) { Log.Info("brightness unavailable: " + ex.Message); }
    }

    /// <summary>Sets the level (the latest wins while a change is under way).</summary>
    public void Set(int level)
    {
        if (!Available) return;
        Level = Math.Clamp(level, 0, 100);
        Interlocked.Exchange(ref _lastSet, Environment.TickCount64);
        _setter.Set(Level);
    }

    static void Apply(int level)
    {
        using var q = new ManagementObjectSearcher(@"root\WMI", "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
        foreach (ManagementObject m in q.Get())
            using (m)
            {
                m.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)level]);
                return;
            }
    }

    public void Dispose()
    {
        _disposed = true;
        try { _watcher?.Stop(); _watcher?.Dispose(); } catch (Exception) { }
    }
}
