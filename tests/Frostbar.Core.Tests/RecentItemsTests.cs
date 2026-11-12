using Frostbar.Core.Localization;
using Frostbar.Core.Menus;
using Frostbar.Core.Settings;

namespace Frostbar.Core.Tests;

public class RecentItemsTests
{
    static readonly Strings S = Strings.For("ja", System.Globalization.CultureInfo.GetCultureInfo("ja-JP"));

    static DockPin App(string id, string? name = null, string kind = "path") => new() { Kind = kind, Id = id, Name = name };

    [Fact]
    public void TheAppInFrontComesFirstOnce()
    {
        var apps = new List<DockPin> { App(@"C:\a.exe", "A"), App(@"C:\b.exe", "B") };
        Assert.True(RecentItems.Note(apps, App(@"C:\B.EXE", "B")));
        Assert.Equal([@"C:\B.EXE", @"C:\a.exe"], apps.Select(a => a.Id));
        // First already: nothing changes (switching between two apps would rewrite the settings every time).
        Assert.False(RecentItems.Note(apps, App(@"c:\b.exe", "B")));
        Assert.Equal(2, apps.Count);
    }

    /// <summary>
    /// As many as the largest count the settings offer are kept (2026-10-22: 10 before the count could be chosen), so a
    /// count raised later shows the apps used before it.
    /// </summary>
    [Fact]
    public void FiftyAreKept()
    {
        var apps = new List<DockPin>();
        for (int i = 0; i < 54; i++) RecentItems.Note(apps, App($@"C:\{i}.exe"));
        Assert.Equal(RecentItems.Max, apps.Count);
        Assert.Equal(@"C:\53.exe", apps[0].Id);
        Assert.Equal(@"C:\4.exe", apps[^1].Id);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(10)]
    [InlineData(30)]
    public void The_submenu_lists_as_many_of_each_as_the_count(int count)
    {
        var apps = Enumerable.Range(0, 40).Select(i => App($@"C:\{i}.exe", $"A{i}")).ToList();
        var all = Enumerable.Range(0, 40).Select(i => Doc($"d{i}.txt", i)).ToList();
        var menu = RecentItems.Submenu(apps, RecentItems.Documents(all, null, count), S, count)!;
        Assert.Equal(count, menu.Children!.Count(i => i.Id.StartsWith("recent-app-")));
        Assert.Equal(count, menu.Children!.Count(i => i.Id.StartsWith("recent-document-")));
    }

    [Fact]
    public void A_count_of_none_leaves_the_submenu_out()
    {
        Assert.Null(RecentItems.Submenu([App(@"C:\a.exe", "A")], [Doc("a.txt", 0)], S, 0));
        Assert.DoesNotContain("recent", MenuBuilder.SystemMenu(S, RecentItems.Submenu([], [], S, 0)).Items.Select(i => i.Id));
    }

    public static TheoryData<int> CountsOffered => new(RecentItems.Counts);

    /// <summary>Every count the menu bar's page offers survives a start; another goes back to 10.</summary>
    [Theory]
    [MemberData(nameof(CountsOffered))]
    public void Every_count_the_page_offers_survives_a_start(int count)
    {
        var s = new AppSettings();
        s.MenuBar.RecentCount = count;
        Assert.Empty(SettingsValidator.Normalize(s));
        Assert.Equal(count, s.MenuBar.RecentCount);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(-1)]
    [InlineData(100)]
    public void A_count_the_page_could_not_have_set_goes_back_to_ten(int count)
    {
        var s = new AppSettings();
        s.MenuBar.RecentCount = count;
        Assert.Contains(SettingsValidator.Normalize(s), f => f.StartsWith("menuBar.recentCount"));
        Assert.Equal(RecentItems.Count, s.MenuBar.RecentCount);
    }

    [Fact]
    public void AFirstAppNamedBetterTakesTheName()
    {
        var apps = new List<DockPin> { App("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", null, "appsFolder") };
        Assert.True(RecentItems.Note(apps, App("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", "電卓", "appsFolder")));
        Assert.Equal("電卓", apps[0].Name);
        Assert.False(RecentItems.Note(apps, App("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", null, "appsFolder")));
        Assert.Equal("電卓", apps[0].Name);
    }

    [Fact]
    public void AppsOpenByTheirEntryOrTheirProgram()
    {
        Assert.Equal(@"shell:AppsFolder\Chrome", RecentItems.Target(App("Chrome", "Google Chrome", "appsFolder")));
        Assert.Equal(@"C:\Tools\x.exe", RecentItems.Target(App(@"C:\Tools\x.exe")));
    }

