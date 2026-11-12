using Frostbar.Core.Search;
using Frostbar.Core.Settings;
using Frostbar.Core.Shell;
using Frostbar.Core.Time;

namespace Frostbar.Core.Tests;

/// <summary>
/// The settings macOS has and Frostbar lacked (2026-10-22): the accent colour, the scroll bars, the kinds of the search's
/// results, the clock's flashing separators, Accessibility, the tiles' margins and the sound effects.
/// </summary>
public sealed class MacSettingsAdditionsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "frostbar-tests-" + Guid.NewGuid().ToString("N"));
    string File_ => Path.Combine(_dir, "settings.json");

    public MacSettingsAdditionsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void The_defaults_are_as_before()
    {
        var s = SettingsStore.Load(File_).Settings;
        Assert.Equal(AccentColors.System, s.Appearance.Accent); // Windows' accent, as before
        Assert.Equal(ScrollBarChoices.WhenScrolling, s.Appearance.ScrollBars);
        Assert.Equal(ScrollBarChoices.NextPage, s.Appearance.ScrollBarClick);
        Assert.False(s.Accessibility.ReduceMotion);
        Assert.False(s.Accessibility.IncreaseContrast);
        Assert.False(s.Window.TileMargins);
        Assert.True(s.Sound.InterfaceSounds);
        Assert.False(s.Sound.VolumeFeedback);
        Assert.True(s.MenuBar.BatteryPercent);
        Assert.False(s.MenuBar.Clock.FlashSeparators);
        Assert.Empty(s.Search.Hidden);
    }

    [Fact]
    public void An_older_file_without_them_reads_with_the_defaults_and_no_fixes()
    {
        File.WriteAllText(File_, """{ "schemaVersion": 2, "appearance": { "theme": "dark" }, "menuBar": { "height": 24 } }""");
        var r = SettingsStore.Load(File_);
        Assert.All(r.Fixes, f => Assert.StartsWith("schemaVersion 2 -> ", f)); // macOS's defaults (2026-11-01) and the Dock's room (2026-11-08), nothing about the new sections
        Assert.Equal(AccentColors.System, r.Settings.Appearance.Accent);
        Assert.NotNull(r.Settings.Accessibility);
        Assert.NotNull(r.Settings.Window);
        Assert.NotNull(r.Settings.Sound);
    }

    public static TheoryData<string> Accents => new(AccentColors.Names);

    [Theory]
    [MemberData(nameof(Accents))]
    public void Every_accent_the_page_offers_survives_a_start(string accent)
    {
        var s = new AppSettings();
        s.Appearance.Accent = accent;
        SettingsStore.Save(File_, s);
        var read = SettingsStore.Load(File_);
        Assert.Equal(accent, read.Settings.Appearance.Accent);
        Assert.Empty(read.Fixes);
        // Windows' own has no colour of its own here; each of macOS's has one, in both appearances.
        Assert.Equal(accent == AccentColors.System, AccentColors.Of(accent, dark: true) == null);
        Assert.Equal(accent == AccentColors.System, AccentColors.Of(accent, dark: false) == null);
    }

    [Theory]
    [InlineData("teal")]
    [InlineData("Blue")]
    [InlineData(null)]
    public void An_accent_the_page_could_not_have_set_goes_back_to_windows(string? accent)
    {
        var s = new AppSettings();
        s.Appearance.Accent = accent!;
        Assert.Contains(SettingsValidator.Normalize(s), f => f.StartsWith("appearance.accent"));
        Assert.Equal(AccentColors.System, s.Appearance.Accent);
    }

    [Fact]
    public void Every_scroll_bar_choice_survives_a_start_and_others_fall_back()
    {
        foreach (var shows in ScrollBarChoices.Shows)
            foreach (var click in ScrollBarChoices.Clicks)
            {
                var s = new AppSettings();
                s.Appearance.ScrollBars = shows;
                s.Appearance.ScrollBarClick = click;
                Assert.Empty(SettingsValidator.Normalize(s));
                Assert.Equal((shows, click), (s.Appearance.ScrollBars, s.Appearance.ScrollBarClick));
            }
        var bad = new AppSettings();
        bad.Appearance.ScrollBars = "never";
        bad.Appearance.ScrollBarClick = "jump";
        Assert.Equal(2, SettingsValidator.Normalize(bad).Count(f => f.StartsWith("appearance.scrollBar")));
        Assert.Equal((ScrollBarChoices.WhenScrolling, ScrollBarChoices.NextPage), (bad.Appearance.ScrollBars, bad.Appearance.ScrollBarClick));
    }

    [Fact]
    public void The_new_sections_keep_what_they_were_given()
    {
        var s = new AppSettings();
        s.Accessibility.ReduceMotion = true;
        s.Accessibility.IncreaseContrast = true;
        s.Window.TileMargins = true;
        s.Sound.InterfaceSounds = false;
        s.Sound.VolumeFeedback = true;
        s.MenuBar.BatteryPercent = false;
        s.MenuBar.Clock.FlashSeparators = true;
        s.Search.Hidden = ["web", "files"];
        SettingsStore.Save(File_, s);
        var r = SettingsStore.Load(File_);
        Assert.Empty(r.Fixes);
        var x = r.Settings;
        Assert.True(x.Accessibility.ReduceMotion && x.Accessibility.IncreaseContrast && x.Window.TileMargins && x.Sound.VolumeFeedback && x.MenuBar.Clock.FlashSeparators);
        Assert.False(x.Sound.InterfaceSounds || x.MenuBar.BatteryPercent);
        Assert.Equal(["web", "files"], x.Search.Hidden);
    }

    [Fact]
    public void Null_sections_from_a_hand_edit_become_their_defaults()
    {
        File.WriteAllText(File_, """{ "schemaVersion": 2, "accessibility": null, "window": null, "sound": null, "search": { "hidden": null } }""");
        var s = SettingsStore.Load(File_).Settings;
        Assert.False(s.Accessibility.ReduceMotion);
        Assert.False(s.Window.TileMargins);
        Assert.True(s.Sound.InterfaceSounds);
        Assert.Empty(s.Search.Hidden);
    }

    // ---- the search's kinds ------------------------------------------------------------------------------------

    public static TheoryData<string> Categories => new(SearchCategories.Names);

    [Theory]
    [MemberData(nameof(Categories))]
    public void Every_kind_the_search_page_offers_survives_a_start_and_stands_for_a_kind(string name)
    {
        var s = new AppSettings();
        s.Search.Hidden = [name];
        Assert.Empty(SettingsValidator.Normalize(s));
        Assert.Equal([name], s.Search.Hidden);
        Assert.NotNull(SearchCategories.Kind(name));
    }

    [Fact]
    public void Kinds_the_page_could_not_have_hidden_are_dropped_and_each_is_kept_once()
    {
        var s = new AppSettings();
        s.Search.Hidden = ["web", "clipboard", "web", "Files", "notes"];
        Assert.Contains(SettingsValidator.Normalize(s), f => f.StartsWith("search.hidden"));
        Assert.Equal(["web", "notes"], s.Search.Hidden);
    }

    [Fact]
    public void Each_kind_of_the_answer_has_a_switch_but_the_clipboard_a_filter_of_its_own()
    {
        var covered = SearchCategories.Names.Select(n => SearchCategories.Kind(n)!.Value).ToHashSet();
        foreach (var kind in Enum.GetValues<SearchKind>())
            Assert.Equal(kind != SearchKind.Clipboard, covered.Contains(kind));
    }

    static SearchResult R(SearchKind kind, string title, int score = Matcher.Prefix) => new(kind, title, null, title, score);

    static SearchAnswers Everything() => new(
        R(SearchKind.Calculation, "4"), [R(SearchKind.App, "Notepad")], [R(SearchKind.Window, "Notes - Notepad")], [R(SearchKind.Action, "Lock")],
        [R(SearchKind.Setting, "Notifications")], [R(SearchKind.Note, "note"), R(SearchKind.Folder, "Notes"), R(SearchKind.File, "notes.txt")], R(SearchKind.Web, "web", 0));

    [Fact]
    public void Hidden_kinds_are_left_out_of_the_answer_and_the_others_keep_their_order()
    {
        var all = SearchRanker.Arrange(Everything());
        var hidden = SearchCategories.Hidden(["web", "files", "calculator"]);
        var some = SearchRanker.Arrange(Everything(), SearchFilter.None, hidden);
        Assert.DoesNotContain(some, r => r.Kind is SearchKind.Web or SearchKind.File or SearchKind.Calculation);
        Assert.Equal(all.Where(r => r.Kind is not (SearchKind.Web or SearchKind.File or SearchKind.Calculation)).Select(r => r.Title), some.Select(r => r.Title));
    }

    [Fact]
    public void A_hidden_kind_is_never_the_top_hit()
    {
        var hidden = SearchCategories.Hidden(["apps"]);
        var answer = SearchRanker.Arrange(Everything(), SearchFilter.None, hidden);
        Assert.DoesNotContain(answer, r => r.Kind == SearchKind.App);
    }

    [Fact]
    public void A_filter_still_lists_its_kinds_when_they_are_hidden()
    {
        var hidden = SearchCategories.Hidden(["files", "folders", "notes"]);
        var files = SearchRanker.Arrange(Everything(), SearchFilter.Files, hidden);
        Assert.Contains(files, r => r.Kind == SearchKind.File);
    }

    [Fact]
    public void A_source_is_asked_unless_every_kind_it_answers_with_is_hidden()
    {
        var hidden = SearchCategories.Hidden(["files", "folders"]);
        Assert.False(SearchCategories.AllHidden(hidden, SearchKind.Folder, SearchKind.File, SearchKind.Note)); // the notes still come by name
        hidden.Add(SearchKind.Note);
        Assert.True(SearchCategories.AllHidden(hidden, SearchKind.Folder, SearchKind.File, SearchKind.Note));
    }

    // ---- the volume's feedback ---------------------------------------------------------------------------------

    [Fact]
    public void The_volumes_feedback_plays_for_a_key_or_the_slider_while_on_and_not_muted()
    {
        Assert.True(VolumeFeedback.Plays(on: true, muted: false, fromKey: true, sinceKeyMs: 30, sinceLastMs: 500));   // a key's change
        Assert.True(VolumeFeedback.Plays(on: true, muted: false, fromKey: false, sinceKeyMs: 99_999, sinceLastMs: 500)); // the slider let go
        Assert.False(VolumeFeedback.Plays(on: false, muted: false, fromKey: true, sinceKeyMs: 30, sinceLastMs: 500));  // the setting off (the default)
        Assert.False(VolumeFeedback.Plays(on: true, muted: true, fromKey: false, sinceKeyMs: 30, sinceLastMs: 500));   // no sound at no volume
        Assert.False(VolumeFeedback.Plays(on: true, muted: false, fromKey: true, sinceKeyMs: 5000, sinceLastMs: 500)); // moved by an app, no key
        Assert.False(VolumeFeedback.Plays(on: true, muted: false, fromKey: true, sinceKeyMs: 30, sinceLastMs: 40));    // a held key's repeats
        Assert.True(VolumeFeedback.Plays(on: true, muted: false, fromKey: true, sinceKeyMs: VolumeFeedback.KeyWindowMs, sinceLastMs: VolumeFeedback.GapMs));
    }

    // ---- the clock's separators --------------------------------------------------------------------------------

    [Fact]
    public void Flashing_separators_show_on_even_seconds_and_tick_every_second()
    {
        var flash = new ClockSettings { FlashSeparators = true };
        Assert.True(ClockFormatter.SeparatorsShown(new DateTime(2026, 10, 22, 12, 0, 0), flash));
        Assert.False(ClockFormatter.SeparatorsShown(new DateTime(2026, 10, 22, 12, 0, 1), flash));
        Assert.True(ClockFormatter.EverySecond(flash));
        var plain = new ClockSettings();
        Assert.True(ClockFormatter.SeparatorsShown(new DateTime(2026, 10, 22, 12, 0, 1), plain));
        Assert.False(ClockFormatter.EverySecond(plain)); // every minute, as before
        Assert.True(ClockFormatter.EverySecond(new ClockSettings { Seconds = true }));
    }
}
