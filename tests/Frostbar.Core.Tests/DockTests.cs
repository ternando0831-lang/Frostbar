using Frostbar.Core.Dock;
using Frostbar.Core.Settings;
using Xunit;

namespace Frostbar.Core.Tests;

public class DockGeometryTests
{
    static SlotKind[] Icons(int n) => Enumerable.Repeat(SlotKind.Icon, n).ToArray();
    const double Area = 1536, Base = 48, Max = 80, Scale = 1.25;

    [Fact]
    public void AtRestTheRowIsCentredAndPlateHoldsEverything()
    {
        var l = DockGeometry.Compute(Icons(5), Area, Base, Max, null, 1, Scale);
        double expected = DockGeometry.Gap + 5 * (Base + DockGeometry.Gap);
        Assert.Equal(expected, l.PlateWidth, 3);
        Assert.InRange(l.PlateLeft, (Area - expected) / 2 - 0.5, (Area - expected) / 2 + 0.5); // within half a pixel
        Assert.All(l.Slots, s => Assert.Equal(Base, s.Size, 3));
        Assert.Equal(l.PlateLeft + DockGeometry.Gap, l.Slots[0].Left, 3);
    }

    [Fact]
    public void TheSpacingIsBetweenTheIconsAndAtBothEnds()
    {
        var l = DockGeometry.Compute(Icons(5), Area, Base, Max, null, 1, Scale, spacing: 12);
        Assert.Equal(12 + 5 * (Base + 12), l.PlateWidth, 3);
        Assert.Equal(l.PlateLeft + 12, l.Slots[0].Left, 3);
        Assert.Equal(l.Slots[0].Right + 12, l.Slots[1].Left, 3);
        Assert.Equal(l.PlateLeft + l.PlateWidth - 12, l.Slots[4].Right, 3);
        Assert.Equal(DockGeometry.MaxPlateWidth(Icons(5), Base, Base, Scale, 12), l.PlateWidth, 3);
    }

    [Fact]
    public void ASlotWithNoWeightTakesNoRoomAndAHalfOneHalf()
    {
        // Five icons, the middle one coming in (or going out).
        var four = DockGeometry.Compute(Icons(4), Area, Base, Max, null, 1, Scale);
        var gone = DockGeometry.Compute(Icons(5), Area, Base, Max, null, 1, Scale, [1, 1, 0, 1, 1]);
        Assert.Equal(four.PlateWidth, gone.PlateWidth, 3);
        Assert.Equal(four.Slots[1].Left, gone.Slots[1].Left, 3);
        Assert.Equal(four.Slots[2].Left, gone.Slots[3].Left, 3);
        Assert.Equal(0, gone.Slots[2].Size, 3);
        var full = DockGeometry.Compute(Icons(5), Area, Base, Max, null, 1, Scale);
        var half = DockGeometry.Compute(Icons(5), Area, Base, Max, null, 1, Scale, [1, 1, 0.5, 1, 1]);
        Assert.InRange(half.PlateWidth, (four.PlateWidth + full.PlateWidth) / 2 - 1 / Scale, (four.PlateWidth + full.PlateWidth) / 2 + 1 / Scale); // its ends on whole pixels
        Assert.Equal(Base / 2, half.Slots[2].Size, 3);
        // All weights 1: the same as no weights.
        var ones = DockGeometry.Compute(Icons(5), Area, Base, Max, full.Slots[2].Center, 1, Scale, [1, 1, 1, 1, 1]);
        var plain = DockGeometry.Compute(Icons(5), Area, Base, Max, full.Slots[2].Center, 1, Scale);
        Assert.Equal(plain.Slots.Select(s => s.Left), ones.Slots.Select(s => s.Left));
    }

    [Fact]
    public void TheIconUnderThePointerReachesTheMagnifiedSize()
    {
        var rest = DockGeometry.Compute(Icons(7), Area, Base, Max, null, 1, Scale);
        var l = DockGeometry.Compute(Icons(7), Area, Base, Max, rest.Slots[3].Center, 1, Scale);
        Assert.Equal(Max, l.Slots[3].Size, 1);
    }

    [Fact]
    public void SizesAreSymmetricAroundThePointerAndShrinkWithDistance()
    {
        var rest = DockGeometry.Compute(Icons(9), Area, Base, Max, null, 1, Scale);
        var l = DockGeometry.Compute(Icons(9), Area, Base, Max, rest.Slots[4].Center, 1, Scale);
        for (int i = 1; i <= 4; i++)
        {
            Assert.Equal(l.Slots[4 - i].Size, l.Slots[4 + i].Size, 3);
            Assert.True(l.Slots[4 + i].Size <= l.Slots[4 + i - 1].Size);
        }
        Assert.Equal(Base, l.Slots[0].Size, 3); // beyond the reach
    }

