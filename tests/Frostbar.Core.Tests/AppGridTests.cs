using System.Globalization;
using Frostbar.Core.AppGrid;
using Frostbar.Core.Search;
using Xunit;

namespace Frostbar.Core.Tests;

public class AppGridModelTests
{
    [Theory]
    [InlineData("Brave", "Brave.EX6LYHT6NIKSJPBI33FJVNQKYY", false)]
    [InlineData("メモ帳", "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\notepad.exe", false)]
    [InlineData("Uninstall Foo", "{6D809377-6AF0-444B-8957-A3773F02200E}\\Foo\\unins000.exe", true)]
    [InlineData("Foo のアンインストール", "{6D809377-6AF0-444B-8957-A3773F02200E}\\Foo\\remove.exe", true)]
    [InlineData("Foo", "{6D809377-6AF0-444B-8957-A3773F02200E}\\Foo\\unins001.exe", true)]
    [InlineData("Foo Manual", "{6D809377-6AF0-444B-8957-A3773F02200E}\\Foo\\manual.pdf", true)]
    [InlineData("Read Me", "{6D809377-6AF0-444B-8957-A3773F02200E}\\Foo\\README.TXT", true)]
    [InlineData("Foo Website", "https://example.com/", true)]
    [InlineData("Foo Web", "{A77F5D77-2E2B-44C3-A6A2-ABA601054A51}\\Foo\\Foo.url", true)]
    public void LeavesOutLinksDocumentsAndUninstallers(string name, string parsing, bool hidden) =>
        Assert.Equal(hidden, AppGridModel.Hidden(name, parsing));

    [Fact]
    public void ArrangesByNameOnceEachWithoutTheHiddenOnes()
    {
        var apps = new[]
        {
            new GridApp("b", "b.exe"), new GridApp("A", "a.exe"), new GridApp("c", "c.exe"),
            new GridApp("A again", "a.exe"), new GridApp("Manual", "x\\manual.pdf"), new GridApp(" ", "blank.exe"),
        };
        var list = AppGridModel.Arrange(apps, CultureInfo.GetCultureInfo("en-US"));
        Assert.Equal(["A", "b", "c"], list.Select(a => a.Name));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(35, 1)]
    [InlineData(36, 2)]
    [InlineData(177, 6)]
    public void Pages(int count, int pages) => Assert.Equal(pages, AppGridModel.PageCount(count, 35));

    static readonly Candidate[] Names = new[] { "Calculator", "Camera", "Clock", "Notepad", "メモ帳", "Visual Studio Code" }.Select(n => new Candidate(n)).ToArray();

    [Fact]
    public void AnEmptyQueryKeepsEveryAppInOrder() => Assert.Equal([0, 1, 2, 3, 4, 5], AppGridModel.Filter(Names, ""));

    [Fact]
    public void FilterPutsTheBestAnswerFirstAndEqualsInGridOrder()
    {
        Assert.Equal([1, 0], AppGridModel.Filter(Names, TextNormalizer.Normalize("ca")));     // both prefixes: the shorter name (closer to the query) first
        Assert.Equal([5], AppGridModel.Filter(Names, TextNormalizer.Normalize("vsc")));        // initials
        Assert.Equal([4], AppGridModel.Filter(Names, TextNormalizer.Normalize("メモ")));
        Assert.Empty(AppGridModel.Filter(Names, TextNormalizer.Normalize("zzz")));
        // "c": Calculator, Camera, Clock are prefixes, Code a later word: the prefixes first, shortest first
        Assert.Equal([2, 1, 0, 5], AppGridModel.Filter(Names, "c"));
    }

    [Fact]
    public void HiddenAppsAnswerAfterAllTheOthers()
    {
        // Clock hidden: after Camera and Calculator, as good an answer as it is; Code still after the shown ones.
        Assert.Equal([1, 0, 5, 2], AppGridModel.Filter(Names, "c", new HashSet<int> { 2 }));
        Assert.Equal([0, 1, 2, 3, 4, 5], AppGridModel.Filter(Names, "", new HashSet<int> { 2 }));
    }
}

