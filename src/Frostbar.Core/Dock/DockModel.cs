namespace Frostbar.Core.Dock;

/// <summary>
/// What a slot holds. <see cref="MinimizedWindow"/>: a window minimized into the Dock (stage 7-2, macOS with "Minimize
/// windows into application icon" off): its picture, before the trash.
/// </summary>
public enum DockItemKind { App, Separator, AppGrid, Trash, Folder, MinimizedWindow }

/// <summary>A pinned app as settings keep it, with its AppID resolved by the platform; or a separator kept among them.</summary>
public sealed record PinnedApp(string PinKey, string AppId, string Name, string LaunchTarget, bool Separator = false);

/// <summary>A top-level app window (callers pass them top to bottom in z-order).</summary>
/// <param name="Elsewhere">On another virtual desktop: the app runs, but the window is not shown here.</param>
public sealed record RunningWindow(long Hwnd, string AppId, string AppName, string? ExePath, bool Minimized, bool Elsewhere = false);

/// <summary>One slot of the Dock.</summary>
public sealed record DockItem(DockItemKind Kind, string Key, string AppId, string Name, string? LaunchTarget, bool Pinned,
    IReadOnlyList<RunningWindow> Windows, bool Active)
{
    public bool Running => Windows.Count > 0;

    /// <summary>Dragged along the row, it moves: an app, or a separator the user put among the kept apps (the fixed items stay).</summary>
    public bool Movable => Kind == DockItemKind.App || Kind == DockItemKind.Separator && Pinned;
}

/// <summary>
/// What the Dock shows: pinned apps in their order (with the separators the user put among them), then running apps that are not pinned in the order they first
/// appeared (so icons do not move around as windows come and go), a separator, the app grid and the trash.
/// Windows belong to an item by AppID (the taskbar's grouping, ADR-005), compared without case.
/// </summary>
public static class DockModel
{
    public const string SeparatorKey = "separator", RecentSeparatorKey = "separator-recent", AppGridKey = "app-grid", TrashKey = "trash";
    public const string PinPrefix = "pin:", RecentPrefix = "recent:", FolderPrefix = "folder:", MinimizedPrefix = "minimized:";

    /// <summary>How many apps that are not running the recent section shows (macOS: three).</summary>
    public const int MaxRecent = 3;

    /// <param name="runningOrder">AppIDs of unpinned running apps in first-seen order; updated in place.</param>
    /// <param name="recent">
    /// macOS's "recent applications" on: apps used lately, the most recent first. The running apps that are not kept
    /// then stand after a separator of their own, followed by up to <see cref="MaxRecent"/> of these that are neither
    /// kept nor running. Null: off (the running apps follow the kept ones directly).
    /// </param>
    /// <param name="folders">Folders kept in the Dock (macOS's stacks: Downloads), between the app grid and the trash.</param>
    /// <param name="minimized">
    /// Windows minimized into the Dock, in their order (<see cref="MinimizedInOrder"/>), with the names they show (their
    /// titles): after the folders, before the trash (macOS). Their apps keep them among their windows too (the app
    /// still runs). Null or empty: none (macOS's "Minimize windows into application icon").
    /// </param>
    /// <param name="launching">
    /// Apps starting that are neither kept nor running yet (opened from the app grid, the search, a stack: 2026-10-23,
    /// macOS puts a starting app's icon in the Dock at once): each an item of the running apps without windows, its key
    /// the one it keeps when its windows come (no second item, nothing moving).
    /// </param>
    /// <param name="stayRecent">
    /// Apps started from the recent section that stay there while they run (its key and place: they moved to the
    /// running apps' end as their first window came).
    /// </param>
    public static IReadOnlyList<DockItem> Build(IReadOnlyList<PinnedApp> pins, IReadOnlyList<RunningWindow> windows, string? frontAppId,
        List<string> runningOrder, string appGridName, string trashName, IReadOnlyList<PinnedApp>? recent = null,
        IReadOnlyList<(string Path, string Name)>? folders = null, IReadOnlyList<(RunningWindow Window, string Name)>? minimized = null,
        IReadOnlyList<PinnedApp>? launching = null, IReadOnlyCollection<string>? stayRecent = null)
    {
        var byApp = new Dictionary<string, List<RunningWindow>>(StringComparer.OrdinalIgnoreCase);
        foreach (var w in windows)
        {
            if (!byApp.TryGetValue(w.AppId, out var list)) byApp[w.AppId] = list = new List<RunningWindow>();
            list.Add(w);
        }

        var items = new List<DockItem>();
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in pins)
        {
            if (p.Separator)
            {
                items.Add(new DockItem(DockItemKind.Separator, p.PinKey, "", p.Name, null, true, [], false));
                continue;
            }
            var mine = claimed.Add(p.AppId) && byApp.TryGetValue(p.AppId, out var ws) ? ws : (IReadOnlyList<RunningWindow>)[];
            items.Add(new DockItem(DockItemKind.App, p.PinKey, p.AppId, p.Name, p.LaunchTarget, true, mine, IsFront(p.AppId, frontAppId) && mine.Count > 0));
        }

