namespace Frostbar.Core.Time;

/// <summary>
/// Japan's national holidays for the calendar in the notification centre (the user's calendar shows them in red), by
/// the Act on National Holidays as it stands (with the changes for 2019–2021). The equinoxes use the usual formula,
/// right for 1980–2099. Substitute holidays (a holiday on Sunday moves to the next weekday that is not a holiday) and
/// citizens' holidays (a day between two holidays) are included.
/// </summary>
public static class JapaneseHolidays
{
    /// <summary>The year's holidays with their Japanese names.</summary>
    public static IReadOnlyDictionary<DateOnly, string> Of(int year)
    {
        var days = new SortedDictionary<DateOnly, string>();
        if (year < 1980 || year > 2099) return days;
        void Add(int month, int day, string name) => days[new DateOnly(year, month, day)] = name;

        Add(1, 1, "元日");
        Add(1, NthMonday(year, 1, 2), "成人の日");
        Add(2, 11, "建国記念の日");
        if (year >= 2020) Add(2, 23, "天皇誕生日");
        else if (year <= 2018) Add(12, 23, "天皇誕生日");
        Add(3, VernalEquinox(year), "春分の日");
        Add(4, 29, year >= 2007 ? "昭和の日" : "みどりの日");
        Add(5, 3, "憲法記念日");
        if (year >= 2007) Add(5, 4, "みどりの日");
        Add(5, 5, "こどもの日");
        // The Olympics moved Marine Day, Mountain Day and Sports Day in 2020 and 2021.
        if (year == 2020) { Add(7, 23, "海の日"); Add(7, 24, "スポーツの日"); Add(8, 10, "山の日"); }
        else if (year == 2021) { Add(7, 22, "海の日"); Add(7, 23, "スポーツの日"); Add(8, 8, "山の日"); }
        else
        {
            if (year >= 2003) Add(7, NthMonday(year, 7, 3), "海の日");
            if (year >= 2016) Add(8, 11, "山の日");
            Add(10, NthMonday(year, 10, 2), year >= 2020 ? "スポーツの日" : "体育の日");
        }
        Add(9, NthMonday(year, 9, 3), "敬老の日");
        Add(9, AutumnalEquinox(year), "秋分の日");
        Add(11, 3, "文化の日");
        Add(11, 23, "勤労感謝の日");
        if (year == 2019)
        {
            Add(4, 30, "国民の休日");
            Add(5, 1, "天皇の即位の日");
            Add(5, 2, "国民の休日");
            Add(10, 22, "即位礼正殿の儀の行われる日");
        }

        // A day between two holidays (not a Sunday) is a holiday (Sep 22 in 2026).
        foreach (var d in days.Keys.ToList())
        {
            var next = d.AddDays(1);
            if (!days.ContainsKey(next) && days.ContainsKey(d.AddDays(2)) && next.DayOfWeek != DayOfWeek.Sunday && next.Year == year)
                days[next] = "国民の休日";
        }
        // A holiday on Sunday: the next day that is not a holiday is one.
        foreach (var d in days.Keys.Where(d => d.DayOfWeek == DayOfWeek.Sunday).ToList())
        {
            var sub = d.AddDays(1);
            while (days.ContainsKey(sub)) sub = sub.AddDays(1);
            if (sub.Year == year) days[sub] = "振替休日";
        }
        return days;
    }

    static int NthMonday(int year, int month, int n)
    {
        var first = new DateOnly(year, month, 1);
        int offset = ((int)DayOfWeek.Monday - (int)first.DayOfWeek + 7) % 7;
        return 1 + offset + (n - 1) * 7;
    }

    static int VernalEquinox(int year) => (int)Math.Floor(20.8431 + 0.242194 * (year - 1980) - Math.Floor((year - 1980) / 4.0));
    static int AutumnalEquinox(int year) => (int)Math.Floor(23.2488 + 0.242194 * (year - 1980) - Math.Floor((year - 1980) / 4.0));
}
