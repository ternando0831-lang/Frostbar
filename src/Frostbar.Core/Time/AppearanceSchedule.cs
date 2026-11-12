namespace Frostbar.Core.Time;

/// <summary>
/// When Windows goes dark and light by itself (stage 4-3, macOS's automatic appearance): from sunset to sunrise at a
/// place, or between two times of the day. Says whether it is dark at a moment, since when and until when; the shell
/// sets a one-off timer for that moment and works it out again after a sleep or a change of the settings.
/// </summary>
public static class AppearanceSchedule
{
    /// <summary>
    /// Dark or not, since when (the last change, or the start of the day looked at on a polar day or night) and until
    /// when. The shell switches Windows once for each <see cref="Since"/>: a switch made by hand meanwhile stays until
    /// the next change.
    /// </summary>
    public readonly record struct State(bool Dark, DateTimeOffset Since, DateTimeOffset Next);

    /// <summary>Dark from sunset to sunrise at the place. On a polar day or night it stays as the sun does, and is looked at again the next day.</summary>
    public static State BySun(DateTimeOffset now, double latitude, double longitude)
    {
        var utc = now.UtcDateTime;
        // The place's date: its solar time (UTC + longitude / 15 h).
        var placeDate = DateOnly.FromDateTime(utc.AddHours(longitude / 15));
        var events = new List<(DateTime at, bool darkAfter)>();
        bool? upAllDay = null;
        for (int d = -1; d <= 2; d++)
        {
            var day = SolarTimes.On(placeDate.AddDays(d), latitude, longitude);
            if (day.Rise is { } rise && day.Set is { } set) { events.Add((rise, false)); events.Add((set, true)); }
            else if (d == 0) upAllDay = day.Up;
        }
        events.Sort((a, b) => a.at.CompareTo(b.at));
        var past = events.LastOrDefault(e => e.at <= utc);
        var coming = events.FirstOrDefault(e => e.at > utc);
        bool dark;
        DateTime since, next;
        if (upAllDay is bool up)
        {
            // A polar day or night: as the sun is today, looked at again within a day.
            dark = !up;
            since = placeDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc).AddHours(-longitude / 15);
            next = coming != default && coming.at < utc.AddDays(1) ? coming.at : utc.AddDays(1);
        }
        else
        {
            // Dark after the last sunset, light after the last sunrise.
            dark = past != default ? past.darkAfter : coming != default && !coming.darkAfter;
            since = past != default ? past.at : utc.AddDays(-1);
            next = coming != default ? coming.at : utc.AddDays(1);
        }
        return new State(dark, Utc(since).ToOffset(now.Offset), Utc(next).ToOffset(now.Offset));
    }

    /// <summary>
    /// Dark from <paramref name="darkFrom"/> until <paramref name="darkUntil"/> (local times in <paramref name="zone"/>;
    /// the dark part may cross midnight). The same two times: never dark.
    /// </summary>
    public static State ByClock(DateTimeOffset now, TimeOnly darkFrom, TimeOnly darkUntil, TimeZoneInfo zone)
    {
        if (darkFrom == darkUntil) return new State(false, now.AddDays(-1), now.AddDays(1));
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var today = DateOnly.FromDateTime(local.DateTime);
        var t = TimeOnly.FromDateTime(local.DateTime);
        bool dark = darkFrom < darkUntil ? t >= darkFrom && t < darkUntil : t >= darkFrom || t < darkUntil;
        // The changes around now: the two times yesterday, today and tomorrow.
        var changes = new List<DateTimeOffset>();
        for (int d = -1; d <= 1; d++)
            foreach (var at in new[] { darkFrom, darkUntil })
                changes.Add(At(today.AddDays(d), at, zone));
        var since = changes.Where(c => c <= now).Max();
        var next = changes.Where(c => c > now).Min();
        return new State(dark, since.ToOffset(now.Offset), next.ToOffset(now.Offset));
    }

    static DateTimeOffset Utc(DateTime utc) => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeSpan.Zero);

    /// <summary>A date and time of the day in <paramref name="zone"/> (a time skipped by a clock change counts as the hour after it).</summary>
    static DateTimeOffset At(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local)) local = local.AddHours(1);
        return new DateTimeOffset(local, zone.GetUtcOffset(local));
    }
}
