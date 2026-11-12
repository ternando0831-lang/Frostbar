using System.Windows.Threading;
using Frostbar.Platform.Infrastructure;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace Frostbar.Platform.Sys;

public enum BluetoothKind { Other, Audio, Mouse, Keyboard, Phone, Computer, Gamepad }

/// <summary>
/// A paired Bluetooth device. <see cref="Container"/> is the device's container ID (shared with its audio endpoints);
/// <see cref="CanConnect"/> when it can be connected and disconnected here (<see cref="BluetoothAudio"/>).
/// </summary>
public sealed record BluetoothItem(string Id, string Name, BluetoothKind Kind, bool Connected, Guid? Container = null, bool CanConnect = false);

/// <summary>
/// Paired Bluetooth devices (classic and Low Energy) and whether each is connected, kept up to date by device
/// watchers. Audio devices are connected and disconnected through their audio drivers (<see cref="BluetoothAudio"/>);
/// pairing stays with Windows' settings. Events come on WinRT threads and are marshalled to the UI.
/// </summary>
public sealed class BluetoothService : IDisposable
{
    const string IsConnectedKey = "System.Devices.Aep.IsConnected", ContainerKey = "System.Devices.Aep.ContainerId";
    // How long a device may take to reach the new state. On the test PC: connected about 1 s after the request,
    // disconnected about 3 s after (as Windows reports it). A device that will not take the PC never connects.
    static readonly TimeSpan ConnectWait = TimeSpan.FromSeconds(20), DisconnectWait = TimeSpan.FromSeconds(10);

    readonly Dispatcher _ui;
    readonly Dictionary<string, BluetoothItem> _items = new(StringComparer.OrdinalIgnoreCase); // guarded by itself
    readonly List<DeviceWatcher> _watchers = new();
    int _made, _shown; // the order lists were made in (under _items) and the last one shown (UI thread)
    bool _disposed;
    // UI thread: the devices being connected (true) or disconnected (false), and who waits for which state.
    readonly Dictionary<Guid, bool> _busy = new();
    readonly List<(Guid Container, bool Connected, TaskCompletionSource Reached)> _waiters = new();

    public IReadOnlyList<BluetoothItem> Devices { get; private set; } = [];
    /// <summary>The list, a connection or a request in progress changed (UI thread).</summary>
    public event Action? Changed;
    /// <summary>How many listen to <see cref="Changed"/> (diagnostics: a closed panel must not stay subscribed).</summary>
    public int Listeners => Changed?.GetInvocationList().Length ?? 0;

    public BluetoothService(Dispatcher ui)
    {
        _ui = ui;
        string[] props = [IsConnectedKey, ContainerKey];
        foreach (var selector in new[] { BluetoothDevice.GetDeviceSelectorFromPairingState(true), BluetoothLEDevice.GetDeviceSelectorFromPairingState(true) })
        {
            try
            {
                var w = DeviceInformation.CreateWatcher(selector, props, DeviceInformationKind.AssociationEndpoint);
                Windows.Foundation.TypedEventHandler<DeviceWatcher, DeviceInformation> added = (sender, d) => { _ = AddAsync(d); };
                Windows.Foundation.TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> updated = (_, u) => Update(u);
                Windows.Foundation.TypedEventHandler<DeviceWatcher, DeviceInformationUpdate> removed = (_, u) => { lock (_items) _items.Remove(u.Id); Publish(); };
                w.Added += added;
                w.Updated += updated;
                w.Removed += removed;
                // Taken off at Dispose (the watcher holds them and they hold this service: one of each menu bar made
                // again stayed, 2026-11-06 review).
                _unhook.Add(() => { w.Added -= added; w.Updated -= updated; w.Removed -= removed; });
                w.Start();
                _watchers.Add(w);
            }
            catch (Exception ex) { Log.Info("bluetooth: no device watcher: " + ex.Message); }
        }
    }

    async Task AddAsync(DeviceInformation d)
    {
        if (string.IsNullOrWhiteSpace(d.Name)) return;
        bool connected = d.Properties.TryGetValue(IsConnectedKey, out var c) && c is true;
        Guid? container = d.Properties.TryGetValue(ContainerKey, out var ct) && ct is Guid g ? g : null;
        var kind = BluetoothKind.Other;
        try
        {
            using var device = await BluetoothDevice.FromIdAsync(d.Id);
            if (device != null) kind = KindOf(device.ClassOfDevice);
        }
        catch (Exception) { } // a Low Energy device: no class of device
        if (kind == BluetoothKind.Other)
        {
            // A Low Energy device says what it is in its appearance (2026-10-12: a Logitech mouse showed as "other").
            try
            {
                using var le = await BluetoothLEDevice.FromIdAsync(d.Id);
                if (le != null) kind = KindOf(le.Appearance);
            }
            catch (Exception) { }
        }
        if (kind == BluetoothKind.Other) kind = Guess(d.Name);
        lock (_items)
        {
            if (_pending.Remove(d.Id, out var later)) connected = later;
            _items[d.Id] = new BluetoothItem(d.Id, d.Name, kind, connected, container);
        }
        Publish();
    }

