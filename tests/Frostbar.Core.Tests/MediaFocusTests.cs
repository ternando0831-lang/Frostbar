using Frostbar.Core.Devices;

namespace Frostbar.Core.Tests;

public sealed class MediaFocusTests
{
    sealed class Session(string name) { public override string ToString() => name; }

    static readonly Session Paused = new("paused"), Other = new("other"), Third = new("third");

    [Fact]
    public void Nothing_pressed_follows_Windows()
    {
        var f = new MediaFocus<Session>();
        Assert.Same(Other, f.Choose(Other, [Paused, Other]));
        Assert.Null(f.Choose(null, [Paused, Other]));
    }

    [Fact]
    public void A_session_paused_from_the_panel_stays_while_Windows_turns_to_one_that_already_played()
    {
        var f = new MediaFocus<Session>();
        f.Pressed(Paused, [Paused, Other]);
        Assert.Same(Paused, f.Choose(Other, [Paused, Other]));
        Assert.Same(Paused, f.Choose(Other, [Paused, Other])); // and again, as Windows tells it again
        Assert.Same(Paused, f.Held);
    }

    [Fact]
    public void It_stays_while_Windows_has_no_current_session()
    {
        var f = new MediaFocus<Session>();
        f.Pressed(Paused, [Other]);
        Assert.Same(Paused, f.Choose(null, [Paused, Other]));
    }

    [Fact]
    public void Another_session_that_starts_takes_its_place()
    {
        var f = new MediaFocus<Session>();
        f.Pressed(Paused, [Other]);
        Assert.Same(Third, f.Choose(Third, [Paused, Other, Third]));
        Assert.Null(f.Held);
        Assert.Same(Other, f.Choose(Other, [Paused, Other, Third])); // let go: Windows is followed again
    }

    [Fact]
    public void It_is_let_go_once_gone_or_current_again()
    {
        var gone = new MediaFocus<Session>();
        gone.Pressed(Paused, [Other]);
        Assert.Same(Other, gone.Choose(Other, [Other]));
        Assert.Null(gone.Held);

        var back = new MediaFocus<Session>();
        back.Pressed(Paused, [Other]);
        Assert.Same(Paused, back.Choose(Paused, [Paused, Other]));
        Assert.Null(back.Held);
        Assert.Same(Other, back.Choose(Other, [Paused, Other]));
    }

    [Fact]
    public void Sessions_are_told_apart_by_the_comparer()
    {
        // Windows hands out a new wrapper for the same session: the comparer says which are the same.
        var byName = EqualityComparer<Session>.Create((a, b) => a?.ToString() == b?.ToString(), s => s.ToString().GetHashCode());
        var f = new MediaFocus<Session>(byName);
        f.Pressed(new Session("paused"), [new Session("other")]);
        Assert.Equal("paused", f.Choose(new Session("other"), [new Session("paused"), new Session("other")])?.ToString());
    }
}
