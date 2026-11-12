using System.Globalization;
using Frostbar.Core.AppGrid;
using Frostbar.Core.Localization;
using Frostbar.Core.Menus;
using Frostbar.Core.Settings;
using Xunit;

namespace Frostbar.Core.Tests;

public class GridArrangementTests
{
    // The grid's apps in grid order (by name).
    static readonly GridApp[] Apps = [new("Alpha", "a"), new("Bravo", "b"), new("Charlie", "c"), new("Delta", "d")];

    /// <summary>Tiles as text: "a", "b" for apps, "[Name: a b]" for folders.</summary>
    static string Text(IEnumerable<GridTile> tiles) =>
        string.Join(" ", tiles.Select(t => t.IsFolder ? $"[{t.FolderName}: {string.Join(" ", t.Apps.Select(i => Apps[i].ParsingName))}]" : Apps[t.App].ParsingName));

    static string Text(IEnumerable<GridPlace> places) =>
        string.Join(" ", places.Select(p => p.Kind == GridPlace.FolderKind ? $"[{p.Id} {p.Name}: {string.Join(" ", p.Apps ?? [])}]" : p.Id));

    static List<GridTile> Tiles(params GridPlace[] places) => GridArrangement.Tiles(Apps, places);

    [Fact]
    public void WithoutPlacesTheAppsGoByName() => Assert.Equal("a b c d", Text(Tiles()));

    [Fact]
    public void PlacesComeFirstThenTheAppsPlacedNowhereByName() =>
        Assert.Equal("c a b d", Text(Tiles(GridPlace.App("c"), GridPlace.App("A"))));

    [Fact]
    public void AFolderShowsItsInstalledAppsInOrder() =>
        Assert.Equal("[Work: b a] c d", Text(Tiles(GridPlace.Folder("f1", "Work", ["b", "gone", "a"]))));

    [Fact]
    public void AFolderWithNoInstalledAppIsLeftOut() =>
        Assert.Equal("a b c d", Text(Tiles(GridPlace.Folder("f1", "Old", ["gone", "also gone"]))));

    [Fact]
    public void AnAppPlacedTwiceShowsAtTheFirstPlace() =>
        Assert.Equal("a [Work: b] c d", Text(Tiles(GridPlace.App("a"), GridPlace.Folder("f1", "Work", ["a", "b"]), GridPlace.App("b"))));

    [Fact]
    public void ASecondFolderWithTheSameIdIsLeftOut() =>
        Assert.Equal("[One: a b] c d", Text(Tiles(GridPlace.Folder("f1", "One", ["a", "b"]), GridPlace.Folder("f1", "Two", ["c"]))));

    [Fact]
    public void WhatIsKeptGivesTheSameTilesBack()
    {
        GridPlace[] places = [GridPlace.App("d"), GridPlace.Folder("f1", "Work", ["c", "a"]), GridPlace.App("b")];
        var tiles = Tiles(places);
        var kept = GridArrangement.Places(tiles, Apps, places);
        Assert.Equal("d [f1 Work: c a] b", Text(kept));
        Assert.Equal(Text(tiles), Text(GridArrangement.Tiles(Apps, kept)));
    }

    [Fact]
    public void PlacesOfAppsNotInstalledNowAreKeptAfterTheOthers()
    {
        GridPlace[] before = [GridPlace.App("a"), GridPlace.App("gone"), GridPlace.App("b")];
        Assert.Equal("a b c d gone", Text(GridArrangement.Places(Tiles(before), Apps, before)));
    }

    [Fact]
    public void AFoldersAppsNotInstalledNowStayInIt()
    {
        GridPlace[] before = [GridPlace.Folder("f1", "Work", ["a", "gone", "b"])];
        var tiles = GridArrangement.Rename(Tiles(before), 0, "Jobs");
        Assert.Equal("[f1 Jobs: a b gone] c d", Text(GridArrangement.Places(tiles, Apps, before)));
    }

    [Fact]
    public void AFolderWhoseAppsAreAllGoneIsKept()
    {
        GridPlace[] before = [GridPlace.App("b"), GridPlace.Folder("f9", "Old", ["gone"])];
        Assert.Equal("b a c d [f9 Old: gone]", Text(GridArrangement.Places(Tiles(before), Apps, before)));
    }

