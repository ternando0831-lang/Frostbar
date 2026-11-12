using System.Globalization;
using Frostbar.Core.Localization;
using Frostbar.Core.Menus;

namespace Frostbar.Core.Tests;

public class ShortcutTests
{
    [Theory]
    [InlineData("Ctrl+Shift+S", Mods.Ctrl | Mods.Shift, (ushort)'S')]
    [InlineData("ctrl+t", Mods.Ctrl, (ushort)'T')]
    [InlineData("Ctrl+=", Mods.Ctrl, Shortcut.VK_OEM_PLUS)]
    [InlineData("Ctrl+-", Mods.Ctrl, Shortcut.VK_OEM_MINUS)]
    [InlineData("Ctrl+0", Mods.Ctrl, (ushort)'0')]
    [InlineData("Alt+Left", Mods.Alt, Shortcut.VK_LEFT)]
    [InlineData("F11", Mods.None, (ushort)0x7A)]
    [InlineData("Win+.", Mods.Win, Shortcut.VK_OEM_PERIOD)]
    [InlineData("Ctrl+Shift+Tab", Mods.Ctrl | Mods.Shift, Shortcut.VK_TAB)]
    [InlineData("Alt+Enter", Mods.Alt, Shortcut.VK_RETURN)]
    [InlineData("Ctrl+`", Mods.Ctrl, Shortcut.VK_OEM_3)]
    [InlineData("Ctrl+,", Mods.Ctrl, Shortcut.VK_OEM_COMMA)]
    public void Parses(string text, Mods mods, ushort vk)
    {
        var s = Shortcut.Parse(text);
        Assert.NotNull(s);
        Assert.Equal(mods, s.Value.Mods);
        Assert.Equal(vk, s.Value.Vk);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+X")]
    [InlineData("Ctrl+F25")]
    [InlineData("Ctrl+Shift+Ö")]
    public void Rejects(string text) => Assert.Null(Shortcut.Parse(text));

    [Theory]
    [InlineData("Ctrl+Shift+S")]
    [InlineData("Alt+F4")]
    [InlineData("Win+.")]
    [InlineData("Ctrl+=")]
    public void Format_round_trips(string text) => Assert.Equal(text, Shortcut.Parse(text)!.Value.Format());

    [Fact]
    public void Modifier_order_is_fixed() => Assert.Equal("Ctrl+Alt+Shift+S", Shortcut.Parse("Shift+Alt+Ctrl+S")!.Value.Format());

    [Theory]
    [InlineData("Ctrl+Alt+Up")]
    [InlineData("Win+Ctrl+Alt+Shift+F12")]
    [InlineData("Ctrl+Shift+NumPad5")]
    [InlineData("Alt+PageDown")]
    [InlineData("Ctrl+Plus")]
    [InlineData("Ctrl+Alt+/")]
    [InlineData("Shift+Esc")]
    public void Stored_text_reads_back(string text)
    {
        var s = Shortcut.Parse(text)!.Value;
        Assert.Equal(text, s.Store());
        Assert.Equal(s, Shortcut.Parse(s.Store()));
    }

    [Fact]
    public void A_key_without_a_name_is_not_stored() => Assert.Null(new Shortcut(Mods.Ctrl, 0xE2 /*VK_OEM_102*/).Store());

    [Fact]
    public void Layout_key_names_are_used_when_given() =>
        // On a JIS keyboard VK_OEM_PLUS is the ";" key.
        Assert.Equal("Ctrl+;", Shortcut.Parse("Ctrl+=")!.Value.Format(vk => vk == Shortcut.VK_OEM_PLUS ? ";" : null));
}

public class MenuBuilderTests
{
    static readonly Strings Ja = Strings.For("ja", CultureInfo.InvariantCulture), En = Strings.For("en", CultureInfo.InvariantCulture);

    static FrontApp App(string exe, bool elevated = false, params AppWindowInfo[] windows) => new("Test", exe, elevated, windows);

