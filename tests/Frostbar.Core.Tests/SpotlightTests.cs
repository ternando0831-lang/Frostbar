using System.Globalization;
using Frostbar.Core.Localization;
using Frostbar.Core.Search;
using Frostbar.Core.Settings;

namespace Frostbar.Core.Tests;

/// <summary>Stage 6-1: Frostbar's own settings pages in the search (found by their names and by the texts on them).</summary>
public class ShellSettingsSearchTests
{
    /// <summary>A page's other names as the index has them: the English title (the Displays page's is macOS's 「Displays」:
    /// a hand-made 「Display」 hid that, 2026-10-16) and its other names.</summary>
    static IReadOnlyList<string> En(string page) => ShellSettingsPages.OtherNames(page, k => Strings.For("en", CultureInfo.InvariantCulture)[k]);

    static readonly ShellPageWords Display = new("display", "ディスプレイ", En("display"), ShellSettingsPages.AlsoFoundBy["display"], ["解像度", "拡大縮小"]);
    static readonly ShellPageWords MenuBar = new("menuBar", "メニューバー", En("menuBar"), [], ["時計の秒を表示", "メニューバーを自動的に隠す", "メニューバーの項目を並べ替えるには、Ctrl キーを押しながら項目をドラッグします。"]);
    static readonly ShellPageWords Wifi = new("wifi", "Wi-Fi", En("wifi"), [], ["既知のネットワーク"]);
    static readonly ShellPageWords Battery = new("battery", "バッテリー", En("battery"), ShellSettingsPages.AlsoFoundBy["battery"], ["省電力モード"]);
    static readonly ShellPageWords[] Pages = [Display, MenuBar, Wifi, Battery];

    static List<SearchResult> Answers(string query, bool japanese = true) => ShellSettingsSearch.Answers(TextNormalizer.Normalize(query), japanese, Pages);

    static SearchResult? First(string query) => Answers(query).OrderByDescending(r => r.Score).FirstOrDefault();

    [Theory]
    [InlineData("ディスプレイ")]
    [InlineData("display")] // the page's title in the other language
    public void FrostbarsOwnPageComesBeforeWindowsOfTheSameName(string query)
    {
        var list = Answers(query).OrderByDescending(r => r.Score).ToList();
        Assert.Equal(ShellSettingsSearch.Target("display"), list[0].Target);
        Assert.Contains(list, r => r.Target == "ms-settings:display"); // Windows' page still answers, after it
    }

    [Fact]
    public void ThroughTheIndexAndTheRankerFrostbarsDisplaysPageComesFirstForDisplay()
    {
        // As the shell does it: the index from what the page shows, the answers, then the panel's order.
        var pages = ShellSettingsSearch.Index([("display", "ディスプレイ", En("display"), (IReadOnlyList<string>)ShellSettingsPages.AlsoFoundBy["display"], (IReadOnlyList<string>)["解像度", "表示スケール"])]);
        var settings = ShellSettingsSearch.Answers("display", true, pages);
        var list = SearchRanker.Arrange(new SearchAnswers(null, [], [], [], settings, [], null)).Where(r => r.Kind == SearchKind.Setting).ToList();
        Assert.Equal(ShellSettingsSearch.Target("display"), list[0].Target);
    }

    [Fact]
    public void AWindowsPageFrostbarTakesThePlaceOfOpensFrostbarsAndEachPageAnswersOnce()
    {
        var list = Answers("wifi");
        Assert.Single(list, r => r.Target == ShellSettingsSearch.Target("wifi"));
        Assert.DoesNotContain(list, r => r.Target == "ms-settings:network-wifi");
        // Without Frostbar's pages read yet, Windows' Wi-Fi page still opens Frostbar's.
        var before = ShellSettingsSearch.Answers("wifi", true, []);
        Assert.Contains(before, r => r.Target == ShellSettingsSearch.Target("wifi"));
    }

    [Fact]
    public void APageFoundByARowIsNamedByTheRowWithItsPageBeside()
    {
        var r = First("秒を表示")!;
        Assert.Equal(ShellSettingsSearch.Target("menuBar"), r.Target);
        Assert.Equal("時計の秒を表示", r.Title);
        Assert.Equal("メニューバー", r.Detail);
    }

    [Fact]
    public void ALongRowIsNotShownAsAName()
    {
        var r = First("ドラッグ")!;
        Assert.Equal(ShellSettingsSearch.Target("menuBar"), r.Target);
        Assert.Equal("メニューバー", r.Title);
        Assert.Null(r.Detail);
    }

