namespace Frostbar.Core.Devices;

/// <summary>
/// An audio endpoint as Core Audio lists it: the device it belongs to (its container ID, shared by every part of one
/// physical device), the bus that enumerated it, its state, and the audio filter behind it (when it was looked up).
/// </summary>
public sealed record AudioEndpointInfo(Guid? Container, string? Enumerator, int State, string? Filter = null);

/// <summary>
/// Which audio filters a paired Bluetooth device is connected or disconnected through. Windows' Bluetooth audio drivers
/// take a one-shot request on their filters: the stereo one (A2DP, enumerated by BTHENUM) and the hands-free one
/// (BTHHFENUM). Both are asked, or the part left connected keeps the device connected. Endpoints Windows no longer has
/// (not present: on the test PC, a filter of an older driver that cannot be opened, 2026-10-13) are skipped. Disabled
/// endpoints (the user turned that part off in the sound settings) are not connected, but are disconnected with the
/// rest.
/// </summary>
public static class BluetoothAudioFilters
{
    // DEVICE_STATE_*
    public const int Active = 1, Disabled = 2, NotPresent = 4, Unplugged = 8;

    public static bool IsBluetooth(string? enumerator) => enumerator != null && enumerator.StartsWith("BTH", StringComparison.OrdinalIgnoreCase);

    static bool Usable(int state, bool connect) => (state & (Active | Unplugged)) != 0 || (!connect && (state & Disabled) != 0);

    /// <summary>The filters to send the request to, each once (a filter serves the output and the input endpoints).</summary>
    public static IReadOnlyList<string> For(IEnumerable<AudioEndpointInfo> endpoints, Guid container, bool connect) =>
        endpoints.Where(e => e.Container == container && IsBluetooth(e.Enumerator) && Usable(e.State, connect) && !string.IsNullOrEmpty(e.Filter))
            .Select(e => e.Filter!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The devices (container IDs) that can be connected this way: those with a Bluetooth audio endpoint Windows still has.</summary>
    public static HashSet<Guid> Connectable(IEnumerable<AudioEndpointInfo> endpoints) =>
        endpoints.Where(e => e.Container is Guid && IsBluetooth(e.Enumerator) && Usable(e.State, connect: true)).Select(e => e.Container!.Value).ToHashSet();
}
