using System.Globalization;
using Frostbar.Core.Settings;
using Frostbar.Core.Time;

namespace Frostbar.Core.Tests;

public class ClockFormatterTests
{
    static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US"), Ja = CultureInfo.GetCultureInfo("ja-JP"), Gb = CultureInfo.GetCultureInfo("en-GB");
    static readonly DateTime Afternoon = new(2026, 10, 11, 14, 32, 5), Morning = new(2026, 10, 12, 0, 5, 9);
    static ClockSettings Auto => new() { Hour24 = "auto" };

    [Fact]
    public void Default_follows_the_regions_time_format()
    {
        // Japanese display language with a US (12-hour) region format: the default is the region's.
        string shown = ClockFormatter.Format(Morning, Ja, En, new ClockSettings());
        Assert.Equal(ClockFormatter.Format(Morning, Ja, En, Auto), shown);
        Assert.DoesNotContain(" 0:05", shown);
    }

    [Fact]
    public void English_auto_follows_a_12h_region() =>
        Assert.Equal("Sun Oct 11 2:32 PM", ClockFormatter.Format(Afternoon, En, En, Auto));

    [Fact]
    public void Japanese_auto_is_24h() =>
        Assert.Equal("10月11日(日) 14:32", ClockFormatter.Format(Afternoon, Ja, Ja, Auto));

    [Fact]
    public void Japanese_auto_follows_a_12h_region() =>
        // 地域の設定 with this PC's US region format: 12-hour, written the Japanese way.
        Assert.Equal("10月11日(日) 午後2:32", ClockFormatter.Format(Afternoon, Ja, En, Auto));

    [Fact]
    public void Midnight_in_12h_is_12_AM() =>
        Assert.Equal("Mon Oct 12 12:05 AM", ClockFormatter.Format(Morning, En, En, Auto));

    [Fact]
    public void Japanese_12h_uses_gozen_gogo() =>
        Assert.Equal("10月11日(日) 午後2:32", ClockFormatter.Format(Afternoon, Ja, Ja, new ClockSettings { Hour24 = "off" }));

    [Fact]
    public void Region_with_24h_clock_gives_24h_in_auto() =>
        Assert.Equal("Sun Oct 11 14:32", ClockFormatter.Format(Afternoon, En, Gb, Auto));

    [Fact]
    public void Seconds_and_no_date() =>
        Assert.Equal("Sun 2:32:05 PM", ClockFormatter.Format(Afternoon, En, En, new ClockSettings { ShowDate = false, Seconds = true, Hour24 = "auto" }));

    [Fact]
    public void Time_only() =>
        Assert.Equal("14:32", ClockFormatter.Format(Afternoon, Ja, Ja, new ClockSettings { ShowDate = false, ShowWeekday = false }));

    [Fact]
    public void Next_tick_lands_just_after_the_boundary()
    {
        var d = ClockFormatter.UntilNextTick(new DateTime(2026, 10, 11, 14, 32, 59, 990), seconds: false);
        Assert.InRange(d.TotalMilliseconds, 25, 40);
        var s = ClockFormatter.UntilNextTick(new DateTime(2026, 10, 11, 14, 32, 5, 500), seconds: true);
        Assert.InRange(s.TotalMilliseconds, 515, 525);
    }
}
