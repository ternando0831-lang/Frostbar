using Frostbar.Core.Settings;
using Frostbar.Core.Time;

namespace Frostbar.Core.Tests;

public sealed class AppearanceSettingsTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "frostbar-tests-" + Guid.NewGuid().ToString("N"));
    string File_ => Path.Combine(_dir, "settings.json");

    public AppearanceSettingsTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    AppSettings SavedAndRead(AppSettings s)
    {
        SettingsStore.Save(File_, s);
        return SettingsStore.Load(File_).Settings;
    }

    [Fact]
    public void ByDefaultWindowsIsLeftAlone()
    {
        var s = SettingsStore.Load(File_).Settings;
        Assert.Equal("off", s.Appearance.DarkSchedule);
        Assert.Equal("", s.Appearance.City);
        Assert.False(s.Wallpaper.Paired);
    }

    // Every schedule the page offers survives a save and a read (2026-10-15: a hot corner's action the validator did
    // not know went back to its default at the next start).
    [Theory]
    [InlineData("off")]
    [InlineData("sun")]
    [InlineData("clock")]
    public void EveryScheduleIsKept(string schedule)
    {
        var s = new AppSettings();
        s.Appearance.DarkSchedule = schedule;
        Assert.Equal(schedule, SavedAndRead(s).Appearance.DarkSchedule);
    }

    public static TheoryData<string> CityKeys() => new(Cities.All.Select(c => c.Key).ToArray());

    [Theory]
    [MemberData(nameof(CityKeys))]
    public void EveryCityIsKept(string city)
    {
        var s = new AppSettings();
        s.Appearance.City = city;
        Assert.Equal(city, SavedAndRead(s).Appearance.City);
    }

    [Fact]
    public void TimesTheWallpapersAndTheNextReadKeepTheirValues()
    {
        var s = new AppSettings();
        s.Appearance.DarkFrom = "20:30";
        s.Appearance.DarkUntil = "06:15";
        s.Wallpaper.Paired = true;
        s.Wallpaper.Light = @"C:\Windows\Web\Wallpaper\Windows\img0.jpg";
        s.Wallpaper.Dark = @"C:\Windows\Web\Wallpaper\Windows\img19.jpg";
        var r = SavedAndRead(s);
        Assert.Equal("20:30", r.Appearance.DarkFrom);
        Assert.Equal("06:15", r.Appearance.DarkUntil);
        Assert.True(r.Wallpaper.Paired);
        Assert.Equal(s.Wallpaper.Light, r.Wallpaper.Light);
        Assert.Equal(s.Wallpaper.Dark, r.Wallpaper.Dark);
    }

    [Fact]
    public void WhatCannotBeReadComesBackToItsDefault()
    {
        var s = new AppSettings();
        s.Appearance.DarkSchedule = "moon";
        s.Appearance.City = "atlantis";
        s.Appearance.DarkFrom = "25:00";
        s.Appearance.DarkUntil = "7am";
        s.Wallpaper = null!;
        var fixes = SettingsValidator.Normalize(s);
        Assert.Equal("off", s.Appearance.DarkSchedule);
        Assert.Equal("", s.Appearance.City);
        Assert.Equal("19:00", s.Appearance.DarkFrom);
        Assert.Equal("07:00", s.Appearance.DarkUntil);
        Assert.NotNull(s.Wallpaper);
        Assert.Equal(4, fixes.Count(f => f.StartsWith("appearance.")));
    }

    // ---- the glass's tint (macOS 27's Liquid Glass slider, 2026-10-18) ----

    [Fact]
    public void TheGlassKeepsTodaysTintByDefault()
    {
        var s = SettingsStore.Load(File_).Settings;
        Assert.Equal(50, s.Appearance.GlassTint);
        Assert.Equal(1.0, Frostbar.Core.Shell.GlassTints.Scale(s.Appearance.GlassTint), 6);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(35, 35)]
    [InlineData(100, 100)]
    [InlineData(37, 35)]  // to its steps of 5
    [InlineData(-5, 50)]  // outside: the default
    [InlineData(140, 50)]
    public void TheTintIsKeptInItsStepsAndRange(int given, int kept)
    {
        var s = new AppSettings();
        s.Appearance.GlassTint = given;
        SettingsValidator.Normalize(s);
        Assert.Equal(kept, s.Appearance.GlassTint);
        Assert.Equal(kept, SavedAndRead(s).Appearance.GlassTint);
    }

    [Theory]
    [InlineData(0, 0.45)]
    [InlineData(25, 0.725)]
    [InlineData(50, 1.0)]
    [InlineData(75, 1.15)]
    [InlineData(100, 1.3)]
    public void TheSliderScalesTodaysTint(int setting, double scale) =>
        Assert.Equal(scale, Frostbar.Core.Shell.GlassTints.Scale(setting), 6);

    [Fact]
    public void ATintedEndNeverGoesPastOpaque()
    {
        Assert.Equal(255, Frostbar.Core.Shell.GlassTints.Alpha(0xF0, 100));
        Assert.Equal(0xB0, Frostbar.Core.Shell.GlassTints.Alpha(0xB0, 50));
        Assert.Equal(79, Frostbar.Core.Shell.GlassTints.Alpha(0xB0, 0)); // 176 × 0.45 = 79.2
    }
}