    // A connection change can arrive while the device is still being added (its kind is read asynchronously).
    readonly Dictionary<string, bool> _pending = new(StringComparer.OrdinalIgnoreCase); // guarded by _items

    void Update(DeviceInformationUpdate u)
    {
        if (!u.Properties.TryGetValue(IsConnectedKey, out var c)) return;
        lock (_items)
        {
            if (!_items.TryGetValue(u.Id, out var it)) { _pending[u.Id] = c is true; return; }
            _items[u.Id] = it with { Connected = c is true };
        }
        Publish();
    }

    /// <summary>Makes the list (on the watcher's thread: which devices can be connected is read from Core Audio) and shows it on the UI thread.</summary>
    void Publish()
    {
        if (_disposed) return;
        var audio = BluetoothAudio.Connectable(); // before the lock: it asks Core Audio
        List<BluetoothItem> list;
        int made;
        lock (_items)
        {
            list = _items.Values
                .Select(i => i with { CanConnect = i.Container is Guid g && audio.Contains(g) })
                .GroupBy(i => i.Name).Select(g => g.OrderByDescending(i => i.Connected).ThenBy(i => i.Kind == BluetoothKind.Other).First()) // a device paired both ways shows once
                .OrderByDescending(i => i.Connected).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            made = ++_made;
        }
        _ui.BeginInvoke(() =>
        {
            if (made < _shown) return; // a later list is shown already (watchers' threads race)
            _shown = made;
            var before = Devices.Where(d => d.Connected).Select(d => d.Container).ToHashSet();
            Devices = list;
            Settle();
            Changed?.Invoke();
            // A device that just connected reports its level a moment later.
            if (list.Any(d => d.Connected && !before.Contains(d.Container)))
                _ = Task.Delay(3000).ContinueWith(_ => _ui.BeginInvoke(() => _ = ReadBatteriesAsync()));
        });
    }

    // ---- battery levels ---------------------------------------------------------------------------------------
    // Windows keeps a connected device's battery level on its device nodes (the property its Bluetooth settings show;
    // a pair of earbuds has one on the device and one on its hands-free node). Read on demand: the views ask when they
    // open, and again a few seconds after a device connects (the level comes after the connection).

    const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    Dictionary<Guid, int> _batteries = new();
    int _reading;

    static int? Level(Windows.Devices.Enumeration.Pnp.PnpObject n) =>
        n.Properties.TryGetValue(BatteryKey, out var v) && v switch { byte b => (int?)b, int i => i, uint u => (int)u, _ => null } is int x && x is >= 0 and <= 100 ? x : null;

    /// <summary>The level in percent of a connected device that reports one, or null (UI thread).</summary>
    public int? BatteryOf(BluetoothItem d) => d.Connected && d.Container is Guid g && _batteries.TryGetValue(g, out int v) ? v : null;

    /// <summary>
    /// The level of the connected device in <paramref name="container"/> (an audio output's device: the menu bar's
    /// volume item shows a headset's), or null (UI thread).
    /// </summary>
    public int? BatteryOfContainer(Guid container) =>
        Devices.Any(d => d.Connected && d.Container == container) && _batteries.TryGetValue(container, out int v) ? v : null;

    /// <summary>A connected device is in <paramref name="container"/> (UI thread).</summary>
    public bool Connects(Guid container) => Devices.Any(d => d.Connected && d.Container == container);

    /// <summary>Reads the connected devices' battery levels; <see cref="Changed"/> when they differ from what was shown (UI thread).</summary>
    public async Task ReadBatteriesAsync()
    {
        var devices = Devices.Where(d => d.Connected && d.Container != null).ToList();
        if (devices.Count == 0 || Interlocked.Exchange(ref _reading, 1) == 1) return;
        try
        {
            string filter = string.Join(" OR ", devices.Select(d => $"System.Devices.ContainerId:=\"{d.Container:B}\""));
            var nodes = await Windows.Devices.Enumeration.Pnp.PnpObject.FindAllAsync(Windows.Devices.Enumeration.Pnp.PnpObjectType.Device,
                [BatteryKey, "System.ItemNameDisplay", "System.Devices.ContainerId"], filter);
            var levels = new Dictionary<Guid, int>();
            foreach (var d in devices)
            {
                var mine = nodes.Where(n => n.Properties.TryGetValue("System.Devices.ContainerId", out var c) && c is Guid g && g == d.Container
                                            && Level(n) != null).ToList();
                if (mine.Count == 0) continue;
                // The node named as the device first (the one Windows' settings show), else the lowest.
                var named = mine.FirstOrDefault(n => n.Properties.TryGetValue("System.ItemNameDisplay", out var name) && string.Equals(name as string, d.Name, StringComparison.CurrentCultureIgnoreCase));
                levels[d.Container!.Value] = named != null ? Level(named)!.Value : mine.Min(n => Level(n)!.Value);
            }
            if (levels.Count == _batteries.Count && levels.All(kv => _batteries.TryGetValue(kv.Key, out int v) && v == kv.Value)) return;
            _batteries = levels;
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Debug("bluetooth: battery levels: " + ex.Message); }
        finally { Interlocked.Exchange(ref _reading, 0); }
    }

