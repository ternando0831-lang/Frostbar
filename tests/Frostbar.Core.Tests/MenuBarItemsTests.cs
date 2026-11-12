using Frostbar.Core.MenuBar;
using Frostbar.Core.Settings;

namespace Frostbar.Core.Tests;

public class MenuBarItemsTests
{
    [Fact]
    public void NoListIsTheFirstOrder()
    {
        Assert.Equal(["nowPlaying", "ime", "wifi", "volume", "battery", "focus", "search"], MenuBarItems.Clean(null));
    }

    [Fact]
    public void UnknownAndRepeatedItemsAreDropped()
    {
        Assert.Equal(["search", "wifi"], MenuBarItems.Clean(["search", "clock", "wifi", "search", "control-center", null!]));
        Assert.Empty(MenuBarItems.Clean([])); // every one hidden is a choice
    }

    [Fact]
    public void HiddenItemsComeBackWhereTheyFirstStood()
    {
        Assert.Equal(["wifi", "volume", "battery", "search"], MenuBarItems.Set(["wifi", "volume", "search"], "battery", true));
        Assert.Equal(["ime", "search"], MenuBarItems.Set(["search"], "ime", true));
        Assert.Equal(["focus", "search"], MenuBarItems.Set(["search"], "focus", true));
    }

    [Fact]
    public void AnItemComesBackAfterTheLastShownThatStoodBeforeIt()
    {
        // Moved by the user: search first, then wifi. Battery (first after volume) comes after wifi, the last before it.
        Assert.Equal(["search", "wifi", "battery"], MenuBarItems.Set(["search", "wifi"], "battery", true));
    }

    [Fact]
    public void HidingTakesItOut()
    {
        Assert.Equal(["nowPlaying", "ime", "volume", "battery", "focus", "search"], MenuBarItems.Set(MenuBarItems.Movable, "wifi", false));
        Assert.Equal(["ime"], MenuBarItems.Set(["ime"], "clock", true)); // not one of them: as it was
    }

    [Fact]
    public void ADragMovesOneItem()
    {
        Assert.Equal(["wifi", "ime", "volume"], MenuBarItems.Move(["ime", "wifi", "volume"], "ime", 1));
        Assert.Equal(["wifi", "volume", "ime"], MenuBarItems.Move(["ime", "wifi", "volume"], "ime", 9));
        Assert.Equal(["volume", "ime", "wifi"], MenuBarItems.Move(["ime", "wifi", "volume"], "volume", -3));
        Assert.Equal(["ime", "wifi"], MenuBarItems.Move(["ime", "wifi"], "battery", 0)); // not shown: as it was
    }

    [Fact]
    public void TheSettingsKeepTheListAndTheAutoHide()
    {
        var s = new AppSettings();
        s.MenuBar.Items = ["search", "bogus", "wifi", "wifi"];
        s.MenuBar.AutoHide = "sometimes";
        SettingsValidator.Normalize(s);
        Assert.Equal(["search", "wifi"], s.MenuBar.Items);
        Assert.Equal("fullScreen", s.MenuBar.AutoHide);
        s.MenuBar.Items = null!;
        s.MenuBar.AutoHide = "always";
        SettingsValidator.Normalize(s);
        Assert.Equal(MenuBarItems.Movable, s.MenuBar.Items);
        Assert.Equal("always", s.MenuBar.AutoHide);
    }

    /// <summary>
    /// Settings of stage 5-1 (schema 1) list the six items they knew. Now Playing (stage 5-2) shows in them, first, as it
    /// does by default; what the user hid or moved stays so. A list of shown items alone cannot tell a new item from a
    /// hidden one, so without the schema's number it would stay hidden for every one who had saved settings.
    /// </summary>
    [Fact]
    public void SettingsFromBeforeNowPlayingShowIt()
    {
        var s = new AppSettings { SchemaVersion = 1 };
        s.MenuBar.Items = ["search", "wifi", "volume"]; // moved and hidden in stage 5-1
        var fixes = SettingsValidator.Normalize(s);
        Assert.Equal(["nowPlaying", "search", "wifi", "volume"], s.MenuBar.Items);
        Assert.Equal(AppSettings.CurrentSchema, s.SchemaVersion);
        Assert.Contains(fixes, f => f.StartsWith("schemaVersion 1 -> 2"));
        // Once read (and saved), hiding it is the user's: it stays hidden.
        s.MenuBar.Items = MenuBarItems.Set(s.MenuBar.Items, "nowPlaying", false);
        Assert.Empty(SettingsValidator.Normalize(s));
        Assert.Equal(["search", "wifi", "volume"], s.MenuBar.Items);
    }

    /// <summary>macOS's "Automatically hide and show the menu bar": its four choices in its order (2026-11-01; two until then).</summary>
    [Fact]
    public void TheBarHidesAsMacOsChoosesInItsOrder()
    {
        Assert.Equal(["always", "desktop", "fullScreen", "never"], MenuBarItems.AutoHides);
    }

    /// <summary>
    /// Out of sight (waiting at the top edge) or standing, a full-screen app in front or not; the strip at the top kept
    /// unless it hides on the desktop. "desktop" and "never" stand over a full-screen app (as macOS's).
    /// </summary>
    [Theory]
    [InlineData("always", false, true, false)]
    [InlineData("always", true, true, false)]
    [InlineData("desktop", false, true, false)]
    [InlineData("desktop", true, false, false)]
    [InlineData("fullScreen", false, false, true)]
    [InlineData("fullScreen", true, true, true)]
    [InlineData("never", false, false, true)]
    [InlineData("never", true, false, true)]
    public void TheBarHidesOrStands(string mode, bool fullScreen, bool hides, bool strip)
    {
        Assert.Equal(hides, MenuBarItems.Hides(mode, fullScreen));
        Assert.Equal(strip, MenuBarItems.TakesStrip(mode));
    }

    public static TheoryData<string> AutoHides => new(MenuBarItems.AutoHides);

    /// <summary>Every hiding the settings page offers survives a start (the validator reads the same list).</summary>
    [Theory]
    [MemberData(nameof(AutoHides))]
    public void EveryHidingIsKept(string mode)
    {
        string dir = Path.Combine(Path.GetTempPath(), "frostbar-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "settings.json");
            var s = new AppSettings();
            s.MenuBar.AutoHide = mode;
            SettingsStore.Save(file, s);
            var read = SettingsStore.Load(file);
            Assert.Equal(mode, read.Settings.MenuBar.AutoHide);
            Assert.Empty(read.Fixes);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ASchemaOneFileGainsNowPlayingOnce()
    {
        string dir = Path.Combine(Path.GetTempPath(), "frostbar-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "settings.json");
            File.WriteAllText(file, """{ "schemaVersion": 1, "menuBar": { "items": [ "ime", "wifi", "volume", "battery", "focus", "search" ] } }""");
            var read = SettingsStore.Load(file);
            Assert.Equal(MenuBarItems.Movable, read.Settings.MenuBar.Items);
            SettingsStore.Save(file, read.Settings);
            Assert.Contains($"\"schemaVersion\": {AppSettings.CurrentSchema}", File.ReadAllText(file));
            var again = SettingsStore.Load(file);
            Assert.Empty(again.Fixes);
            // An old file without the list: the default, Now Playing once.
            File.WriteAllText(file, """{ "schemaVersion": 1 }""");
            Assert.Equal(MenuBarItems.Movable, SettingsStore.Load(file).Settings.MenuBar.Items);
        }
        finally { Directory.Delete(dir, true); }
    }
}