    [Theory]
    [InlineData("brave.exe", "chromium")]
    [InlineData("BRAVE.EXE", "chromium")]
    [InlineData("msedge.exe", "chromium")]
    [InlineData("explorer.exe", "explorer")]
    [InlineData("Notepad.exe", "notepad")]
    [InlineData("WindowsTerminal.exe", "terminal")]
    [InlineData("Code.exe", "vscode")]
    [InlineData("Bipane.exe", "bipane")]
    [InlineData("Obsidian.exe", "obsidian")]
    [InlineData("Discord.exe", "discord")]
    [InlineData("Antigravity.exe", "vscode")]
    [InlineData("LINE.exe", "generic")]
    public void Profile_by_executable(string exe, string profile) => Assert.Equal(profile, Profiles.Find(exe).Id);

    [Fact]
    public void A_profile_can_name_its_own_help_keys()
    {
        static Shortcut? Help(string exe) => MenuBuilder.ForApp(App(exe), En).Single(m => m.Id == "help").Items[0].Shortcut;
        Assert.Equal(Shortcut.Parse("Ctrl+Shift+H"), Help("Discord.exe"));
        Assert.Equal(Shortcut.Parse("F1"), Help("notepad.exe"));
    }

    [Fact]
    public void No_menu_offers_the_same_keys_twice()
    {
        foreach (var p in Profiles.All.Append(Profiles.Generic))
        {
            var keys = p.Menus(En).SelectMany(m => m.Items).Where(i => i.Action.Kind == ActionKind.Keys).Select(i => i.Shortcut).ToList();
            // Discord's Esc does two things by design: it marks the channel read, and declines a call.
            var twice = keys.GroupBy(k => k).Where(g => g.Count() > 1 && !(p.Id == "discord" && g.Key == Shortcut.Parse("Esc"))).Select(g => g.Key).ToList();
            Assert.True(twice.Count == 0, $"{p.Id}: {string.Join(", ", twice)}");
        }
    }

    [Fact]
    public void Desktop_has_a_profile_of_its_own()
    {
        // File Explorer's items for a folder window did nothing on the desktop: tabs, the address bar, search, Back.
        Assert.Equal("desktop", Profiles.Find("explorer.exe", isDesktop: true).Id);
        Assert.Equal("explorer", Profiles.Find("explorer.exe").Id);
        var menus = MenuBuilder.ForApp(new FrontApp("Desktop", "explorer.exe", false, [], IsDesktop: true), Ja);
        var ids = menus.SelectMany(m => m.Items).Select(i => i.Id).ToHashSet();
        Assert.DoesNotContain(menus, m => m.Id == "go");
        foreach (var gone in new[] { "new-tab", "close-tab", "address", "search", "back" }) Assert.DoesNotContain(gone, ids);
        foreach (var kept in new[] { "new-window", "new-folder", "rename", "delete", "refresh", "paste" }) Assert.Contains(kept, ids);
    }

    [Fact]
    public void Order_is_app_menu_then_profile_then_window_and_help()
    {
        var ids = MenuBuilder.ForApp(App("brave.exe"), Ja).Select(m => m.Id).ToList();
        Assert.Equal(["app", "file", "edit", "view", "history", "bookmarks", "tab", "window", "help"], ids);
        Assert.True(MenuBuilder.ForApp(App("brave.exe"), Ja)[0].Bold);
    }

    [Fact]
    public void Every_profile_builds_in_both_languages_with_valid_shortcuts()
    {
        foreach (var p in Profiles.All.Append(Profiles.Generic))
            foreach (var s in new[] { Ja, En })
                foreach (var m in p.Menus(s))
                {
                    Assert.NotEmpty(m.Items);
                    Assert.All(m.Items.Where(i => i.Action.Kind == ActionKind.Keys), i => Assert.NotNull(i.Action.Keys));
                    Assert.False(m.Items[0].IsSeparator);
                    Assert.False(m.Items[^1].IsSeparator);
                }
    }

    [Fact]
    public void Elevated_apps_get_key_items_disabled_with_a_reason()
    {
        var menus = MenuBuilder.ForApp(App("brave.exe", elevated: true, new AppWindowInfo(1, "w", true, false)), Ja);
        var keyItems = menus.SelectMany(m => m.Items).Where(i => i.Action.Kind == ActionKind.Keys).ToList();
        Assert.NotEmpty(keyItems);
        Assert.All(keyItems, i => { Assert.False(i.Enabled); Assert.Equal(Ja["app.elevated"], i.Hint); });
    }

