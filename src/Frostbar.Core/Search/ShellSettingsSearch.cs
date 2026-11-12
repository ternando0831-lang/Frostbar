using Frostbar.Core.Settings;

namespace Frostbar.Core.Search;

/// <summary>
/// A page of Frostbar's settings as the search finds it: its key, its title, its names (the title in both languages), the
/// words it is found by without showing them (<see cref="Settings.ShellSettingsPages.AlsoFoundBy"/>) and the texts on
/// it (labels, details, choices), each prepared once for matching.
/// </summary>
public sealed class ShellPageWords
{
    public string Key { get; }
    public string Title { get; }
    public IReadOnlyList<Candidate> Names { get; }
    public IReadOnlyList<Candidate> Words { get; }
    public IReadOnlyList<Candidate> Texts { get; }

    public ShellPageWords(string key, string title, IEnumerable<string> otherNames, IEnumerable<string> words, IEnumerable<string> texts)
    {
        Key = key;
        Title = title;
        static Candidate[] Prepare(IEnumerable<string> words) =>
            [.. words.Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => w.Trim()).Distinct(StringComparer.Ordinal).Select(w => new Candidate(w))];
        Names = Prepare(otherNames.Prepend(title));
        Words = Prepare(words);
        Texts = Prepare(texts);
    }
}

/// <summary>
/// The settings the search answers with (stage 6-1): Windows Settings pages, and Frostbar's own pages found by their names
/// and by the texts on them, as System Settings' search finds a pane by a setting on it. A Windows page Frostbar takes the
/// place of opens Frostbar's (ADR-013), and each page answers once.
/// </summary>
public static class ShellSettingsSearch
{
    /// <summary>What a Frostbar page's answer opens: <see cref="Scheme"/> and the page's key.</summary>
    public const string Scheme = "frostbar-settings:";
    /// <summary>A row's text ranks below a page's own names of the same tier.</summary>
    public const int RowPenalty = 150;
    /// <summary>Frostbar's own page before Windows' when both answer as well ("ディスプレイ": both have one).</summary>
    public const int OwnFirst = 5;
    /// <summary>A longer row's text (a detail's sentence) is not shown as a name: its page's title is.</summary>
    public const int LongestRowTitle = 32;

    /// <summary>A text on this many pages or more finds none of them (「オン」, 「なし」, 「自動」 would find every page).</summary>
    public const int CommonOnPages = 3;

    public static string Target(string page) => Scheme + page;

    /// <summary>
    /// The pages prepared for the search from what each shows: its title, its names in the other language, the words
    /// it is found by, and its texts but those on <see cref="CommonOnPages"/> pages or more and those of one character.
    /// </summary>
    public static List<ShellPageWords> Index(IEnumerable<(string Key, string Title, IReadOnlyList<string> OtherNames, IReadOnlyList<string> Words, IReadOnlyList<string> Texts)> pages)
    {
        var list = pages.ToList();
        var onPages = list.SelectMany(p => p.Texts.Select(TextNormalizer.Normalize).Where(t => t.Length > 0).Distinct())
                          .GroupBy(t => t).ToDictionary(g => g.Key, g => g.Count());
        bool Kept(string text)
        {
            string n = TextNormalizer.Normalize(text);
            return n.Length >= 2 && onPages.GetValueOrDefault(n) < CommonOnPages;
        }
        return [.. list.Select(p => new ShellPageWords(p.Key, p.Title, p.OtherNames, p.Words, p.Texts.Where(Kept)))];
    }

    /// <summary>The Frostbar page an answer opens, or null for a Windows Settings page.</summary>
    public static string? PageOf(string target) => target.StartsWith(Scheme, StringComparison.Ordinal) ? target[Scheme.Length..] : null;

    /// <summary>
    /// How well <paramref name="page"/> answers the normalized <paramref name="query"/> (0: not at all), and the row's
    /// text that did when the page's names did not do as well. Rows from two characters: one character is in nearly
    /// every sentence.
    /// </summary>
    public static (int Score, string? Row) Score(ShellPageWords page, string query)
    {
        int named = Math.Max(page.Names.Count == 0 ? 0 : page.Names.Max(c => Matcher.Score(query, c)),
                             page.Words.Count == 0 ? 0 : page.Words.Max(c => Matcher.Score(query, c) - SearchRanker.KeywordPenalty));
        int best = 0;
        string? row = null;
        if (query.Length >= 2)
            foreach (var text in page.Texts)
            {
                int s = Matcher.Score(query, text) - RowPenalty;
                if (s <= 0) continue;
                // The shortest of the rows that answer as well: the closest to what was typed.
                if (s > best || s == best && row != null && text.Name.Length < row.Length) { best = s; row = text.Name; }
            }
        return best > named ? (best, row) : (Math.Max(0, named), null);
    }

    /// <summary>
    /// The answers for the normalized <paramref name="query"/>: the Windows pages (named in the display language), and
    /// Frostbar's pages. A Frostbar page found by a short row is named by that row, its page's title in
    /// <see cref="SearchResult.Detail"/>. One answer per page, the best.
    /// </summary>
    public static List<SearchResult> Answers(string query, bool japanese, IReadOnlyList<ShellPageWords> shell)
    {
        var all = new List<SearchResult>();
        if (query.Length == 0) return all;
        foreach (var p in SettingsPages.All)
        {
            int score = SettingsPages.Score(p, query);
            if (score <= 0) continue;
            string target = ShellSettingsPages.Replacing(p.Uri) is { } page ? Target(page) : p.Uri;
            all.Add(new SearchResult(SearchKind.Setting, japanese ? p.Ja : p.En, null, target, score));
        }
        foreach (var page in shell)
        {
            var (score, row) = Score(page, query);
            if (score <= 0) continue;
            bool named = row != null && row.Length <= LongestRowTitle;
            all.Add(new SearchResult(SearchKind.Setting, named ? row! : page.Title, named ? page.Title : null, Target(page.Key), score + OwnFirst));
        }
        return [.. all.GroupBy(r => r.Target, StringComparer.Ordinal).Select(g => g.OrderByDescending(r => r.Score).First())];
    }
}
