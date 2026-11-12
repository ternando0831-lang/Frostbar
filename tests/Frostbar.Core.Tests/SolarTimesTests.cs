using Frostbar.Core.Time;

namespace Frostbar.Core.Tests;

public class SolarTimesTests
{
    static readonly TimeSpan Jst = TimeSpan.FromHours(9);

    // The National Astronomical Observatory of Japan's tables (eco.mtk.nao.ac.jp/koyomi/dni/2026, read 2026-10-16):
    // Osaka 34.6833 N 135.4833 E, Kyoto 35.0167 N 135.75 E, at sea level, in JST.
    [Theory]
    [InlineData("osaka", 2026, 10, 1, "05:52", "17:43")]
    [InlineData("osaka", 2026, 10, 16, "06:04", "17:23")]
    [InlineData("osaka", 2026, 10, 31, "06:17", "17:06")]
    [InlineData("kyoto", 2026, 6, 1, "04:44", "19:06")]
    [InlineData("kyoto", 2026, 6, 21, "04:43", "19:14")]
    [InlineData("kyoto", 2026, 6, 30, "04:46", "19:15")]
    [InlineData("kyoto", 2026, 10, 1, "05:51", "17:42")]
    [InlineData("kyoto", 2026, 10, 16, "06:03", "17:22")]
    [InlineData("kyoto", 2026, 10, 31, "06:17", "17:04")]
    [InlineData("kyoto", 2026, 12, 1, "06:46", "16:45")]
    [InlineData("kyoto", 2026, 12, 22, "07:01", "16:50")]
    [InlineData("kyoto", 2026, 12, 31, "07:05", "16:55")]
    public void WithinAMinuteOfTheObservatorysTables(string city, int y, int m, int d, string rise, string set)
    {
        var c = Cities.For(city, null);
        var day = SolarTimes.On(new DateOnly(y, m, d), c.Latitude, c.Longitude);
        Assert.True(day.Up);
        Near(new DateOnly(y, m, d), rise, day.Rise!.Value);
        Near(new DateOnly(y, m, d), set, day.Set!.Value);
    }

    static void Near(DateOnly date, string jst, DateTime utc)
    {
        var want = new DateTimeOffset(date.ToDateTime(TimeOnly.Parse(jst)), Jst);
        var got = new DateTimeOffset(utc, TimeSpan.Zero).ToOffset(Jst);
        double minutes = Math.Abs((got - want).TotalMinutes);
        Assert.True(minutes <= 1, $"{date} {jst}: worked out {got:HH:mm:ss} ({minutes:0.0} min off)");
    }

    [Fact]
    public void APolarDayAndAPolarNight()
    {
        // Tromsø, 69.65 N: the midnight sun at midsummer, no sun at midwinter.
        var summer = SolarTimes.On(new DateOnly(2026, 6, 21), 69.6492, 18.9553);
        Assert.Null(summer.Rise);
        Assert.True(summer.Up);
        var winter = SolarTimes.On(new DateOnly(2026, 12, 21), 69.6492, 18.9553);
        Assert.Null(winter.Set);
        Assert.False(winter.Up);
    }

    [Fact]
    public void TheCityOfTheKeyElseTheTimeZonesElseTokyo()
    {
        Assert.Equal("osaka", Cities.For("osaka", "Pacific Standard Time").Key);
        Assert.Equal("losangeles", Cities.For(null, "Pacific Standard Time").Key);
        Assert.Equal("tokyo", Cities.For("nowhere", "Tokyo Standard Time").Key);
        Assert.Equal("tokyo", Cities.For("", "Some Other Standard Time").Key);
        Assert.Equal(Cities.All.Count, Cities.All.Select(c => c.Key).Distinct().Count());
    }
}

public class AppearanceScheduleTests
{
    static readonly TimeSpan Jst = TimeSpan.FromHours(9);
    static readonly City Osaka = Cities.For("osaka", null);
    static DateTimeOffset At(int m, int d, int h, int min = 0) => new(2026, m, d, h, min, 0, Jst);

    static void Near(DateTimeOffset want, DateTimeOffset got, double minutes = 1) =>
        Assert.True(Math.Abs((got - want).TotalMinutes) <= minutes, $"want {want:yyyy-MM-dd HH:mm}, got {got:yyyy-MM-dd HH:mm:ss}");