    [Fact]
    public void AFolderTakenApartIsNotKept()
    {
        GridPlace[] before = [GridPlace.Folder("f1", "Work", ["a", "b"])];
        var tiles = GridArrangement.WithoutMember(Tiles(before), 0, 1);
        tiles = GridArrangement.Settle(GridArrangement.Insert(tiles, 1, GridTile.ForApp(1, "b")), "f1", Apps);
        Assert.Equal("a b c d", Text(GridArrangement.Places(tiles, Apps, before)));
    }

    [Fact]
    public void ACarriedAppGoesWhereItIsLetGo()
    {
        var tiles = Tiles();
        var carried = tiles[2];
        Assert.Equal("c a b d", Text(GridArrangement.Insert(GridArrangement.Without(tiles, 2), 0, carried)));
        Assert.Equal("a b d c", Text(GridArrangement.Insert(GridArrangement.Without(tiles, 2), 3, carried)));
    }

    [Fact]
    public void LetGoOnAnAppTheTwoMakeAFolderInItsPlace()
    {
        var rest = GridArrangement.Without(Tiles(), 3); // d carried
        var tiles = GridArrangement.Merge(rest, 1, 3, "f2", "New");
        Assert.Equal("a [New: b d] c", Text(tiles));
        Assert.Equal("f2", tiles[1].Key);
    }

    [Fact]
    public void LetGoOnAFolderTheAppGoesInLast()
    {
        var tiles = Tiles(GridPlace.Folder("f1", "Work", ["a", "b"]));
        var rest = GridArrangement.Without(tiles, 1); // c carried
        Assert.Equal("[Work: a b c] d", Text(GridArrangement.Merge(rest, 0, 2, "unused", "unused")));
    }

    [Fact]
    public void AFolderLeftWithOneAppBecomesThatApp()
    {
        var tiles = Tiles(GridPlace.App("d"), GridPlace.Folder("f1", "Work", ["a", "b"]));
        var rest = GridArrangement.WithoutMember(tiles, 1, 0); // a taken out
        Assert.Equal("d [Work: b] c", Text(rest)); // as it is while a is carried
        var down = GridArrangement.Settle(GridArrangement.Insert(rest, 0, GridTile.ForApp(0, "a")), "f1", Apps);
        Assert.Equal("a d b c", Text(down));
    }

    [Fact]
    public void AFolderWithTwoAppsLeftStays()
    {
        var tiles = Tiles(GridPlace.Folder("f1", "Work", ["a", "b", "c"]));
        var rest = GridArrangement.WithoutMember(tiles, 0, 2);
        Assert.Equal("[Work: a b] c d", Text(GridArrangement.Settle(GridArrangement.Insert(rest, 1, GridTile.ForApp(2, "c")), "f1", Apps)));
    }

    [Fact]
    public void AnAppMovesInsideItsFolder()
    {
        var tiles = Tiles(GridPlace.Folder("f1", "Work", ["a", "b", "c"]));
        var rest = GridArrangement.WithoutMember(tiles, 0, 2); // c
        Assert.Equal("[Work: c a b] d", Text(GridArrangement.InsertMember(rest, 0, 0, 2)));
    }

    [Theory]
    [InlineData("  Daily   tools ", "Daily tools")]
    [InlineData("   ", "Work")]
    public void AFolderIsRenamedUnlessTheNameIsBlank(string name, string shown) =>
        Assert.Equal(shown, GridArrangement.Rename(Tiles(GridPlace.Folder("f1", "Work", ["a", "b"])), 0, name)[0].FolderName);

    [Fact]
    public void AFolderUngroupedLeavesItsAppsInItsPlace() =>
        Assert.Equal("d b a c", Text(GridArrangement.Disband(Tiles(GridPlace.App("d"), GridPlace.Folder("f1", "Work", ["b", "a"])), 1, Apps)));

    // ---- hidden apps (2026-10-24) ----

    static List<GridTile> WithHidden(params GridPlace[] places) => GridArrangement.Tiles(Apps, places, "Hidden");

    static GridPlace Hidden(params string[] apps) => GridPlace.Folder(GridPlace.HiddenId, "Old name", apps);