    [Fact]
    public void StrengthEasesMagnificationInAndOut()
    {
        var rest = DockGeometry.Compute(Icons(5), Area, Base, Max, null, 1, Scale);
        var off = DockGeometry.Compute(Icons(5), Area, Base, Max, rest.Slots[2].Center, 0, Scale);
        var half = DockGeometry.Compute(Icons(5), Area, Base, Max, rest.Slots[2].Center, 0.5, Scale);
        Assert.Equal(rest.PlateWidth, off.PlateWidth, 3);
        Assert.InRange(half.Slots[2].Size, Base + 1, Max - 1);
    }

    [Fact]
    public void ThePlateGrowsSmoothlyAsThePointerMoves()
    {
        var kinds = Icons(10);
        var rest = DockGeometry.Compute(kinds, Area, Base, Max, null, 1, Scale);
        double previous = double.NaN;
        for (double x = rest.PlateLeft - 50; x <= rest.PlateLeft + rest.PlateWidth + 50; x += 1)
        {
            double w = DockGeometry.Compute(kinds, Area, Base, Max, x, 1, Scale).PlateWidth;
            if (!double.IsNaN(previous)) Assert.True(Math.Abs(w - previous) <= 4, $"plate jumped from {previous} to {w} at x={x}");
            previous = w;
        }
    }

    [Fact]
    public void MaxPlateWidthBoundsEveryPointerPosition()
    {
        var kinds = Icons(12).Concat([SlotKind.Separator, SlotKind.Icon, SlotKind.Icon]).ToArray();
        double max = DockGeometry.MaxPlateWidth(kinds, Base, Max, Scale);
        var rest = DockGeometry.Compute(kinds, Area, Base, Max, null, 0, Scale);
        double seen = 0;
        for (double x = rest.PlateLeft - 200; x <= rest.PlateLeft + rest.PlateWidth + 200; x += 0.5)
            seen = Math.Max(seen, DockGeometry.Compute(kinds, Area, Base, Max, x, 1, Scale).PlateWidth);
        Assert.True(seen <= max + 1e-6, $"the plate reached {seen} DIP, more than MaxPlateWidth {max}");
        Assert.True(max - seen <= 1 / Scale, $"MaxPlateWidth {max} is more than a pixel above the widest seen {seen}");
        // Far less than the old allowance of 2 × growth × reach: the cosine falloff adds about 2.8 icons' growth.
        Assert.InRange(max - rest.PlateWidth, 2 * (Max - Base), 3 * (Max - Base));
    }

    [Fact]
    public void EverySizeIsAWholeNumberOfPixels()
    {
        var rest = DockGeometry.Compute(Icons(8), Area, Base, Max, null, 1, Scale);
        for (double x = rest.PlateLeft; x < rest.PlateLeft + rest.PlateWidth; x += 7.3)
        {
            var l = DockGeometry.Compute(Icons(8), Area, Base, Max, x, 0.73, Scale);
            foreach (var s in l.Slots)
            {
                Assert.Equal(Math.Round(s.Size * Scale), s.Size * Scale, 6);
                Assert.Equal(Math.Round(s.Left * Scale), s.Left * Scale, 6);
            }
        }
    }

    // The user's Dock (2026-10-23, "Dockをカーソルでなぞってるとびくびく動いてる"): 34 icons of 32 DIP among 3
    // separators, 10 DIP apart, magnified to 36 (and to 64), at 125%.
    static readonly SlotKind[] UsersRow = Icons(10).Append(SlotKind.Separator).Concat(Icons(12)).Append(SlotKind.Separator)
        .Concat(Icons(9)).Append(SlotKind.Separator).Concat(Icons(3)).ToArray();

    [Theory]
    [InlineData(32, 36)]
    [InlineData(32, 64)]
    public void SweepingTheRowNoIconMovesAgainstTheWayItIsGoing(double size, double magnified)
    {
        // The same layout unrounded (the gap given as the whole pixels it rounds to: 12.5 px are 12), and drawn: an icon
        // that keeps its size moves only the way its unrounded middle does. They shook a pixel back and forth: 3018 of
        // 6000 such moves went against it with 36, 15532 of 38539 with 64.
        double gap = DockGeometry.Snap(10, Scale);
        var rest = DockGeometry.Compute(UsersRow, Area, size, magnified, null, 0, Scale, spacing: 10);
        DockLayout? before = null, beforeExact = null;
        int moves = 0, against = 0;
        for (double x = rest.PlateLeft - 20; x <= rest.PlateLeft + rest.PlateWidth + 20; x += 0.2)
        {
            var drawn = DockGeometry.Compute(UsersRow, Area, size, magnified, x, 1, Scale, spacing: 10);
            var exact = DockGeometry.Compute(UsersRow, Area, size, magnified, x, 1, 0, spacing: gap);
            if (before != null && beforeExact != null)
                for (int i = 0; i < UsersRow.Length; i++)
                {
                    if (drawn.Slots[i].Size != before.Slots[i].Size) continue;
                    double moved = drawn.Slots[i].Left - before.Slots[i].Left, way = exact.Slots[i].Center - beforeExact.Slots[i].Center;
                    if (Math.Abs(moved) < 1e-9) continue;
                    moves++;
                    if (moved * way <= 0) against++;
                }
            for (int i = 0; i < UsersRow.Length; i++)
                Assert.InRange(drawn.Slots[i].Center - exact.Slots[i].Center, -0.5 / Scale - 1e-9, 0.5 / Scale + 1e-9);
            before = drawn;
            beforeExact = exact;
        }
        Assert.True(moves > 0);
        Assert.True(against == 0, $"{against} of {moves} moves of icons that kept their size went against the way they were going");
    }

