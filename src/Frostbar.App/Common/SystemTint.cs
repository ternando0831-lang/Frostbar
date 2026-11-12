using System.Windows.Media;

namespace Frostbar.App.Common;

/// <summary>
/// macOS's system colours where the shell shows a state (2026-10-19, the user's "C7"): one red, one yellow, one green,
/// each in its light and its dark appearance as Apple gives them (red #FF3B30 in the light, #FF453A in the dark). The
/// Dock's badge (always the light one) and the calendar's Sundays (always the dark one) were two different reds.
/// </summary>
public static class SystemTint
{
    public static Color Red(bool dark) => dark ? Color.FromRgb(0xFF, 0x45, 0x3A) : Color.FromRgb(0xFF, 0x3B, 0x30);
    public static Color Yellow(bool dark) => dark ? Color.FromRgb(0xFF, 0xD6, 0x0A) : Color.FromRgb(0xFF, 0xCC, 0x00);
    public static Color Green(bool dark) => dark ? Color.FromRgb(0x30, 0xD1, 0x58) : Color.FromRgb(0x34, 0xC7, 0x59);

    /// <summary>A frozen brush of <paramref name="c"/>.</summary>
    public static SolidColorBrush Brush(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
