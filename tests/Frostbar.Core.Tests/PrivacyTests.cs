using Frostbar.Core.Localization;
using Frostbar.Core.MenuBar;

namespace Frostbar.Core.Tests;

public class PrivacyTests
{
    static readonly Strings S = Strings.For("ja", System.Globalization.CultureInfo.GetCultureInfo("ja-JP"));

    [Fact]
    public void TheCameraWinsOverTheMicrophone()
    {
        Assert.Equal(PrivacyKind.None, PrivacyState.None.Kind);
        Assert.Equal(PrivacyKind.Microphone, new PrivacyState(["LINE"], []).Kind);
        Assert.Equal(PrivacyKind.Camera, new PrivacyState(["LINE"], ["Teams"]).Kind);
        Assert.Equal(PrivacyKind.Camera, new PrivacyState([], ["Teams"]).Kind);
    }

    [Fact]
    public void TheLinesPutTheCameraFirstWithItsApps()
    {
        var s = new PrivacyState(["LINE", "Discord"], ["Teams"]);
        Assert.Equal([PrivacyKind.Camera, PrivacyKind.Microphone], s.Lines().Select(l => l.Kind));
        Assert.Equal("カメラ: Teams\nマイク: LINE、Discord", s.Describe(S));
        Assert.Equal("", PrivacyState.None.Describe(S));
        Assert.Empty(PrivacyState.None.Lines());
    }

    [Fact]
    public void TheSameStateComparesTheLists()
    {
        Assert.True(new PrivacyState(["LINE"], []).Same(new PrivacyState(["LINE"], [])));
        Assert.False(new PrivacyState(["LINE"], []).Same(new PrivacyState(["LINE", "Discord"], [])));
        Assert.False(new PrivacyState(["LINE"], []).Same(new PrivacyState([], ["LINE"])));
    }
}