    [Theory]
    [InlineData(40, 80, 8)] // the defaults
    [InlineData(32, 36, 10)] // the user's
    [InlineData(32, 64, 10)]
    public void AlongTheRowTheMagnifiedRowKeepsItsLengthAndItsEndsStay(double size, double magnified, double spacing)
    {
        // With the pointer between the fourth icon and the fourth from the end, the falloff lies wholly in the row: the
        // plate and the icons at its ends stay where they are, separators or not. The row swayed with the pointer (11 px
        // with the user's icons magnified to 64), and the rounding shook it on top of that.
        foreach (var kinds in new[] { Icons(14), UsersRow })
        {
            var rest = DockGeometry.Compute(kinds, Area, size, magnified, null, 0, Scale, spacing: spacing);
            var icons = Enumerable.Range(0, kinds.Length).Where(i => kinds[i] == SlotKind.Icon).ToArray();
            double from = rest.Slots[icons[3]].Center, to = rest.Slots[icons[^4]].Center;
            var first = DockGeometry.Compute(kinds, Area, size, magnified, from, 1, Scale, spacing: spacing);
            for (double x = from; x <= to; x += 0.2)
            {
                var l = DockGeometry.Compute(kinds, Area, size, magnified, x, 1, Scale, spacing: spacing);
                Assert.True(l.PlateLeft == first.PlateLeft && l.PlateWidth == first.PlateWidth,
                    $"{kinds.Length} slots: the plate moved to {l.PlateLeft}+{l.PlateWidth} (from {first.PlateLeft}+{first.PlateWidth}) at x={x:0.0}");
                Assert.Equal(first.Slots[0].Left, l.Slots[0].Left);
                Assert.Equal(first.Slots[^1].Left, l.Slots[^1].Left);
            }
        }
    }

    [Fact]
    public void APositionMovingAPixelAtATimeMovesItsRoundingAPixel()
    {
        // Halves up (a middle of an odd number of pixels stands on half a pixel): Math.Round's halves to even moved it 0
        // and 2 pixels a step.
        for (int px = 100; px < 110; px++)
            Assert.Equal(px + 1, DockGeometry.Place((px + 0.5) / Scale, Scale) * Scale, 6);
        // And floating-point noise does not flip a half.
        Assert.Equal(DockGeometry.Place(100.5 / Scale, Scale), DockGeometry.Place(100.5 / Scale - 1e-12, Scale));
    }

    [Fact]
    public void ARowThatFitsKeepsItsSizes()
    {
        Assert.Equal((48.0, 80.0), DockGeometry.Fit(Icons(10), 48, 80, Scale, Area - 80, 8));
        Assert.Equal((48.0, 48.0), DockGeometry.Fit(Icons(10), 48, 48, Scale, Area - 80, 8));
    }

    [Fact]
    public void ARowTooWideForTheScreenShrinksUntilItFits()
    {
        // 21 icons of 128 DIP and a separator on this PC's screen, less the room for a name at each end.
        var kinds = Icons(20).Append(SlotKind.Separator).Append(SlotKind.Icon).ToArray();
        double room = Area - 80;
        var (b, m) = DockGeometry.Fit(kinds, 128, 160, Scale, room, 8);
        Assert.True(b < 128, $"icons {b}");
        Assert.Equal(DockGeometry.Snap(b, Scale), b, 6); // whole pixels
        Assert.True(DockGeometry.Compute(kinds, 100_000, b, b, null, 0, Scale, spacing: 8).PlateWidth <= room);
        double bigger = b + 1 / Scale; // as large as fits
        Assert.True(DockGeometry.Compute(kinds, 100_000, bigger, bigger, null, 0, Scale, spacing: 8).PlateWidth > room);
        Assert.True(m >= b && DockGeometry.MaxPlateWidth(kinds, b, m, Scale, 8) <= room, $"magnified {m}");
    }

    [Fact]
    public void TheMagnificationGivesWayBeforeTheIcons()
    {
        // 20 icons of 48 DIP fit at rest (8 + 20 × 56 = 1128) but not magnified to 160.
        var kinds = Icons(20);
        var (b, m) = DockGeometry.Fit(kinds, 48, 160, Scale, 1200, 8);
        Assert.Equal(48, b);
        Assert.InRange(m, 48.5, 159.5);
        Assert.True(DockGeometry.MaxPlateWidth(kinds, b, m, Scale, 8) <= 1200);
        Assert.True(DockGeometry.MaxPlateWidth(kinds, b, m + 1 / Scale, Scale, 8) > 1200);
    }

