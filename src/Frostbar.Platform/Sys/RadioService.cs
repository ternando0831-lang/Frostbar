using System.Windows.Threading;
using Frostbar.Platform.Infrastructure;
using Windows.Devices.Radios;

namespace Frostbar.Platform.Sys;

/// <summary>
/// Wi-Fi and Bluetooth on and off (Windows.Devices.Radios): their states, live (the quick settings, a key on the
/// keyboard, airplane mode change them too), and switching them. A radio Windows has disabled (airplane mode's
/// hardware switch, a missing driver) cannot be switched on. Events come on WinRT threads and are marshalled to the UI.
/// </summary>
public sealed class RadioService : IDisposable
{
    readonly Dispatcher _ui;
    Radio? _wifi, _bluetooth;
    bool _disposed;

    public RadioState Wifi => _wifi?.State ?? RadioState.Unknown;
    public RadioState Bluetooth => _bluetooth?.State ?? RadioState.Unknown;
    public bool HasWifi => _wifi != null;
    public bool HasBluetooth => _bluetooth != null;
    /// <summary>The states arrived, or one changed (UI thread).</summary>
    public event Action? Changed;
    /// <summary>How many listen to <see cref="Changed"/> (diagnostics: a closed panel must not stay subscribed).</summary>
    public int Listeners => Changed?.GetInvocationList().Length ?? 0;

    public RadioService(Dispatcher ui)
    {
        _ui = ui;
        _ = LoadAsync();
    }

    async Task LoadAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed) Log.Warn($"radios: access {access}; they can be read but maybe not switched");
            var radios = await Radio.GetRadiosAsync();
            if (_disposed) return;
            _wifi = radios.FirstOrDefault(r => r.Kind == RadioKind.WiFi);
            _bluetooth = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            if (_wifi != null) _wifi.StateChanged += OnStateChanged;
            if (_bluetooth != null) _bluetooth.StateChanged += OnStateChanged;
            Log.Info($"radios: Wi-Fi {Wifi}, Bluetooth {Bluetooth}");
            _ = _ui.BeginInvoke(() => Changed?.Invoke());
        }
        catch (Exception ex) { Log.Warn("radios unavailable", ex); }
    }

    void OnStateChanged(Radio sender, object args)
    {
        if (!_disposed) _ui.BeginInvoke(() => Changed?.Invoke());
    }

    /// <summary>Switches a radio; false when Windows refused (or there is no such radio).</summary>
    public async Task<bool> SetAsync(RadioKind kind, bool on)
    {
        var radio = kind == RadioKind.WiFi ? _wifi : kind == RadioKind.Bluetooth ? _bluetooth : null;
        if (radio == null) return false;
        try
        {
            var result = await radio.SetStateAsync(on ? RadioState.On : RadioState.Off);
            Log.Info($"radios: {kind} {(on ? "on" : "off")}: {result}");
            return result == RadioAccessStatus.Allowed;
        }
        catch (Exception ex) { Log.Warn($"radios: {kind} could not be switched", ex); return false; }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_wifi != null) _wifi.StateChanged -= OnStateChanged;
        if (_bluetooth != null) _bluetooth.StateChanged -= OnStateChanged;
    }
}
