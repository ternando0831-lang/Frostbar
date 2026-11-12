using System.Globalization;
using Frostbar.Core.Devices;
using Frostbar.Core.Localization;
using Frostbar.Core.Search;
using Frostbar.Core.Shell;
using Frostbar.Core.Time;

namespace Frostbar.Core.Tests;

/// <summary>The settings' system pages and the notification centre: how their values read.</summary>
public class SystemPagesTests
{
    static readonly Strings Ja = Strings.For("ja", CultureInfo.InvariantCulture), En = Strings.For("en", CultureInfo.InvariantCulture);

    // ---- Wi-Fi ----

    [Theory]
    [InlineData(10, 5240, "Wi-Fi 6 (802.11ax)")]
    [InlineData(10, 6115, "Wi-Fi 6E (802.11ax)")] // 802.11ax on 6 GHz
    [InlineData(10, 0, "Wi-Fi 6 (802.11ax)")]
    [InlineData(8, 5180, "Wi-Fi 5 (802.11ac)")]
    [InlineData(7, 2437, "Wi-Fi 4 (802.11n)")]
    [InlineData(11, 6115, "Wi-Fi 7 (802.11be)")]
    [InlineData(6, 2412, "802.11g")]
    [InlineData(99, 2412, "")]
    public void Standard(int phy, int mhz, string expected) => Assert.Equal(expected, WifiDetails.Standard(phy, mhz));

    [Theory]
    [InlineData(2437, 6, "2.4 GHz")]
    [InlineData(5240, 48, "5 GHz")]
    [InlineData(6115, 37, "6 GHz")]
    [InlineData(0, 48, "5 GHz")] // the test PC's connection: channel 48
    [InlineData(0, 11, "2.4 GHz")]
    [InlineData(0, 233, "")] // a 6 GHz channel number alone says nothing
    public void Band(int mhz, int channel, string expected) => Assert.Equal(expected, WifiDetails.Band(mhz, channel));

    [Theory]
    [InlineData(7, 4, "WPA2 パーソナル")] // the test PC's network: RSNA_PSK with CCMP
    [InlineData(9, 4, "WPA3 パーソナル")]
    [InlineData(6, 4, "WPA2 エンタープライズ")]
    [InlineData(1, 0, "なし")]
    [InlineData(1, 1, "WEP")]
    [InlineData(10, 4, "拡張オープン（OWE）")]
    public void Security(int auth, int cipher, string expected) => Assert.Equal(expected, Ja[WifiDetails.SecurityKey(auth, cipher)]);

    [Theory]
    [InlineData(0, 0)]
    [InlineData(20, 1)]
    [InlineData(49, 2)]
    [InlineData(74, 3)]
    [InlineData(92, 4)]
    public void Bars(int quality, int bars) => Assert.Equal(bars, WifiDetails.Bars(quality));

    // ---- power ----

    [Fact]
    public void A_timeout_set_elsewhere_is_among_the_choices_in_order_with_never_last()
    {
        var (values, index) = PowerChoices.With(PowerChoices.Timeouts, 420); // 7 minutes
        Assert.Equal(420u, values[index]);
        Assert.Equal(300u, values[index - 1]);
        Assert.Equal(600u, values[index + 1]);
        Assert.Equal(0u, values[^1]);
        Assert.Equal(PowerChoices.Timeouts.Length + 1, values.Length);
    }

    [Fact]
    public void A_timeout_among_the_choices_keeps_them_as_they_are()
    {
        var (values, index) = PowerChoices.With(PowerChoices.Timeouts, 300);
        Assert.Same(PowerChoices.Timeouts, values);
        Assert.Equal(3, index);
        Assert.Equal(-1, PowerChoices.With(PowerChoices.Timeouts, null).index);
        Assert.Equal(PowerChoices.Timeouts.Length - 1, PowerChoices.With(PowerChoices.Timeouts, 0).index); // never
    }

    [Theory]
    [InlineData(0u, "しない")]
    [InlineData(60u, "1 分")]
    [InlineData(300u, "5 分")]
    [InlineData(3600u, "1 時間")]
    [InlineData(5400u, "1 時間 30 分")]
    public void Timeouts_read_in_japanese(uint seconds, string expected) => Assert.Equal(expected, PowerChoices.Timeout(seconds, Ja));

    [Fact]
    public void Durations_and_thresholds_read_in_english()
    {
        Assert.Equal("3 hr 20 min", PowerChoices.Duration(TimeSpan.FromMinutes(200), En));
        Assert.Equal("1 min", PowerChoices.Duration(TimeSpan.FromSeconds(10), En)); // never "0 min"
        Assert.Equal("Never", PowerChoices.Threshold(0, En));
        Assert.Equal("20%", PowerChoices.Threshold(20, En));
        Assert.Equal("Always", PowerChoices.Threshold(100, En));
    }

    // ---- the settings' pages in place of Windows' ----

