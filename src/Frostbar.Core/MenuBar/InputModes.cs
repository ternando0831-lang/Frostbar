namespace Frostbar.Core.MenuBar;

/// <summary>
/// The Japanese IME's input modes as the menu bar's input menu offers them (stage 5-3, macOS's input menu: ひらがな,
/// カタカナ, 英字…). Each is the IME on or off with a conversion mode (IME_CMODE bits: native 1, katakana 2, full
/// shape 8, roman 16, as Microsoft IME answers WM_IME_CONTROL). Choosing one keeps the roman bit (romaji or kana input)
/// as it was.
/// </summary>
public static class InputModes
{
    public const long Native = 0x1, Katakana = 0x2, FullShape = 0x8, Roman = 0x10;
    const long Kind = Native | Katakana | FullShape;

    /// <summary>The modes in the menu's order: the key, the IME on, the conversion bits (without the roman bit).</summary>
    public static readonly IReadOnlyList<(string Key, bool Open, long Mode)> All =
    [
        ("hiragana", true, Native | FullShape),
        ("katakana", true, Native | Katakana | FullShape),
        ("fullAlphanumeric", true, FullShape),
        ("halfKatakana", true, Native | Katakana),
        ("direct", false, 0),
    ];

    /// <summary>
    /// The mode the IME is in: "direct" while it is off (or on with half-width letters: Microsoft IME's 半角英数 is
    /// typed as directly), null for bits none of the modes has (another IME's).
    /// </summary>
    public static string? Current(bool open, long conversion)
    {
        long bits = conversion & Kind;
        if (!open || bits == 0) return "direct";
        foreach (var m in All) if (m.Open && m.Mode == bits) return m.Key;
        return null;
    }

    /// <summary>What to set for <paramref name="key"/>: the IME on or off, and the conversion bits (the roman bit kept from <paramref name="conversion"/>); null for no such mode.</summary>
    public static (bool Open, long Conversion)? For(string key, long conversion)
    {
        foreach (var m in All)
            if (m.Key == key) return (m.Open, m.Open ? m.Mode | (conversion & Roman) : conversion);
        return null;
    }

    static readonly string FullWidthA = ((char)0xFF21).ToString(), HalfWidthKa = ((char)0xFF76).ToString();

    /// <summary>
    /// The bar's text for the Japanese IME: あ hiragana, カ katakana, a half-width ka for half-width katakana, a
    /// full-width A for full-width letters, A for direct input; another layout shows its language's two letters.
    /// </summary>
    public static string BarText(bool japanese, bool open, long conversion, string otherLanguage) =>
        !japanese ? otherLanguage : Current(open, conversion) switch
        {
            "hiragana" => "あ",
            "katakana" => "カ",
            "halfKatakana" => HalfWidthKa,
            "fullAlphanumeric" => FullWidthA,
            _ => "A",
        };
}
