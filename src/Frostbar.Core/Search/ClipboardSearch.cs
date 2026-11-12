using Frostbar.Core.Localization;
using Frostbar.Core.Shell;

namespace Frostbar.Core.Search;

/// <summary>What a clipboard history item holds.</summary>
public enum ClipboardKind { Text, Image, Files, Other }

/// <summary>
/// An item of Windows' clipboard history: its id there (to put it back), what it holds, its text (for files their
/// names, for a picture nothing), and when it was copied.
/// </summary>
public sealed record ClipboardItem(string Id, ClipboardKind Kind, string Text, DateTimeOffset Time);

/// <summary>Windows' clipboard history as read: on (with its items), off (Windows' setting), or not to be had.</summary>
public enum ClipboardState { On, Off, Unavailable }

/// <summary>
/// The search's Clipboard filter (stage 6, E3c; macOS 26's ⌘4): Windows' clipboard history (the user's choice "a",
/// 2026-10-16), the newest first, each as its first line with words (a picture and files as they are); words typed keep
/// the items whose text has them. Enter puts the item back on the clipboard. With the history off, one row says so and
/// opens Windows' clipboard settings.
/// </summary>
public static class ClipboardSearch
{
    /// <summary>The targets of the rows that are not items: the history is off (opens Windows' settings), nothing to show.</summary>
    public const string OffTarget = "clipboard:off", EmptyTarget = "clipboard:empty";

    /// <summary>A text item's row shows this many characters of its first line.</summary>
    public const int PreviewWidth = 80;

    /// <param name="query">Normalized (<see cref="TextNormalizer"/>); empty lists them all.</param>
    public static List<SearchResult> Rows(ClipboardState state, IReadOnlyList<ClipboardItem> items, string query, DateTimeOffset now, Strings s)
    {
        if (state == ClipboardState.Off) return [new SearchResult(SearchKind.Clipboard, s["clipboard.off"], s["clipboard.offDetail"], OffTarget, 1)];
        if (state == ClipboardState.Unavailable) return [new SearchResult(SearchKind.Clipboard, s["clipboard.unavailable"], null, EmptyTarget, 1)];
        var rows = new List<SearchResult>();
        foreach (var item in items.OrderByDescending(i => i.Time))
        {
            if (query.Length > 0 && !TextNormalizer.Normalize(item.Text).Contains(query, StringComparison.Ordinal)) continue;
            rows.Add(new SearchResult(SearchKind.Clipboard, Title(item, s), Detail(item, now, s), item.Id, 100_000 - rows.Count));
            if (rows.Count == SearchRanker.MaxFiltered) break;
        }
        if (rows.Count == 0 && query.Length == 0) rows.Add(new SearchResult(SearchKind.Clipboard, s["clipboard.empty"], null, EmptyTarget, 1));
        return rows;
    }

    /// <summary>A row's title: a text's first line with words, a file's names, else what it holds.</summary>
    public static string Title(ClipboardItem item, Strings s) => item.Kind switch
    {
        ClipboardKind.Text when Preview(item.Text) is { Length: > 0 } line => line,
        ClipboardKind.Files when item.Text.Length > 0 => Preview(item.Text),
        _ => KindName(item.Kind, s),
    };

    /// <summary>What it holds and when it was copied ("テキスト · 12分前"), as the notification centre says times.</summary>
    public static string Detail(ClipboardItem item, DateTimeOffset now, Strings s) => KindName(item.Kind, s) + " · " + NotificationGroups.When(item.Time, now, s);

    public static string KindName(ClipboardKind kind, Strings s) => kind switch
    {
        ClipboardKind.Text => s["clipboard.text"],
        ClipboardKind.Image => s["clipboard.image"],
        ClipboardKind.Files => s["clipboard.files"],
        _ => s["clipboard.other"],
    };

    /// <summary>
    /// The first line with words of <paramref name="text"/>, its runs of spaces and tabs made one, cut to
    /// <paramref name="width"/> characters with "…"; empty when no line has words.
    /// </summary>
    public static string Preview(string text, int width = PreviewWidth)
    {
        foreach (var raw in text.Split('\n'))
        {
            string line = string.Join(' ', raw.Split([' ', '\t', '\r', (char)0x3000], StringSplitOptions.RemoveEmptyEntries));
            if (line.Length == 0) continue;
            return line.Length <= width ? line : line[..(width - 1)].TrimEnd() + "…";
        }
        return "";
    }
}
