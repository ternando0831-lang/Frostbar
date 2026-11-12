using Frostbar.Core.Localization;

namespace Frostbar.Core.Shell;

/// <summary>A notification as the notification centre lists it (from Windows' notification centre).</summary>
public sealed record Notice(uint Id, string App, string AppId, DateTimeOffset Created, string Title, string Body);

/// <summary>One app's notifications, newest first.</summary>
public sealed record NoticeGroup(string App, string AppId, IReadOnlyList<Notice> Items)
{
    public DateTimeOffset Newest => Items[0].Created;
}

/// <summary>
/// How the notification centre arranges Windows' notifications (macOS's stacks): one group per app, the app with the
/// newest notification first; and how a notification's time reads ("now", "12m ago", "Yesterday", "Oct 11").
/// </summary>
public static class NotificationGroups
{
    public static IReadOnlyList<NoticeGroup> Group(IEnumerable<Notice> notices) =>
        notices.GroupBy(n => n.AppId.Length > 0 ? n.AppId : n.App, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var items = g.OrderByDescending(n => n.Created).ToList();
                return new NoticeGroup(items[0].App, items[0].AppId, items);
            })
            .OrderByDescending(g => g.Newest)
            .ToList();

    /// <summary>When, as the notification centre says it: within a minute "now", then minutes, hours, yesterday, a date.</summary>
    public static string When(DateTimeOffset created, DateTimeOffset now, Strings s)
    {
        var age = now - created;
        if (age < TimeSpan.FromMinutes(1)) return s["time.now"];
        if (age < TimeSpan.FromHours(1)) return s.F("time.minutes", (int)age.TotalMinutes);
        var local = created.ToLocalTime();
        var today = now.ToLocalTime().Date;
        if (local.Date == today) return s.F("time.hours", Math.Max(1, (int)age.TotalHours));
        if (local.Date == today.AddDays(-1)) return s["time.yesterday"];
        return local.ToString(s.Language == "ja" ? "M月d日" : "MMM d", s.Culture);
    }
}
