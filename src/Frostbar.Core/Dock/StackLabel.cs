namespace Frostbar.Core.Dock;

/// <summary>A file's name under its icon in a stack, set as macOS sets it: at most two lines.</summary>
public static class StackLabel
{
    /// <summary>Japanese characters that do not start a line (small kana, the long vowel mark, closing punctuation).</summary>
    const string NoStart = "ぁぃぅぇぉっゃゅょゎゕゖァィゥェォッャュョヮヵヶーゝゞヽヾ々・、。，．：；？！」』）】〕〉》";

    /// <summary>Opening brackets, which do not end a line.</summary>
    const string NoEnd = "「『（【〔〈《([{";

    /// <summary>
    /// <paramref name="name"/> in at most two lines no wider than <paramref name="max"/> as <paramref name="width"/>
    /// measures them: the first line ends at the last place a line may end (see <see cref="BreakBefore"/>) if that
    /// leaves it a third full, else where it is full; the second line is shortened in the middle when it is still too
    /// long, so that its end (a date, the extension) stays.
    /// </summary>
    public static string TwoLines(string name, Func<string, double> width, double max)
    {
        if (name.Length < 2 || width(name) <= max) return name;
        int n = Longest(name.Length - 1, k => width(name[..k]) <= max); // the most characters the first line takes
        int at = n;
        for (int i = n; i >= 1; i--)
        {
            if (!BreakBefore(name, i)) continue;
            if (width(name[..i]) >= max / 3) at = i;
            break;
        }
        string first = name[..at].TrimEnd(), rest = name[at..].TrimStart();
        if (rest.Length == 0) return first;
        if (width(rest) <= max) return first + "\n" + rest;
        // The second line: its start and its end in about equal parts, with an ellipsis between.
        int keep = Longest(rest.Length - 1, k => width(Shortened(rest, k)) <= max);
        return first + "\n" + Shortened(rest, keep);
    }

    /// <summary>
    /// Whether a line may end between <c>s[i - 1]</c> and <c>s[i]</c>, much as UAX #14 has it for names: after a
    /// space, after a hyphen or an underscore (not before a digit: "2026-10" stays whole), before a dot (the extension
    /// stays whole), and between Japanese characters unless the next one may not start a line or the last one may not
    /// end it.
    /// </summary>
    static bool BreakBefore(string s, int i)
    {
        char prev = s[i - 1], next = s[i];
        if (next == '.') return true;
        if (prev == ' ' || prev == (char)0x3000) return true;
        if (prev == '-' || prev == '_') return !char.IsAsciiDigit(next);
        if (Cjk(prev) || Cjk(next)) return !NoStart.Contains(next) && !NoEnd.Contains(prev) && next != ' ';
        return false;
    }

    /// <summary>Kana, CJK punctuation, ideographs and full-width forms.</summary>
    static bool Cjk(char c) => c is >= (char)0x3000 and <= (char)0x30FF or >= (char)0x3400 and <= (char)0x9FFF
        or >= (char)0xF900 and <= (char)0xFAFF or >= (char)0xFF00 and <= (char)0xFFEF;

    /// <summary><paramref name="keep"/> characters of <paramref name="s"/>: its start and its end, an ellipsis between.</summary>
    static string Shortened(string s, int keep) => s[..(keep - keep / 2)].TrimEnd() + "…" + s[^(keep / 2)..].TrimStart();

    /// <summary>The largest k in 1..<paramref name="most"/> that <paramref name="fits"/> (it fits for every smaller one); 1 if none.</summary>
    static int Longest(int most, Func<int, bool> fits)
    {
        int lo = 1, hi = Math.Max(1, most);
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (fits(mid)) lo = mid; else hi = mid - 1;
        }
        return lo;
    }
}