    [Fact]
    public void WithoutMagnificationTheIconsOnlyShrink()
    {
        var (b, m) = DockGeometry.Fit(Icons(40), 64, 64, Scale, Area - 80, 8);
        Assert.True(b < 64);
        Assert.Equal(b, m);
    }

    [Fact]
    public void SeparatorsKeepTheirWidth()
    {
        SlotKind[] kinds = [SlotKind.Icon, SlotKind.Separator, SlotKind.Icon];
        var rest = DockGeometry.Compute(kinds, Area, Base, Max, null, 1, Scale);
        var l = DockGeometry.Compute(kinds, Area, Base, Max, rest.Slots[1].Center, 1, Scale);
        Assert.Equal(DockGeometry.Snap(DockGeometry.SeparatorWidth, Scale), l.Slots[1].Size, 3);
    }

    [Fact]
    public void HitTestFindsTheSlotUnderThePointer()
    {
        var l = DockGeometry.Compute(Icons(4), Area, Base, Max, null, 1, Scale);
        for (int i = 0; i < 4; i++) Assert.Equal(i, DockGeometry.HitTest(l, l.Slots[i].Center));
        Assert.Equal(0, DockGeometry.HitTest(l, l.PlateLeft + 1));
        Assert.Equal(-1, DockGeometry.HitTest(l, l.PlateLeft - 1));
        Assert.Equal(-1, DockGeometry.HitTest(l, l.PlateLeft + l.PlateWidth + 1));
    }
}

