using Frostbar.Core.MenuBar;

namespace Frostbar.Core.Tests;

public sealed class MenuBarFitTests
{
    // A bar like this PC's: the mark 40, the app's name and two menus, the status items 300, icons 28, the » button 28.
    static readonly double[] Titles = [80, 50, 50];
    const double Mark = 40, Status = 300, Icon = 28, Button = 28;

    static TrayFitIcon[] Icons(string keys, TrayPlace place = TrayPlace.Bar) =>
        [.. keys.Select(k => new TrayFitIcon(k.ToString(), place, Icon))];

    static MenuBarLayout Fit(double width, IReadOnlyList<TrayFitIcon> tray) =>
        MenuBarFit.Fit(width, Mark, Titles, Status, tray, Button);

    [Fact]
    public void Everything_fits_on_a_wide_bar()
    {
        var l = Fit(1000, Icons("abcde"));
        Assert.Equal(["a", "b", "c", "d", "e"], l.Bar);
        Assert.Empty(l.Overflow);
        Assert.False(l.OverflowButton);
        Assert.Equal(3, l.Titles);
    }

    [Fact]
    public void Icons_go_into_the_panel_before_any_title_from_the_titles_side()
    {
        // 650 - 40 - 8 - 300 - 180 = 122 for the tray: three icons and the » button.
        var l = Fit(650, Icons("abcde"));
        Assert.Equal(["c", "d", "e"], l.Bar);
        Assert.Equal(["a", "b"], l.Overflow);
        Assert.True(l.OverflowButton);
        Assert.Equal(3, l.Titles);
    }

    [Fact]
    public void An_icon_the_user_put_in_the_panel_stays_there_and_hidden_ones_show_nowhere()
    {
        TrayFitIcon[] tray = [new("a", TrayPlace.Bar, Icon), new("b", TrayPlace.Overflow, Icon), new("c", TrayPlace.Hidden, Icon), new("d", TrayPlace.Bar, Icon)];
        var l = Fit(1000, tray);
        Assert.Equal(["a", "d"], l.Bar);
        Assert.Equal(["b"], l.Overflow);
        Assert.True(l.OverflowButton);
    }

    [Fact]
    public void The_panel_keeps_the_bars_order()
    {
        // Room for one icon beside the » button: 56 for the tray.
        TrayFitIcon[] tray = [new("a", TrayPlace.Bar, Icon), new("b", TrayPlace.Overflow, Icon), new("c", TrayPlace.Bar, Icon), new("d", TrayPlace.Bar, Icon)];
        var l = Fit(584, tray);
        Assert.Equal(["d"], l.Bar);
        Assert.Equal(["a", "b", "c"], l.Overflow);
    }

    [Fact]
    public void Titles_go_only_when_the_right_group_alone_does_not_fit_the_last_menu_first()
    {
        // 460 - 40 - 8 - 300 - 28 (the » button, every icon in the panel) = 84: the app's name only.
        var l = Fit(460, Icons("abcde"));
        Assert.Empty(l.Bar);
        Assert.Equal(5, l.Overflow.Count);
        Assert.Equal(1, l.Titles);
        Assert.Equal(0, Fit(400, Icons("abcde")).Titles);
    }

    [Fact]
    public void Without_tray_icons_there_is_no_button_and_the_titles_keep_their_room()
    {
        var l = Fit(530, []);
        Assert.False(l.OverflowButton);
        Assert.Equal(3, l.Titles); // 40 + 8 + 300 + 180 = 528
    }

    [Fact]
    public void Keys_stay_the_same_across_restarts()
    {
        var guid = Guid.Parse("7820ae78-23e3-4229-82c1-e41cb67d5b9c");
        Assert.Equal("guid:7820ae78-23e3-4229-82c1-e41cb67d5b9c", TrayKeys.Of(guid, "LINE.exe", 1));
        Assert.Equal("exe:line.exe:1", TrayKeys.Of(Guid.Empty, "LINE.exe", 1));
        Assert.Equal(TrayKeys.Of(Guid.Empty, " line.EXE", 1), TrayKeys.Of(Guid.Empty, "LINE.exe", 1));
        Assert.NotEqual(TrayKeys.Of(Guid.Empty, "LINE.exe", 1), TrayKeys.Of(Guid.Empty, "LINE.exe", 2));
    }
}
