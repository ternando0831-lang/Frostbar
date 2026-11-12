namespace Frostbar.Core.Shell;

/// <summary>
/// The accent colours the appearance page offers (macOS's Appearance › Accent colour, 2026-10-22): Windows' own first
/// (macOS's "Multicolour", where each app picks: here the shell follows Windows, as it did before), then macOS's eight
/// in its order. The one list the page and the validator both use (the hot corners' lesson, 2026-10-15).
/// </summary>
public static class AccentColors
{
    public const string System = "system";

    /// <summary>The choices in the page's order.</summary>
    public static readonly string[] Names = [System, "blue", "purple", "pink", "red", "orange", "yellow", "green", "graphite"];

    /// <summary>
    /// The colour of <paramref name="name"/> (null: Windows' own). Values after macOS's eight, not matched against its
    /// screens: blue a little lighter in the dark appearance, as macOS's system blue is.
    /// </summary>
    public static (byte R, byte G, byte B)? Of(string name, bool dark) => name switch
    {
        "blue" => dark ? ((byte)0x0A, (byte)0x84, (byte)0xFF) : ((byte)0x00, (byte)0x7A, (byte)0xFF),
        "purple" => (0x95, 0x3D, 0x96),
        "pink" => (0xF7, 0x4F, 0x9E),
        "red" => (0xE0, 0x38, 0x3E),
        "orange" => (0xF7, 0x82, 0x1B),
        "yellow" => (0xFF, 0xC6, 0x00),
        "green" => (0x62, 0xBA, 0x46),
        "graphite" => (0x98, 0x98, 0x98),
        _ => null,
    };
}

/// <summary>macOS's Appearance › scroll bars (2026-10-22): when they show, and what a click on one's track does.</summary>
public static class ScrollBarChoices
{
    public const string WhenScrolling = "scrolling", Always = "always";
    public const string NextPage = "page", ClickedSpot = "spot";

    /// <summary>"Show scroll bars", in the page's order: while a list moves (as before), or always.</summary>
    public static readonly string[] Shows = [WhenScrolling, Always];

    /// <summary>"Click in the scroll bar to", in the page's order: jump to the next page, or to the spot clicked.</summary>
    public static readonly string[] Clicks = [NextPage, ClickedSpot];
}
