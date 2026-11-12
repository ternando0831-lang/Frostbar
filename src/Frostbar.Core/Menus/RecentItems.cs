using Frostbar.Core.Localization;
using Frostbar.Core.Settings;

namespace Frostbar.Core.Menus;

/// <summary>A document Windows lists as used lately (a link in shell:recent).</summary>
/// <param name="Name">As File Explorer shows it (known extensions hidden if it hides them).</param>
/// <param name="Link">The link's path: what opens it, as Explorer's Recent does (the link follows a moved file).</param>
/// <param name="Target">The file it points at (its icon, and a spec's guard); null when the link does not say.</param>
/// <param name="Used">When it was last used: the link's last write (UTC).</param>
public sealed record RecentDocument(string Name, string Link, string? Target, DateTime Used);

/// <summary>
/// macOS's Recent Items (the Apple menu, stage 5-2): the apps that came to the front lately (the shell keeps them) and
/// the documents Windows lists as used lately, the most recent first, as many of each as the settings say (macOS's
/// "Recent documents, applications and servers": <see cref="Counts"/>, <see cref="Count"/> by default). "Clear Menu"
/// forgets the apps and leaves out the documents used until then: Windows' own list (File Explorer's Recent) is not touched.
/// </summary>
public static class RecentItems
{
    /// <summary>How many apps and how many documents by default (macOS's).</summary>
    public const int Count = 10;

    /// <summary>The most the settings can ask for, and so the apps kept (a larger count then shows those used before it was raised).</summary>
    public const int Max = 50;

    /// <summary>The counts the menu bar's page offers, in its order (macOS's: none, 5, 10, 15, 20, 30, 50). The validator's list too.</summary>
    public static readonly int[] Counts = [0, 5, 10, 15, 20, 30, Max];

    /// <summary>
    /// Remembers <paramref name="app"/> as the most recent: first, once, at most <see cref="Max"/> kept. False when it
    /// was first already (nothing changed: switching back and forth between two apps changes the list every time).
    /// </summary>
    public static bool Note(List<DockPin> apps, DockPin app)
    {
        if (apps.Count > 0 && string.Equals(apps[0].Id, app.Id, StringComparison.OrdinalIgnoreCase))
        {
            if (app.Name == null || apps[0].Name == app.Name) return false;
            apps[0].Name = app.Name; // the same app, named better now (the Start menu's list came)
            return true;
        }
        apps.RemoveAll(p => string.Equals(p.Id, app.Id, StringComparison.OrdinalIgnoreCase));
        apps.Insert(0, app);
        if (apps.Count > Max) apps.RemoveRange(Max, apps.Count - Max);
        return true;
    }

    /// <summary>What opens an app kept as <paramref name="app"/>: its AppsFolder entry, or its program.</summary>
    public static string Target(DockPin app) => app.Kind == "appsFolder" ? @"shell:AppsFolder\" + app.Id : app.Id;

    /// <summary>The documents to list: used after <paramref name="clearedAt"/>, the newest first, at most <paramref name="count"/>.</summary>
    public static IReadOnlyList<RecentDocument> Documents(IEnumerable<RecentDocument> all, DateTime? clearedAt, int count = Count) =>
        all.Where(d => clearedAt is not { } c || d.Used > c).OrderByDescending(d => d.Used).Take(count).ToList();

    /// <summary>
    /// The system menu's "Recent Items ›": "Applications" over the apps, "Documents" over the documents (each heading
    /// only when it has some), at most <paramref name="count"/> of each, then "Clear Menu" (dim when there is nothing to
    /// clear). Each item opens its target. Null for a count of none: the system menu goes without it.
    /// </summary>
    public static MenuItem? Submenu(IReadOnlyList<DockPin> apps, IReadOnlyList<RecentDocument> documents, Strings s, int count = Count)
    {
        if (count <= 0) return null;
        var children = new List<MenuItem>();
        if (apps.Count > 0)
        {
            children.Add(Heading("recent-apps", s["sys.recentApps"]));
            for (int i = 0; i < apps.Count && i < count; i++)
            {
                string target = Target(apps[i]);
                children.Add(new($"recent-app-{i}", apps[i].Name ?? Path.GetFileNameWithoutExtension(apps[i].Id), null, MenuAction.Open(target), Icon: target));
            }
        }
        if (documents.Count > 0)
        {
            children.Add(MenuItem.Separator);
            children.Add(Heading("recent-documents", s["sys.recentDocuments"]));
            for (int i = 0; i < documents.Count && i < count; i++)
                children.Add(new($"recent-document-{i}", documents[i].Name, null, MenuAction.Open(documents[i].Link), Icon: documents[i].Target ?? documents[i].Link));
        }
        bool any = children.Count > 0;
        if (!any) children.Add(new("recent-none", s["sys.recentNone"], null, MenuAction.Nothing, Enabled: false));
        children.Add(MenuItem.Separator);
        children.Add(new("clear-recent", s["sys.recentClear"], null, MenuAction.System(SystemOps.ClearRecent), Enabled: any));
        return new MenuItem("recent", s["sys.recent"], null, MenuAction.Nothing, Children: MenuBuilder.Tidy(children));
    }

    /// <summary>A section's name: dim, never chosen (macOS's "Applications" and "Documents").</summary>
    static MenuItem Heading(string id, string label) => new(id, label, null, MenuAction.Nothing, Enabled: false);
}
