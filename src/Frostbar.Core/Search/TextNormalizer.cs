using System.Globalization;
using System.Text;

namespace Frostbar.Core.Search;

/// <summary>
/// One form for comparing what is typed with names: full-width and half-width alike (NFKC: "ＡＢＣ１" → "abc1",
/// half-width ｶﾅ → カナ), case folded, katakana as hiragana (so "でぃすぷれい" finds "ディスプレイ"), accents removed
/// ("café" → "cafe", but the voiced marks of kana stay: "ガ" must not become "か"), runs of spaces as one.
/// </summary>
public static class TextNormalizer
{
    public static string Normalize(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        string decomposed = s.Normalize(NormalizationForm.FormKC).ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        bool space = false;
        foreach (char c in decomposed)
        {
            if (char.IsWhiteSpace(c)) { space = sb.Length > 0; continue; }
            // Combining marks go (accents), except the kana voiced and semi-voiced marks.
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark && c != Voiced && c != SemiVoiced) continue;
            if (space) { sb.Append(' '); space = false; }
            sb.Append(Hiragana(c));
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    // Written as numbers: the combining marks are invisible in source (2026-10-12: \u escapes became the bare characters).
    const char Voiced = (char)0x3099, SemiVoiced = (char)0x309A;
    const char FirstKatakana = (char)0x30A1, LastKatakana = (char)0x30F6, KatakanaRepeat = (char)0x30FD, KatakanaRepeatVoiced = (char)0x30FE;

    /// <summary>Katakana ァ..ヶ and the iteration marks ヽヾ as their hiragana; anything else unchanged.</summary>
    static char Hiragana(char c) => c switch
    {
        >= FirstKatakana and <= LastKatakana => (char)(c - 0x60),
        KatakanaRepeat or KatakanaRepeatVoiced => (char)(c - 0x60),
        _ => c,
    };
}
