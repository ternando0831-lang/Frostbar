using Frostbar.Core.Search;
using Xunit;

namespace Frostbar.Core.Tests;

public class TextNormalizerTests
{
    [Theory]
    [InlineData("ＡＢＣ１", "abc1")]
    [InlineData("ﾃﾞｨｽﾌﾟﾚｲ", "でぃすぷれい")]
    [InlineData("ディスプレイ", "でぃすぷれい")]
    [InlineData("Café", "cafe")]
    [InlineData("ガギグ", "がぎぐ")]   // the voiced marks stay
    [InlineData("パ", "ぱ")]           // and the semi-voiced
    [InlineData("", "")]
    public void NormalizesForComparison(string input, string expected) => Assert.Equal(expected, TextNormalizer.Normalize(input));

    [Fact]
    public void SpacesOfAnyWidthCollapse() =>
        Assert.Equal("visual studio code", TextNormalizer.Normalize("  Visual   Studio" + (char)0x3000 + "Code ")); // ideographic space
}

public class MatcherTests
{
    static int S(string query, string name) => Matcher.Score(TextNormalizer.Normalize(query), new Candidate(name));

    [Fact]
    public void TiersRankWholeNamePrefixWordStartInitialsInside()
    {
        int exact = S("code", "Code"), prefix = S("vis", "Visual Studio Code"), word = S("stu", "Visual Studio Code");
        int initials = S("vsc", "Visual Studio Code"), inside = S("isu", "Visual Studio Code"), words = S("code visual", "Visual Studio Code");
        Assert.True(exact > prefix && prefix > word && word > initials && initials > inside && inside > words && words > 0,
            $"{exact} {prefix} {word} {initials} {inside} {words}");
        Assert.Equal(0, S("xyz", "Visual Studio Code"));
    }

    [Fact]
    public void InitialsIncludeInnerCapitals() => Assert.True(S("ps", "PowerShell") > 0);

    [Fact]
    public void KanaMatchesWhicheverWayItIsTyped()
    {
        Assert.Equal(Matcher.Exact, S("でぃすぷれい", "ディスプレイ"));
        Assert.Equal(Matcher.Exact, S("ﾃﾞｨｽﾌﾟﾚｲ", "ディスプレイ"));
        Assert.True(S("めも", "メモ帳") >= Matcher.Prefix - 99);
    }

    [Fact]
    public void ShorterNamesRankHigherWithinATier() => Assert.True(S("note", "Notepad") > S("note", "Notepad++ Portable Edition"));
}

public class CalculatorTests
{
    [Theory]
    [InlineData("１＋２×３", 7)]
    [InlineData("1+2*3", 7)]
    [InlineData("2^3^2", 512)]       // right to left
    [InlineData("-2^2", -4)]         // the power binds tighter than the sign
    [InlineData("2^-1", 0.5)]
    [InlineData("2(3+4)", 14)]       // implicit multiplication
    [InlineData("10/4", 2.5)]
    [InlineData("10 ÷ 4", 2.5)]
    [InlineData("7 % 4", 3)]
    [InlineData("1,000+1", 1001)]
    [InlineData("5!", 120)]
    [InlineData("sqrt(16)", 4)]
    [InlineData("√16", 4)]
    [InlineData("1ー2", -1)]         // the IME's long-vowel mark typed for minus
    [InlineData("2x3", 6)]
    [InlineData("1+2=", 3)]
    [InlineData("（１＋２）＊３", 9)]
    public void Evaluates(string input, double expected)
    {
        Assert.True(Calculator.TryEvaluate(input, out double v), input);
        Assert.Equal(expected, v, 9);
    }

    [Theory]
    [InlineData("2023")]   // a number alone is a search, not a sum
    [InlineData("-5")]
    [InlineData("abc")]
    [InlineData("1+")]
    [InlineData("(1+2")]
    [InlineData("1/0")]
    [InlineData("foo(2)")]
    [InlineData("")]
    public void IsNotACalculation(string input) => Assert.False(Calculator.TryEvaluate(input, out _), input);

    // Each "(" and leading "-" is a call deeper: pasted by the thousand, the stack overflowed, which no catch can stop,
    // and the whole shell went with it (the unit converter already took 60 characters at most).
    [Theory]
    [InlineData('(')]
    [InlineData('-')]
    public void TooLongIsNotACalculation(char c) => Assert.False(Calculator.TryEvaluate(new string(c, 100_000) + "1+1", out _));

    [Theory]
    [InlineData(0.1 + 0.2, "0.3")]
    [InlineData(1234567.891, "1,234,567.891")]
    [InlineData(7, "7")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(1e20, "1E+20")]
    public void Formats(double v, string expected) => Assert.Equal(expected, Calculator.Format(v));
}

public class SettingsPagesTests
{
    /// <summary>The best page, or "none" when no page matches at all (Display is first in the list: an unmatched query
    /// must not pass for it).</summary>
    static string Best(string query)
    {
        string q = TextNormalizer.Normalize(query);
        var best = SettingsPages.All.OrderByDescending(p => SettingsPages.Score(p, q)).First();
        return SettingsPages.Score(best, q) > 0 ? best.Uri : "none";
    }