    static RecentDocument Doc(string name, int minutesAgo) =>
        new(name, $@"C:\Users\u\AppData\Roaming\Microsoft\Windows\Recent\{name}.lnk", $@"C:\Users\u\Documents\{name}", new DateTime(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc).AddMinutes(-minutesAgo));

    [Fact]
    public void DocumentsAreTheNewestTenSinceTheMenuWasCleared()
    {
        var all = Enumerable.Range(0, 15).Select(i => Doc($"d{i}.txt", i)).Reverse().ToList();
        var listed = RecentItems.Documents(all, null);
        Assert.Equal(RecentItems.Count, listed.Count);
        Assert.Equal("d0.txt", listed[0].Name);
        Assert.Equal("d9.txt", listed[^1].Name);
        // Cleared 2.5 minutes before noon: d0, d1, d2 were used after it.
        var cleared = new DateTime(2026, 10, 16, 11, 57, 30, DateTimeKind.Utc);
        Assert.Equal(["d0.txt", "d1.txt", "d2.txt"], RecentItems.Documents(all, cleared).Select(d => d.Name));
    }

    [Fact]
    public void TheSubmenuHasAHeadingForEachKindItHas()
    {
        var menu = RecentItems.Submenu([App("Chrome", "Google Chrome", "appsFolder"), App(@"C:\x.exe", "X")], [Doc("報告.docx", 1)], S)!;
        Assert.Equal("最近使った項目", menu.Label);
        var c = menu.Children!;
        Assert.Equal(["recent-apps", "recent-app-0", "recent-app-1", "-", "recent-documents", "recent-document-0", "-", "clear-recent"], c.Select(i => i.Id));
        Assert.False(c[0].Enabled); // a heading
        Assert.Equal(ActionKind.Open, c[1].Action.Kind);
        Assert.Equal(@"shell:AppsFolder\Chrome", c[1].Action.Name);
        Assert.Equal(@"shell:AppsFolder\Chrome", c[1].Icon);
        Assert.EndsWith(@"Recent\報告.docx.lnk", c[5].Action.Name); // the link opens it
        Assert.Equal(@"C:\Users\u\Documents\報告.docx", c[5].Icon);   // the file's own icon
        Assert.True(c[^1].Enabled);
        Assert.Equal(SystemOps.ClearRecent, c[^1].Action.Name);
    }

    [Fact]
    public void AnEmptySubmenuSaysSoAndCannotBeCleared()
    {
        var c = RecentItems.Submenu([], [], S)!.Children!;
        Assert.Equal(["recent-none", "-", "clear-recent"], c.Select(i => i.Id));
        Assert.All(c, i => Assert.False(i.Enabled));
        // Documents only: no heading for the apps, no separator first.
        Assert.Equal(["recent-documents", "recent-document-0", "-", "clear-recent"], RecentItems.Submenu([], [Doc("a.txt", 0)], S)!.Children!.Select(i => i.Id));
    }

    [Fact]
    public void TheSystemMenuHasRecentItemsAsASectionOfItsOwn()
    {
        var recent = RecentItems.Submenu([], [], S);
        var ids = MenuBuilder.SystemMenu(S, recent).Items.Select(i => i.Id).ToList();
        int at = ids.IndexOf("recent");
        Assert.Equal(["quit-shell", "-", "recent", "-", "force-quit"], ids.Skip(at - 2).Take(5));
        Assert.DoesNotContain("recent", MenuBuilder.SystemMenu(S).Items.Select(i => i.Id));
    }

    [Fact]
    public void TheSettingsKeepFiftyGoodRecentApps()
    {
        var s = new AppSettings();
        s.MenuBar.RecentApps = [null!, App(""), DockPin.NewSeparator(), .. Enumerable.Range(0, 52).Select(i => App($@"C:\{i}.exe"))];
        var fixes = SettingsValidator.Normalize(s);
        Assert.Equal(RecentItems.Max, s.MenuBar.RecentApps.Count);
        Assert.Equal(@"C:\0.exe", s.MenuBar.RecentApps[0].Id);
        Assert.Contains(fixes, f => f.StartsWith("menuBar.recentApps: dropped 3"));
        s.MenuBar.RecentApps = null!;
        SettingsValidator.Normalize(s);
        Assert.NotNull(s.MenuBar.RecentApps);
    }
}