public class DockModelTests
{
    static RunningWindow W(long h, string app, bool minimized = false) => new(h, app, app + " name", @"C:\x\" + app + ".exe", minimized);
    static PinnedApp P(string app) => new("pin:" + app, app, app, @"C:\x\" + app + ".lnk");

    static IReadOnlyList<DockItem> Build(IReadOnlyList<PinnedApp> pins, IReadOnlyList<RunningWindow> windows, string? front, List<string> order) =>
        DockModel.Build(pins, windows, front, order, "Apps", "Trash");

    static PinnedApp R(string app) => new("recent:" + app, app, app, @"C:\x\" + app + ".exe");

    [Fact]
    public void RecentAppsStandAfterTheirOwnSeparatorWithTheRunningOnesThatAreNotKept()
    {
        // Kept: a. Running: c (not kept), a. Used lately: d, a, c, e, f, g (a is kept, c runs: neither is shown again).
        var items = DockModel.Build([P("a")], [W(1, "c"), W(2, "a")], "a", new List<string>(), "Apps", "Trash", [R("d"), R("a"), R("c"), R("e"), R("f"), R("g")]);
        Assert.Equal(["pin:a", DockModel.RecentSeparatorKey, "running:c", "recent:d", "recent:e", "recent:f", DockModel.SeparatorKey, DockModel.AppGridKey, DockModel.TrashKey],
            items.Select(i => i.Key).ToArray());
        Assert.All(items.Where(i => i.Key.StartsWith(DockModel.RecentPrefix)), i => Assert.False(i.Running || i.Pinned));
    }

    [Fact]
    public void FoldersStandBetweenTheAppGridAndTheTrash()
    {
        var items = DockModel.Build([P("a")], [], null, new List<string>(), "Apps", "Trash", null, [(@"C:\u\Downloads", "Downloads"), (@"C:\u\Docs", "Docs")]);
        Assert.Equal(["pin:a", DockModel.SeparatorKey, DockModel.AppGridKey, @"folder:C:\u\Downloads", @"folder:C:\u\Docs", DockModel.TrashKey],
            items.Select(i => i.Key).ToArray());
        Assert.All(items.Where(i => i.Kind == DockItemKind.Folder), i => Assert.Equal(i.Key[DockModel.FolderPrefix.Length..], i.LaunchTarget));
    }

    // ---- minimized windows (stage 7-2) ------------------------------------------------------------------------------

    [Fact]
    public void MinimizedWindowsStandAfterTheFoldersBeforeTheTrashAndTheirAppsStillRun()
    {
        var a1 = W(1, "a", minimized: true);
        var items = DockModel.Build([P("a")], [a1, W(2, "a")], "a", new List<string>(), "Apps", "Trash", null,
            [(@"C:\u\Downloads", "Downloads")], [(a1, "Report.docx - Word")]);
        Assert.Equal(["pin:a", DockModel.SeparatorKey, DockModel.AppGridKey, @"folder:C:\u\Downloads", "minimized:1", DockModel.TrashKey],
            items.Select(i => i.Key).ToArray());
        var m = items.Single(i => i.Kind == DockItemKind.MinimizedWindow);
        Assert.Equal("Report.docx - Word", m.Name);
        Assert.Equal("a", m.AppId);
        Assert.Equal([1L], m.Windows.Select(w => w.Hwnd).ToArray());
        Assert.False(m.Pinned || m.Movable);
        // The app keeps the window among its own: it still runs (its dot), and its click can bring the window back.
        Assert.Equal([1L, 2L], items[0].Windows.Select(w => w.Hwnd).ToArray());
    }

    [Fact]
    public void AMinimizedWindowWithoutATitleShowsItsAppsName()
    {
        var a1 = W(1, "a", minimized: true);
        var items = DockModel.Build([], [a1], null, new List<string>(), "Apps", "Trash", null, null, [(a1, " ")]);
        Assert.Equal("a name", items.Single(i => i.Kind == DockItemKind.MinimizedWindow).Name);
    }

    [Fact]
    public void MinimizedWindowsKeepTheOrderTheyWereFirstSeenMinimizedIn()
    {
        var order = new List<long>();
        // 2 minimized, then 1 too (windows come top to bottom: 1 is above 2 now), then 2 restored and minimized again.
        Assert.Equal([2L], DockModel.MinimizedInOrder([W(1, "a"), W(2, "b", true)], order).Select(w => w.Hwnd).ToArray());
        Assert.Equal([2L, 1L], DockModel.MinimizedInOrder([W(1, "a", true), W(2, "b", true)], order).Select(w => w.Hwnd).ToArray());
        Assert.Equal([1L], DockModel.MinimizedInOrder([W(2, "b"), W(1, "a", true)], order).Select(w => w.Hwnd).ToArray());
        Assert.Equal([1L, 2L], DockModel.MinimizedInOrder([W(2, "b", true), W(1, "a", true)], order).Select(w => w.Hwnd).ToArray());
        Assert.Equal([1L, 2L], order);
    }

    [Fact]
    public void OnlyThisDesktopsMinimizedWindowsThatMayShowAreShown()
    {
        var order = new List<long>();
        var elsewhere = new RunningWindow(3, "c", "c name", @"C:\x\c.exe", Minimized: true, Elsewhere: true);
        var shown = DockModel.MinimizedInOrder([W(1, "a", true), W(2, "b", true), elsewhere], order, w => w.AppId != "b");
        Assert.Equal([1L], shown.Select(w => w.Hwnd).ToArray());
        Assert.Equal([1L], order);
    }

    [Fact]
    public void AClickOnAMinimizedWindowBringsThatWindowBack()
    {
        var a1 = W(7, "a", minimized: true);
        var item = DockModel.Build([], [a1], null, new List<string>(), "Apps", "Trash", null, null, [(a1, "t")]).Single(i => i.Kind == DockItemKind.MinimizedWindow);
        Assert.Equal(new DockClick(DockAction.Restore, 7), DockModel.Click(item, minimizeWhenFront: true));
    }

    [Fact]
    public void NothingRecentAndNothingRunningLeavesNoSeparatorOfItsOwn()
    {
        var items = DockModel.Build([P("a")], [W(1, "a")], "a", new List<string>(), "Apps", "Trash", []);
        Assert.Equal(["pin:a", DockModel.SeparatorKey, DockModel.AppGridKey, DockModel.TrashKey], items.Select(i => i.Key).ToArray());
    }

    [Fact]
    public void AnAppThatQuitBecomesTheMostRecentOnce()
    {
        var recent = new List<Frostbar.Core.Settings.DockPin>();
        foreach (var id in new[] { "a", "b", "a", "c" }) DockModel.NoteRecent(recent, new Frostbar.Core.Settings.DockPin { Kind = "path", Id = id }, keep: 2);
        Assert.Equal(["c", "a"], recent.Select(p => p.Id).ToArray());
    }

    [Fact]
    public void PinsComeFirstThenRunningAppsThenTheFixedItems()
    {
        var items = Build([P("a"), P("b")], [W(1, "c"), W(2, "a")], "a", new List<string>());
        Assert.Equal(["a", "b", "c"], items.Where(i => i.Kind == DockItemKind.App).Select(i => i.AppId).ToArray());
        Assert.Equal([DockItemKind.Separator, DockItemKind.AppGrid, DockItemKind.Trash], items.Skip(3).Select(i => i.Kind).ToArray());
        Assert.True(items[0].Running && items[0].Active && items[0].Pinned);
        Assert.False(items[1].Running);
        Assert.False(items[2].Pinned);
    }

    [Fact]
    public void WindowsGroupByAppIdWithoutCase()
    {
        var items = Build([P("Brave")], [W(1, "brave"), W(2, "BRAVE"), W(3, "other")], null, new List<string>());
        Assert.Equal(2, items[0].Windows.Count);
        Assert.Equal("other", items[1].AppId);
    }

    [Fact]
    public void RunningAppsKeepTheOrderTheyFirstAppearedIn()
    {
        var order = new List<string>();
        Build([], [W(1, "x"), W(2, "y")], null, order);
        Assert.Equal(["x", "y"], order);
        // y comes to the front (z-order changes) and z starts: x stays first, z goes last
        var items = Build([], [W(2, "y"), W(3, "z"), W(1, "x")], null, order);
        Assert.Equal(["x", "y", "z"], items.Where(i => i.Kind == DockItemKind.App).Select(i => i.AppId).ToArray());
        // x quits
        items = Build([], [W(2, "y"), W(3, "z")], null, order);
        Assert.Equal(["y", "z"], items.Where(i => i.Kind == DockItemKind.App).Select(i => i.AppId).ToArray());
    }

    [Fact]
    public void AnAppThatGetsPinnedLeavesTheRunningPart()
    {
        var order = new List<string>();
        Build([], [W(1, "x")], null, order);
        var items = Build([P("x")], [W(1, "x")], null, order);
        Assert.Single(items, i => i.Kind == DockItemKind.App);
        Assert.Empty(order);
    }

    [Fact]
    public void DuplicatePinsDoNotShareWindows()
    {
        var items = Build([P("a"), new PinnedApp("pin:a2", "A", "a again", "x")], [W(1, "a")], null, new List<string>());
        Assert.True(items[0].Running);
        Assert.False(items[1].Running);
    }

    // ---- separators the user puts among the kept apps (2026-10-12) ----

    static DockPin App(string id) => new() { Kind = "path", Id = id };
    static DockPin Sep(string id) => new() { Kind = DockPin.SeparatorKind, Id = id };

    /// <summary>The row the Dock shows for these pins (all of them shown, nothing running).</summary>
    static IReadOnlyList<DockItem> Row(IEnumerable<DockPin> pins) =>
        Build(pins.Select(p => p.Kind == DockPin.SeparatorKind
            ? new PinnedApp(DockModel.PinPrefix + p.Id, "", "Separator", "", Separator: true)
            : new PinnedApp(DockModel.PinPrefix + p.Id, p.Id, p.Id, p.Id)).ToList(), [], null, new List<string>());

    static string Ids(IEnumerable<DockPin> pins) => string.Join(" ", pins.Select(p => p.Id));

    [Fact]
    public void ASeparatorKeptAmongThePinsStandsWhereItIsAndMoves()
    {
        var items = Build([P("a"), new PinnedApp("pin:s1", "", "Separator", "", Separator: true), P("b")], [W(1, "a")], "a", new List<string>());
        Assert.Equal([DockItemKind.App, DockItemKind.Separator, DockItemKind.App, DockItemKind.Separator, DockItemKind.AppGrid, DockItemKind.Trash],
            items.Select(i => i.Kind).ToArray());
        Assert.True(items[1].Pinned && items[1].Movable && !items[1].Running);
        Assert.False(items[3].Movable); // the Dock's own separator stays
        Assert.True(items[0].Running); // the separator claims no windows
    }

    [Fact]
    public void DraggingASeparatorMovesItsPinAmongTheApps()
    {
        var pins = new List<DockPin> { App("a"), Sep("s"), App("b"), App("c") };
        // Let go after b: two kept items left of it (itself left out).
        Assert.True(DockModel.MovePin(pins, Row(pins), 1, 2));
        Assert.Equal("a b s c", Ids(pins));
        // Let go where it is: nothing moves.
        Assert.False(DockModel.MovePin(pins, Row(pins), 2, 2));
        Assert.Equal("a b s c", Ids(pins));
        // To the front.
        Assert.True(DockModel.MovePin(pins, Row(pins), 2, 0));
        Assert.Equal("s a b c", Ids(pins));
    }

    [Fact]
    public void AnAppDraggedAcrossASeparatorLandsOnTheSideItWasLetGo()
    {
        var pins = new List<DockPin> { App("a"), Sep("s"), App("b"), App("c") };
        // c let go right of the separator (a and s left of it): between s and b. Counting the apps only put it left of s.
        Assert.True(DockModel.MovePin(pins, Row(pins), 3, 2));
        Assert.Equal("a s c b", Ids(pins));
        // and left of it (only a left of it)
        Assert.True(DockModel.MovePin(pins, Row(pins), 2, 1));
        Assert.Equal("a c s b", Ids(pins));
    }

    [Fact]
    public void APinThatIsNotShownKeepsItsPlaceInTheSettings()
    {
        // "gone" is kept in the settings but its file is missing, so the Dock does not show it.
        var pins = new List<DockPin> { App("a"), App("gone"), Sep("s"), App("b") };
        var row = Row(pins.Where(p => p.Id != "gone"));
        Assert.True(DockModel.MovePin(pins, row, 2, 0)); // b to the front
        Assert.Equal("b a gone s", Ids(pins));
        // A new pin let go after the separator (two kept items left of it).
        row = Row(pins.Where(p => p.Id != "gone"));
        Assert.Equal(3, DockModel.KeptBefore(row, -1, 3));
        Assert.Equal(4, DockModel.PinIndexFor(pins, row, DockModel.KeptBefore(row, -1, 3), null));
    }

    // ---- apps carried in from the app grid (2026-10-14) ----

    static DockPin Grid(string id) => new() { Kind = "appsFolder", Id = id };

    [Fact]
    public void AnAppCarriedFromTheGridIsKeptWhereItIsLetGo()
    {
        var pins = new List<DockPin> { App("a"), App("b") };
        // Between a and b (one item left of it).
        Assert.True(DockModel.KeepCarried(pins, Row(pins), Grid("x"), -1, 1));
        Assert.Equal("a x b", Ids(pins));
        Assert.Equal("appsFolder", pins[1].Kind);
        // At the front.
        Assert.True(DockModel.KeepCarried(pins, Row(pins), Grid("y"), -1, 0));
        Assert.Equal("y a x b", Ids(pins));
        // After the last kept app (the Dock's own separator, the grid and the trash right of it).
        Assert.True(DockModel.KeepCarried(pins, Row(pins), Grid("z"), -1, 4));
        Assert.Equal("y a x b z", Ids(pins));
    }

    [Fact]
    public void AnAppCarriedFromTheGridLandsOnTheSideOfASeparatorItWasLetGo()
    {
        var pins = new List<DockPin> { App("a"), Sep("s"), App("b") };
        Assert.True(DockModel.KeepCarried(pins, Row(pins), Grid("x"), -1, 2)); // a and s left of it
        Assert.Equal("a s x b", Ids(pins));
        Assert.True(DockModel.KeepCarried(pins, Row(pins), Grid("y"), -1, 1)); // a left of it
        Assert.Equal("a y s x b", Ids(pins));
    }

    [Fact]
    public void AnAppTheDockKeepsAlreadyMovesInsteadOfComingTwice()
    {
        var pins = new List<DockPin> { App("a"), App("b"), App("c") };
        // c carried in and let go after a: one item left of it, c itself left out of the row.
        Assert.True(DockModel.KeepCarried(pins, Row(pins), Grid("c"), 2, 1));
        Assert.Equal("a c b", Ids(pins));
        // b let go where it is (a and c left of it): nothing changes.
        Assert.False(DockModel.KeepCarried(pins, Row(pins), Grid("b"), 2, 2));
        Assert.Equal("a c b", Ids(pins));
        Assert.Equal(3, pins.Count);
    }

    [Fact]
    public void AnAppKeptButNotShownIsNotKeptASecondTime()
    {
        var pins = new List<DockPin> { App("a"), Grid("X") };
        var row = Row(pins.Where(p => p.Id != "X")); // its item not found
        Assert.False(DockModel.KeepCarried(pins, row, Grid("x"), -1, 1));
        Assert.Equal("a X", Ids(pins));
    }

    // ---- the apps' taskbar progress and badges on the Dock (2026-10-15) ----

    static WindowMarks M(ProgressState s, double done, bool overlay = false, string? text = null, long at = 0) =>
        new(s, (int)Math.Round(done * DockMarks.Full), overlay, text, at);

    [Fact]
    public void AnAppShowsItsMostTellingProgress()
    {
        Assert.Equal(new AppMarks(ProgressState.None, 0, -1, null), DockMarks.Combine([]));
        var running = DockMarks.Combine([M(ProgressState.Normal, 0.25)]);
        Assert.Equal(ProgressState.Normal, running.State);
        Assert.Equal(0.25, running.Progress, 3);
        // Two downloads: the least done; an error before either; no measure counts least.
        Assert.Equal(0.2, DockMarks.Combine([M(ProgressState.Normal, 0.8), M(ProgressState.Normal, 0.2)]).Progress, 3);
        Assert.Equal(ProgressState.Error, DockMarks.Combine([M(ProgressState.Normal, 0.5), M(ProgressState.Error, 0.9)]).State);
        Assert.Equal(ProgressState.Normal, DockMarks.Combine([M(ProgressState.Indeterminate, 0), M(ProgressState.Normal, 0.4)]).State);
        Assert.Equal(0, DockMarks.Combine([M(ProgressState.Indeterminate, 0.7)]).Progress);
        Assert.False(DockMarks.Combine([M(ProgressState.None, 0.7)]).Any);
    }

    [Fact]
    public void AnAppsBadgeIsTheOverlaySetLast()
    {
        var m = DockMarks.Combine([M(ProgressState.None, 0, overlay: true, text: "2 unread", at: 5), M(ProgressState.None, 0, overlay: true, text: "未読 12 件", at: 9), M(ProgressState.None, 0)]);
        Assert.Equal(1, m.BadgeWindow);
        Assert.Equal("12", m.Count);
        Assert.True(m.Any);
        Assert.Equal(-1, DockMarks.Combine([M(ProgressState.Normal, 0.5)]).BadgeWindow);
    }

    [Theory]
    [InlineData("3", "3")]
    [InlineData("3 unread messages", "3")]
    [InlineData("未読 12 件", "12")]
    [InlineData("1,234 new", "999+")]
    [InlineData("999", "999")]
    [InlineData("Unread", null)]
    [InlineData("0", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ABadgeShowsTheNumberItsDescriptionGives(string? description, string? count) => Assert.Equal(count, DockMarks.CountOf(description));

    [Fact]
    public void ANewSeparatorHasAnIdOfItsOwn()
    {
        var a = DockPin.NewSeparator();
        var b = DockPin.NewSeparator();
        Assert.Equal(DockPin.SeparatorKind, a.Kind);
        Assert.NotEqual(a.Id, b.Id);
    }

    [Fact]
    public void ClickLaunchesActivatesOrMinimizes()
    {
        var items = Build([P("a"), P("b"), P("c")], [W(1, "a"), W(2, "b", minimized: true), W(3, "b"), W(4, "c", true)], "a", new List<string>());
        Assert.Equal(DockAction.MinimizeAll, DockModel.Click(items[0], minimizeWhenFront: true).Action);
        Assert.Equal(DockAction.Activate, DockModel.Click(items[0], minimizeWhenFront: false).Action);
        Assert.Equal(new DockClick(DockAction.Activate, 3), DockModel.Click(items[1], true)); // topmost window that is not minimized
        Assert.Equal(new DockClick(DockAction.Activate, 4), DockModel.Click(items[2], true)); // all minimized: the topmost
        var notRunning = Build([P("d")], [], null, new List<string>());
        Assert.Equal(DockAction.Launch, DockModel.Click(notRunning[0], true).Action);
        Assert.Equal(DockAction.None, DockModel.Click(notRunning[^1], true).Action); // trash
    }

    static RunningWindow Away(long h, string app, bool minimized = false) => W(h, app, minimized) with { Elsewhere = true };

    /// <summary>
    /// An app whose windows are all on another virtual desktop runs: a click brings one forward (Windows goes to that
    /// desktop) instead of starting it again (before 2026-10-15 its cloaked windows were not counted).
    /// </summary>
    [Fact]
    public void An_app_on_another_desktop_is_brought_forward_not_started_again()
    {
        var items = Build([P("a")], [Away(7, "a")], null, new List<string>());
        Assert.True(items[0].Running);
        Assert.Equal(new DockClick(DockAction.Activate, 7), DockModel.Click(items[0], true));
    }

    [Fact]
    public void A_window_of_this_desktop_comes_before_one_elsewhere()
    {
        // Top to bottom: one elsewhere, one here minimized, one here shown.
        var items = Build([P("a")], [Away(1, "a"), W(2, "a", minimized: true), W(3, "a")], null, new List<string>());
        Assert.Equal(new DockClick(DockAction.Activate, 3), DockModel.Click(items[0], true));
        var minimizedHere = Build([P("a")], [Away(1, "a"), W(2, "a", minimized: true)], null, new List<string>());
        Assert.Equal(new DockClick(DockAction.Activate, 2), DockModel.Click(minimizedHere[0], true));
    }

    /// <summary>Stage 3-1 (macOS): a click brings every window of the app shown on this desktop forward, in their order.</summary>
    [Fact]
    public void A_click_brings_all_the_apps_shown_windows_here_forward_in_their_order()
    {
        // Top to bottom: shown 1, minimized 2, elsewhere 3, shown 4.
        var items = Build([P("a")], [W(1, "a"), W(2, "a", minimized: true), Away(3, "a"), W(4, "a")], null, new List<string>());
        var click = DockModel.Click(items[0], true);
        Assert.Equal(DockAction.ActivateAll, click.Action);
        Assert.Equal(1L, click.Hwnd);
        Assert.Equal([1L, 4L], click.Windows!);
        // In front with the setting off: the same (nothing is minimized).
        var front = Build([P("a")], [W(1, "a"), W(4, "a")], "a", new List<string>());
        Assert.Equal(DockAction.ActivateAll, DockModel.Click(front[0], minimizeWhenFront: false).Action);
    }

    [Fact]
    public void Minimize_all_leaves_the_windows_of_other_desktops()
    {
        var items = Build([P("a")], [W(1, "a"), Away(2, "a"), W(3, "a", minimized: true)], "a", new List<string>());
        Assert.Equal(DockAction.MinimizeAll, DockModel.Click(items[0], true).Action);
        Assert.Equal([1L], DockModel.ToMinimize(items[0]).Select(w => w.Hwnd));
    }

    /// <summary>
    /// In front with nothing shown here: a window moved to another desktop while in front stays the foreground window,
    /// hidden there (2026-10-15, vd-probe). A click brings it forward (to its desktop); "minimize" did nothing.
    /// </summary>
    [Fact]
    public void The_front_app_with_nothing_shown_here_is_brought_forward()
    {
        var away = Build([P("a")], [Away(4, "a")], "a", new List<string>());
        Assert.True(away[0].Active);
        Assert.Equal(new DockClick(DockAction.Activate, 4), DockModel.Click(away[0], true));
        // Only minimized here (and one away): the minimized one comes back.
        var minimized = Build([P("a")], [Away(4, "a"), W(5, "a", minimized: true)], "a", new List<string>());
        Assert.Equal(new DockClick(DockAction.Activate, 5), DockModel.Click(minimized[0], true));
    }
}