        // Apps starting: the kept ones bounce in their own items; the others stand with the running apps from now on.
        var starting = new Dictionary<string, PinnedApp>(StringComparer.OrdinalIgnoreCase);
        foreach (var l in launching ?? []) if (!claimed.Contains(l.AppId)) starting.TryAdd(l.AppId, l);
        bool Stays(string id) => recent != null && stayRecent != null && stayRecent.Contains(id, StringComparer.OrdinalIgnoreCase)
            && recent.Any(r => string.Equals(r.AppId, id, StringComparison.OrdinalIgnoreCase));

        // Unpinned running apps: keep the order they first appeared in (a starting one from when it started), forget the ones that are gone.
        runningOrder.RemoveAll(id => !byApp.ContainsKey(id) && !starting.ContainsKey(id) || claimed.Contains(id) || Stays(id));
        foreach (var w in windows)
            if (!claimed.Contains(w.AppId) && !Stays(w.AppId) && !runningOrder.Contains(w.AppId, StringComparer.OrdinalIgnoreCase)) runningOrder.Add(w.AppId);
        foreach (var id in starting.Keys)
            if (!Stays(id) && !runningOrder.Contains(id, StringComparer.OrdinalIgnoreCase)) runningOrder.Add(id);
        var running = new List<DockItem>();
        foreach (var id in runningOrder)
        {
            if (byApp.TryGetValue(id, out var ws))
            {
                var first = ws[0];
                running.Add(new DockItem(DockItemKind.App, "running:" + id, id, first.AppName, first.ExePath, false, ws, IsFront(id, frontAppId)));
            }
            else
            {
                var s = starting[id];
                running.Add(new DockItem(DockItemKind.App, "running:" + id, id, s.Name, s.LaunchTarget, false, [], false));
            }
        }
        if (recent == null) items.AddRange(running);
        else
        {
            var lately = recent
                .Where(r => !claimed.Contains(r.AppId) && (Stays(r.AppId) || !byApp.ContainsKey(r.AppId) && !runningOrder.Contains(r.AppId, StringComparer.OrdinalIgnoreCase)))
                .DistinctBy(r => r.AppId, StringComparer.OrdinalIgnoreCase)
                .Take(MaxRecent)
                .Select(r =>
                {
                    var ws = byApp.TryGetValue(r.AppId, out var l) ? l : (IReadOnlyList<RunningWindow>)[];
                    return new DockItem(DockItemKind.App, RecentPrefix + r.AppId, r.AppId, r.Name, r.LaunchTarget, false, ws, IsFront(r.AppId, frontAppId) && ws.Count > 0);
                })
                .ToList();
            if (running.Count + lately.Count > 0)
            {
                items.Add(new DockItem(DockItemKind.Separator, RecentSeparatorKey, "", "", null, false, [], false));
                items.AddRange(running);
                items.AddRange(lately);
            }
        }