    [Theory]
    [InlineData("display", "ms-settings:display")]
    [InlineData("ディスプレイ", "ms-settings:display")]
    [InlineData("でぃすぷれい", "ms-settings:display")]
    [InlineData("wifi", "ms-settings:network-wifi")]
    [InlineData("壁紙", "ms-settings:personalization-background")]
    [InlineData("bluetooth", "ms-settings:bluetooth")]
    public void FindsThePageInEitherLanguage(string query, string uri) => Assert.Equal(uri, Best(query));

    [Fact]
    public void UrisAreUnique() => Assert.Equal(SettingsPages.All.Count, SettingsPages.All.Select(p => p.Uri).Distinct().Count());
}

public class SearchRankerTests
{
    static SearchResult R(SearchKind k, string title, int score) => new(k, title, null, title, score);

    [Fact]
    public void CalculationFirstThenTopHitThenSectionsWebLast()
    {
        var calc = R(SearchKind.Calculation, "7", 0);
        var apps = new[] { R(SearchKind.App, "Notepad", 800), R(SearchKind.App, "Notes", 850) };
        var settings = new[] { R(SearchKind.Setting, "Notifications", 600) };
        var files = new[] { R(SearchKind.File, "notes.txt", 700), R(SearchKind.Folder, "Notes", 700) };
        var web = R(SearchKind.Web, "Search the web", 0);
        var list = SearchRanker.Arrange(calc, apps, settings, files, web);
        Assert.Equal(["7", "Notes", "Notepad", "Notifications", "Notes", "notes.txt", "Search the web"], list.Select(x => x.Title));
        Assert.Equal(SearchKind.Folder, list[4].Kind);
    }

    [Fact]
    public void NotesComeAfterTheSettingsAndBeforeTheFoldersAndFiles()
    {
        var files = new[]
        {
            R(SearchKind.File, "plan.txt", 900), R(SearchKind.Note, "Plan", 700), R(SearchKind.Folder, "Plans", 800), R(SearchKind.Note, "Plan B", 650),
        };
        var list = SearchRanker.Arrange(null, [R(SearchKind.App, "Planner", 800)], [R(SearchKind.Setting, "Power plan", 600)], files, null);
        Assert.Equal(["Planner", "Power plan", "Plan", "Plan B", "Plans", "plan.txt"], list.Select(x => x.Title));
        // Capped apart from the files: many notes leave room for the files.
        var many = Enumerable.Range(0, 9).Select(i => R(SearchKind.Note, "Note " + i, 500)).Append(R(SearchKind.File, "note.txt", 400));
        var capped = SearchRanker.Arrange(null, [], [], many, null);
        Assert.Equal(SearchRanker.MaxNotes, capped.Count(x => x.Kind == SearchKind.Note));
        Assert.Contains(capped, x => x.Kind == SearchKind.File);
    }

    [Fact]
    public void AWholeSettingNameBeatsAPartOfAnAppName()
    {
        var list = SearchRanker.Arrange(null, [R(SearchKind.App, "Display Tuner Pro", Matcher.Inside)], [R(SearchKind.Setting, "Display", Matcher.Exact)], [], null);
        Assert.Equal("Display", list[0].Title);
    }

    [Fact]
    public void UseRaisesAMatchButNotANonMatch()
    {
        Assert.True(SearchRanker.WithUse(500, 10) > SearchRanker.WithUse(500, 0));
        Assert.Equal(0, SearchRanker.WithUse(0, 100));
        Assert.True(SearchRanker.WithUse(500, 1_000_000) <= 650);
    }
}

public class UnitConverterTests
{
    [Theory]
    [InlineData("10 km", "10 km", "6.2137 mi")]
    [InlineData("10km in mi", "10 km", "6.2137 mi")]
    [InlineData("１０ｋｍ", "10 km", "6.2137 mi")]
    [InlineData("10kmをマイルに", "10 km", "6.2137 mi")]
    [InlineData("5 ft to cm", "5 ft", "152.4 cm")]
    [InlineData("100 kg を lb", "100 kg", "220.462 lb")]
    [InlineData("70 F", "70 °F", "21.1111 °C")]
    [InlineData("-40 c", "-40 °C", "-40 °F")]
    [InlineData("20 坪", "20 坪", "66.1157 m²")]
    [InlineData("1.5 GB in MB", "1.5 GB", "1,536 MB")]
    [InlineData("90 min in h", "90 min", "1.5 h")]
    [InlineData("3 cups in ml", "3 cups", "709.765 ml")]
    [InlineData("1 week in days", "1 week", "7 days")]
    [InlineData("36 h in day", "36 h", "1.5 days")]
    [InlineData("1 cup in ml", "1 cup", "236.588 ml")]
    [InlineData("60 mph", "60 mph", "96.5606 km/h")]
    [InlineData("2 fl oz to ml", "2 fl oz", "59.1471 ml")]
    // The inch is " in ", also a separator: these read no unit before it (2026-11-06 review).
    [InlineData("10 in", "10 in", "25.4 cm")]
    [InlineData("12in", "12 in", "30.48 cm")]
    [InlineData("12 in in cm", "12 in", "30.48 cm")]
    [InlineData("12 in to cm", "12 in", "30.48 cm")]
    public void Converts(string query, string expression, string result)
    {
        Assert.True(UnitConverter.TryConvert(query, out var c), query);
        Assert.Equal(expression, c.Expression);
        Assert.Equal(result, c.Result);
        Assert.Equal(result.Split(' ')[0], c.Number);
    }

