using Frostbar.Core.MenuBar;

namespace Frostbar.Core.Tests;

/// <summary>Stage 5-2's source: what Windows' ConsentStore says uses the microphone and the camera now.</summary>
public class PrivacyRecordsTests
{
    static string Name(PrivacyRecord r) => r.Packaged ? PrivacyRecords.PackageName(r.App) : Path.GetFileNameWithoutExtension(PrivacyRecords.ProgramOf(r.App));

    [Fact]
    public void AnAppIsInUseFromItsStartUntilItsStop()
    {
        Assert.True(PrivacyRecords.InUse(new PrivacyRecord(PrivacyKind.Microphone, "a", false, Start: 100, Stop: 0)));
        Assert.False(PrivacyRecords.InUse(new PrivacyRecord(PrivacyKind.Microphone, "a", false, Start: 100, Stop: 200))); // let go
        Assert.False(PrivacyRecords.InUse(new PrivacyRecord(PrivacyKind.Microphone, "a", false, Start: 0, Stop: 0))); // allowed, never used
    }

    [Fact]
    public void EachDeviceListsItsAppsTheLatestFirst()
    {
        var state = PrivacyRecords.State([
            new(PrivacyKind.Microphone, @"C:#Apps#Discord.exe", false, Start: 100, Stop: 0),
            new(PrivacyKind.Microphone, @"C:#Apps#LINE.exe", false, Start: 300, Stop: 0),
            new(PrivacyKind.Microphone, @"C:#Apps#Old.exe", false, Start: 50, Stop: 60),
            new(PrivacyKind.Camera, "Microsoft.WindowsCamera_8wekyb3d8bbwe", true, Start: 200, Stop: 0),
        ], Name);
        Assert.Equal(["LINE", "Discord"], state.Microphone);
        Assert.Equal(["WindowsCamera"], state.Camera);
        Assert.Equal(PrivacyKind.Camera, state.Kind);
    }

    [Fact]
    public void AnAppNamedTwiceIsListedOnce()
    {
        var state = PrivacyRecords.State([
            new(PrivacyKind.Microphone, @"C:#A#app.exe", false, Start: 100, Stop: 0),
            new(PrivacyKind.Microphone, @"D:#B#APP.exe", false, Start: 200, Stop: 0),
        ], Name);
        Assert.Equal(["APP"], state.Microphone); // the latest's name
    }

    [Fact]
    public void NothingInUseIsNone()
    {
        var state = PrivacyRecords.State([new(PrivacyKind.Camera, "x", true, Start: 5, Stop: 6)], Name);
        Assert.True(state.Same(PrivacyState.None));
        Assert.Equal(PrivacyKind.None, state.Kind);
    }

    [Theory]
    [InlineData("C:#Program Files#Discord#Discord.exe", @"C:\Program Files\Discord\Discord.exe")]
    [InlineData(@"C:\already\a.exe", @"C:\already\a.exe")]
    public void ADesktopAppsProgramComesFromItsKey(string key, string program) => Assert.Equal(program, PrivacyRecords.ProgramOf(key));

    [Theory]
    [InlineData("Microsoft.WindowsCamera_8wekyb3d8bbwe", "WindowsCamera")]
    [InlineData("Microsoft.WindowsSoundRecorder_8wekyb3d8bbwe", "WindowsSoundRecorder")]
    [InlineData("NoDots_abc", "NoDots")]
    [InlineData("Trailing._abc", "Trailing.")]
    public void APackagesNameComesFromItsFamilyName(string family, string name) => Assert.Equal(name, PrivacyRecords.PackageName(family));
}