    [Fact]
    public void RowsAnswerFromTwoCharacters() => Assert.DoesNotContain(Answers("秒"), r => r.Target == ShellSettingsSearch.Target("menuBar"));

    [Fact]
    public void ARowRanksBelowAPageNameOfTheSameTier()
    {
        // 「メニューバー」 is the page's name; a row starting with it ranks below a name starting with it.
        var (named, row) = ShellSettingsSearch.Score(MenuBar, TextNormalizer.Normalize("メニューバー"));
        Assert.Equal(Matcher.Exact, named);
        Assert.Null(row);
        var (byRow, text) = ShellSettingsSearch.Score(MenuBar, TextNormalizer.Normalize("自動的に隠す"));
        Assert.NotNull(text);
        Assert.True(byRow < Matcher.Inside, $"{byRow}");
    }

    [Fact]
    public void AWordAPageIsFoundByRanksJustBelowAName()
    {
        // 「スリープ」 is a word of the battery page (shown nowhere on it): just below a page or an action named so.
        var (score, _) = ShellSettingsSearch.Score(Battery, TextNormalizer.Normalize("スリープ"));
        Assert.Equal(Matcher.Exact - SearchRanker.KeywordPenalty, score);
    }

    [Fact]
    public void ATextOnThreePagesOrMoreFindsNone()
    {
        IReadOnlyList<string> none = [];
        var pages = ShellSettingsSearch.Index(
        [
            ("a", "A", none, none, ["オン", "時計", "x"]),
            ("b", "B", none, none, ["オン", "時計"]),
            ("c", "C", none, none, ["オン", "壁紙"]),
        ]);
        Assert.All(pages, p => Assert.DoesNotContain(p.Texts, t => t.Name == "オン"));
        Assert.Contains(pages[0].Texts, t => t.Name == "時計"); // on two pages: kept
        Assert.DoesNotContain(pages[0].Texts, t => t.Name == "x"); // one character
        Assert.Contains(pages[2].Texts, t => t.Name == "壁紙");
    }

    [Fact]
    public void EveryPageHasATitleInBothLanguages()
    {
        foreach (var page in ShellSettingsPages.All)
        {
            string key = ShellSettingsPages.TitleKey(page);
            Assert.Contains(key, Strings.JaKeys);
            Assert.Contains(key, Strings.EnKeys);
        }
    }

    [Fact]
    public void TheTargetCarriesThePage()
    {
        Assert.Equal("hotCorners", ShellSettingsSearch.PageOf(ShellSettingsSearch.Target("hotCorners")));
        Assert.Null(ShellSettingsSearch.PageOf("ms-settings:display"));
    }
}

/// <summary>Stage 6-1: the search's actions (Spotlight's).</summary>
public class SearchActionsTests
{
    static readonly ActionIndex Ja = new(Strings.For("ja", CultureInfo.InvariantCulture), Strings.For("en", CultureInfo.InvariantCulture));

    static List<SearchResult> Find(string q, Func<string, bool>? available = null) =>
        [.. Ja.Find(TextNormalizer.Normalize(q), available ?? (_ => true)).OrderByDescending(r => r.Score)];

    [Theory]
    [InlineData("ロック", SearchActions.Lock)]
    [InlineData("lock", SearchActions.Lock)] // the other language's words
    [InlineData("スリープ", SearchActions.Sleep)]
    [InlineData("再起動", SearchActions.Restart)]
    [InlineData("shut down", SearchActions.Shutdown)]
    [InlineData("ごみ箱", SearchActions.EmptyTrash)]
    [InlineData("ゴミ箱", SearchActions.EmptyTrash)]
    [InlineData("通知センター", SearchActions.Notifications)]
    [InlineData("launchpad", SearchActions.AppGrid)]
    public void FindsTheActionInEitherLanguage(string query, string id) => Assert.Equal(id, Find(query).First().Target);

    [Fact]
    public void AnActionsNameRanksAboveAWordItIsFoundBy()
    {
        Assert.Equal(Matcher.Exact, Find("スリープ").First().Score); // its name
        Assert.Equal(Matcher.Exact - SearchRanker.KeywordPenalty, Find("ロック").First().Score); // a word (its name is 画面をロック)
    }

    [Fact]
    public void OnlyTheWayASwitchCanGoIsOffered()
    {
        var dark = Find("ダークモード", id => id != SearchActions.DarkOn); // already dark
        Assert.Contains(dark, r => r.Target == SearchActions.DarkOff);
        Assert.DoesNotContain(dark, r => r.Target == SearchActions.DarkOn);
    }