    [Theory]
    [InlineData("ms-settings:network-wifi", "wifi")]
    [InlineData("ms-settings:bluetooth", "bluetooth")]
    [InlineData("ms-settings:sound", "sound")]
    [InlineData("ms-settings:apps-volume", "sound")]
    [InlineData("ms-settings:powersleep", "battery")]
    [InlineData("ms-settings:about", "about")]
    [InlineData("ms-settings:storagesense", "storage")]
    [InlineData("ms-settings:display", null)]
    [InlineData("ms-settings:privacy-location", null)]
    public void Search_opens_the_shell_page_where_there_is_one(string uri, string? page) => Assert.Equal(page, Frostbar.Core.Settings.ShellSettingsPages.Replacing(uri));

    [Fact]
    public void Every_page_a_windows_page_opens_instead_is_a_page_of_the_shell() =>
        Assert.All(SettingsPages.All.Select(p => Frostbar.Core.Settings.ShellSettingsPages.Replacing(p.Uri)).OfType<string>(),
            page => Assert.Contains(page, Frostbar.Core.Settings.ShellSettingsPages.All));

    // ---- Japan's holidays (the calendar) ----

    [Fact]
    public void The_holidays_of_2026()
    {
        var h = JapaneseHolidays.Of(2026);
        string[] expected =
        [
            "01-01 元日", "01-12 成人の日", "02-11 建国記念の日", "02-23 天皇誕生日", "03-20 春分の日", "04-29 昭和の日", "05-03 憲法記念日",
            "05-04 みどりの日", "05-05 こどもの日", "05-06 振替休日", "07-20 海の日", "08-11 山の日", "09-21 敬老の日", "09-22 国民の休日",
            "09-23 秋分の日", "10-12 スポーツの日", "11-03 文化の日", "11-23 勤労感謝の日",
        ];
        Assert.Equal(expected, h.Select(x => $"{x.Key:MM-dd} {x.Value}").ToArray());
    }

    [Fact]
    public void The_holidays_of_2025_with_their_substitutes()
    {
        var h = JapaneseHolidays.Of(2025);
        Assert.Equal("振替休日", h[new DateOnly(2025, 2, 24)]); // 天皇誕生日 on a Sunday
        Assert.Equal("振替休日", h[new DateOnly(2025, 5, 6)]);  // みどりの日 (May 4) on a Sunday, May 5 a holiday too
        Assert.Equal("振替休日", h[new DateOnly(2025, 11, 24)]); // 勤労感謝の日 on a Sunday
        Assert.Equal("春分の日", h[new DateOnly(2025, 3, 20)]);
        Assert.Equal("秋分の日", h[new DateOnly(2025, 9, 23)]);
        Assert.Equal(19, h.Count);
    }

    [Fact]
    public void The_olympics_moved_three_holidays_in_2021()
    {
        var h = JapaneseHolidays.Of(2021);
        Assert.Equal("海の日", h[new DateOnly(2021, 7, 22)]);
        Assert.Equal("スポーツの日", h[new DateOnly(2021, 7, 23)]);
        Assert.Equal("山の日", h[new DateOnly(2021, 8, 8)]);
        Assert.Equal("振替休日", h[new DateOnly(2021, 8, 9)]);
        Assert.False(h.ContainsKey(new DateOnly(2021, 10, 11)));
    }

    // ---- the notification centre ----

    static Notice N(uint id, string app, int minutesAgo, DateTimeOffset now) => new(id, app, app + ".id", now.AddMinutes(-minutesAgo), "t" + id, "");

    [Fact]
    public void Groups_by_app_with_the_newest_app_first()
    {
        var now = new DateTimeOffset(2026, 10, 13, 21, 0, 0, TimeSpan.FromHours(9));
        var groups = NotificationGroups.Group([N(1, "Claude", 30, now), N(2, "NVIDIA", 5, now), N(3, "Claude", 10, now), N(4, "Claude", 50, now)]);
        Assert.Equal(["NVIDIA", "Claude"], groups.Select(g => g.App).ToArray());
        Assert.Equal([3u, 1u, 4u], groups[1].Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public void When_reads_as_the_notification_centre_says_it()
    {
        var now = new DateTimeOffset(2026, 10, 13, 21, 0, 0, TimeSpan.FromHours(9)).ToLocalTime();
        Assert.Equal("たった今", NotificationGroups.When(now.AddSeconds(-20), now, Ja));
        Assert.Equal("12分前", NotificationGroups.When(now.AddMinutes(-12), now, Ja));
        Assert.Equal("12m ago", NotificationGroups.When(now.AddMinutes(-12), now, En));
        Assert.Equal("昨日", NotificationGroups.When(now.Date.AddHours(-3), now, Ja));
        Assert.Equal("10月10日", NotificationGroups.When(now.Date.AddDays(-3), now, Ja));
        Assert.Equal("Oct 10", NotificationGroups.When(now.Date.AddDays(-3), now, En));
    }
}
