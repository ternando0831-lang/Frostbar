using Frostbar.Core.Dock;

namespace Frostbar.Core.Tests;

/// <summary>The Dock's kept shortcuts' AppIDs between starts (asking Explorer's resolver took about 6.5 ms a pin).</summary>
public class ShortcutIdsTests
{
    const string Lnk = @"C:\Users\u\AppData\Roaming\Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar\App.lnk";

    [Fact]
    public void AnAppIdIsKeptForTheFileAsItWasWritten()
    {
        var ids = new ShortcutIds();
        ids.Set(Lnk, 100, "Vendor.App");
        Assert.Equal("Vendor.App", ids.Get(Lnk, 100));
        Assert.Equal("Vendor.App", ids.Get(Lnk.ToUpperInvariant(), 100)); // paths without case
        Assert.Null(ids.Get(Lnk, 101)); // written since: asked again
        Assert.Null(ids.Get(@"C:\other.lnk", 100));
    }

    [Fact]
    public void OnlyADifferenceIsAChange()
    {
        var ids = ShortcutIds.Parse(new ShortcutIds().ToJson());
        Assert.False(ids.Changed);
        ids.Set(Lnk, 100, "Vendor.App");
        Assert.True(ids.Changed);
        ids.ToJson();
        Assert.False(ids.Changed); // saved
        ids.Set(Lnk, 100, "Vendor.App");
        Assert.False(ids.Changed); // the same
        ids.Set(Lnk, 100, "Vendor.Other");
        Assert.True(ids.Changed);
    }

    [Fact]
    public void FilesNoLongerKeptAreForgotten()
    {
        var ids = new ShortcutIds();
        ids.Set(Lnk, 100, "Vendor.App");
        ids.Set(@"C:\b.lnk", 5, "B");
        ids.ToJson();
        ids.KeepOnly([Lnk.ToUpperInvariant()]);
        Assert.True(ids.Changed);
        Assert.Equal(1, ids.Count);
        Assert.Null(ids.Get(@"C:\b.lnk", 5));
        ids.ToJson();
        ids.KeepOnly([Lnk]);
        Assert.False(ids.Changed);
    }

    [Fact]
    public void TheyComeBackFromTheirJson()
    {
        var ids = new ShortcutIds();
        ids.Set(Lnk, 638_960_000_000_000_000, "Microsoft.Windows.Explorer");
        ids.Set(@"C:\Program Files\B\b.exe", 7, @"C:\Program Files\B\b.exe");
        var back = ShortcutIds.Parse(ids.ToJson());
        Assert.Equal(2, back.Count);
        Assert.Equal("Microsoft.Windows.Explorer", back.Get(Lnk, 638_960_000_000_000_000));
        Assert.Equal(@"C:\Program Files\B\b.exe", back.Get(@"C:\Program Files\B\b.exe", 7));
        Assert.False(back.Changed);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1, 2]")]
    [InlineData("{\"C:\\\\a.lnk\": {\"written\": 1, \"appId\": \"\"}}")] // an empty AppID is asked again
    public void UnreadableTextKeepsNothing(string? json) => Assert.Equal(0, ShortcutIds.Parse(json).Count);
}