    [Theory]
    [InlineData("90 min in h", "90分", "1.5時間", "1.5")]
    [InlineData("1 week in days", "1週間", "7日", "7")]
    [InlineData("3カップをmlに", "3カップ", "709.765 ml", "709.765")] // the symbols stay as they are
    [InlineData("10 km", "10 km", "6.2137 mi", "6.2137")]
    public void InJapaneseTheUnitsThatAreWordsAreWrittenInJapanese(string query, string expression, string result, string number)
    {
        Assert.True(UnitConverter.TryConvert(query, out var c, "ja"), query);
        Assert.Equal(expression, c.Expression);
        Assert.Equal(result, c.Result);
        Assert.Equal(number, c.Number);
    }

    [Theory]
    [InlineData("2023")]          // a number alone
    [InlineData("km")]            // a unit alone
    [InlineData("1+2")]           // arithmetic is the calculator's
    [InlineData("5 s")]           // times need a unit to go to
    [InlineData("1 gb")]          // so do data sizes
    [InlineData("10 km to kg")]   // a length is not a mass
    [InlineData("3 apples")]
    [InlineData("hello")]
    public void LeavesOtherQueriesAlone(string query) => Assert.False(UnitConverter.TryConvert(query, out _), query);
}

public class ObsidianVaultTests
{
    static readonly IReadOnlyList<ObsidianVault> Vaults = ObsidianVaults.Parse(
        """{"vaults":{"a1":{"path":"C:\\Users\\me\\Notes","ts":1,"open":true},"b2":{"path":"D:\\Work Vault"},"c3":{"path":"C:\\Users\\me\\Notes\\Inner"}}}""");

    [Fact]
    public void ReadsTheVaultsOfObsidianJson()
    {
        Assert.Equal(["a1", "b2", "c3"], Vaults.Select(v => v.Id));
        Assert.Equal(["Notes", "Work Vault", "Inner"], Vaults.Select(v => v.Name));
        Assert.Empty(ObsidianVaults.Parse("not json"));
        Assert.Empty(ObsidianVaults.Parse("""{"other":1}"""));
        Assert.Empty(ObsidianVaults.Parse("""{"vaults":{"x":{"open":true}}}"""));
    }

    [Theory]
    [InlineData(@"C:\Users\me\Notes\Projects\Plan.md", "a1", "Projects/Plan")]
    [InlineData(@"c:\users\ME\notes\Top.MD", "a1", "Top")]                     // Windows' paths: any case
    [InlineData(@"D:\Work Vault\会議\10月 メモ.md", "b2", "会議/10月 メモ")]
    [InlineData(@"C:\Users\me\Notes\Inner\Deep.md", "c3", "Deep")]              // the innermost vault
    public void AVaultsMarkdownFileIsANote(string file, string vault, string note)
    {
        var n = ObsidianVaults.NoteOf(Vaults, file);
        Assert.NotNull(n);
        Assert.Equal(vault, n.Value.Vault.Id);
        Assert.Equal(note, n.Value.Note);
    }

    [Theory]
    [InlineData(@"C:\Users\me\Notes\Plan.txt")]              // not Markdown
    [InlineData(@"C:\Users\me\NotesOld\Plan.md")]            // a folder whose name only starts like a vault's
    [InlineData(@"C:\Users\me\Notes\.obsidian\snippet.md")]  // Obsidian's own settings
    [InlineData(@"C:\Users\me\Notes\.trash\Old.md")]          // Obsidian's own trash
    [InlineData(@"E:\Elsewhere\Plan.md")]
    public void OtherFilesAreNotNotes(string file) => Assert.Null(ObsidianVaults.NoteOf(Vaults, file));

    [Fact]
    public void ANoteOpensInObsidianByItsVaultsId()
    {
        Assert.Equal("obsidian://open?vault=b2&file=%E4%BC%9A%E8%AD%B0%2F10%E6%9C%88%20%E3%83%A1%E3%83%A2",
            ObsidianVaults.OpenUri(Vaults[1], "会議/10月 メモ"));
        Assert.Equal("会議", ObsidianVaults.FolderOf("会議/10月 メモ"));
        Assert.Equal("", ObsidianVaults.FolderOf("Top"));
    }
}
