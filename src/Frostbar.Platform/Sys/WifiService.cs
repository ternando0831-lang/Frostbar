using System.Windows.Threading;
using Frostbar.Platform.Infrastructure;
using Windows.Devices.WiFi;
using Windows.Networking.Connectivity;
using Windows.Security.Credentials;

namespace Frostbar.Platform.Sys;

/// <summary>A Wi-Fi network nearby, one per name (its strongest access point).</summary>
public sealed record WifiNetwork(string Ssid, int Bars, bool Secured, bool Connected);

/// <summary>
/// Wi-Fi networks nearby and joining them (Windows.Devices.WiFi). Desktop apps need location access for this on
/// Windows 11 24H2 and later (this PC allows it: 2026-10-12); without it <see cref="Problem"/> says so and the list
/// stays empty. Networks of the same name (2.4 and 5 GHz) show once. Joining tries Windows' saved profile first and
/// asks for the password only when that fails; a joined network is remembered and joined again automatically, as
/// Windows does. Events come on WinRT threads and are marshalled to the UI.
/// </summary>
public sealed class WifiService : IDisposable
{
    readonly Dispatcher _ui;
    WiFiAdapter? _adapter;
    bool _disposed;

    public bool Available => _adapter != null;
    /// <summary>Why there is no list (no adapter, access denied), or null.</summary>
    public string? Problem { get; private set; }
    public IReadOnlyList<WifiNetwork> Networks { get; private set; } = [];
    public string ConnectedSsid { get; private set; } = "";
    public bool Scanning { get; private set; }
    /// <summary>The list, the connection or the scanning state changed (UI thread).</summary>
    public event Action? Changed;
    /// <summary>How many listen to <see cref="Changed"/> (diagnostics: a closed panel must not stay subscribed).</summary>
    public int Listeners => Changed?.GetInvocationList().Length ?? 0;

    public WifiService(Dispatcher ui)
    {
        _ui = ui;
        _ = StartAsync();
    }

    async Task StartAsync()
    {
        try
        {
            var access = await WiFiAdapter.RequestAccessAsync();
            if (access != WiFiAccessStatus.Allowed) { Problem = access.ToString(); Post(); return; }
            var adapters = await WiFiAdapter.FindAllAdaptersAsync();
            if (adapters.Count == 0) { Problem = "no adapter"; Post(); return; }
            if (_disposed) return;
            _adapter = adapters[0];
            _adapter.AvailableNetworksChanged += OnNetworksChanged;
            NetworkInformation.NetworkStatusChanged += OnStatusChanged;
            await ReadAsync();
        }
        catch (Exception ex) { Problem = ex.Message; Log.Info("wifi: " + ex.Message); Post(); }
    }

    void OnNetworksChanged(WiFiAdapter sender, object args) => _ = ReadAsync();
    void OnStatusChanged(object? sender) => _ = ReadAsync();

    /// <summary>Asks the adapter to look again (a couple of seconds); the list updates when it is done.</summary>
    public async Task ScanAsync()
    {
        if (_adapter == null || Scanning) return;
        _ui.Invoke(() => { Scanning = true; Changed?.Invoke(); });
        try { await _adapter.ScanAsync(); }
        catch (Exception ex) { Log.Debug("wifi: scan: " + ex.Message); }
        await ReadAsync();
        _ = _ui.BeginInvoke(() => { Scanning = false; Changed?.Invoke(); });
    }

    async Task ReadAsync()
    {
        var adapter = _adapter;
        if (adapter == null || _disposed) return;
        string connected = "";
        try
        {
            var profile = await adapter.NetworkAdapter.GetConnectedProfileAsync();
            if (profile?.IsWlanConnectionProfile == true) connected = profile.WlanConnectionProfileDetails.GetConnectedSsid() ?? "";
        }
        catch (Exception) { }
        var list = adapter.NetworkReport.AvailableNetworks
            .Where(n => !string.IsNullOrEmpty(n.Ssid))
            .GroupBy(n => n.Ssid)
            .Select(g =>
            {
                var best = g.OrderByDescending(n => n.SignalBars).First();
                var auth = best.SecuritySettings.NetworkAuthenticationType;
                bool secured = auth is not (Windows.Networking.Connectivity.NetworkAuthenticationType.Open80211 or Windows.Networking.Connectivity.NetworkAuthenticationType.None);
                return new WifiNetwork(g.Key, best.SignalBars, secured, g.Key == connected);
            })
            .OrderByDescending(n => n.Connected).ThenByDescending(n => n.Bars).ThenBy(n => n.Ssid, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        _ = _ui.BeginInvoke(() =>
        {
            Networks = list;
            ConnectedSsid = connected;
            Changed?.Invoke();
        });
    }

    void Post() => _ui.BeginInvoke(() => Changed?.Invoke());

    /// <summary>
    /// Joins a network by name: with <paramref name="password"/>, or with Windows' saved profile when it is null.
    /// Returns how it went (Success, InvalidCredential when a password is needed or wrong, …).
    /// </summary>
    public async Task<WiFiConnectionStatus> ConnectAsync(string ssid, string? password)
    {
        if (_adapter == null) return WiFiConnectionStatus.NetworkNotAvailable;
        var network = _adapter.NetworkReport.AvailableNetworks.Where(n => n.Ssid == ssid).OrderByDescending(n => n.SignalBars).FirstOrDefault();
        if (network == null) return WiFiConnectionStatus.NetworkNotAvailable;
        try
        {
            var result = password == null
                ? await _adapter.ConnectAsync(network, WiFiReconnectionKind.Automatic)
                : await _adapter.ConnectAsync(network, WiFiReconnectionKind.Automatic, new PasswordCredential { Password = password });
            Log.Info($"wifi: joining a network: {result.ConnectionStatus}");
            _ = ReadAsync();
            return result.ConnectionStatus;
        }
        catch (Exception ex) { Log.Warn("wifi: joining failed", ex); return WiFiConnectionStatus.UnspecifiedFailure; }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_adapter != null) _adapter.AvailableNetworksChanged -= OnNetworksChanged;
        NetworkInformation.NetworkStatusChanged -= OnStatusChanged;
    }
}