    [Fact]
    public void AvailabilityIsAskedOnlyOfTheActionsThatAnswer()
    {
        var asked = new List<string>();
        _ = Find("ロック", id => { asked.Add(id); return true; });
        Assert.DoesNotContain(SearchActions.DndOn, asked); // reading do not disturb costs a COM call
        Assert.Contains(SearchActions.Lock, asked);
    }

    [Fact]
    public void EverythingListsTheAvailableInTheListsOrder()
    {
        var all = Ja.Everything(id => id != SearchActions.EmptyTrash).Select(r => r.Target).ToList();
        Assert.Equal(SearchActions.All.Select(a => a.Id).Where(id => id != SearchActions.EmptyTrash), all);
    }

    [Fact]
    public void TheOnesThatCannotBeUndoneAsk()
    {
        foreach (var id in new[] { SearchActions.Restart, SearchActions.Shutdown, SearchActions.Logout, SearchActions.EmptyTrash })
            Assert.True(SearchActions.All.Single(a => a.Id == id).Asks, id);
        Assert.False(SearchActions.All.Single(a => a.Id == SearchActions.Lock).Asks);
    }

    [Fact]
    public void IdsAreUniqueAndTitlesExistInBothLanguages()
    {
        Assert.Equal(SearchActions.All.Count, SearchActions.All.Select(a => a.Id).Distinct().Count());
        foreach (var a in SearchActions.All)
        {
            Assert.Contains(a.TitleKey, Strings.JaKeys);
            Assert.Contains(a.TitleKey, Strings.EnKeys);
        }
    }
}

/// <summary>Stage 6-1: windows, actions, the filters and the recent documents in the search's order.</summary>
public class SearchArrangeTests
{
    static SearchResult R(SearchKind k, string title, int score, string? target = null) => new(k, title, null, target ?? title, score);

    static SearchAnswers A(SearchResult[]? apps = null, SearchResult[]? windows = null, SearchResult[]? actions = null, SearchResult[]? settings = null,
        SearchResult[]? files = null, bool listing = false) =>
        new(null, apps ?? [], windows ?? [], actions ?? [], settings ?? [], files ?? [], R(SearchKind.Web, "web", 0), listing);

    [Fact]
    public void WindowsComeAfterTheAppsThenTheActionsThenTheSettings()
    {
        var list = SearchRanker.Arrange(A([R(SearchKind.App, "Notepad", 900)], [R(SearchKind.Window, "notes - Notepad", 700)],
            [R(SearchKind.Action, "Lock Screen", 600)], [R(SearchKind.Setting, "Notifications", 650)]));
        Assert.Equal(["Notepad", "notes - Notepad", "Lock Screen", "Notifications", "web"], list.Select(x => x.Title));
    }

    [Fact]
    public void AnActionNamedByWhatWasTypedIsTheTopHitOverAPartOfAnApp()
    {
        var list = SearchRanker.Arrange(A([R(SearchKind.App, "Sleep Timer", Matcher.Inside)], actions: [R(SearchKind.Action, "Sleep", Matcher.Exact)]));
        Assert.Equal("Sleep", list[0].Title);
        // A word it is found by (just below a name) is enough too.
        var byWord = SearchRanker.Arrange(A([R(SearchKind.App, "Clock", Matcher.Inside)], actions: [R(SearchKind.Action, "Lock Screen", Matcher.Exact - SearchRanker.KeywordPenalty)]));
        Assert.Equal("Lock Screen", byWord[0].Title);
        // An app that starts with what was typed stays the top hit.
        var app = SearchRanker.Arrange(A([R(SearchKind.App, "Sleepy", Matcher.Prefix)], actions: [R(SearchKind.Action, "Sleep", Matcher.Exact)]));
        Assert.Equal("Sleepy", app[0].Title);
    }

    [Fact]
    public void TheActionsNameBeatsTheWordOfASetting()
    {
        // 「スリープ」: the action is named so, the battery's page is found by it.
        var list = SearchRanker.Arrange(A(actions: [R(SearchKind.Action, "スリープ", Matcher.Exact)], settings: [R(SearchKind.Setting, "バッテリー", Matcher.Exact - SearchRanker.KeywordPenalty)]));
        Assert.Equal("スリープ", list[0].Title);
    }

