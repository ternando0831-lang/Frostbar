using System.Globalization;
using Frostbar.Core.Settings;

namespace Frostbar.Core.Time;

/// <summary>
/// The menu bar clock, macOS style: "Sun Oct 12 2:32 PM" (English) / "10月12日(日) 14:32" (Japanese).
/// The language comes from the UI language; 12/24-hour from the region's short time pattern unless forced.
/// </summary>
public static class ClockFormatter
{
    public static string Format(DateTime t, CultureInfo language, CultureInfo region, ClockSettings s)
    {
        bool ja = language.TwoLetterISOLanguageName == "ja";
        bool h24 = s.Hour24 switch
        {
            "on" => true,
            "off" => false,
            // "auto" is the settings' 地域の設定: the region's format in either language (Japanese was always 24-hour,
            // whatever the region said).
            _ => !region.DateTimeFormat.ShortTimePattern.Contains('t'),
        };
        string time;
        if (h24) time = t.ToString(s.Seconds ? "H:mm:ss" : "H:mm", CultureInfo.InvariantCulture);
        else if (ja) time = (t.Hour < 12 ? "午前" : "午後") + t.ToString(s.Seconds ? "h:mm:ss" : "h:mm", CultureInfo.InvariantCulture);
        else time = t.ToString(s.Seconds ? "h:mm:ss" : "h:mm", CultureInfo.InvariantCulture) + (t.Hour < 12 ? " AM" : " PM");

        if (ja)
        {
            var jp = CultureInfo.GetCultureInfo("ja-JP");
            string date = s.ShowDate ? $"{t.Month}月{t.Day}日" : "";
            string week = s.ShowWeekday ? $"({t.ToString("ddd", jp)})" : "";
            string head = date + week;
            return head.Length > 0 ? head + " " + time : time;
        }
        var en = CultureInfo.GetCultureInfo("en-US");
        var parts = new List<string>();
        if (s.ShowWeekday) parts.Add(t.ToString("ddd", en));
        if (s.ShowDate) parts.Add(t.ToString("MMM d", en));
        parts.Add(time);
        return string.Join(" ", parts);
    }

    /// <summary>The clock is set every second (the seconds shown, or the separators flashing), else every minute.</summary>
    public static bool EverySecond(ClockSettings s) => s.Seconds || s.FlashSeparators;

    /// <summary>
    /// The time's separators (its colons) show at <paramref name="t"/>: always, unless they flash (macOS's "Flash the time
    /// separators", 2026-10-22), when they show on the even seconds and not on the odd ones.
    /// </summary>
    public static bool SeparatorsShown(DateTime t, ClockSettings s) => !s.FlashSeparators || t.Second % 2 == 0;

    /// <summary>Delay until the next second or minute boundary, plus a small margin so the tick lands after it.</summary>
    public static TimeSpan UntilNextTick(DateTime now, bool seconds)
    {
        var next = seconds
            ? new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, now.Second, now.Kind).AddSeconds(1)
            : new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, now.Kind).AddMinutes(1);
        return next - now + TimeSpan.FromMilliseconds(20);
    }
}
