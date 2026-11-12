using Frostbar.Core.Localization;

namespace Frostbar.Core.Devices;

/// <summary>The choices the Battery page offers for the power plan's values, and how durations read.</summary>
public static class PowerChoices
{
    /// <summary>Screen off and sleep after (seconds; 0 = never), as Windows' Power &amp; battery page offers them.</summary>
    public static readonly uint[] Timeouts = [60, 120, 180, 300, 600, 900, 1200, 1500, 1800, 2700, 3600, 7200, 10800, 14400, 18000, 0];

    /// <summary>Energy saver turns on below (battery percent; 0 = never, 100 = always), as Windows offers it.</summary>
    public static readonly uint[] EnergySaver = [0, 10, 20, 30, 40, 50, 100];

    /// <summary>
    /// The choices with <paramref name="current"/> among them (a value set elsewhere, 7 minutes say, would otherwise
    /// show as none chosen and could not be kept), and its index.
    /// </summary>
    public static (uint[] values, int index) With(uint[] choices, uint? current)
    {
        if (current is not uint c) return (choices, -1);
        int i = Array.IndexOf(choices, c);
        if (i >= 0) return (choices, i);
        // In order, "never" (0) last.
        var list = choices.Where(v => v != 0).Append(c).Where(v => v != 0).OrderBy(v => v).ToList();
        if (choices.Contains(0u) || c == 0) list.Add(0);
        var values = list.Distinct().ToArray();
        return (values, Array.IndexOf(values, c));
    }

    /// <summary>"5 分", "1 時間", "1 時間 30 分", "なし" (0).</summary>
    public static string Timeout(uint seconds, Strings s)
    {
        if (seconds == 0) return s["power.never"];
        return Duration(TimeSpan.FromSeconds(seconds), s);
    }

    /// <summary>"3 時間 20 分", "45 分", "2 時間" (whole minutes; under a minute reads as 1 minute).</summary>
    public static string Duration(TimeSpan t, Strings s)
    {
        int minutes = Math.Max(1, (int)Math.Round(t.TotalMinutes));
        int h = minutes / 60, m = minutes % 60;
        return h == 0 ? s.F("dur.m", m) : m == 0 ? s.F("dur.h", h) : s.F("dur.hm", h, m);
    }

    /// <summary>"10%", "なし" (0), "常に" (100).</summary>
    public static string Threshold(uint percent, Strings s) => percent switch
    {
        0 => s["power.never"],
        >= 100 => s["power.always"],
        _ => $"{percent}%",
    };
}