    [Fact]
    public void ActionsAndWindowsAreCapped()
    {
        var many = Enumerable.Range(0, 9).Select(i => R(SearchKind.Action, "a" + i, 500)).ToArray();
        var windows = Enumerable.Range(0, 9).Select(i => R(SearchKind.Window, "w" + i, 500)).ToArray();
        var list = SearchRanker.Arrange(A(windows: windows, actions: many));
        Assert.Equal(SearchRanker.MaxActions, list.Count(x => x.Kind == SearchKind.Action)); // the top hit is one of them
        Assert.Equal(SearchRanker.MaxWindows, list.Count(x => x.Kind == SearchKind.Window));
    }

    [Fact]
    public void UnderAFilterOnlyItsKindsAnswer()
    {
        var a = A([R(SearchKind.App, "Notes", 900)], [R(SearchKind.Window, "notes", 700)], [R(SearchKind.Action, "Lock", 600)], [R(SearchKind.Setting, "Notifications", 650)],
            [R(SearchKind.File, "notes.txt", 700), R(SearchKind.Note, "Note", 600)]);
        Assert.Equal(["Notes", "notes"], SearchRanker.Arrange(a, SearchFilter.Apps).Select(x => x.Title));
        Assert.Equal(["Note", "notes.txt"], SearchRanker.Arrange(a, SearchFilter.Files).Select(x => x.Title));
        Assert.Equal(["Lock"], SearchRanker.Arrange(a, SearchFilter.Actions).Select(x => x.Title));
        Assert.Empty(SearchRanker.Arrange(a, SearchFilter.Clipboard));
    }

    [Fact]
    public void AFilterWithNothingTypedListsInTheOrderGiven()
    {
        // The recent documents the most recent first, whatever their kind; the apps as listed.
        var files = new[] { R(SearchKind.File, "b.txt", 3), R(SearchKind.Note, "a", 2), R(SearchKind.File, "c.txt", 1) };
        Assert.Equal(["b.txt", "a", "c.txt"], SearchRanker.Arrange(A(files: files, listing: true), SearchFilter.Files).Select(x => x.Title));
        var apps = Enumerable.Range(0, 80).Select(i => R(SearchKind.App, "app" + i, 100_000 - i)).ToArray();
        var listed = SearchRanker.Arrange(A(apps, listing: true), SearchFilter.Apps);
        Assert.Equal(SearchRanker.MaxFiltered, listed.Count);
        Assert.Equal("app0", listed[0].Title);
    }

    [Fact]
    public void ARecentDocumentAndTheSameFileFoundAnswerOnceTheBetter()
    {
        var found = new[] { R(SearchKind.File, "plan.txt", 700, @"C:\Users\u\plan.txt"), R(SearchKind.File, "other.txt", 600, @"C:\Users\u\other.txt") };
        var recent = new[] { R(SearchKind.File, "plan.txt", 700 + SearchRanker.RecentBonus, @"c:\users\u\PLAN.txt") };
        var merged = SearchRanker.MergeFiles(found, recent);
        Assert.Equal(2, merged.Count);
        Assert.Equal(700 + SearchRanker.RecentBonus, merged.Single(r => r.Title == "plan.txt").Score);
    }
}

/// <summary>Stage 6-1: the keys that open the search, recorded on the settings page.</summary>
public class SearchHotkeyTests
{
    static AppSettings With(string hotkey, Dictionary<string, string>? shortcuts = null)
    {
        var s = new AppSettings();
        s.Search.Hotkey = hotkey;
        if (shortcuts != null) s.HotCorners.Shortcuts = shortcuts;
        SettingsValidator.Normalize(s);
        return s;
    }

    [Theory]
    [InlineData("Alt+Space", "Alt+Space")]
    [InlineData("Ctrl+Shift+F", "Ctrl+Shift+F")]
    [InlineData("none", "none")] // no keys: the magnifier still opens it
    [InlineData("Space", "Alt+Space")] // without Ctrl, Alt or Win it would be gone from every app
    [InlineData("Shift+F", "Alt+Space")]
    [InlineData("nonsense", "Alt+Space")]
    [InlineData("", "Alt+Space")]
    public void KeepsKeysTheSettingsPageCouldRecord(string stored, string kept) => Assert.Equal(kept, With(stored).Search.Hotkey);

    [Fact]
    public void AShortcutWithTheSearchsKeysIsDropped()
    {
        var s = With("Ctrl+Alt+Space", new() { ["overview"] = "Ctrl+Alt+Space", ["appGrid"] = "Ctrl+Alt+G" });
        Assert.False(s.HotCorners.Shortcuts.ContainsKey("overview"));
        Assert.Equal("Ctrl+Alt+G", s.HotCorners.Shortcuts["appGrid"]);
        Assert.Equal("Ctrl+Alt+Space", s.Search.Hotkey);
    }
}
