using Frostbar.Core.Localization;

namespace Frostbar.Core.Search;

/// <summary>
/// Something the search does rather than opens (Spotlight's actions, stage 6-1): its id, the string its title is,
/// words it is found by in either language, and whether it asks first (it cannot be undone, or it ends the session).
/// </summary>
public sealed record SearchAction(string Id, string TitleKey, string[] Keywords, bool Asks = false);

/// <summary>
/// The search's actions: the system menu's (lock, sleep, restart…), the Dock's "Empty Trash", what the hot corners
/// open, and switches the Control Center has. A switch has one action for each way, and only the one that changes
/// something is offered (<see cref="ActionIndex.Find"/>'s <c>available</c>).
/// </summary>
public static class SearchActions
{
    public const string Lock = "lock", Sleep = "sleep", Restart = "restart", Shutdown = "shutdown", Logout = "logout", EmptyTrash = "emptyTrash",
        ForceQuit = "forceQuit", AboutPc = "aboutPc", Desktop = "desktop", Overview = "overview", AppGrid = "appGrid", Notifications = "notifications",
        ControlCenter = "controlCenter", DarkOn = "darkOn", DarkOff = "darkOff", DndOn = "dndOn", DndOff = "dndOff";

    /// <summary>In the order the Actions filter lists them with nothing typed.</summary>
    public static readonly IReadOnlyList<SearchAction> All =
    [
        new(Lock, "sys.lock", ["lock", "lock screen", "ロック", "画面をロック", "画面ロック"]),
        new(Sleep, "sys.sleep", ["sleep", "スリープ"]),
        new(Restart, "sys.restart", ["restart", "reboot", "再起動"], Asks: true),
        new(Shutdown, "sys.shutdown", ["shut down", "shutdown", "power off", "システム終了", "シャットダウン", "電源を切る"], Asks: true),
        new(Logout, "sys.logout", ["log out", "logout", "sign out", "ログアウト", "サインアウト"], Asks: true),
        new(EmptyTrash, "trash.emptyAction", ["empty trash", "trash", "recycle bin", "ごみ箱を空にする", "ゴミ箱を空にする", "ごみ箱", "ゴミ箱"], Asks: true),
        new(ForceQuit, "sys.forceQuit", ["force quit", "kill", "強制終了"]),
        new(DarkOn, "act.darkOn", ["dark mode", "dark", "ダークモード", "ダーク"]),
        new(DarkOff, "act.darkOff", ["light mode", "dark mode", "light", "ライトモード", "ダークモード", "ライト"]),
        new(DndOn, "act.dndOn", ["do not disturb", "dnd", "focus", "おやすみモード", "おやすみ", "集中モード"]),
        new(DndOff, "act.dndOff", ["do not disturb", "dnd", "focus", "おやすみモード", "おやすみ", "集中モード"]),
        new(Desktop, "act.desktop", ["show desktop", "desktop", "デスクトップを表示", "デスクトップ"]),
        new(Overview, "corner.overview", ["mission control", "window overview", "ウインドウ一覧", "ミッションコントロール"]),
        new(AppGrid, "corner.appGrid", ["launchpad", "app grid", "アプリ一覧", "ローンチパッド"]),
        new(Notifications, "corner.notifications", ["notification center", "notifications", "通知センター", "通知"]),
        new(ControlCenter, "act.controlCenter", ["control center", "コントロールセンター"]),
        new(AboutPc, "sys.aboutPc", ["about this pc", "about", "このPCについて", "このPC"]),
    ];
}

/// <summary>
/// The actions prepared for matching once (a language's titles do not change while the shell runs): each is found by
/// its title in the display language and in the other, and by its words.
/// </summary>
public sealed class ActionIndex
{
    readonly (SearchAction Action, string Title, Candidate[] Names, Candidate[] Words)[] _items;

    public ActionIndex(Strings shown, Strings other)
    {
        static Candidate[] Prepare(IEnumerable<string> names) =>
            [.. names.Select(n => n.TrimEnd('…')).Distinct(StringComparer.Ordinal).Select(n => new Candidate(n))];
        _items = [.. SearchActions.All.Select(a => (a, shown[a.TitleKey], Prepare([shown[a.TitleKey], other[a.TitleKey]]), Prepare(a.Keywords)))];
    }

    /// <summary>
    /// The actions that answer the normalized <paramref name="query"/>, among those <paramref name="available"/> says
    /// can be done now (a switch's other way, an empty bin's emptying are not). A word ranks just below a name
    /// (<see cref="SearchRanker.KeywordPenalty"/>).
    /// </summary>
    public IEnumerable<SearchResult> Find(string query, Func<string, bool> available) =>
        _items.Select(i => (i, score: Math.Max(i.Names.Max(n => Matcher.Score(query, n)), i.Words.Max(n => Matcher.Score(query, n) - SearchRanker.KeywordPenalty))))
              .Where(x => x.score > 0 && available(x.i.Action.Id)) // asked only of the ones that answer (reading a switch can cost)
              .Select(x => new SearchResult(SearchKind.Action, x.i.Title, null, x.i.Action.Id, x.score));

    /// <summary>Every action <paramref name="available"/> allows, in the list's order (the Actions filter with nothing typed).</summary>
    public IEnumerable<SearchResult> Everything(Func<string, bool> available) =>
        _items.Where(i => available(i.Action.Id)).Select((i, n) => new SearchResult(SearchKind.Action, i.Title, null, i.Action.Id, Matcher.Exact - n));

    /// <summary>The action an answer is, or null.</summary>
    public static SearchAction? Of(SearchResult r) => r.Kind == SearchKind.Action ? SearchActions.All.FirstOrDefault(a => a.Id == r.Target) : null;
}
