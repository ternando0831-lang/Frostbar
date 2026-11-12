using Frostbar.Core.Wm;

namespace Frostbar.Core.Tests;

public sealed class DesktopWalkTests
{
    static readonly Guid A = new("4c43b5e8-3219-46cf-9276-05a00b13115b"), B = new("83743ebf-5e89-43be-87af-f448a4bead3e"),
        C = new("aa75f338-0000-4000-8000-000000000003"), D = new("d7f1d5c2-5b5e-4b8e-9a0e-1f2a3b4c5d6e");

    /// <summary>The desktops as they are, the one shown, and the list the registry has (which may lag behind).</summary>
    sealed class Row(Guid[] desktops, int shown, Guid[] listed)
    {
        public int Shown = shown;
        public readonly List<int> Presses = new();
        public Guid Current() => desktops[Shown];
        public IReadOnlyList<Guid> Listed() => listed;

        /// <summary>Win+Ctrl+Left/Right: nothing at the end of the row.</summary>
        public bool Step(int way)
        {
            Presses.Add(way);
            int to = Shown + way;
            if (to < 0 || to >= desktops.Length) return false;
            Shown = to;
            return true;
        }

        public bool Walk(Guid target) => DesktopWalk.Run(target, Current, Listed, Step);
    }

    [Fact]
    public void Two_desktops_to_the_right_take_two_steps_right()
    {
        var row = new Row([A, B, C], 0, [A, B, C]);
        Assert.True(row.Walk(C));
        Assert.Equal([1, 1], row.Presses.ToArray());
    }

    [Fact]
    public void A_desktop_to_the_left_is_one_step_left()
    {
        var row = new Row([A, B], 1, [A, B]);
        Assert.True(row.Walk(A));
        Assert.Equal([-1], row.Presses.ToArray());
    }

    [Fact]
    public void The_current_desktop_presses_nothing()
    {
        var row = new Row([A, B], 1, [A, B]);
        Assert.True(row.Walk(B));
        Assert.Empty(row.Presses);
    }

    /// <summary>2026-10-15: the desktop just added (Win+Ctrl+D, at the end) was not yet in the registry's list.</summary>
    [Fact]
    public void A_desktop_missing_from_the_list_is_looked_for_past_the_last()
    {
        var row = new Row([A, B], 0, [A]);
        Assert.True(row.Walk(B));
        Assert.Equal([1], row.Presses.ToArray());
    }

    /// <summary>On the new desktop (not listed) with the target listed: back towards the listed ones.</summary>
    [Fact]
    public void From_a_desktop_missing_from_the_list_it_goes_left()
    {
        var row = new Row([A, B], 1, [A]);
        Assert.True(row.Walk(A));
        Assert.Equal([-1], row.Presses.ToArray());
    }

    /// <summary>The list says right, the target is left (moved in Task View since): the end of the row, then the other way.</summary>
    [Fact]
    public void A_wrong_order_turns_back_at_the_end_of_the_row()
    {
        var row = new Row([C, A, B], 1, [A, B, C]);
        Assert.True(row.Walk(C));
        Assert.Equal([1, 1, -1, -1], row.Presses.ToArray());
    }

    [Fact]
    public void Neither_desktop_listed_tries_right_then_left()
    {
        var row = new Row([C, A, B], 1, []);
        Assert.True(row.Walk(C));
        Assert.Equal([1, 1, -1, -1], row.Presses.ToArray());
    }

    /// <summary>A desktop that is gone (closed meanwhile): both ends, then it gives up.</summary>
    [Fact]
    public void A_desktop_that_is_not_there_is_given_up_after_both_ends()
    {
        var row = new Row([A, B], 0, [A, B, D]);
        Assert.False(row.Walk(D));
        Assert.Equal([1, 1, -1, -1], row.Presses.ToArray());
    }

    [Fact]
    public void Without_a_current_desktop_nothing_is_pressed()
    {
        var presses = new List<int>();
        Assert.False(DesktopWalk.Run(B, () => Guid.Empty, () => [A, B], way => { presses.Add(way); return true; }));
        Assert.Empty(presses);
    }

    /// <summary>A step that keeps reporting a change but never arrives (a list that moves under it) stops at the limit.</summary>
    [Fact]
    public void Steps_stop_at_the_limit()
    {
        int presses = 0;
        Assert.False(DesktopWalk.Run(B, () => A, () => [A, B], _ => { presses++; return true; }, maxSteps: 5));
        Assert.Equal(5, presses);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(false, true, -1)]
    [InlineData(true, true, 0)]
    public void Next_turns_back_from_a_way_that_changed_nothing(bool leftEnd, bool rightEnd, int want) =>
        Assert.Equal(want, DesktopWalk.Next([A, B], A, B, leftEnd, rightEnd));
}