    [Fact]
    public void Elevated_apps_get_the_moves_and_sizes_of_their_windows_disabled()
    {
        // Measured with the elevated test target (2026-10-12, 10-16): SetWindowPos, ShowWindowAsync and SC_MAXIMIZE refused.
        var menus = MenuBuilder.ForApp(App("brave.exe", elevated: true, new AppWindowInfo(1, "w", true, false)) with { FrameKept = true }, Ja);
        var all = Flatten(menus.Single(m => m.Id == "window").Items).ToList();
        string[] ops = ["zoom", "fill", "center", "move-resize", "tile-left", "tile-right", "tile-top", "tile-bottom", "tile-top-left",
            "tile-top-right", "tile-bottom-left", "tile-bottom-right", "return-frame", "front-all"];
        foreach (var id in ops)
        {
            var item = all.Single(i => i.Id == id); // each there (one missing was skipped before: the test passed without looking)
            Assert.False(item.Enabled, id);
            Assert.Equal(Ja["app.elevatedWindows"], item.Hint);
        }
    }

    /// <summary>
    /// Its windows take their window menu's Minimize and Close (WM_SYSCOMMAND, elevated-ops 2026-10-16): Minimize, Hide
    /// and Quit stay, as the user chose (2026-10-22), and so do the list of its windows and its About.
    /// </summary>
    [Fact]
    public void Elevated_apps_can_still_be_minimized_hidden_and_quit()
    {
        var menus = MenuBuilder.ForApp(App("brave.exe", elevated: true, new AppWindowInfo(1, "w", true, false)), Ja);
        var window = menus.Single(m => m.Id == "window").Items;
        var minimize = window.Single(i => i.Id == "minimize");
        Assert.True(minimize.Enabled);
        Assert.Null(minimize.Hint);
        Assert.True(window.Single(i => i.Id == "window-1").Enabled); // its windows can still be chosen
        var app = menus[0].Items;
        foreach (var op in new[] { AppOps.Hide, AppOps.Quit, AppOps.About, AppOps.HideOthers, AppOps.ShowAll })
        {
            var item = app.Single(i => i.Action.Name == op);
            Assert.True(item.Enabled, op);
            Assert.Null(item.Hint);
        }
        Assert.True(WindowOps.ReachesElevated(WindowOps.Minimize));
        Assert.True(WindowOps.ReachesElevated(WindowOps.ActivatePrefix + 1));
        Assert.False(WindowOps.ReachesElevated(WindowOps.Zoom));
        Assert.False(WindowOps.ReachesElevated(WindowOps.FrontAll));
        Assert.False(WindowOps.ReachesElevated(WindowOps.TileLeft));
    }

    static IEnumerable<MenuItem> Flatten(IEnumerable<MenuItem> items) => items.SelectMany(i => new[] { i }.Concat(Flatten(i.Children ?? [])));

    /// <summary>macOS's Window menu (15): Fill and Center at the top, the halves and quarters under Move &amp; Resize.</summary>
    [Fact]
    public void Window_menu_has_move_and_resize_with_the_halves_and_quarters()
    {
        var menu = MenuBuilder.ForApp(App("notepad.exe", false, new AppWindowInfo(10, "a.txt", true, false)), Ja).Single(m => m.Id == "window");
        Assert.Equal(["minimize", "zoom", "fill", "center", "-", "move-resize", "-", "front-all", "-", "window-10"], menu.Items.Select(i => i.Id));
        var move = menu.Items.Single(i => i.Id == "move-resize");
        Assert.True(move.Enabled);
        Assert.Equal(ActionKind.None, move.Action.Kind); // it opens its items, it does nothing itself
        Assert.Equal(["tile-left", "tile-right", "tile-top", "tile-bottom", "-", "tile-top-left", "tile-top-right", "tile-bottom-left", "tile-bottom-right", "-", "return-frame"],
            move.Children!.Select(i => i.Id));
        // Each item's id is its operation, and each placement has its place.
        Assert.All(Flatten(menu.Items).Where(i => i.Action.Kind == ActionKind.Window && !i.Id.StartsWith("window-")), i => Assert.Equal(i.Id, i.Action.Name));
        Assert.All(move.Children!.Where(i => !i.IsSeparator && i.Id != "return-frame"), i => Assert.NotNull(WindowOps.Place(i.Action.Name)));
        Assert.Equal(Wm.TilePlace.Fill, WindowOps.Place(WindowOps.Fill));
        Assert.Equal(Wm.TilePlace.Center, WindowOps.Place(WindowOps.Center));
        Assert.Null(WindowOps.Place(WindowOps.Zoom));
    }

