using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Frostbar.Platform.Infrastructure;

namespace Frostbar.Platform.Sys;

/// <summary>A network Windows remembers (a WLAN profile): its name and whether Windows joins it by itself.</summary>
public sealed record WlanProfile(string Name, bool AutoConnect);

/// <summary>
/// The network joined now, as the Wi-Fi settings show it: the radio's view (standard, channel and its frequency,
/// signal, link speeds, security) and the IP side (addresses, router, DNS, the adapter's hardware address).
/// <see cref="Auth"/> and <see cref="Cipher"/> are the DOT11_AUTH_ALGORITHM and DOT11_CIPHER_ALGORITHM values.
/// </summary>
public sealed record WlanConnection(
    string Ssid, string Profile, int PhyType, int Channel, int FrequencyMhz, int Quality, int? RssiDbm, int RxMbps, int TxMbps,
    int Auth, int Cipher, IReadOnlyList<string> IPv4, IReadOnlyList<string> IPv6, string SubnetMask, IReadOnlyList<string> Routers,
    IReadOnlyList<string> Dns, string HardwareAddress);

/// <summary>
/// Windows' Native Wifi API (wlanapi): the networks it remembers, forgetting one, joining automatically or not, and
/// the details of the connection. A standard user may change the profiles (Windows grants write access, checked on
/// the test PC: 0x70023). Changing a profile rewrites its XML with the key material as Windows returned it
/// (encrypted for this machine), which Windows takes back as it is. Every call opens its own handle; call off the UI
/// thread (a few ms each).
/// </summary>
public static class WlanProfiles
{
    [DllImport("wlanapi.dll")] static extern int WlanOpenHandle(int version, IntPtr reserved, out int negotiated, out IntPtr handle);
    [DllImport("wlanapi.dll")] static extern int WlanCloseHandle(IntPtr handle, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanEnumInterfaces(IntPtr handle, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern int WlanGetProfileList(IntPtr handle, ref Guid iface, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanGetProfile(IntPtr handle, ref Guid iface, string name, IntPtr reserved, out IntPtr xml, ref int flags, out int access);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanSetProfile(IntPtr handle, ref Guid iface, int flags, string xml, string? security, bool overwrite, IntPtr reserved, out int reason);
    [DllImport("wlanapi.dll", CharSet = CharSet.Unicode)] static extern int WlanDeleteProfile(IntPtr handle, ref Guid iface, string name, IntPtr reserved);
    [DllImport("wlanapi.dll")] static extern int WlanQueryInterface(IntPtr handle, ref Guid iface, int opcode, IntPtr reserved, out int size, out IntPtr data, IntPtr type);
    [DllImport("wlanapi.dll")] static extern int WlanGetNetworkBssList(IntPtr handle, ref Guid iface, IntPtr ssid, int bssType, bool secure, IntPtr reserved, out IntPtr list);
    [DllImport("wlanapi.dll")] static extern void WlanFreeMemory(IntPtr p);

    const int InterfaceInfoSize = 16 + 512 + 4, ProfileInfoSize = 512 + 4, BssEntrySize = 360;
    const int OpcodeCurrentConnection = 7, OpcodeChannel = 8, StateConnected = 1;

    /// <summary>Runs <paramref name="use"/> with a handle and the first Wi-Fi adapter; <paramref name="none"/> without one.</summary>
    static T With<T>(Func<IntPtr, Guid, T> use, T none)
    {
        if (WlanOpenHandle(2, IntPtr.Zero, out _, out var h) != 0) return none;
        try
        {
            if (WlanEnumInterfaces(h, IntPtr.Zero, out var list) != 0) return none;
            Guid iface;
            try
            {
                if (Marshal.ReadInt32(list) == 0) return none;
                iface = Marshal.PtrToStructure<Guid>(list + 8);
            }
            finally { WlanFreeMemory(list); }
            return use(h, iface);
        }
        catch (Exception ex) { Log.Debug("wlan: " + ex.Message); return none; }
        finally { WlanCloseHandle(h, IntPtr.Zero); }
    }

    /// <summary>The networks Windows remembers, in its order of preference.</summary>
    public static IReadOnlyList<WlanProfile> List() => With<IReadOnlyList<WlanProfile>>((h, iface) =>
    {
        if (WlanGetProfileList(h, ref iface, IntPtr.Zero, out var list) != 0) return [];
        var result = new List<WlanProfile>();
        try
        {
            int n = Marshal.ReadInt32(list);
            for (int i = 0; i < n; i++)
            {
                string name = Marshal.PtrToStringUni(list + 8 + i * ProfileInfoSize) ?? "";
                if (name.Length == 0) continue;
                string? xml = Xml(h, iface, name);
                result.Add(new WlanProfile(name, xml == null || ConnectionMode(xml) != "manual"));
            }
        }
        finally { WlanFreeMemory(list); }
        return result;
    }, []);

    static string? Xml(IntPtr h, Guid iface, string name)
    {
        int flags = 0;
        if (WlanGetProfile(h, ref iface, name, IntPtr.Zero, out var p, ref flags, out _) != 0) return null;
        try { return Marshal.PtrToStringUni(p); }
        finally { WlanFreeMemory(p); }
    }

    static readonly Regex Mode = new("<connectionMode>\\s*(auto|manual)\\s*</connectionMode>", RegexOptions.IgnoreCase);

    static string? ConnectionMode(string xml) => Mode.Match(xml) is { Success: true } m ? m.Groups[1].Value.ToLowerInvariant() : null;

    /// <summary>Forgets <paramref name="name"/> (its saved password goes with it). False when Windows refused.</summary>
    public static bool Delete(string name) => With((h, iface) =>
    {
        int rc = WlanDeleteProfile(h, ref iface, name, IntPtr.Zero);
        Log.Info($"wlan: forgetting a network: {(rc == 0 ? "ok" : "0x" + rc.ToString("X"))}");
        return rc == 0;
    }, false);

    /// <summary>Joins <paramref name="name"/> automatically when in range, or only when asked. False when Windows refused.</summary>
    public static bool SetAutoConnect(string name, bool auto) => With((h, iface) =>
    {
        string? xml = Xml(h, iface, name);
        if (xml == null || ConnectionMode(xml) == null) return false;
        string changed = Mode.Replace(xml, $"<connectionMode>{(auto ? "auto" : "manual")}</connectionMode>", 1);
        int rc = WlanSetProfile(h, ref iface, 0, changed, null, true, IntPtr.Zero, out int reason);
        Log.Info($"wlan: auto-join {(auto ? "on" : "off")}: {(rc == 0 ? "ok" : $"0x{rc:X} (reason {reason})")}");
        return rc == 0;
    }, false);

    /// <summary>Adds or replaces a profile from its XML (tests: a network of their own that is never in range).</summary>
    public static int Save(string xml) => With((h, iface) => WlanSetProfile(h, ref iface, 0, xml, null, true, IntPtr.Zero, out _), -1);

    /// <summary>The profile's XML as Windows keeps it, the key material encrypted (tests compare before and after).</summary>
    public static string? ReadXml(string name) => With((h, iface) => Xml(h, iface, name), null);

    /// <summary>The network joined now, or null.</summary>
    public static WlanConnection? Current() => With((h, iface) =>
    {
        if (WlanQueryInterface(h, ref iface, OpcodeCurrentConnection, IntPtr.Zero, out _, out var data, IntPtr.Zero) != 0) return null;
        string ssid, profile;
        byte[] bssid = new byte[6];
        int phy, quality, rx, tx, auth, cipher;
        try
        {
            if (Marshal.ReadInt32(data) != StateConnected) return null;
            profile = Marshal.PtrToStringUni(data + 8) ?? "";
            IntPtr assoc = data + 8 + 512;
            int len = Math.Clamp(Marshal.ReadInt32(assoc), 0, 32);
            var raw = new byte[len];
            Marshal.Copy(assoc + 4, raw, 0, len);
            ssid = Encoding.UTF8.GetString(raw);
            IntPtr after = assoc + 36;
            Marshal.Copy(after + 4, bssid, 0, 6);
            phy = Marshal.ReadInt32(after + 12);
            quality = Marshal.ReadInt32(after + 20);
            rx = Marshal.ReadInt32(after + 24) / 1000;
            tx = Marshal.ReadInt32(after + 28) / 1000;
            IntPtr security = after + 32;
            auth = Marshal.ReadInt32(security + 8);
            cipher = Marshal.ReadInt32(security + 12);
        }
        finally { WlanFreeMemory(data); }

        int channel = 0;
        if (WlanQueryInterface(h, ref iface, OpcodeChannel, IntPtr.Zero, out _, out var ch, IntPtr.Zero) == 0)
        {
            channel = Marshal.ReadInt32(ch);
            WlanFreeMemory(ch);
        }
        // The channel number alone does not tell 6 GHz from 2.4 or 5 GHz: the access point's entry has the frequency.
        int mhz = 0;
        int? rssi = null;
        if (WlanGetNetworkBssList(h, ref iface, IntPtr.Zero, 0 /*any*/, false, IntPtr.Zero, out var bss) == 0)
        {
            try
            {
                int n = Marshal.ReadInt32(bss + 4);
                var mac = new byte[6];
                for (int i = 0; i < n; i++)
                {
                    IntPtr e = bss + 8 + i * BssEntrySize;
                    Marshal.Copy(e + 40, mac, 0, 6);
                    if (!mac.AsSpan().SequenceEqual(bssid)) continue;
                    rssi = Marshal.ReadInt32(e + 56);
                    mhz = Marshal.ReadInt32(e + 92) / 1000;
                    break;
                }
            }
            finally { WlanFreeMemory(bss); }
        }

        var (v4, v6, mask, routers, dns, hw) = Ip(iface);
        return new WlanConnection(ssid, profile, phy, channel, mhz, quality, rssi, rx, tx, auth, cipher, v4, v6, mask, routers, dns, hw);
    }, null);

    static (List<string> v4, List<string> v6, string mask, List<string> routers, List<string> dns, string hw) Ip(Guid iface)
    {
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => string.Equals(n.Id, iface.ToString("B"), StringComparison.OrdinalIgnoreCase));
        if (nic == null) return ([], [], "", [], [], "");
        var p = nic.GetIPProperties();
        var v4 = p.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork).ToList();
        var v6 = p.UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && !a.Address.IsIPv6LinkLocal).Select(a => a.Address.ToString()).ToList();
        string mask = v4.FirstOrDefault()?.IPv4Mask?.ToString() ?? "";
        // IPv4 first (the router's IPv6 address is often a link-local one, fe80::…%3: as Windows lists them, it came first).
        var routers = p.GatewayAddresses.Select(g => g.Address).Where(a => !a.Equals(System.Net.IPAddress.Any))
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6).Select(a => a.ToString()).ToList();
        var dns = p.DnsAddresses.OrderBy(a => a.AddressFamily == AddressFamily.InterNetworkV6).Select(a => a.ToString()).ToList();
        string hw = string.Join(":", nic.GetPhysicalAddress().GetAddressBytes().Select(b => b.ToString("X2")));
        return (v4.Select(a => a.Address.ToString()).ToList(), v6, mask, routers, dns, hw);
    }
}
