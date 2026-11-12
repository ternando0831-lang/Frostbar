using System.Diagnostics;
using System.Windows.Threading;
using Frostbar.Core.MenuBar;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Shell;
using Microsoft.Win32;

namespace Frostbar.Platform.Sys;

/// <summary>
/// Where the menu bar learns what uses the microphone and the camera now (stage 5-2, the privacy indicator): Windows'
/// records of the apps' use (<see cref="ConsentStorePrivacy"/>, the user's choice "windowsの記録", 2026-10-16), or a
/// spec's shell's own state (<see cref="SpecPrivacy"/>).
/// </summary>
public interface IPrivacySource : IDisposable
{
    /// <summary>What is in use now (UI thread).</summary>
    PrivacyState Current { get; }
    /// <summary>What is in use changed (UI thread).</summary>
    event Action? Changed;
}

/// <summary>
/// A spec's shell's own (FROSTBAR_E2E_PRIVACY=spec, honoured with --diag only): set through the diagnostics pipe
/// (privacy-set), nothing of the PC read.
/// </summary>
public sealed class SpecPrivacy : IPrivacySource
{
    public PrivacyState Current { get; private set; } = PrivacyState.None;
    public event Action? Changed;

    public void Set(PrivacyState state)
    {
        if (state.Same(Current)) return;
        Current = state;
        Changed?.Invoke();
    }

    public void Dispose() { }
}

/// <summary>
/// Windows' records of the apps' use of the microphone and the camera (HKCU\…\CapabilityAccessManager\ConsentStore):
/// an app uses a device while its record has a start and no stop (<see cref="PrivacyRecords"/>). The devices' keys are
/// watched (<see cref="RegistryWatcher"/>: nothing polls); a change is read again 150 ms after the last one, off the UI
/// thread, with the apps' names (a desktop app's file description, a packaged app's display name; remembered). S26:
/// 31 records for the microphone and 3 for the camera on this PC, read in 1.5–13 ms. A spec's shell reads a key of its
/// own (<see cref="SpecStore"/>), never the user's records.
/// </summary>
public sealed class ConsentStorePrivacy : IPrivacySource
{
    public const string WindowsStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    static readonly (string Key, PrivacyKind Kind)[] Devices = [("microphone", PrivacyKind.Microphone), ("webcam", PrivacyKind.Camera)];

    readonly string _store;
    readonly Dispatcher _ui;
    readonly List<RegistryWatcher> _watchers = new();
    readonly Timer _settle;
    readonly object _reading = new();
    readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);
    volatile bool _disposed;

    public PrivacyState Current { get; private set; } = PrivacyState.None;
    public event Action? Changed;

    /// <summary>Diagnostics: the records read so far (each read, after a change), and the last read's time (ms).</summary>
    public int Reads { get; private set; }
    public double LastReadMs { get; private set; } = -1;

    public ConsentStorePrivacy(Dispatcher ui, string? store = null)
    {
        _ui = ui;
        _store = store ?? WindowsStore;
        _settle = new Timer(_ => Read());
        foreach (var (key, _) in Devices)
            if (RegistryWatcher.Open(_store + "\\" + key, () => _settle.Change(150, Timeout.Infinite)) is { } watcher) _watchers.Add(watcher);
            else Log.Info($"privacy: {key} has no records to watch");
        _settle.Change(0, Timeout.Infinite); // the first read, off the UI thread
    }

    /// <summary>A spec's shell's own store: a key under the specs' own prefix (StartupStore's), else null.</summary>
    public static string? SpecStore(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        key = key.Trim().Trim('\\');
        return key.StartsWith(StartupStore.SpecKeyPrefix, StringComparison.OrdinalIgnoreCase) ? key : null;
    }

    void Read()
    {
        if (_disposed) return;
        lock (_reading)
        {
            var sw = Stopwatch.StartNew();
            var records = new List<PrivacyRecord>();
            try
            {
                foreach (var (key, kind) in Devices)
                {
                    using var device = Registry.CurrentUser.OpenSubKey(_store + "\\" + key);
                    if (device == null) continue;
                    AddRecords(device, kind, packaged: true, records);
                    using var desktop = device.OpenSubKey("NonPackaged");
                    if (desktop != null) AddRecords(desktop, kind, packaged: false, records);
                }
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                Log.Debug("privacy: the records unread: " + ex.Message);
            }
            var state = PrivacyRecords.State(records, NameOf);
            double ms = sw.Elapsed.TotalMilliseconds;
            _ui.BeginInvoke(() =>
            {
                Reads++;
                LastReadMs = ms;
                if (_disposed || state.Same(Current)) return;
                Current = state;
                Log.Info($"privacy: microphone {state.Microphone.Count}, camera {state.Camera.Count} app(s) in use");
                Changed?.Invoke();
            });
        }
    }

    static void AddRecords(RegistryKey parent, PrivacyKind kind, bool packaged, List<PrivacyRecord> records)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            if (packaged && name == "NonPackaged") continue;
            using var app = parent.OpenSubKey(name);
            if (app == null) continue;
            long start = app.GetValue("LastUsedTimeStart") is long s ? s : 0, stop = app.GetValue("LastUsedTimeStop") is long e ? e : 0;
            if (start != 0 || stop != 0) records.Add(new PrivacyRecord(kind, name, packaged, start, stop));
        }
    }

    /// <summary>The name an app shows (remembered: an app in use is read again at each change).</summary>
    string NameOf(PrivacyRecord r)
    {
        if (_names.TryGetValue(r.App, out var known)) return known;
        string name = r.Packaged ? PackageDisplayName(r.App) : ProgramName(PrivacyRecords.ProgramOf(r.App));
        _names[r.App] = name;
        return name;
    }

    static string ProgramName(string program)
    {
        try
        {
            if (File.Exists(program))
            {
                var info = FileVersionInfo.GetVersionInfo(program);
                if (!string.IsNullOrWhiteSpace(info.FileDescription)) return info.FileDescription.Trim();
                if (!string.IsNullOrWhiteSpace(info.ProductName)) return info.ProductName.Trim();
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Path.GetFileNameWithoutExtension(program);
    }

    static string PackageDisplayName(string familyName)
    {
        try
        {
            var package = new Windows.Management.Deployment.PackageManager().FindPackagesForUser("", familyName).FirstOrDefault();
            if (package != null && !string.IsNullOrWhiteSpace(package.DisplayName)) return package.DisplayName;
        }
        catch (Exception ex) { Log.Debug($"privacy: no display name for a package: {ex.Message}"); }
        return PrivacyRecords.PackageName(familyName);
    }

    public void Dispose()
    {
        _disposed = true;
        _settle.Dispose();
        foreach (var w in _watchers) w.Dispose();
    }
}