    [Fact]
    public void Return_to_previous_size_only_while_a_frame_is_kept()
    {
        var w = new AppWindowInfo(10, "a.txt", true, false);
        MenuItem Return(bool kept) => Flatten(MenuBuilder.ForApp(App("notepad.exe", false, w) with { FrameKept = kept }, Ja).Single(m => m.Id == "window").Items)
            .Single(i => i.Id == "return-frame");
        Assert.False(Return(false).Enabled);
        Assert.True(Return(true).Enabled);
    }

    [Fact]
    public void Window_menu_lists_windows_and_checks_the_active_one()
    {
        var menu = MenuBuilder.ForApp(App("notepad.exe", false, new AppWindowInfo(10, "a.txt", false, false), new AppWindowInfo(11, "", true, false)), Ja)
            .Single(m => m.Id == "window");
        var w10 = menu.Items.Single(i => i.Id == "window-10");
        var w11 = menu.Items.Single(i => i.Id == "window-11");
        Assert.False(w10.Checked);
        Assert.True(w11.Checked);
        Assert.Equal(Ja["win.untitled"], w11.Label);
        Assert.Equal("activate:11", w11.Action.Name);
    }

    [Fact]
    public void Without_windows_the_window_operations_are_disabled()
    {
        var menu = MenuBuilder.ForApp(App("notepad.exe"), Ja).Single(m => m.Id == "window");
        Assert.All(menu.Items, i => Assert.False(i.Enabled));
        Assert.False(menu.Items[^1].IsSeparator);
    }

    [Fact]
    public void App_menu_uses_the_app_name()
    {
        var app = MenuBuilder.ForApp(App("LINE.exe") with { DisplayName = "LINE" }, Ja)[0];
        Assert.Equal("LINE", app.Title);
        Assert.Equal("LINEについて", app.Items[0].Label);
        Assert.Equal("LINEを終了", app.Items.Single(i => i.Id == "quit").Label);
        Assert.Equal("Quit LINE", MenuBuilder.ForApp(App("LINE.exe") with { DisplayName = "LINE" }, En)[0].Items.Single(i => i.Id == "quit").Label);
    }

    [Fact]
    public void System_menu_has_the_power_items()
    {
        var ids = MenuBuilder.SystemMenu(Ja).Items.Select(i => i.Id).ToList();
        Assert.Contains("sleep", ids);
        Assert.Contains("restart", ids);
        Assert.Contains("shutdown", ids);
        Assert.Contains("lock", ids);
        Assert.Contains("logout", ids);
    }

    [Fact]
    public void System_menu_quits_Frostbar_itself_under_its_settings()
    {
        var items = MenuBuilder.SystemMenu(Ja).Items;
        var ids = items.Select(i => i.Id).ToList();
        Assert.Equal(ids.IndexOf("shell-settings") + 1, ids.IndexOf("quit-shell"));
        var quit = items.Single(i => i.Id == "quit-shell");
        Assert.Equal("Frostbar を終了", quit.Label);
        Assert.Equal(SystemOps.QuitShell, quit.Action.Name);
        Assert.Equal("Quit Frostbar", MenuBuilder.SystemMenu(En).Items.Single(i => i.Id == "quit-shell").Label);
    }

    [Fact]
    public void Tidy_drops_leading_trailing_and_double_separators()
    {
        var a = new MenuItem("a", "A", null, MenuAction.Nothing);
        var t = MenuBuilder.Tidy([MenuItem.Separator, a, MenuItem.Separator, MenuItem.Separator, a, MenuItem.Separator]);
        Assert.Equal(["a", "-", "a"], t.Select(i => i.Id));
    }

