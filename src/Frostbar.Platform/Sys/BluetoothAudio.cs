using System.Diagnostics;
using System.Runtime.InteropServices;
using Frostbar.Core.Devices;
using Frostbar.Platform.Infrastructure;
using Frostbar.Platform.Interop;

namespace Frostbar.Platform.Sys;

[ComImport, Guid("2A07407E-6497-4A18-9787-32F79BD0D98F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IDeviceTopology
{
    [PreserveSig] int GetConnectorCount(out uint n);
    [PreserveSig] int GetConnector(uint i, out IConnector connector);
}

[ComImport, Guid("9c2c4058-23f5-41de-877a-df3af236a09e"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IConnector
{
    [PreserveSig] int GetConnectorType(out int type);
    [PreserveSig] int GetDataFlow(out int flow);
    [PreserveSig] int ConnectTo(IntPtr other);
    [PreserveSig] int Disconnect();
    [PreserveSig] int IsConnected(out int connected);
    [PreserveSig] int GetConnectedTo(out IntPtr other);
    [PreserveSig] int GetConnectorIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
    [PreserveSig] int GetDeviceIdConnectedTo([MarshalAs(UnmanagedType.LPWStr)] out string id);
}

[ComImport, Guid("28F54685-06FD-11D2-B27A-00A0C9223196"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
interface IKsControl
{
    [PreserveSig] int KsProperty(ref KsPropertyHeader property, uint propertyLength, IntPtr data, uint dataLength, out uint returned);
}

[StructLayout(LayoutKind.Sequential)]
struct KsPropertyHeader { public Guid Set; public uint Id; public uint Flags; }

/// <summary>
/// Connects and disconnects paired Bluetooth audio devices (headphones, speakers) the way Windows' Bluetooth audio
/// drivers offer it to apps: the one-shot requests of KSPROPSETID_BtAudio, sent to the device's audio filters, which
/// are found behind its audio endpoints (Core Audio's device topology). The request returns at once (up to 0.8 s on the
/// test PC) and the connection follows within seconds, or never when the device is out of reach or will not take
/// another host; the caller watches the device. Other kinds of device (mice, keyboards) have no such request, and
/// Windows' settings offer none for them either: they connect themselves. Any thread; the UI's is best avoided.
/// </summary>
public static class BluetoothAudio
{
    static readonly Guid KSPROPSETID_BtAudio = new("7FA06C40-B8F6-4C7E-8556-E8C33A12E54D");
    const uint ONESHOT_RECONNECT = 0, ONESHOT_DISCONNECT = 1, KSPROPERTY_TYPE_GET = 1;
    const int CLSCTX_ALL = 23, eAll = 2, AllStates = 0xF;
    static readonly PROPERTYKEY ContainerId = new(new Guid("8c7ed206-3f8a-4827-b3ab-ae9e1faefc6c"), 2);
    static readonly PROPERTYKEY EnumeratorName = new(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);

    /// <summary>The paired devices (container IDs) that can be connected and disconnected here. Reads the endpoints' properties only.</summary>
    public static HashSet<Guid> Connectable()
    {
        try { return BluetoothAudioFilters.Connectable(WithEnumerator(e => Endpoints(e, resolveFor: null))); }
        catch (Exception ex) { Log.Debug("bluetooth: audio endpoints: " + ex.Message); return []; }
    }

    /// <summary>The filters a request for the device with <paramref name="container"/> goes to (diagnostics; nothing is sent).</summary>
    public static IReadOnlyList<string> Filters(Guid container, bool connect) => WithEnumerator(e => BluetoothAudioFilters.For(Endpoints(e, container), container, connect));

    /// <summary>Asks the drivers of the device with <paramref name="container"/> to connect or disconnect it; how many of its filters took the request.</summary>
    public static int Request(Guid container, bool connect) => WithEnumerator(e =>
    {
        var sw = Stopwatch.StartNew();
        var filters = BluetoothAudioFilters.For(Endpoints(e, container), container, connect);
        int took = 0;
        foreach (var f in filters)
        {
            if (e.GetDevice(f, out var device) != 0 || device == null) continue;
            try
            {
                if (device.Activate(typeof(IKsControl).GUID, CLSCTX_ALL, IntPtr.Zero, out object o) != 0) continue;
                var ks = (IKsControl)o;
                try
                {
                    var p = new KsPropertyHeader { Set = KSPROPSETID_BtAudio, Id = connect ? ONESHOT_RECONNECT : ONESHOT_DISCONNECT, Flags = KSPROPERTY_TYPE_GET };
                    int hr = ks.KsProperty(ref p, (uint)Marshal.SizeOf<KsPropertyHeader>(), IntPtr.Zero, 0, out _);
                    if (hr == 0) took++;
                    else Log.Info($"bluetooth: a filter refused the request: 0x{hr:X8}");
                }
                finally { Marshal.ReleaseComObject(ks); }
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        Log.Info($"bluetooth: {(connect ? "connect" : "disconnect")} taken by {took} of {filters.Count} filters ({sw.ElapsedMilliseconds} ms)");
        return took;
    });

    static T WithEnumerator<T>(Func<IMMDeviceEnumerator, T> use)
    {
        var e = (IMMDeviceEnumerator)new MMDeviceEnumeratorCo();
        try { return use(e); }
        finally { Marshal.ReleaseComObject(e); }
    }

    /// <summary>Every audio endpoint, output and input, in any state; the filter behind it looked up only for the Bluetooth endpoints of <paramref name="resolveFor"/>.</summary>
    static List<AudioEndpointInfo> Endpoints(IMMDeviceEnumerator e, Guid? resolveFor)
    {
        var list = new List<AudioEndpointInfo>();
        if (e.EnumAudioEndpoints(eAll, AllStates, out IntPtr p) != 0 || p == IntPtr.Zero) return list;
        var all = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(p);
        Marshal.Release(p);
        try
        {
            all.GetCount(out uint n);
            for (uint i = 0; i < n; i++)
            {
                if (all.Item(i, out var d) != 0 || d == null) continue;
                try
                {
                    d.GetState(out int state);
                    Guid? container = null;
                    string? enumerator = null;
                    if (d.OpenPropertyStore(0 /*STGM_READ*/, out var ps) == 0)
                        try { (container, enumerator) = (ReadGuid(ps, ContainerId), ReadString(ps, EnumeratorName)); }
                        finally { Marshal.ReleaseComObject(ps); }
                    string? filter = resolveFor != null && container == resolveFor && BluetoothAudioFilters.IsBluetooth(enumerator) ? FilterOf(d) : null;
                    list.Add(new AudioEndpointInfo(container, enumerator, state, filter));
                }
                finally { Marshal.ReleaseComObject(d); }
            }
        }
        finally { Marshal.ReleaseComObject(all); }
        return list;
    }

    /// <summary>The KS filter at the other end of the endpoint's connector (the driver's), or null.</summary>
    static string? FilterOf(IMMDevice endpoint)
    {
        if (endpoint.Activate(typeof(IDeviceTopology).GUID, CLSCTX_ALL, IntPtr.Zero, out object o) != 0) return null;
        var topology = (IDeviceTopology)o;
        try
        {
            if (topology.GetConnector(0, out var c) != 0 || c == null) return null;
            try { return c.GetDeviceIdConnectedTo(out var id) == 0 ? id : null; }
            finally { Marshal.ReleaseComObject(c); }
        }
        finally { Marshal.ReleaseComObject(topology); }
    }

    static Guid? ReadGuid(IPropertyStore ps, PROPERTYKEY key)
    {
        if (ps.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.vt == 72 /*VT_CLSID*/ && pv.p != IntPtr.Zero ? Marshal.PtrToStructure<Guid>(pv.p) : null; }
        finally { ShellApi.PropVariantClear(ref pv); }
    }

    static string? ReadString(IPropertyStore ps, PROPERTYKEY key)
    {
        if (ps.GetValue(ref key, out var pv) != 0) return null;
        try { return pv.vt == 31 /*VT_LPWSTR*/ ? Marshal.PtrToStringUni(pv.p) : null; }
        finally { ShellApi.PropVariantClear(ref pv); }
    }
}
