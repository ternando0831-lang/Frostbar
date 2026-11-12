namespace Frostbar.Core.Search;

public enum SearchKind { Calculation, App, Setting, Folder, File, Web, Conversion, Note, Window, Action, Clipboard }

/// <summary>
/// Spotlight's filters (macOS 26: ⌘1 Applications, ⌘2 Files, ⌘3 Actions, ⌘4 Clipboard), here Ctrl+1…4. Under one,
/// only its kinds answer, more of them, and with nothing typed it lists them (the apps, the recent documents, the actions).
/// </summary>
public enum SearchFilter { None, Apps, Files, Actions, Clipboard }

/// <summary>
/// One row of the search's answer. <paramref name="Target"/> is what Enter opens: an AppsFolder parsing name, an
/// ms-settings: URI or a Frostbar page (<see cref="ShellSettingsSearch.Scheme"/>), a path (a note's path too: it opens in
/// Obsidian), the result to copy, the query for the web, a window's handle, or an action's id.
/// </summary>
public sealed record SearchResult(SearchKind Kind, string Title, string? Detail, string Target, int Score);

/// <summary>
/// What each source answered for one query. <paramref name="Listing"/>: nothing was typed, and a filter lists its kinds
/// in the order given (the recent documents the most recent first). <paramref name="Clipboard"/>: the Clipboard
/// filter's rows, in their order (<see cref="ClipboardSearch"/>).
/// </summary>
public sealed record SearchAnswers(SearchResult? Calculation, IReadOnlyList<SearchResult> Apps, IReadOnlyList<SearchResult> Windows,
    IReadOnlyList<SearchResult> Actions, IReadOnlyList<SearchResult> Settings, IReadOnlyList<SearchResult> Files, SearchResult? Web, bool Listing = false,
    IReadOnlyList<SearchResult>? Clipboard = null)
{
    public static readonly SearchAnswers None = new(null, [], [], [], [], [], null);
}

/// <summary>
/// Puts the sources' answers in the order the panel shows them: a calculation first (the query is a sum), then the
/// best match as the top hit, then the apps, windows, actions, settings, notes, folders and files, each capped, and
/// searching the web last. Apps used often rank higher (Spotlight learns the same way). Under a filter, only its kinds.
/// </summary>
public static class SearchRanker
{
    public const int MaxApps = 6, MaxWindows = 4, MaxActions = 3, MaxSettings = 4, MaxNotes = 5, MaxFiles = 8;
    /// <summary>Under a filter its kinds are all there is: more of each (a list of every app would be slow to show).</summary>
    public const int MaxFiltered = 50;
    /// <summary>
    /// A word a thing is found by without being its name ranks just below a name: 「スリープ」 is the action's name and
    /// only a word of the battery's page.
    /// </summary>
    public const int KeywordPenalty = 10;
    /// <summary>A recent document ranks above a file of the same match (Spotlight ranks what was used lately higher).</summary>
    public const int RecentBonus = 60;

    /// <summary>
    /// A note found by its body (stage 6-2): below any note found by its name (whose least is
    /// <see cref="Matcher.Inside"/> − 100), the more often the words are in it the higher (up to 40 more).
    /// </summary>
    public static int BodyScore(int count) => Matcher.Inside - 200 + Math.Min(40, 4 * Math.Max(1, count));

    /// <summary>A match's score raised by how often the app was opened from the search or the Dock (log-scaled).</summary>
    public static int WithUse(int score, int uses) => score <= 0 ? 0 : score + (int)Math.Min(150, 50 * Math.Log2(1 + Math.Max(0, uses)));

    public static IReadOnlyList<SearchResult> Arrange(SearchResult? calculation, IEnumerable<SearchResult> apps, IEnumerable<SearchResult> settings,
        IEnumerable<SearchResult> files, SearchResult? web) =>
        Arrange(new SearchAnswers(calculation, [.. apps], [], [], [.. settings], [.. files], web));