    /// <summary>Whether a request for <paramref name="d"/> is in progress, and which (UI thread).</summary>
    public bool IsBusy(BluetoothItem d, out bool connecting)
    {
        connecting = false;
        return d.Container is Guid g && _busy.TryGetValue(g, out connecting);
    }

    /// <summary>
    /// Connects or disconnects <paramref name="d"/> (one that <see cref="BluetoothItem.CanConnect"/>), as macOS's
    /// Bluetooth menu does on a click (UI thread). True once Windows reports the device in the new state; false when the
    /// drivers took no request or the device did not follow in time (out of reach, or with another host).
    /// </summary>
    public async Task<bool> SetConnectedAsync(BluetoothItem d, bool connect)
    {
        if (d.Container is not Guid container || !d.CanConnect || _busy.ContainsKey(container)) return false;
        _busy[container] = connect;
        Changed?.Invoke();
        var waiter = (container, connect, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        _waiters.Add(waiter);
        bool done = false;
        try
        {
            int took = await Task.Run(() => BluetoothAudio.Request(container, connect));
            if (took > 0)
            {
                Settle(); // a change that came while the request was being sent
                done = await Task.WhenAny(waiter.Item3.Task, Task.Delay(connect ? ConnectWait : DisconnectWait)) == waiter.Item3.Task;
            }
        }
        catch (Exception ex) { Log.Warn("bluetooth: the request could not be sent", ex); }
        finally
        {
            _waiters.Remove(waiter);
            _busy.Remove(container);
        }
        Log.Info($"bluetooth: {(connect ? "connect" : "disconnect")} {(done ? "done" : "did not happen")}");
        Changed?.Invoke();
        return done;
    }

    /// <summary>Lets go of the requests whose device reached the state asked for (UI thread).</summary>
    void Settle()
    {
        foreach (var (container, connected, reached) in _waiters)
            if (Devices.Any(x => x.Container == container && x.Connected) == connected) reached.TrySetResult();
    }

    static BluetoothKind KindOf(BluetoothClassOfDevice? c) => c?.MajorClass switch
    {
        BluetoothMajorClass.AudioVideo => BluetoothKind.Audio,
        BluetoothMajorClass.Phone => BluetoothKind.Phone,
        BluetoothMajorClass.Computer => BluetoothKind.Computer,
        BluetoothMajorClass.Peripheral => c.MinorClass switch
        {
            BluetoothMinorClass.PeripheralJoystick or BluetoothMinorClass.PeripheralGamepad => BluetoothKind.Gamepad,
            _ => c.RawValue is var raw && (raw & 0x80) != 0 ? BluetoothKind.Mouse : (raw & 0x40) != 0 ? BluetoothKind.Keyboard : BluetoothKind.Other,
        },
        _ => BluetoothKind.Other,
    };

    /// <summary>A Low Energy device's appearance (Bluetooth assigned numbers: category in the upper 10 bits, subcategory in the lower 6).</summary>
    static BluetoothKind KindOf(BluetoothLEAppearance? a) => a?.Category switch
    {
        0x001 => BluetoothKind.Phone,
        0x002 => BluetoothKind.Computer,
        0x00F => a.SubCategory switch // human interface device
        {
            0x01 => BluetoothKind.Keyboard,
            0x02 => BluetoothKind.Mouse,
            0x03 or 0x04 => BluetoothKind.Gamepad,
            _ => BluetoothKind.Other,
        },
        0x021 or 0x025 => BluetoothKind.Audio, // audio sink, wearable audio device
        _ => BluetoothKind.Other,
    };

    /// <summary>For Low Energy devices that report no appearance: a guess from the name.</summary>
    static BluetoothKind Guess(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("mouse") || n.Contains("マウス")) return BluetoothKind.Mouse;
        if (n.Contains("keyboard") || n.Contains("キーボード")) return BluetoothKind.Keyboard;
        if (n.Contains("controller") || n.Contains("gamepad") || n.Contains("コントローラー")) return BluetoothKind.Gamepad;
        if (n.Contains("buds") || n.Contains("airpods") || n.Contains("headphone") || n.Contains("earphone") || n.Contains("speaker") || n.Contains("イヤホン") || n.Contains("ヘッドホン")) return BluetoothKind.Audio;
        return BluetoothKind.Other;
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var w in _watchers)
            try { if (w.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted) w.Stop(); } catch (Exception) { }
        foreach (var off in _unhook)
            try { off(); } catch (Exception) { }
        _unhook.Clear();
    }

    readonly List<Action> _unhook = new();
}
