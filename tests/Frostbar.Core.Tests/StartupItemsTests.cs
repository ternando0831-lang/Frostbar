using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

public class StartupItemsTests
{
    [Fact]
    public void AMarkIsOnWhenItsFirstByteIsEvenOrThereIsNone()
    {
        Assert.True(StartupApproval.IsOn(null));
        Assert.True(StartupApproval.IsOn([]));
        Assert.True(StartupApproval.IsOn([2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.True(StartupApproval.IsOn([6, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.False(StartupApproval.IsOn([1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
        Assert.False(StartupApproval.IsOn([3, 0, 0, 0, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60, 0x70, 0x01]));
        Assert.False(StartupApproval.IsOn([7, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]));
    }

    [Fact]
    public void TheMarksAreTaskManagersAndKeepWhenItWasTurnedOff()
    {
        var when = new DateTime(2026, 10, 16, 3, 4, 5, DateTimeKind.Utc);
        var off = StartupApproval.Mark(false, when);
        Assert.Equal(StartupApproval.Length, off.Length);
        Assert.Equal(3, off[0]);
        Assert.Equal([0, 0, 0], off[1..4]);
        Assert.False(StartupApproval.IsOn(off));
        Assert.Equal(when, StartupApproval.OffSince(off));

        var on = StartupApproval.Mark(true, when);
        Assert.Equal([2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0], on);
        Assert.True(StartupApproval.IsOn(on));
        Assert.Null(StartupApproval.OffSince(on));
        Assert.Null(StartupApproval.OffSince([3, 0, 0, 0])); // too short for a time
    }

    [Theory]
    [InlineData(@"""C:\Program Files\App\app.exe"" --min", @"C:\Program Files\App\app.exe")]
    [InlineData(@"""C:\Program Files\App\app.exe""", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe -min", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Program Files\App\app.exe", @"C:\Program Files\App\app.exe")]
    [InlineData(@"C:\Tools\my.executor\run.exe /quiet", @"C:\Tools\my.executor\run.exe")]
    [InlineData(@"C:\Windows\System32\rundll32.exe shell32.dll,Control_RunDLL", @"C:\Windows\System32\rundll32.exe")]
    [InlineData(@"rundll32 C:\x.dll,Start", "rundll32")]
    [InlineData(@"C:\Scripts\start.cmd", @"C:\Scripts\start.cmd")]
    [InlineData(@"  ""C:\a b\c.exe"" ", @"C:\a b\c.exe")]
    [InlineData(@"""C:\no end quote\app.exe", @"C:\no end quote\app.exe")]
    public void TheProgramOfARunValue(string command, string program) => Assert.Equal(program, StartupItems.ProgramPath(command));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(@"""""")]
    public void NoProgramInAnEmptyCommand(string? command) => Assert.Null(StartupItems.ProgramPath(command));

    [Theory]
    [InlineData(0, false)] // Disabled
    [InlineData(1, false)] // DisabledByUser
    [InlineData(2, true)]  // Enabled
    [InlineData(3, false)] // DisabledByPolicy
    [InlineData(4, true)]  // EnabledByPolicy
    public void APackagedAppsStartupTask(int state, bool on) => Assert.Equal(on, StartupItems.PackagedOn(state));

    static StartupEntry E(StartupSource s, string name, string title, bool on = true) => new(s, name, "", null, title, null, on);

    [Fact]
    public void ListedByTitleAndBeingOnDoesNotMoveARow()
    {
        var before = StartupItems.Order([E(StartupSource.UserRun, "b", "Beta"), E(StartupSource.MachineRun, "a", "alpha"), E(StartupSource.UserFolder, "c.lnk", "Gamma")]);
        Assert.Equal(["alpha", "Beta", "Gamma"], before.Select(e => e.Title));
        var after = StartupItems.Order(before.Select(e => e with { On = !e.On }));
        Assert.Equal(before.Select(e => e.Key), after.Select(e => e.Key));
    }

    [Fact]
    public void TheSameTitleIsOrderedBySourceThenName()
    {
        // By name alone the machine's "a" would come first.
        var list = StartupItems.Order([E(StartupSource.MachineRun, "a", "App"), E(StartupSource.UserRun, "y", "App"), E(StartupSource.UserRun, "x", "App")]);
        Assert.Equal(["UserRun:x", "UserRun:y", "MachineRun:a"], list.Select(e => e.Key));
    }

    [Fact]
    public void FrostbarsOwnValueIsNotAmongTheOthers()
    {
        var list = StartupItems.Others([E(StartupSource.UserRun, LoginItem.ValueName, "Frostbar"), E(StartupSource.MachineRun, LoginItem.ValueName, "Frostbar for all"), E(StartupSource.UserRun, "app", "App")]);
        Assert.Equal(["MachineRun:" + LoginItem.ValueName, "UserRun:app"], list.Select(e => e.Key).OrderBy(k => k));
    }

    [Fact]
    public void OnlyTheUsersOwnCanBeChanged()
    {
        Assert.True(E(StartupSource.UserRun, "a", "A").CanChange);
        Assert.True(E(StartupSource.UserFolder, "a.lnk", "A").CanChange);
        Assert.False(E(StartupSource.MachineRun, "a", "A").CanChange);
        Assert.False(E(StartupSource.MachineRun32, "a", "A").CanChange);
        Assert.False(E(StartupSource.MachineFolder, "a.lnk", "A").CanChange);
        Assert.False(E(StartupSource.Package, "a", "A").CanChange);
    }
}