    /// <summary>
    /// The answer as the panel shows it. <paramref name="hidden"/>: the kinds the settings leave out (Spotlight's "Search
    /// results", <see cref="SearchCategories"/>), out of the answer before the top hit is chosen; a filter's listing
    /// keeps them (Ctrl+2 still lists the files).
    /// </summary>
    public static IReadOnlyList<SearchResult> Arrange(SearchAnswers a, SearchFilter filter = SearchFilter.None, ISet<SearchKind>? hidden = null)
    {
        if (filter != SearchFilter.None) return Filtered(a, filter);
        if (hidden is { Count: > 0 }) a = Without(a, hidden);
        var list = new List<SearchResult>();
        if (a.Calculation != null) list.Add(a.Calculation);
        var apps = Best(a.Apps, MaxApps);
        var windows = Best(a.Windows, MaxWindows);
        var actions = Best(a.Actions, MaxActions);
        var settings = Best(a.Settings, MaxSettings);
        // The top hit: the best app, unless a setting or an action is named by what was typed and the app only partly.
        SearchResult? top = apps.FirstOrDefault();
        var named = settings.Concat(actions).OrderByDescending(x => x.Score).FirstOrDefault();
        if (named != null && (top == null || named.Score >= Matcher.Exact - KeywordPenalty && top.Score < Matcher.Prefix)) top = named;
        if (top != null) list.Add(top);
        list.AddRange(apps.Where(x => !ReferenceEquals(x, top)));
        list.AddRange(windows);
        list.AddRange(actions.Where(x => !ReferenceEquals(x, top)));
        list.AddRange(settings.Where(x => !ReferenceEquals(x, top)));
        list.AddRange(Files(a.Files, MaxNotes, MaxFiles));
        if (a.Web != null) list.Add(a.Web);
        return list;
    }

    static SearchAnswers Without(SearchAnswers a, ISet<SearchKind> hidden)
    {
        List<SearchResult> Keep(IEnumerable<SearchResult> xs) => [.. xs.Where(x => !hidden.Contains(x.Kind))];
        return a with
        {
            Calculation = a.Calculation is { } c && hidden.Contains(c.Kind) ? null : a.Calculation,
            Apps = Keep(a.Apps), Windows = Keep(a.Windows), Actions = Keep(a.Actions), Settings = Keep(a.Settings), Files = Keep(a.Files),
            Web = a.Web is { } w && hidden.Contains(w.Kind) ? null : a.Web,
        };
    }

    static IReadOnlyList<SearchResult> Filtered(SearchAnswers a, SearchFilter filter) => filter switch
    {
        SearchFilter.Apps => [.. (a.Listing ? a.Apps.Take(MaxFiltered) : Best(a.Apps, MaxFiltered)), .. Best(a.Windows, MaxFiltered)],
        SearchFilter.Files => a.Listing ? [.. a.Files.Take(MaxFiltered)] : Files(a.Files, MaxFiltered, MaxFiltered),
        SearchFilter.Actions => a.Listing ? [.. a.Actions] : Best(a.Actions, MaxFiltered),
        SearchFilter.Clipboard => [.. (a.Clipboard ?? []).Take(MaxFiltered)], // the newest first, as the history keeps them
        _ => [],
    };

    static List<SearchResult> Best(IEnumerable<SearchResult> xs, int max) =>
        [.. xs.Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Title.Length).ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase).Take(max)];

    /// <summary>Notes (an Obsidian vault's Markdown files: they come in with the files), then the folders, then the files.</summary>
    static List<SearchResult> Files(IEnumerable<SearchResult> files, int maxNotes, int maxFiles)
    {
        var list = new List<SearchResult>();
        list.AddRange(files.Where(x => x.Kind == SearchKind.Note).OrderByDescending(x => x.Score).ThenBy(x => x.Detail?.Length ?? 0).Take(maxNotes));
        var f = files.Where(x => x.Kind != SearchKind.Note).OrderByDescending(x => x.Score).ThenBy(x => x.Kind).ThenBy(x => x.Detail?.Length ?? 0).Take(maxFiles).ToList();
        list.AddRange(f.Where(x => x.Kind == SearchKind.Folder));
        list.AddRange(f.Where(x => x.Kind == SearchKind.File));
        return list;
    }

    /// <summary>The files a source found and the recent documents that answer, one answer per path (the better).</summary>
    public static List<SearchResult> MergeFiles(IEnumerable<SearchResult> found, IEnumerable<SearchResult> recent) =>
        [.. found.Concat(recent).GroupBy(r => r.Target, StringComparer.OrdinalIgnoreCase).Select(g => g.OrderByDescending(r => r.Score).First())];
}
