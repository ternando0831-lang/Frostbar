namespace Frostbar.Core.Search;

/// <summary>A name prepared once for matching: its normalized form and the initials of its words.</summary>
public sealed class Candidate
{
    public string Name { get; }
    public string Normalized { get; }
    /// <summary>First letters of its words, also at inner capitals ("Visual Studio Code" → "vsc", "PowerShell" → "ps").</summary>
    public string Initials { get; }

    public Candidate(string name)
    {
        Name = name;
        Normalized = TextNormalizer.Normalize(name);
        Initials = InitialsOf(name);
    }

    static string InitialsOf(string name)
    {
        var sb = new System.Text.StringBuilder();
        char prev = ' ';
        foreach (char c in name)
        {
            bool start = char.IsLetterOrDigit(c) && (!char.IsLetterOrDigit(prev) || char.IsUpper(c) && char.IsLower(prev));
            if (start) sb.Append(c);
            prev = c;
        }
        return TextNormalizer.Normalize(sb.ToString()).Replace(" ", "");
    }
}

/// <summary>
/// How well a name answers a query (both normalized; see <see cref="TextNormalizer"/>). 0: no match. The tiers, best
/// first: the whole name, a prefix, the start of a later word, the initials ("vsc" for Visual Studio Code), somewhere
/// inside, every word of the query somewhere. Within a tier, shorter names (closer to what was typed) rank higher.
/// </summary>
public static class Matcher
{
    public const int Exact = 1000, Prefix = 900, WordStart = 750, InitialsMatch = 650, Inside = 500, AllWords = 400;

    public static int Score(string query, Candidate c)
    {
        string q = query, n = c.Normalized;
        if (q.Length == 0 || n.Length == 0) return 0;
        int extra = Math.Max(0, n.Length - q.Length);
        int Rank(int tier) => tier - Math.Min(99, extra);
        if (n == q) return Exact;
        if (n.StartsWith(q, StringComparison.Ordinal)) return Rank(Prefix);
        int at = n.IndexOf(q, StringComparison.Ordinal);
        if (at > 0 && IsWordStart(n, at)) return Rank(WordStart);
        if (q.Length >= 2 && !q.Contains(' ') && c.Initials.StartsWith(q, StringComparison.Ordinal)) return Rank(InitialsMatch);
        if (at > 0) return Rank(Inside);
        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > 1 && words.All(w => n.Contains(w, StringComparison.Ordinal))) return Rank(AllWords);
        return 0;
    }

    /// <summary>After a space or punctuation, or where the script changes (ascii after kana: "ms のりもの").</summary>
    static bool IsWordStart(string n, int at)
    {
        char before = n[at - 1], here = n[at];
        if (!char.IsLetterOrDigit(before)) return true;
        return IsAscii(before) != IsAscii(here);
    }

    static bool IsAscii(char c) => c < 0x80;
}