        items.Add(new DockItem(DockItemKind.Separator, SeparatorKey, "", "", null, false, [], false));
        items.Add(new DockItem(DockItemKind.AppGrid, AppGridKey, "", appGridName, null, false, [], false));
        foreach (var (path, name) in folders ?? [])
            items.Add(new DockItem(DockItemKind.Folder, FolderPrefix + path, "", name, path, false, [], false));
        foreach (var (w, name) in minimized ?? [])
            items.Add(new DockItem(DockItemKind.MinimizedWindow, MinimizedPrefix + w.Hwnd, w.AppId, string.IsNullOrWhiteSpace(name) ? w.AppName : name,
                w.ExePath, false, [w], false));
        items.Add(new DockItem(DockItemKind.Trash, TrashKey, "", trashName, null, false, [], false));
        return items;
    }

    /// <summary>
    /// The windows minimized into the Dock, in the order they were first seen minimized (the latest next to the trash,
    /// as on macOS): <paramref name="order"/> (window handles) is updated in place, the windows no longer minimized here
    /// (restored, closed, or on another desktop) leave it. <paramref name="shows"/> picks the windows that may show (a
    /// spec's shell: the test target's only); null: all.
    /// </summary>
    public static IReadOnlyList<RunningWindow> MinimizedInOrder(IReadOnlyList<RunningWindow> windows, List<long> order, Func<RunningWindow, bool>? shows = null)
    {
        var now = new Dictionary<long, RunningWindow>();
        foreach (var w in windows)
            if (w.Minimized && !w.Elsewhere && (shows == null || shows(w))) now.TryAdd(w.Hwnd, w);
        order.RemoveAll(h => !now.ContainsKey(h));
        // New ones in the order they come (top to bottom) after the ones already there.
        foreach (var w in windows) if (now.ContainsKey(w.Hwnd) && !order.Contains(w.Hwnd)) order.Add(w.Hwnd);
        return [.. order.Select(h => now[h])];
    }

    static bool IsFront(string appId, string? front) => front != null && string.Equals(appId, front, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Remembers <paramref name="app"/> as the most recently used (an app that was not kept has just quit): first in
    /// <paramref name="recent"/>, once, at most <paramref name="keep"/> remembered.
    /// </summary>
    public static void NoteRecent(List<Settings.DockPin> recent, Settings.DockPin app, int keep = 10)
    {
        recent.RemoveAll(p => string.Equals(p.Id, app.Id, StringComparison.OrdinalIgnoreCase));
        recent.Insert(0, app);
        if (recent.Count > keep) recent.RemoveRange(keep, recent.Count - keep);
    }

    /// <summary>
    /// Where in the settings' <paramref name="pins"/> a pin goes to stand at <paramref name="position"/> among the kept
    /// items the Dock shows (apps and separators; a pin whose file is missing stays in the settings but is not shown),
    /// leaving out <paramref name="exceptKey"/>.
    /// </summary>
    public static int PinIndexFor(IReadOnlyList<Settings.DockPin> pins, IReadOnlyList<DockItem> items, int position, string? exceptKey)
    {
        var shown = items.Where(x => x.Pinned && x.Key != exceptKey).ToList();
        int IndexOf(DockItem x)
        {
            for (int i = 0; i < pins.Count; i++) if (PinPrefix + pins[i].Id == x.Key) return i;
            return -1;
        }
        if (position < shown.Count && IndexOf(shown[position]) is var at and >= 0) return at;
        if (shown.Count == 0) return pins.Count;
        int last = IndexOf(shown[^1]);
        return last >= 0 ? last + 1 : pins.Count;
    }

    /// <summary>
    /// How many kept items (apps and separators) stand left of a place in the row that has <paramref name="before"/>
    /// items left of it, item <paramref name="dragged"/> (or -1) left out.
    /// </summary>
    public static int KeptBefore(IReadOnlyList<DockItem> items, int dragged, int before) =>
        items.Where((_, i) => i != dragged).Take(before).Count(x => x.Pinned);

    /// <summary>
    /// A kept item (an app or a separator) dragged along the row and let go with <paramref name="before"/> items of the
    /// row left of it (itself left out): its pin moves to the same place among the pins. False when it was let go where
    /// it was.
    /// </summary>
    public static bool MovePin(List<Settings.DockPin> pins, IReadOnlyList<DockItem> items, int dragged, int before)
    {
        string key = items[dragged].Key;
        int from = pins.FindIndex(p => PinPrefix + p.Id == key);
        if (from < 0) return false;
        var pin = pins[from];
        pins.RemoveAt(from);
        int to = Math.Min(PinIndexFor(pins, items, KeptBefore(items, dragged, before), key), pins.Count);
        pins.Insert(to, pin);
        return to != from;
    }

    /// <summary>
    /// An app carried in from the app grid and let go with <paramref name="before"/> items of the row left of it: kept
    /// there as <paramref name="app"/>; or, when the Dock keeps it already (its item <paramref name="kept"/>, left out
    /// of <paramref name="before"/>), that item moves there: one item per app. False when nothing changed.
    /// </summary>
    public static bool KeepCarried(List<Settings.DockPin> pins, IReadOnlyList<DockItem> items, Settings.DockPin app, int kept, int before)
    {
        if (kept >= 0) return MovePin(pins, items, kept, before);
        // Kept already but not shown (its item not found): not a second pin.
        if (pins.Any(p => string.Equals(p.Id, app.Id, StringComparison.OrdinalIgnoreCase))) return false;
        pins.Insert(Math.Min(PinIndexFor(pins, items, KeptBefore(items, -1, before), null), pins.Count), app);
        return true;
    }

    /// <summary>
    /// What a click on an app does: start it if no window of it is anywhere; minimize this desktop's windows if it is
    /// in front (and the setting says so) and one of them is shown; otherwise, as macOS does, bring forward every window
    /// of this desktop that is not minimized, keeping their order (stage 3-1: the topmost one ends up active; the
    /// minimized ones stay so); with none shown, the topmost of this desktop, and only then one on another desktop
    /// (Windows goes to its desktop). An app can be in front with nothing shown here: a window moved to another desktop
    /// while in front stays the foreground window, hidden (2026-10-15, vd-probe); minimizing then did nothing.
    /// </summary>
    public static DockClick Click(DockItem item, bool minimizeWhenFront)
    {
        // A minimized window's picture: that window comes back (macOS).
        if (item.Kind == DockItemKind.MinimizedWindow) return item.Windows.Count > 0 ? new DockClick(DockAction.Restore, item.Windows[0].Hwnd) : new DockClick(DockAction.None, 0);
        if (item.Kind != DockItemKind.App) return new DockClick(DockAction.None, 0);
        if (!item.Running) return new DockClick(DockAction.Launch, 0);
        if (item.Active && minimizeWhenFront && ToMinimize(item).Any()) return new DockClick(DockAction.MinimizeAll, 0);
        var here = item.Windows.Where(w => !w.Elsewhere).ToList();
        var shown = here.Where(w => !w.Minimized).ToList();
        if (shown.Count > 1) return new DockClick(DockAction.ActivateAll, shown[0].Hwnd, [.. shown.Select(w => w.Hwnd)]);
        var pick = shown.FirstOrDefault() ?? here.FirstOrDefault()
            ?? item.Windows.FirstOrDefault(w => !w.Minimized) ?? item.Windows[0];
        return new DockClick(DockAction.Activate, pick.Hwnd);
    }

    /// <summary>The windows "minimize all" minimizes: this desktop's (those on another desktop are not shown here).</summary>
    public static IEnumerable<RunningWindow> ToMinimize(DockItem item) => item.Windows.Where(w => !w.Elsewhere && !w.Minimized);
}

public enum DockAction { None, Launch, Activate, ActivateAll, MinimizeAll, Restore }

/// <param name="Windows">For <see cref="DockAction.ActivateAll"/>: the windows, top to bottom (the first is <paramref name="Hwnd"/>).</param>
public readonly record struct DockClick(DockAction Action, long Hwnd, IReadOnlyList<long>? Windows = null);