    [Fact]
    public void HiddenAppsShowLastInTheirFolderWhereverItIsKept()
    {
        Assert.Equal("c b d [Hidden: a]", Text(WithHidden(GridPlace.App("c"), Hidden("a"))));
        Assert.Equal("c b d [Hidden: a]", Text(WithHidden(Hidden("a"), GridPlace.App("c"))));
        Assert.True(WithHidden(Hidden("a"))[^1].IsHidden);
    }

    [Fact]
    public void AnAppHiddenAndPlacedElsewhereShowsHidden()
    {
        Assert.Equal("b c d [Hidden: a]", Text(WithHidden(GridPlace.App("a"), Hidden("a"))));
        Assert.Equal("[Work: b c] d [Hidden: a]", Text(WithHidden(GridPlace.Folder("f1", "Work", ["a", "b", "c"]), Hidden("a"))));
    }

    [Fact]
    public void TheHiddenFolderKeepsItsNameFromTheLanguageAndItsPlaceIsKept()
    {
        Assert.Equal("[Old name: a]", Text(GridArrangement.Tiles(Apps, [Hidden("a")]).Where(t => t.IsFolder)));
        GridPlace[] places = [GridPlace.App("d"), Hidden("b", "gone")];
        var tiles = WithHidden(places);
        var kept = GridArrangement.Places(tiles, Apps, places);
        Assert.Equal("d a c [hidden Hidden: b gone]", Text(kept));
        Assert.Equal(Text(tiles), Text(WithHidden([.. kept])));
        Assert.Equal("Hidden", GridArrangement.Rename(tiles, tiles.Count - 1, "Other")[^1].FolderName);
    }

    [Fact]
    public void HidingTakesTheAppFromItsPlaceOrItsFolder()
    {
        var tiles = GridArrangement.Hide(WithHidden(), 1, Apps, "Hidden");
        Assert.Equal("a c d [Hidden: b]", Text(tiles));
        Assert.Equal("a d [Hidden: b c]", Text(GridArrangement.Hide(tiles, 2, Apps, "Hidden")));
        Assert.Equal("a d [Hidden: b c]", Text(GridArrangement.Hide(GridArrangement.Hide(tiles, 2, Apps, "Hidden"), 1, Apps, "Hidden")));
        // Out of a folder of two: the folder becomes the app left.
        Assert.Equal("b c d [Hidden: a]", Text(GridArrangement.Hide(WithHidden(GridPlace.Folder("f1", "Work", ["a", "b"])), 0, Apps, "Hidden")));
    }

    [Fact]
    public void UnhidingPutsTheAppLastBeforeTheHiddenFolderWhichGoesWhenEmpty()
    {
        var tiles = WithHidden(GridPlace.App("a"), GridPlace.App("d"), Hidden("b", "c"));
        tiles = GridArrangement.Unhide(tiles, 1, Apps);
        Assert.Equal("a d b [Hidden: c]", Text(tiles));
        tiles = GridArrangement.Unhide(tiles, 2, Apps);
        Assert.Equal("a d b c", Text(tiles));
        Assert.Equal("a d b c", Text(GridArrangement.Unhide(tiles, 0, Apps))); // not hidden: as it was
    }

    [Fact]
    public void TheHiddenFolderStaysWithOneAppAndGoesWithNone()
    {
        var tiles = WithHidden(GridPlace.App("a"), Hidden("b", "c")); // a d [Hidden: b c]
        var rest = GridArrangement.WithoutMember(tiles, 2, 0); // b carried out
        Assert.Equal("a b d [Hidden: c]", Text(GridArrangement.Settle(GridArrangement.Insert(rest, 1, GridTile.ForApp(1, "b")), GridPlace.HiddenId, Apps)));
        var none = GridArrangement.WithoutMember(GridArrangement.WithoutMember(tiles, 2, 0), 2, 0);
        Assert.Equal("a d", Text(GridArrangement.Settle(none, GridPlace.HiddenId, Apps)));
    }