    /// <summary>
    /// Apps without a profile get only keys that mean the same everywhere (2026-10-12: the generic View and File menus
    /// sent Ctrl+- / Ctrl+0 / F11 / Ctrl+W / Ctrl+Shift+S, which in Excel delete cells, hide columns and insert a chart
    /// sheet, in Word apply styles, in Paint resize).
    /// </summary>
    [Fact]
    public void Apps_without_a_profile_get_no_keys_that_mean_other_things_elsewhere()
    {
        var sent = MenuBuilder.ForApp(App("EXCEL.EXE", false, new AppWindowInfo(1, "Book1", true, false)), En)
            .SelectMany(m => m.Items).Where(i => i.Action.Kind == ActionKind.Keys).Select(i => i.Shortcut).ToList();
        foreach (var risky in new[] { "Ctrl+=", "Ctrl+-", "Ctrl+0", "F11", "Ctrl+W", "Ctrl+Shift+S", "Ctrl+," })
            Assert.DoesNotContain(Shortcut.Parse(risky), sent);
        Assert.Contains(Shortcut.Parse("Ctrl+S"), sent);
        Assert.Contains(Shortcut.Parse("Ctrl+C"), sent);
    }

    [Fact]
    public void VS_Code_resets_zoom_with_the_number_pad_and_has_no_F1_help()
    {
        var menus = MenuBuilder.ForApp(App("Code.exe"), En);
        // Ctrl+0 is Focus Side Bar there; F1 is the Command Palette (already in View).
        Assert.Equal(Shortcut.Parse("Ctrl+NumPad0"), menus.Single(m => m.Id == "view").Items.Single(i => i.Id == "zoom-reset").Shortcut);
        Assert.DoesNotContain(menus, m => m.Id == "help");
    }

    [Fact]
    public void Windows_Terminal_closes_a_pane_and_has_no_F1_help()
    {
        var menus = MenuBuilder.ForApp(App("WindowsTerminal.exe"), Ja);
        var close = menus.Single(m => m.Id == "shell").Items.Single(i => i.Shortcut == Shortcut.Parse("Ctrl+Shift+W"));
        Assert.Equal(Ja["file.closePane"], close.Label); // Ctrl+Shift+W is its closePane
        Assert.DoesNotContain(menus, m => m.Id == "help"); // F1 would go to the shell
    }

    [Theory]
    [InlineData("brave.exe", null)]
    [InlineData("EXCEL.EXE", null)]
    [InlineData("explorer.exe", null)]
    [InlineData("Code.exe", "Ctrl+,")]
    [InlineData("WindowsTerminal.exe", "Ctrl+,")]
    [InlineData("Obsidian.exe", "Ctrl+,")]
    [InlineData("Discord.exe", "Ctrl+,")]
    [InlineData("Bipane.exe", "Ctrl+,")]
    public void Settings_only_where_the_app_opens_them_with_keys(string exe, string? keys)
    {
        var item = MenuBuilder.ForApp(App(exe), En)[0].Items.SingleOrDefault(i => i.Id == "settings");
        Assert.Equal(Shortcut.Parse(keys), item?.Shortcut);
    }

    [Theory]
    [InlineData("brave.exe", "新規プライベートウインドウ")]
    [InlineData("chrome.exe", "新規シークレットウインドウ")]
    [InlineData("msedge.exe", "新規 InPrivate ウインドウ")]
    public void Each_browser_names_its_private_window(string exe, string label) =>
        Assert.Equal(label, MenuBuilder.ForApp(App(exe), Ja).Single(m => m.Id == "file").Items.Single(i => i.Id == "new-private").Label);

    [Fact]
    public void Quit_shows_no_keys() // it closes every window of the app; Alt+F4 closes one
        => Assert.Null(MenuBuilder.ForApp(App("notepad.exe"), En)[0].Items.Single(i => i.Id == "quit").Shortcut);
}

public class StringsTests
{
    [Fact]
    public void Japanese_and_English_have_the_same_keys()
    {
        Assert.Empty(Strings.JaKeys.Except(Strings.EnKeys));
        Assert.Empty(Strings.EnKeys.Except(Strings.JaKeys));
    }

    [Theory]
    [InlineData("system", "ja-JP", "ja")]
    [InlineData("system", "en-US", "en")]
    [InlineData("en", "ja-JP", "en")]
    [InlineData("ja", "en-US", "ja")]
    public void Language_choice(string setting, string ui, string expected) =>
        Assert.Equal(expected, Strings.For(setting, CultureInfo.GetCultureInfo(ui)).Language);
}
