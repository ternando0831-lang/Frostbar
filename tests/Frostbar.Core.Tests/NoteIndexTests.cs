using Frostbar.Core.Search;

namespace Frostbar.Core.Tests;

/// <summary>Stage 6-2: the bodies of Obsidian's notes in the search.</summary>
public class NoteIndexTests
{
    static readonly DateTime Old = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), New = new(2026, 10, 16, 0, 0, 0, DateTimeKind.Utc);
    const string Vault = @"C:\v";

    static NoteIndex With(params (string name, string text, DateTime written)[] notes)
    {
        var index = new NoteIndex();
        foreach (var (name, text, written) in notes) index.Put(Vault + "\\" + name + ".md", text, written);
        return index;
    }

    static List<string> Names(NoteIndex index, string query, int max = 10) =>
        [.. index.Find(TextNormalizer.Normalize(query), max).Select(h => Path.GetFileNameWithoutExtension(h.Path))];

    [Fact]
    public void FindsAWordInTheBodyAsTheQueryIsNormalized()
    {
        var index = With(("a", "ディスプレイの拡大率を変えた", Old), ("b", "関係のない話", Old));
        Assert.Equal(["a"], Names(index, "でぃすぷれい")); // katakana as hiragana
        Assert.Equal(["a"], Names(index, "拡大率"));
        Assert.Empty(Names(index, "壁紙"));
    }

    [Fact]
    public void CaseAndWidthDoNotMatter() => Assert.Equal(["a"], Names(With(("a", "Everything の IPC", Old)), "ｅｖｅｒｙｔｈｉｎｇ"));

    [Fact]
    public void TheFrontMatterIsNotSearched()
    {
        var index = With(("a", "---\ntags: [coding, 作業ログ]\n---\n\n# 題\n本文の語", Old));
        Assert.Empty(Names(index, "作業ログ"));
        Assert.Equal(["a"], Names(index, "本文の語"));
    }

    [Fact]
    public void MoreOftenFirstThenTheNewest()
    {
        var index = With(("once-new", "検索", New), ("twice-old", "検索と検索", Old), ("once-old", "検索", Old));
        Assert.Equal(["twice-old", "once-new", "once-old"], Names(index, "検索"));
        Assert.Equal(["twice-old"], Names(index, "検索", max: 1));
    }

    [Fact]
    public void OccurrencesAreCountedUpToTheCap()
    {
        var index = With(("many", string.Join(" ", Enumerable.Repeat("語", 100)), Old));
        Assert.Equal(NoteIndex.CountCap, index.Find("語", 5).Single().Count);
    }

    [Fact]
    public void AChangedNoteIsFoundByItsNewTextAndARemovedOneNotAtAll()
    {
        var index = With(("a", "古い語", Old));
        index.Put(Vault + @"\a.md", "新しい語", New);
        Assert.Empty(Names(index, "古い"));
        Assert.Equal(["a"], Names(index, "新しい"));
        Assert.True(index.Remove(Vault + @"\A.MD")); // paths are compared without case
        Assert.Empty(Names(index, "新しい"));
    }

    [Fact]
    public void AVaultsNotesAreForgottenTogether()
    {
        var index = new NoteIndex();
        index.Put(@"C:\v\a.md", "語", Old);
        index.Put(@"C:\v\sub\b.md", "語", Old);
        index.Put(@"C:\v2\c.md", "語", Old);
        index.RemoveUnder(@"C:\v");
        Assert.Equal(1, index.Count);
        Assert.True(index.Contains(@"C:\v2\c.md"));
    }

    [Fact]
    public void OnlyAFolderOfNotesThatWentReadsTheVaultAgain()
    {
        // Any file that went read every note again (an attachment, a plugin's temporary file, git's index.lock).
        var index = new NoteIndex();
        index.Put(@"C:\v\sub\b.md", "語", Old);
        Assert.True(index.AnyUnder(@"C:\v\sub"));
        Assert.True(index.AnyUnder(@"C:\v\SUB\"));
        Assert.False(index.AnyUnder(@"C:\v\sub\image.png"));
        Assert.False(index.AnyUnder(@"C:\v\.git\index.lock"));
        Assert.False(index.AnyUnder(@"C:\v\su")); // not a folder of it
    }

    [Theory]
    [InlineData("---\na: 1\n---\nbody", "body")]
    [InlineData("---\na: 1\nno end", "---\na: 1\nno end")] // no closing line: not front matter
    [InlineData("no front matter\n---\nx", "no front matter\n---\nx")]
    [InlineData("---\r\na: 1\r\n---\r\nbody", "body")]
    public void TheBodyIsTheTextAfterTheFrontMatter(string text, string body) => Assert.Equal(body, NoteIndex.Body(text));

    [Fact]
    public void TheSnippetIsTheLineAsWrittenWithoutMarkdownsMarks()
    {
        string text = "---\ntags: [x]\n---\n# 見出し\n- [ ] ディスプレイの拡大率を変える\n> 引用";
        Assert.Equal("ディスプレイの拡大率を変える", NoteIndex.Snippet(text, TextNormalizer.Normalize("拡大率")));
        Assert.Equal("見出し", NoteIndex.Snippet(text, TextNormalizer.Normalize("見出し")));
        Assert.Equal("引用", NoteIndex.Snippet(text, TextNormalizer.Normalize("引用")));
        Assert.Null(NoteIndex.Snippet(text, TextNormalizer.Normalize("tags"))); // the front matter
    }

    [Fact]
    public void ALongLineIsCutAroundTheMatch()
    {
        string line = new string('あ', 60) + "目印" + new string('い', 60);
        string? s = NoteIndex.Snippet(line, TextNormalizer.Normalize("目印"), width: 30);
        Assert.NotNull(s);
        Assert.Contains("目印", s);
        Assert.StartsWith("…", s);
        Assert.EndsWith("…", s);
        Assert.True(s.Length <= 32, s);
    }

    static List<string> Named(NoteIndex index, string query, int max = 10) =>
        [.. index.FindNames(TextNormalizer.Normalize(query), max).Select(n => Path.GetFileNameWithoutExtension(n.Path))];

    [Fact]
    public void ANoteIsFoundByItsNameNotByItsBody()
    {
        var index = With(("Quokka harbour plan", "nothing here", Old), ("Other", "a quokka in the body", Old));
        Assert.Equal(["Quokka harbour plan"], Named(index, "quokka"));
        Assert.Equal(["Quokka harbour plan"], Named(index, "ｈａｒｂｏｕｒ")); // a later word, written full width
        Assert.Empty(Named(index, "md")); // the extension is not part of the name
        Assert.Empty(Named(index, "nothing"));
    }

    [Fact]
    public void TheBestNameFirstThenTheNewest()
    {
        var index = With(("plan-old", "", Old), ("plan", "", Old), ("garden plan", "", New), ("plan-new", "", New));
        // The whole name, then the prefixes (the newer first: the same length), then a later word.
        Assert.Equal(["plan", "plan-new", "plan-old", "garden plan"], Named(index, "plan"));
        Assert.Equal(Matcher.Exact, index.FindNames("plan", 1).Single().Score);
        Assert.Equal(["plan"], Named(index, "plan", max: 1));
    }

    [Fact]
    public void ARenamedNoteIsFoundByItsNewName()
    {
        var index = With(("old name", "語", Old));
        index.Remove(Vault + @"\old name.md");
        index.Put(Vault + @"\new name.md", "語", New);
        Assert.Empty(Named(index, "old"));
        Assert.Equal(["new name"], Named(index, "new"));
    }

    [Fact]
    public void ABodyHitRanksBelowANoteFoundByItsName()
    {
        int nameFloor = Matcher.Inside - 100; // the search's least score for a note found by its name
        Assert.True(SearchRanker.BodyScore(NoteIndex.CountCap) < nameFloor);
        Assert.True(SearchRanker.BodyScore(5) > SearchRanker.BodyScore(1));
        Assert.True(SearchRanker.BodyScore(1) > 0);
    }
}
