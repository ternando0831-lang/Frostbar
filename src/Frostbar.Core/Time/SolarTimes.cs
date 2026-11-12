namespace Frostbar.Core.Time;

/// <summary>
/// A day of the sun at a place: when it rises and sets (UTC), or, on a polar day or night, neither, and whether it stays
/// up (<see cref="Up"/>) or down all day.
/// </summary>
public readonly record struct SunDay(DateTime? Rise, DateTime? Set, bool Up);

/// <summary>
/// Sunrise and sunset by NOAA's solar equations (Jean Meeus' Astronomical Algorithms): the moments the sun's upper edge
/// meets the horizon with the usual refraction, 90.833° from the zenith (the National Astronomical Observatory of Japan's
/// tables use about 90.85°, under a tenth of a minute apart). Each moment is worked out again with the sun where it is
/// at that moment, which keeps it within a minute of NAOJ's tables (unit-tested against them).
/// </summary>
public static class SolarTimes
{
    const double Zenith = 90.833;

    /// <summary>The sun on <paramref name="date"/> (the date at the place) at <paramref name="latitude"/> (north +) and <paramref name="longitude"/> (east +).</summary>
    public static SunDay On(DateOnly date, double latitude, double longitude)
    {
        var midnight = date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        // A first guess at the place's noon, then each event from the sun at the event's own moment.
        var noon = midnight.AddHours(12 - longitude / 15);
        var (rise, riseUp) = Event(midnight, noon, latitude, longitude, -1);
        var (set, setUp) = Event(midnight, noon, latitude, longitude, +1);
        if (rise == null || set == null) return new SunDay(null, null, (rise == null ? riseUp : setUp));
        return new SunDay(rise, set, true);
    }

    static (DateTime? at, bool up) Event(DateTime midnight, DateTime guess, double lat, double lon, int sign)
    {
        DateTime at = guess;
        for (int i = 0; i < 3; i++)
        {
            var (minutes, up) = Minutes(at, lat, lon, sign);
            if (minutes == null) return (null, up);
            at = midnight.AddMinutes(minutes.Value);
        }
        return (at, true);
    }

    /// <summary>The event's minutes from the date's 0h UTC, with the sun where it is at <paramref name="at"/>; null and whether the sun stays up when it neither rises nor sets.</summary>
    static (double? minutes, bool up) Minutes(DateTime at, double lat, double lon, int sign)
    {
        double jd = at.ToOADate() + 2415018.5; // the Julian day (OLE dates count from 1899-12-30 0h)
        double t = (jd - 2451545.0) / 36525.0;  // Julian centuries from J2000
        double l0 = Norm(280.46646 + t * (36000.76983 + t * 0.0003032)); // the sun's mean longitude
        double m = 357.52911 + t * (35999.05029 - 0.0001537 * t);        // its mean anomaly
        double e = 0.016708634 - t * (0.000042037 + 0.0000001267 * t);   // the earth orbit's eccentricity
        double c = Sin(m) * (1.914602 - t * (0.004817 + 0.000014 * t)) + Sin(2 * m) * (0.019993 - 0.000101 * t) + Sin(3 * m) * 0.000289;
        double omega = 125.04 - 1934.136 * t;
        double lambda = l0 + c - 0.00569 - 0.00478 * Sin(omega);         // its apparent longitude
        double eps0 = 23 + (26 + (21.448 - t * (46.815 + t * (0.00059 - t * 0.001813))) / 60) / 60;
        double eps = eps0 + 0.00256 * Cos(omega);                         // the obliquity, corrected
        double decl = Deg(Math.Asin(Sin(eps) * Sin(lambda)));             // the sun's declination
        double y = Math.Pow(Math.Tan(Rad(eps / 2)), 2);
        double eqTime = 4 * Deg(y * Sin(2 * l0) - 2 * e * Sin(m) + 4 * e * y * Sin(m) * Cos(2 * l0) - 0.5 * y * y * Sin(4 * l0) - 1.25 * e * e * Sin(2 * m));
        double cosH = Cos(Zenith) / (Cos(lat) * Cos(decl)) - Math.Tan(Rad(lat)) * Math.Tan(Rad(decl));
        if (cosH < -1) return (null, true);  // above the horizon all day
        if (cosH > 1) return (null, false);  // below it all day
        double hourAngle = Deg(Math.Acos(cosH));
        return (720 - 4 * lon - eqTime + sign * 4 * hourAngle, true);
    }

    static double Rad(double deg) => deg * Math.PI / 180;
    static double Deg(double rad) => rad * 180 / Math.PI;
    static double Sin(double deg) => Math.Sin(Rad(deg));
    static double Cos(double deg) => Math.Cos(Rad(deg));
    static double Norm(double deg) => ((deg % 360) + 360) % 360;
}
