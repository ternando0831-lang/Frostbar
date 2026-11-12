using System.Globalization;
using Frostbar.Core.Search;

namespace Frostbar.Core.AppGrid;

/// <summary>An app of the grid: its Start menu name and AppsFolder parsing name (what launches it).</summary>
public sealed record GridApp(string Name, string ParsingName);

/// <summary>
/// What the app grid (M5, Launchpad-like) shows: the Start menu's "All apps" without uninstallers, documents (manuals,
/// read-me files) and web links, each app once, by name in the display language's order.
/// </summary>
public static class AppGridModel
{
    static readonly string[] DocumentEndings =
        [".url", ".pdf", ".chm", ".hlp", ".txt", ".htm", ".html", ".mht", ".rtf", ".md", ".doc", ".docx", ".xps", ".website"];
    static readonly string[] LinkStarts = ["http:", "https:", "ftp:", "mailto:"];
    static readonly string[] UninstallWords = ["uninstall", "アンインストール"];

    /// <summary>Entries the grid leaves out: web links, documents, uninstallers.</summary>
    public static bool Hidden(string name, string parsingName)
    {
        string p = parsingName.Trim();
        if (LinkStarts.Any(s => p.StartsWith(s, StringComparison.OrdinalIgnoreCase))) return true;
        if (DocumentEndings.Any(e => p.EndsWith(e, StringComparison.OrdinalIgnoreCase))) return true;
        if (UninstallWords.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase))) return true;
        string file = p[(p.LastIndexOfAny(['\\', '/']) + 1)..];
        return file.StartsWith("unins", StringComparison.OrdinalIgnoreCase) && file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
    }

    public static IReadOnlyList<GridApp> Arrange(IEnumerable<GridApp> apps, CultureInfo culture)
    {
        var byName = StringComparer.Create(culture, CompareOptions.IgnoreCase | CompareOptions.IgnoreKanaType | CompareOptions.IgnoreWidth);
        return apps.Where(a => !string.IsNullOrWhiteSpace(a.Name) && !Hidden(a.Name, a.ParsingName))
            .GroupBy(a => a.ParsingName, StringComparer.OrdinalIgnoreCase).Select(g => g.First())
            .OrderBy(a => a.Name, byName).ThenBy(a => a.ParsingName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static int PageCount(int count, int perPage) => Math.Max(1, (count + perPage - 1) / perPage);

    /// <summary>
    /// The apps (indices into <paramref name="names"/>) that answer <paramref name="query"/> (normalized, see
    /// <see cref="TextNormalizer"/>), best first and equals in grid order, the ones in <paramref name="last"/> (hidden:
    /// the user's choice, 2026-10-24) after all the others; every app in order for an empty query.
    /// </summary>
    public static IReadOnlyList<int> Filter(IReadOnlyList<Candidate> names, string query, IReadOnlySet<int>? last = null)
    {
        if (query.Length == 0) return Enumerable.Range(0, names.Count).ToList();
        return names.Select((c, i) => (i, score: Matcher.Score(query, c)))
            .Where(x => x.score > 0)
            .OrderBy(x => last?.Contains(x.i) == true).ThenByDescending(x => x.score).ThenBy(x => x.i)
            .Select(x => x.i).ToList();
    }
}