    [Fact]
    public void LightByDayUntilSunset()
    {
        var s = AppearanceSchedule.BySun(At(10, 16, 12), Osaka.Latitude, Osaka.Longitude);
        Assert.False(s.Dark);
        Near(At(10, 16, 17, 23), s.Next);
        Assert.Equal(Jst, s.Next.Offset);
    }

    [Fact]
    public void DarkAfterSunsetUntilTomorrowsSunrise()
    {
        var s = AppearanceSchedule.BySun(At(10, 16, 20), Osaka.Latitude, Osaka.Longitude);
        Assert.True(s.Dark);
        Near(At(10, 17, 6, 5), s.Next, 2); // a minute after the 16th's 6:04
    }

    [Fact]
    public void DarkBeforeSunriseUntilItsSunrise()
    {
        var s = AppearanceSchedule.BySun(At(10, 16, 3), Osaka.Latitude, Osaka.Longitude);
        Assert.True(s.Dark);
        Near(At(10, 16, 6, 4), s.Next);
    }

    [Fact]
    public void SinceTheLastChangeTheSameAllThroughAPeriod()
    {
        // From the evening to the night, the dark part began at the 16th's sunset (17:23): one switch for the two.
        var evening = AppearanceSchedule.BySun(At(10, 16, 18), Osaka.Latitude, Osaka.Longitude);
        var night = AppearanceSchedule.BySun(At(10, 17, 2), Osaka.Latitude, Osaka.Longitude);
        Near(At(10, 16, 17, 23), evening.Since);
        Assert.Equal(evening.Since, night.Since);
        // After the sunrise, another.
        var morning = AppearanceSchedule.BySun(At(10, 17, 9), Osaka.Latitude, Osaka.Longitude);
        Assert.NotEqual(night.Since, morning.Since);
        Assert.Equal(night.Next, morning.Since);
    }

    [Fact]
    public void AtThePolesAsTheSunIsLookedAtAgainWithinADay()
    {
        var noon = new DateTimeOffset(2026, 6, 21, 12, 0, 0, TimeSpan.Zero);
        var summer = AppearanceSchedule.BySun(noon, 69.6492, 18.9553);
        Assert.False(summer.Dark);
        Assert.True(summer.Next > noon && summer.Next <= noon.AddDays(1));
        var winter = AppearanceSchedule.BySun(noon.AddMonths(6), 69.6492, 18.9553);
        Assert.True(winter.Dark);
    }

    static readonly TimeZoneInfo Tokyo = TimeZoneInfo.FindSystemTimeZoneById("Tokyo Standard Time");

    [Theory]
    [InlineData(12, 0, false, 16, 19, 0)] // day: dark at 19:00
    [InlineData(20, 0, true, 17, 7, 0)]   // evening: light at 7:00 the next day
    [InlineData(6, 0, true, 16, 7, 0)]    // early morning: light at 7:00 the same day
    [InlineData(19, 0, true, 17, 7, 0)]   // the dark part starts at its time
    [InlineData(7, 0, false, 16, 19, 0)]  // and ends at its time
    public void ByTheClockAcrossMidnight(int h, int min, bool dark, int nextDay, int nextH, int nextMin)
    {
        var s = AppearanceSchedule.ByClock(At(10, 16, h, min), new TimeOnly(19, 0), new TimeOnly(7, 0), Tokyo);
        Assert.Equal(dark, s.Dark);
        Assert.Equal(At(10, nextDay, nextH, nextMin), s.Next);
        Assert.True(s.Since <= At(10, 16, h, min) && s.Since > At(10, 16, h, min).AddHours(-24));
        Assert.Equal(dark ? 19 : 7, s.Since.Hour); // the dark part began at 19:00, the light one at 7:00
    }

    [Fact]
    public void ByTheClockWithinADayAndNeverForTheSameTimes()
    {
        var s = AppearanceSchedule.ByClock(At(10, 16, 14), new TimeOnly(13, 0), new TimeOnly(15, 0), Tokyo);
        Assert.True(s.Dark);
        Assert.Equal(At(10, 16, 15), s.Next);
        var never = AppearanceSchedule.ByClock(At(10, 16, 14), new TimeOnly(9, 0), new TimeOnly(9, 0), Tokyo);
        Assert.False(never.Dark);
        Assert.Equal(At(10, 17, 14), never.Next);
    }
}