    [Fact]
    public void NothingGoesAfterTheHiddenFolder()
    {
        var tiles = WithHidden(Hidden("b")); // a c d [Hidden: b]
        Assert.Equal(3, GridArrangement.Room(tiles));
        Assert.Equal(4, GridArrangement.Room(Tiles()));
        var rest = GridArrangement.Without(tiles, 0); // a carried
        Assert.Equal("c d a [Hidden: b]", Text(GridArrangement.Insert(rest, rest.Count, tiles[0])));
        Assert.Equal("c [Hidden: b a]", Text(GridArrangement.Merge(GridArrangement.Without(rest, 1), 1, 0, "unused", "unused")));
    }

    [Theory]
    [InlineData("Microsoft Word", "Microsoft Excel", "Microsoft")]
    [InlineData("Visual Studio Code", "Visual Studio 2022", "Visual Studio")]
    [InlineData("Adobe Photoshop 2024", "adobe Illustrator", "Adobe")]
    [InlineData("Git Bash", "Git CMD", "Git")]
    [InlineData("7-Zip File Manager", "7-Zip Help", "7-Zip")]
    [InlineData("Foo - Bar", "Foo - Baz", "Foo")]
    [InlineData("Steam", "Steam", "名称未設定")]
    [InlineData("Microsoft Edge", "Microsoft", "名称未設定")]
    [InlineData("メモ帳", "電卓", "名称未設定")]
    [InlineData("2 Player", "2 Players Online", "名称未設定")]
    public void ANewFolderIsNamedByTheWordsTheNamesBeginWith(string first, string second, string name) =>
        Assert.Equal(name, GridArrangement.FolderName(first, second, "名称未設定"));
}

public class GridArrangementSettingsTests
{
    [Fact]
    public void BrokenPlacesAreDroppedAndFoldersMadeWhole()
    {
        var s = new AppSettings();
        s.AppGrid.Arrangement =
        [
            GridPlace.App("a"),
            null!,
            new GridPlace { Kind = "app", Id = " " },
            new GridPlace { Kind = "widget", Id = "w" },
            new GridPlace { Kind = "folder", Id = "f1", Name = null, Apps = ["b", "", "B", "c"] },
            new GridPlace { Kind = "folder", Id = "f2", Name = "Empty", Apps = null },
            new GridPlace { Kind = "folder", Id = "f1", Name = "Again", Apps = ["d"] },
        ];
        var fixes = SettingsValidator.Normalize(s);
        Assert.Contains(fixes, f => f.StartsWith("appGrid.arrangement: dropped 5", StringComparison.Ordinal));
        Assert.Equal(2, s.AppGrid.Arrangement.Count);
        var folder = s.AppGrid.Arrangement[1];
        Assert.Equal("", folder.Name);
        Assert.Equal(["b", "c"], folder.Apps);
    }

    [Fact]
    public void ANullArrangementIsEmpty()
    {
        var s = new AppSettings();
        s.AppGrid.Arrangement = null!;
        SettingsValidator.Normalize(s);
        Assert.Empty(s.AppGrid.Arrangement);
    }

