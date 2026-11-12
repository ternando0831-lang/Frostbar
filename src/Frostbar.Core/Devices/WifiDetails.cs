namespace Frostbar.Core.Devices;

/// <summary>
/// How the Wi-Fi settings name a connection's radio side: the standard ("Wi-Fi 6E (802.11ax)"), the band, and the
/// security, from what the Native Wifi API reports (DOT11_PHY_TYPE, the access point's centre frequency, and the
/// DOT11_AUTH_ALGORITHM / DOT11_CIPHER_ALGORITHM values).
/// </summary>
public static class WifiDetails
{
    /// <summary>"Wi-Fi 6E (802.11ax)", "802.11g", or "" when unknown. 6 GHz makes Wi-Fi 6 into 6E.</summary>
    public static string Standard(int phyType, int frequencyMhz) => phyType switch
    {
        4 => "802.11a",
        5 => "802.11b",
        6 => "802.11g",
        7 => "Wi-Fi 4 (802.11n)",
        8 => "Wi-Fi 5 (802.11ac)",
        9 => "802.11ad",
        10 => Band(frequencyMhz, 0) == "6 GHz" ? "Wi-Fi 6E (802.11ax)" : "Wi-Fi 6 (802.11ax)",
        11 => "Wi-Fi 7 (802.11be)",
        _ => "",
    };

    /// <summary>"2.4 GHz", "5 GHz", "6 GHz" from the frequency, or from the channel number when the frequency is unknown (never 6 GHz then: its channel numbers repeat the others').</summary>
    public static string Band(int frequencyMhz, int channel)
    {
        if (frequencyMhz > 0)
            return frequencyMhz < 3000 ? "2.4 GHz" : frequencyMhz < 5925 ? "5 GHz" : frequencyMhz < 7200 ? "6 GHz" : "60 GHz";
        return channel switch
        {
            >= 1 and <= 14 => "2.4 GHz",
            >= 32 and <= 177 => "5 GHz",
            _ => "",
        };
    }

    /// <summary>The Strings key of the security's name ("wifi.sec.wpa2Personal"…).</summary>
    public static string SecurityKey(int auth, int cipher) => auth switch
    {
        1 => cipher == 0 ? "wifi.sec.none" : "wifi.sec.wep", // open (with WEP keys when there is a cipher)
        2 => "wifi.sec.wep",
        3 => "wifi.sec.wpaEnterprise",
        4 => "wifi.sec.wpaPersonal",
        6 => "wifi.sec.wpa2Enterprise",
        7 => "wifi.sec.wpa2Personal",
        8 or 11 => "wifi.sec.wpa3Enterprise",
        9 => "wifi.sec.wpa3Personal",
        10 => "wifi.sec.owe",
        _ => "wifi.sec.other",
    };

    /// <summary>Signal bars (0–4) from the quality Windows reports (0–100), as its own network list shows them.</summary>
    public static int Bars(int quality) => quality switch { <= 0 => 0, < 25 => 1, < 50 => 2, < 75 => 3, _ => 4 };
}