public class GridPagingTests
{
    [Theory]
    [InlineData(0, 4, -300, 0, 1)]     // past a sixth of 1536 (256): next page
    [InlineData(1, 4, 300, 0, 0)]      // the other way
    [InlineData(1, 4, -200, 0, 1)]     // short and slow: back where it was
    [InlineData(1, 4, -40, -0.8, 2)]   // a flick
    [InlineData(1, 4, -40, 0.8, 1)]    // a flick back against the drag: stays
    [InlineData(1, 4, -10, -2, 1)]     // too short to be a flick
    [InlineData(3, 4, -600, 0, 3)]     // nothing after the last page
    [InlineData(0, 4, 600, 0, 0)]      // nothing before the first
    [InlineData(0, 1, -600, -2, 0)]    // a single page
    public void DragsTurnOnePageAtMost(int page, int pages, double dx, double v, int want) =>
        Assert.Equal(want, GridPaging.AfterDrag(page, pages, dx, v, 1536));

    [Fact]
    public void PagesStretchLessBeyondTheEnds()
    {
        Assert.Equal(-90, GridPaging.Follow(1, 4, -90));
        Assert.Equal(30, GridPaging.Follow(0, 4, 90));
        Assert.Equal(-30, GridPaging.Follow(3, 4, -90));
        Assert.Equal(90, GridPaging.Follow(3, 4, 90));
    }

    [Theory]
    [InlineData(-1, 1, 0, 10, 0)]      // nothing chosen: the first
    [InlineData(0, 1, 0, 10, 1)]
    [InlineData(6, 1, 0, 10, 7)]       // the end of a row: the next row's first
    [InlineData(0, -1, 0, 10, 0)]      // the very first stays
    [InlineData(9, 0, 1, 20, 16)]      // down a row
    [InlineData(16, 0, -1, 20, 9)]     // and up again
    [InlineData(2, 0, -1, 10, 2)]      // nothing above the first row
    [InlineData(5, 0, 1, 10, 9)]       // down from above a short last row: its last item
    [InlineData(9, 0, 1, 10, 9)]       // already in the last row
    [InlineData(9, 1, 0, 10, 9)]       // the very last stays
    [InlineData(30, 0, 1, 40, 37)]     // down from a page's last row: the next page's first row
    public void ArrowsMoveThroughResults(int from, int dx, int dy, int count, int want) =>
        Assert.Equal(want, GridPaging.Move(from, dx, dy, 7, count));

    [Fact]
    public void NoResultsNoSelection() => Assert.Equal(-1, GridPaging.Move(0, 1, 0, 7, 0));
}

public class WheelPagerTests
{
    [Fact]
    public void ANotchTurnsAPage()
    {
        Assert.Equal(1, new WheelPager().Feed(120, 1000));
        Assert.Equal(-1, new WheelPager().Feed(-120, 1000));
    }

    [Fact]
    public void AFastSpinTurnsOnlyOnce()
    {
        var w = new WheelPager();
        int turns = 0;
        for (int i = 0; i < 10; i++) turns += w.Feed(120, 1000 + i * 30);
        Assert.Equal(1, turns);
        Assert.Equal(1, w.Feed(120, 1000 + 9 * 30 + 400)); // after a pause it turns again
    }

    [Fact]
    public void DeliberateNotchesEachTurn()
    {
        var w = new WheelPager();
        Assert.Equal(3, Enumerable.Range(0, 3).Sum(i => w.Feed(120, 1000 + i * 300)));
    }

    [Fact]
    public void ATouchpadSwipeWithItsInertiaTurnsOnce()
    {
        var w = new WheelPager();
        int turns = 0;
        for (int i = 0; i < 80; i++) turns += w.Feed(Math.Max(2, 30 - i / 3), 1000 + i * 8);
        Assert.Equal(1, turns);
    }

    [Fact]
    public void ASmallNudgeTurnsNothingAndIsForgottenAfterAPause()
    {
        var w = new WheelPager();
        Assert.Equal(0, w.Feed(60, 1000));
        Assert.Equal(0, w.Feed(60, 1400)); // a new gesture: the first 60 is gone
        Assert.Equal(1, w.Feed(60, 1450));
    }
}

public class GridGeometryTests
{
    // 1920×1080 at 125%: under the menu bar (24 DIP), above the Dock's strip (68 DIP)
    static readonly GridLayout L = GridGeometry.Compute(1536, 772);

    [Fact]
    public void SevenByFiveCellsInsideTheArea()
    {
        Assert.Equal(35, L.PerPage);
        for (int i = 0; i < L.PerPage; i++)
        {
            var (icon, label) = GridGeometry.Item(L, i);
            Assert.True(icon.X >= L.Cells.X && icon.Right <= L.Cells.Right && icon.Y >= L.Cells.Y && label.Bottom <= L.Cells.Bottom, $"item {i}");
        }
        Assert.True(L.Field.Bottom < L.Cells.Y && L.DotsY > L.Cells.Bottom && L.DotsY + 8 < 772); // the dots above the Dock's strip
    }