    [Fact]
    public void TheArrangementIsSavedAndReadBack()
    {
        string dir = Path.Combine(Path.GetTempPath(), "frostbar-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            string file = Path.Combine(dir, "settings.json");
            var s = new AppSettings();
            s.AppGrid.Arrangement = [GridPlace.App("c"), GridPlace.Folder("folder-1", "仕事", ["a", "b"])];
            SettingsStore.Save(file, s);
            var back = SettingsStore.Load(file).Settings.AppGrid.Arrangement;
            Assert.Equal(["app", "folder"], back.Select(p => p.Kind));
            Assert.Equal("仕事", back[1].Name);
            Assert.Equal(["a", "b"], back[1].Apps);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}

public class GridDropTests
{
    static readonly GridLayout L = GridGeometry.Compute(1536, 772); // 7 × 5, icons 72

    static (double x, double y) Middle(int cell)
    {
        var (icon, _) = GridGeometry.Item(L, cell);
        return (icon.X + icon.Width / 2, icon.Y + icon.Height / 2);
    }

    static (double x, double y) Name(int cell)
    {
        var (_, label) = GridGeometry.Item(L, cell);
        return (label.X + label.Width / 2, label.Y + label.Height / 2);
    }

    // Ten tiles besides the carried one, the gap at 3 (where it was picked up).
    [Fact]
    public void OverTheGapNothingChanges()
    {
        var (x, y) = Middle(3);
        Assert.Equal((3, -1), GridDrop.Target(L, x, y, 0, 3, 10));
    }

    [Fact]
    public void OverAnEarlierTilesNameTheGapMovesThere()
    {
        var (x, y) = Name(0);
        Assert.Equal((0, -1), GridDrop.Target(L, x, y, 0, 3, 10));
    }

    [Fact]
    public void OverAnEarlierTilesMiddleItIsTheOneUnderThePointer()
    {
        var (x, y) = Middle(1);
        Assert.Equal((3, 1), GridDrop.Target(L, x, y, 0, 3, 10));
    }

    [Fact]
    public void OverALaterTilesMiddleTheTileIsCountedWithoutTheGap()
    {
        var (x, y) = Middle(5); // the gap at 3 shows tile 4 in cell 5
        Assert.Equal((3, 4), GridDrop.Target(L, x, y, 0, 3, 10));
    }

    [Fact]
    public void WithoutPuttingInTheGapMovesEvenOverAMiddle()
    {
        var (x, y) = Middle(5);
        Assert.Equal((5, -1), GridDrop.Target(L, x, y, 0, 3, 10, into: false));
    }

    [Fact]
    public void PastTheLastTileTheGapGoesAfterIt()
    {
        var (x, y) = Middle(20);
        Assert.Equal((10, -1), GridDrop.Target(L, x, y, 0, 3, 10));
    }

    [Fact]
    public void OnTheNextPageCellsCountFromItsFirst()
    {
        var (x, y) = Name(2);
        Assert.Equal((37, -1), GridDrop.Target(L, x, y, 1, 3, 50));
    }

    [Fact]
    public void OutsideTheCellsNothingChanges() => Assert.Equal((3, -1), GridDrop.Target(L, 20, 20, 0, 3, 10));

    // The tenth of the ten (rest 9) stays last: the hidden apps' folder, shown in cell 10 with the gap at 3.
    [Fact]
    public void PastTheLastTileTheGapStaysBeforeTheHiddenFolder()
    {
        var (x, y) = Middle(20);
        Assert.Equal((9, -1), GridDrop.Target(L, x, y, 0, 3, 10, room: 9));
        (x, y) = Name(10);
        Assert.Equal((9, -1), GridDrop.Target(L, x, y, 0, 3, 10, room: 9));
    }

    [Fact]
    public void OverTheHiddenFoldersMiddleTheAppGoesIn()
    {
        var (x, y) = Middle(10);
        Assert.Equal((3, 9), GridDrop.Target(L, x, y, 0, 3, 10, room: 9));
        // The gap just before it (where the app would land): its middle still takes the app in.
        Assert.Equal((9, 9), GridDrop.Target(L, x, y, 0, 9, 10, room: 9));
    }

    [Theory]
    [InlineData(10, -1)]
    [InlineData(200, 0)]
    [InlineData(768, 0)]
    [InlineData(1530, 1)]
    public void TheMarginsOuterHalvesTurnThePage(double x, int side) => Assert.Equal(side, GridDrop.Edge(L, x, 1536));
}

public class FolderGeometryTests
{
    static readonly GridLayout L = GridGeometry.Compute(1536, 772); // 7 × 5, cells 171 × 122 DIP

    [Theory]
    [InlineData(2, 1, 1)]
    [InlineData(7, 1, 1)]
    [InlineData(8, 2, 1)]
    [InlineData(28, 4, 1)]
    [InlineData(29, 4, 2)]
    public void RowsUpToOneFewerThanTheGridsThenPages(int count, int rows, int pages)
    {
        var f = FolderGeometry.Compute(L, count);
        Assert.Equal(rows, f.Apps.Rows);
        Assert.Equal(7, f.Apps.Columns);
        Assert.Equal(pages, f.Pages);
    }

    [Fact]
    public void ThePanelSpansTheGridsColumnsCentredWithTheNameAbove()
    {
        var f = FolderGeometry.Compute(L, 3);
        Assert.Equal(L.Cells.X - FolderGeometry.Pad, f.Panel.X);
        Assert.Equal(L.Cells.Width + 2 * FolderGeometry.Pad, f.Panel.Width, 3);
        Assert.Equal(L.CellWidth, f.Apps.CellWidth, 3);
        Assert.Equal(L.CellHeight, f.Apps.CellHeight, 3);
        Assert.Equal(f.Title.Bottom + FolderGeometry.TitleGap, f.Panel.Y);
        Assert.Equal(f.Panel.Y + FolderGeometry.Pad, f.Apps.Cells.Y);
        double above = f.Title.Y - L.Cells.Y, below = L.Cells.Bottom - f.Panel.Bottom;
        Assert.True(Math.Abs(above - below) <= 1, $"{above} above, {below} below");
    }

    [Fact]
    public void PagesLeaveRoomForTheirDotsInsideThePanel()
    {
        var f = FolderGeometry.Compute(L, 40);
        Assert.True(f.Apps.DotsY > f.Apps.Cells.Bottom && f.Apps.DotsY < f.Panel.Bottom, $"dots at {f.Apps.DotsY}");
        Assert.True(f.Panel.Bottom <= L.Cells.Bottom + 1);
    }

    [Fact]
    public void AClosedFolderShowsNineLittleIconsThreeAcross()
    {
        var minis = FolderGeometry.MiniIcons(56, 12);
        Assert.Equal(9, minis.Count);
        // The candidate A as drawn: 12 DIP icons 4 apart, 6 in from the plate's edge.
        Assert.Equal(new Box(6, 6, 12, 12), minis[0]);
        Assert.Equal(new Box(22, 6, 12, 12), minis[1]);
        Assert.Equal(new Box(6, 22, 12, 12), minis[3]);
        Assert.Equal(new Box(38, 38, 12, 12), minis[8]);
        Assert.Equal(2, FolderGeometry.MiniIcons(90, 2).Count);
    }
}

public class GridMenuTests
{
    static readonly Strings Ja = Strings.For("ja", CultureInfo.InvariantCulture);

    static string Text(IEnumerable<MenuItem> items) => string.Join(" | ", items.Select(i => i.IsSeparator ? "-" : i.Label));

    [Fact]
    public void AProgramsMenuHasEverything() =>
        Assert.Equal("開く | 管理者として実行 | - | Dock に追加 | エクスプローラーで表示 | - | 非表示にする",
            Text(GridMenu.ForApp(Ja, new GridAppFacts(Hidden: false, InDock: false, Program: true, File: true), "エクスプローラー")));

    [Fact]
    public void APackagedAppKeptInTheDockAndHidden() =>
        Assert.Equal("開く | - | Dock から削除 | - | 表示する",
            Text(GridMenu.ForApp(Ja, new GridAppFacts(Hidden: true, InDock: true, Program: false, File: false), "エクスプローラー")));

    [Fact]
    public void WithoutADockNorAFileTheirGroupGoes() =>
        Assert.Equal("開く | - | 非表示にする", Text(GridMenu.ForApp(Ja, new GridAppFacts(false, null, false, false), "Files")));

    [Fact]
    public void TheFileManagerNamesItsItem() =>
        Assert.Contains("Filesで表示", Text(GridMenu.ForApp(Ja, new GridAppFacts(false, null, true, true), "Files")));

    [Fact]
    public void AFoldersMenuAndTheHiddenOnes()
    {
        Assert.Equal("開く | 名前を変更 | - | フォルダを解除", Text(GridMenu.ForFolder(Ja, hidden: false)));
        Assert.Equal("開く", Text(GridMenu.ForFolder(Ja, hidden: true)));
    }

    static readonly Box Screen = new(0, 0, 1280, 720);

    [Fact]
    public void TheMenuOpensAtThePointer() => Assert.Equal((300.0, 200.0), GridMenu.At(300, 200, 220, 160, Screen));

    [Fact]
    public void NearTheRightAndTheBottomItOpensLeftAndAboveThePointer() =>
        Assert.Equal((1200.0 - 220, 700.0 - 160), GridMenu.At(1200, 700, 220, 160, Screen));

    [Fact]
    public void ItStaysOnTheScreen() => Assert.Equal((100.0, 4.0), GridMenu.At(100, 100, 220, 900, Screen));
}
