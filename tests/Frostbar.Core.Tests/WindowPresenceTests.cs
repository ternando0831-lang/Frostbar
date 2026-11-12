using Frostbar.Core.Wm;

namespace Frostbar.Core.Tests;

public sealed class WindowPresenceTests
{
    static readonly Guid Home = new("4c43b5e8-3219-46cf-9276-05a00b13115b"), Other = new("d7f1d5c2-5b5e-4b8e-9a0e-1f2a3b4c5d6e");

    [Fact]
    public void A_window_not_cloaked_is_here() =>
        Assert.Equal(Presence.Here, WindowPresence.Of(0, true, Home));

    /// <summary>Another desktop's window: cloaked by the shell, not on the current desktop, on a desktop of its own.</summary>
    [Fact]
    public void A_window_the_shell_cloaked_on_another_desktop_is_elsewhere() =>
        Assert.Equal(Presence.Elsewhere, WindowPresence.Of(WindowPresence.CloakedShell, false, Other));

    /// <summary>Seen on this PC (2026-10-15): a closed Settings app's frame, cloaked by the shell, "on the current desktop" with an empty id.</summary>
    [Fact]
    public void A_suspended_store_apps_frame_is_not_counted() =>
        Assert.Equal(Presence.Hidden, WindowPresence.Of(WindowPresence.CloakedShell, true, Guid.Empty));

    [Fact]
    public void A_window_the_app_cloaked_itself_is_not_counted() =>
        Assert.Equal(Presence.Hidden, WindowPresence.Of(WindowPresence.CloakedApp, false, Other));

    /// <summary>The text input host's window: no desktop at all (GetWindowDesktopId failed).</summary>
    [Fact]
    public void A_window_with_no_desktop_is_not_counted() =>
        Assert.Equal(Presence.Hidden, WindowPresence.Of(WindowPresence.CloakedShell, true, null));

    [Fact]
    public void A_cloaked_window_the_desktop_manager_could_not_place_is_not_counted() =>
        Assert.Equal(Presence.Hidden, WindowPresence.Of(WindowPresence.CloakedShell, null, Other));
}
