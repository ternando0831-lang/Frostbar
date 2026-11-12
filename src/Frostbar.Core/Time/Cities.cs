namespace Frostbar.Core.Time;

/// <summary>A place to take the sunrise and sunset from (the Appearance page's choice): its key, names and position.</summary>
public sealed record City(string Key, string NameJa, string NameEn, double Latitude, double Longitude);

/// <summary>
/// The places the Appearance page offers for switching to dark at sunset: Japan's large cities (their positions as the
/// National Astronomical Observatory of Japan's tables have them, where they are listed) and some of the world's.
/// </summary>
public static class Cities
{
    public static readonly IReadOnlyList<City> All =
    [
        new("sapporo", "札幌", "Sapporo", 43.0667, 141.3500),
        new("sendai", "仙台", "Sendai", 38.2667, 140.8667),
        new("tokyo", "東京", "Tokyo", 35.6833, 139.7500),
        new("nagoya", "名古屋", "Nagoya", 35.1667, 136.9667),
        new("kyoto", "京都", "Kyoto", 35.0167, 135.7500),
        new("osaka", "大阪", "Osaka", 34.6833, 135.4833),
        new("hiroshima", "広島", "Hiroshima", 34.4000, 132.4667),
        new("fukuoka", "福岡", "Fukuoka", 33.6000, 130.4000),
        new("naha", "那覇", "Naha", 26.2167, 127.6833),
        new("seoul", "ソウル", "Seoul", 37.5665, 126.9780),
        new("shanghai", "上海", "Shanghai", 31.2304, 121.4737),
        new("singapore", "シンガポール", "Singapore", 1.3521, 103.8198),
        new("sydney", "シドニー", "Sydney", -33.8688, 151.2093),
        new("london", "ロンドン", "London", 51.5074, -0.1278),
        new("paris", "パリ", "Paris", 48.8566, 2.3522),
        new("newyork", "ニューヨーク", "New York", 40.7128, -74.0060),
        new("losangeles", "ロサンゼルス", "Los Angeles", 34.0522, -118.2437),
    ];

    /// <summary>The city of <paramref name="key"/>, else the one of the PC's time zone (<paramref name="timeZoneId"/>, Windows' id), else Tokyo.</summary>
    public static City For(string? key, string? timeZoneId)
    {
        if (All.FirstOrDefault(c => c.Key == key) is { } chosen) return chosen;
        string fallback = timeZoneId switch
        {
            "Korea Standard Time" => "seoul",
            "China Standard Time" => "shanghai",
            "Singapore Standard Time" => "singapore",
            "AUS Eastern Standard Time" => "sydney",
            "GMT Standard Time" => "london",
            "Romance Standard Time" or "W. Europe Standard Time" => "paris",
            "Eastern Standard Time" => "newyork",
            "Pacific Standard Time" => "losangeles",
            _ => "tokyo",
        };
        return All.First(c => c.Key == fallback);
    }
}
