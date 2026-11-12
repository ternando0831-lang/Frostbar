using System.Globalization;
using Frostbar.Core.Localization;
using Frostbar.Core.Search;

namespace Frostbar.Core.Tests;

/// <summary>Stage 6, E3c: the search's Clipboard filter over Windows' clipboard history.</summary>
public class ClipboardSearchTests
{
    static readonly Strings S = Strings.For("ja", CultureInfo.GetCultureInfo("ja-JP"));
    static readonly DateTimeOffset Now = new(2026, 10, 16, 15, 0, 0, TimeSpan.FromHours(9));

    static ClipboardItem Text(string id, string text, int minutesAgo) => new(id, ClipboardKind.Text, text, Now.AddMinutes(-minutesAgo));

    static List<string> Ids(IEnumerable<SearchResult> rows) => [.. rows.Select(r => r.Target)];

    [Fact]
    public void TheNewestComesFirst()
    {
        var rows = ClipboardSearch.Rows(ClipboardState.On, [Text("old", "a", 30), Text("new", "b", 1), Text("mid", "c", 10)], "", Now, S);
        Assert.Equal(["new", "mid", "old"], Ids(rows));
        Assert.True(rows[0].Score > rows[1].Score && rows[1].Score > rows[2].Score);
        Assert.All(rows, r => Assert.Equal(SearchKind.Clipboard, r.Kind));
    }

    [Fact]
    public void WordsTypedKeepTheItemsThatHaveThem()
    {
        var items = new List<ClipboardItem> { Text("a", "Hello world", 1), Text("b", "ディスプレイの拡大率", 2), Text("c", "nothing", 3) };
        Assert.Equal(["a"], Ids(ClipboardSearch.Rows(ClipboardState.On, items, TextNormalizer.Normalize("ＨＥＬＬＯ"), Now, S)));
        Assert.Equal(["b"], Ids(ClipboardSearch.Rows(ClipboardState.On, items, TextNormalizer.Normalize("でぃすぷれい"), Now, S)));
        Assert.Empty(ClipboardSearch.Rows(ClipboardState.On, items, TextNormalizer.Normalize("zzz"), Now, S)); // no row that says there is nothing
    }

    [Fact]
    public void TheHistoryOffIsOneRowThatOpensWindowsSettings()
    {
        var rows = ClipboardSearch.Rows(ClipboardState.Off, [Text("a", "x", 1)], "", Now, S);
        var row = Assert.Single(rows);
        Assert.Equal(ClipboardSearch.OffTarget, row.Target);
        Assert.Equal(S["clipboard.off"], row.Title);
        Assert.Equal(ClipboardSearch.EmptyTarget, Assert.Single(ClipboardSearch.Rows(ClipboardState.Unavailable, [], "", Now, S)).Target);
    }

    [Fact]
    public void AnEmptyHistorySaysSo()
    {
        var row = Assert.Single(ClipboardSearch.Rows(ClipboardState.On, [], "", Now, S));
        Assert.Equal(ClipboardSearch.EmptyTarget, row.Target);
        Assert.Equal(S["clipboard.empty"], row.Title);
    }

    [Fact]
    public void EachKindHasItsTitleAndDetail()
    {
        var picture = new ClipboardItem("p", ClipboardKind.Image, "", Now.AddMinutes(-12));
        var files = new ClipboardItem("f", ClipboardKind.Files, "a.txt, b.png", Now);
        var other = new ClipboardItem("o", ClipboardKind.Other, "", Now);
        Assert.Equal("画像", ClipboardSearch.Title(picture, S));
        Assert.Equal("画像 · 12分前", ClipboardSearch.Detail(picture, Now, S));
        Assert.Equal("a.txt, b.png", ClipboardSearch.Title(files, S));
        Assert.Equal("ファイル · たった今", ClipboardSearch.Detail(files, Now, S));
        Assert.Equal("その他", ClipboardSearch.Title(other, S));
        Assert.Equal("テキスト", ClipboardSearch.Title(Text("w", " \t\n  \n", 0), S)); // only spaces: what it holds
    }

    [Theory]
    [InlineData("first line\nsecond", "first line")]
    [InlineData("\n\n  \t  spaced   out\twords  \nnext", "spaced out words")]
    [InlineData("全角　スペース", "全角 スペース")]
    public void ATextsTitleIsItsFirstLineWithWords(string text, string title) => Assert.Equal(title, ClipboardSearch.Title(Text("t", text, 0), S));

    [Fact]
    public void ALongLineIsCut()
    {
        string preview = ClipboardSearch.Preview(new string('あ', 200));
        Assert.Equal(ClipboardSearch.PreviewWidth, preview.Length);
        Assert.EndsWith("…", preview);
        Assert.Equal("short", ClipboardSearch.Preview("short"));
    }

    [Fact]
    public void AtMostAsManyAsAFilterShows()
    {
        var items = Enumerable.Range(0, 70).Select(i => Text("i" + i, "item " + i, i)).ToList();
        Assert.Equal(SearchRanker.MaxFiltered, ClipboardSearch.Rows(ClipboardState.On, items, "", Now, S).Count);
    }

    [Fact]
    public void TheFilterShowsTheRowsInTheirOrder()
    {
        var rows = ClipboardSearch.Rows(ClipboardState.On, [Text("a", "x", 5), Text("b", "y", 1)], "", Now, S);
        var answers = SearchAnswers.None with { Clipboard = rows, Apps = [new SearchResult(SearchKind.App, "App", null, "app", 1000)] };
        Assert.Equal(["b", "a"], Ids(SearchRanker.Arrange(answers, SearchFilter.Clipboard)));
        Assert.DoesNotContain(SearchRanker.Arrange(answers), r => r.Kind == SearchKind.Clipboard); // never without the filter
    }
}