    [Fact]
    public void IconsAreWholePixelsAndNeverOverlap()
    {
        Assert.Equal(0, L.IconSize % 4);
        Assert.InRange(L.IconSize, 40, 96);
        var boxes = Enumerable.Range(0, L.PerPage).Select(i => GridGeometry.Item(L, i).icon).ToList();
        for (int a = 0; a < boxes.Count; a++)
            for (int b = a + 1; b < boxes.Count; b++)
                Assert.False(boxes[a].X < boxes[b].Right && boxes[b].X < boxes[a].Right && boxes[a].Y < boxes[b].Bottom && boxes[b].Y < boxes[a].Bottom, $"{a} and {b} overlap");
    }

    [Fact]
    public void HitTestFindsEachItemAndNothingBetween()
    {
        for (int i = 0; i < L.PerPage; i++)
        {
            var (icon, label) = GridGeometry.Item(L, i);
            Assert.Equal(i, GridGeometry.HitTest(L, icon.X + icon.Width / 2, icon.Y + icon.Height / 2, L.PerPage));
            Assert.Equal(i, GridGeometry.HitTest(L, label.X + label.Width / 2, label.Y + label.Height / 2, L.PerPage));
        }
        var first = GridGeometry.Cell(L, 0);
        Assert.Equal(-1, GridGeometry.HitTest(L, first.X + 1, first.Y + 1, L.PerPage)); // the cell's corner, away from the icon
        Assert.Equal(-1, GridGeometry.HitTest(L, L.Cells.X + 1, L.Cells.Y - 10, L.PerPage)); // above the cells
        var (lastIcon, _) = GridGeometry.Item(L, 3);
        Assert.Equal(-1, GridGeometry.HitTest(L, lastIcon.X + 5, lastIcon.Y + 5, 3)); // a page with only 3 items
    }

    [Theory]
    [InlineData(64, 64)]   // fits: as chosen
    [InlineData(66, 64)]   // 4 DIP steps
    [InlineData(160, 84)]  // too large for 7 × 5 here: as large as a cell holds with its name
    [InlineData(20, 32)]   // never below the smallest offered
    public void AChosenIconSizeIsKeptWhereItFits(int chosen, int want)
    {
        var l = GridGeometry.Compute(1536, 772, 7, 5, chosen);
        Assert.Equal(want, l.IconSize);
        var boxes = Enumerable.Range(0, l.PerPage).Select(i => GridGeometry.Item(l, i)).ToList();
        for (int i = 0; i < boxes.Count; i++)
        {
            var (icon, label) = boxes[i];
            Assert.True(icon.X >= l.Cells.X && icon.Right <= l.Cells.Right && icon.Y >= l.Cells.Y && label.Bottom <= l.Cells.Bottom, $"item {i}");
        }
        for (int a = 0; a + 1 < boxes.Count; a++)
            Assert.True(boxes[a].icon.Right <= boxes[a + 1].icon.X || boxes[a].icon.Y != boxes[a + 1].icon.Y, $"{a} and {a + 1} overlap");
    }

    [Theory]
    [InlineData(1536, 772, 7, 5)] // this PC at 125%
    [InlineData(1280, 628, 7, 8)] // 1080p at 150%, the most rows
    [InlineData(1280, 628, 12, 8)]
    public void TheAutomaticSizeLeavesRoomForTheNames(double width, double height, int columns, int rows)
    {
        var l = GridGeometry.Compute(width, height, columns, rows);
        var boxes = Enumerable.Range(0, l.PerPage).Select(i => GridGeometry.Item(l, i)).ToList();
        for (int i = 0; i + columns < boxes.Count; i++)
            Assert.True(boxes[i].label.Bottom <= boxes[i + columns].icon.Y, $"the name of item {i} runs into the icon under it (icon {l.IconSize})");
        Assert.True(boxes[^1].label.Bottom <= l.Cells.Bottom);
    }

    [Fact]
    public void FewerColumnsAndRowsMakeRoomForLargerIcons()
    {
        var crowded = GridGeometry.Compute(1536, 772, 7, 5, 160);
        var roomy = GridGeometry.Compute(1536, 772, 5, 3, 160);
        Assert.True(roomy.IconSize > crowded.IconSize);
        Assert.Equal(15, roomy.PerPage);
    }

    [Fact]
    public void DotsAreCentred()
    {
        var dots = GridGeometry.Dots(1536, 6);
        Assert.Equal(1536 / 2.0, dots.Average(), 6);
        Assert.Equal(18, dots[1] - dots[0], 6);
    }
}
