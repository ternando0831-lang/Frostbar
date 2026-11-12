namespace Frostbar.Core.Search;

/// <summary>A note whose body has what was typed: its path, how many times (up to a cap), and when it was written.</summary>
public sealed record NoteHit(string Path, int Count, DateTime Written);

/// <summary>A note whose name answers what was typed: its path and how well (<see cref="Matcher"/>).</summary>
public sealed record NoteName(string Path, int Score);

/// <summary>
/// Obsidian's notes for the search (stage 6-2): each note's text, normalized as the query is (<see cref="TextNormalizer"/>)
/// and without its front matter, and its name, kept in memory only (never written anywhere) and found by a plain scan.
/// Windows Search keeps no contents for .md (no filter is registered for it); S24 measured this index on 955 notes at
/// 2.9 M characters, about 6.2 MB of the managed heap, a search of the bodies at 0.3–0.4 ms and of the names at
/// 0.2–0.5 ms. Not thread-safe: one thread owns it.
/// </summary>
public sealed class NoteIndex
{
    /// <summary>A note's occurrences are counted up to this (enough to rank; a long note is not scanned to its end for more).</summary>
    public const int CountCap = 20;

    readonly Dictionary<string, (string Text, DateTime Written, Candidate Name)> _notes = new(StringComparer.OrdinalIgnoreCase);

    public int Count => _notes.Count;

    /// <summary>The characters kept (normalized), for diagnostics.</summary>
    public long Characters => _notes.Values.Sum(n => (long)n.Text.Length);

    /// <summary>Keeps (or replaces) a note's body, and its name (the file's, without ".md") ready for matching.</summary>
    public void Put(string path, string text, DateTime written) =>
        _notes[path] = (TextNormalizer.Normalize(Body(text)), written, new Candidate(System.IO.Path.GetFileNameWithoutExtension(path)));

    public bool Remove(string path) => _notes.Remove(path);

    /// <summary>Forgets every note under <paramref name="folder"/> (a vault no longer listed).</summary>
    public void RemoveUnder(string folder)
    {
        string root = folder.TrimEnd('\\', '/') + "\\";
        foreach (var path in _notes.Keys.Where(p => p.StartsWith(root, StringComparison.OrdinalIgnoreCase)).ToList()) _notes.Remove(path);
    }

    public bool Contains(string path) => _notes.ContainsKey(path);

    /// <summary>Whether a note is kept under <paramref name="folder"/> (a folder gone or renamed: its notes have to be read again).</summary>
    public bool AnyUnder(string folder)
    {
        string root = folder.TrimEnd('\\', '/') + "\\";
        return _notes.Keys.Any(p => p.StartsWith(root, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The notes whose body has the normalized <paramref name="query"/>, the most occurrences first, then the newest,
    /// at most <paramref name="max"/>.
    /// </summary>
    public List<NoteHit> Find(string query, int max)
    {
        var hits = new List<NoteHit>();
        if (query.Length == 0 || max <= 0) return hits;
        foreach (var (path, note) in _notes)
        {
            int n = 0;
            for (int at = note.Text.IndexOf(query, StringComparison.Ordinal); at >= 0 && n < CountCap; at = note.Text.IndexOf(query, at + query.Length, StringComparison.Ordinal)) n++;
            if (n > 0) hits.Add(new NoteHit(path, n, note.Written));
        }
        return [.. hits.OrderByDescending(h => h.Count).ThenByDescending(h => h.Written).ThenBy(h => h.Path, StringComparer.OrdinalIgnoreCase).Take(max)];
    }

    /// <summary>
    /// The notes whose name answers the normalized <paramref name="query"/>, the best first, then the newest, at most
    /// <paramref name="max"/>. The search finds notes by their name here, whatever the file source covers: Everything's
    /// and Windows Search's answers leave out AppData, and Windows Search may not index the vault at all.
    /// </summary>
    public List<NoteName> FindNames(string query, int max)
    {
        if (query.Length == 0 || max <= 0) return [];
        var names = new List<(NoteName Name, DateTime Written)>();
        foreach (var (path, note) in _notes)
            if (Matcher.Score(query, note.Name) is > 0 and var score) names.Add((new NoteName(path, score), note.Written));
        return [.. names.OrderByDescending(n => n.Name.Score).ThenByDescending(n => n.Written).ThenBy(n => n.Name.Path, StringComparer.OrdinalIgnoreCase)
                        .Take(max).Select(n => n.Name)];
    }

    /// <summary>A note's text without its front matter (a first line "---" up to the next "---").</summary>
    public static string Body(string text)
    {
        if (!text.StartsWith("---", StringComparison.Ordinal)) return text;
        int firstEnd = text.IndexOf('\n');
        if (firstEnd < 0 || text[..firstEnd].Trim() != "---") return text;
        int at = firstEnd + 1;
        while (at < text.Length)
        {
            int end = text.IndexOf('\n', at);
            string line = (end < 0 ? text[at..] : text[at..end]).Trim();
            if (line == "---") return end < 0 ? "" : text[(end + 1)..];
            if (end < 0) break;
            at = end + 1;
        }
        return text; // no closing line: not front matter
    }

    /// <summary>
    /// What the search shows of a body hit: the first line of <paramref name="text"/>'s body that has the normalized
    /// <paramref name="query"/>, as written, without Markdown's marks at its start (headings, lists, quotes, check boxes)
    /// and cut to about <paramref name="width"/> characters around the match. Null when no line has it whole (it spans
    /// lines: the scan joins them with a space).
    /// </summary>
    public static string? Snippet(string text, string query, int width = 48)
    {
        if (query.Length == 0) return null;
        foreach (var raw in Body(text).Split('\n'))
        {
            string line = Unmark(raw.Trim());
            string normalized = TextNormalizer.Normalize(line);
            int at = normalized.IndexOf(query, StringComparison.Ordinal);
            if (at < 0) continue;
            if (line.Length <= width) return line;
            // The match's place in the line as written: about the same place (normalizing keeps most lengths).
            int center = Math.Clamp((int)((long)at * line.Length / Math.Max(1, normalized.Length)), 0, line.Length);
            int start = Math.Clamp(center - width / 3, 0, line.Length - width);
            string cut = line.Substring(start, width).Trim();
            return (start > 0 ? "…" : "") + cut + (start + width < line.Length ? "…" : "");
        }
        return null;
    }

    /// <summary>A line without Markdown's marks at its start: "# ", "- ", "* ", "+ ", "> ", "1. ", "- [ ] ".</summary>
    static string Unmark(string line)
    {
        string s = line;
        for (int guard = 0; guard < 4; guard++)
        {
            string before = s;
            s = s.TrimStart('#', '>').TrimStart();
            if (s.Length >= 2 && s[0] is '-' or '*' or '+' && s[1] == ' ') s = s[2..].TrimStart();
            if (s.StartsWith("[ ] ", StringComparison.Ordinal) || s.StartsWith("[x] ", StringComparison.OrdinalIgnoreCase)) s = s[4..];
            int digits = 0;
            while (digits < s.Length && char.IsAsciiDigit(s[digits])) digits++;
            if (digits > 0 && digits + 1 < s.Length && s[digits] == '.' && s[digits + 1] == ' ') s = s[(digits + 2)..];
            if (s == before) break;
        }
        return s;
    }
}
