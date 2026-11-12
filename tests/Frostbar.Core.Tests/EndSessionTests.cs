using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

public sealed class EndSessionTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 16, 19, 48, 31, TimeSpan.FromHours(9));

    /// <summary>
    /// Restart Manager's "close this program" (another program's installer or update; the session goes on), with or
    /// without "critical", whatever the shutting-down metric says (2026-10-16: the Claude app's update).
    /// </summary>
    [Theory]
    [InlineData(EndSession.CloseApp, false)]
    [InlineData(EndSession.CloseApp, true)]
    [InlineData(EndSession.CloseApp | EndSession.Critical, false)]
    public void Close_the_app_is_not_the_session_ending(uint flags, bool shuttingDown) =>
        Assert.Equal(EndSessionKind.CloseApp, EndSession.Classify(flags, shuttingDown));

    [Theory]
    [InlineData(EndSession.Logoff, false)]
    [InlineData(EndSession.Logoff, true)]
    [InlineData(EndSession.Critical, false)]
    [InlineData(0u, true)]
    public void Signing_out_a_critical_end_and_a_shutdown_end_the_session(uint flags, bool shuttingDown) =>
        Assert.Equal(EndSessionKind.SessionEnd, EndSession.Classify(flags, shuttingDown));

    /// <summary>
    /// A shutdown's flags (none) while Windows does not shut down, and a query not seen, go by the metric: what sent
    /// the shell its "session ending" on 2026-10-16 was not recorded.
    /// </summary>
    [Fact]
    public void No_flags_and_no_query_seen_go_by_the_shutting_down_metric()
    {
        Assert.Equal(EndSessionKind.CloseApp, EndSession.Classify(0u, shuttingDown: false));
        Assert.Equal(EndSessionKind.CloseApp, EndSession.Classify(null, shuttingDown: false));
        Assert.Equal(EndSessionKind.SessionEnd, EndSession.Classify(null, shuttingDown: true));
    }

    [Fact]
    public void The_log_names_the_flags()
    {
        Assert.Equal("close the app (0x00000001)", EndSession.Describe(EndSession.CloseApp));
        Assert.Equal("sign out (0x80000000)", EndSession.Describe(EndSession.Logoff));
        Assert.Equal("close the app, critical (0x40000001)", EndSession.Describe(EndSession.CloseApp | EndSession.Critical));
        Assert.Equal("shut down or restart (0x00000000)", EndSession.Describe(0u));
        Assert.Equal("not seen", EndSession.Describe(null));
    }

    static ShellState Gone(bool clean, string? endedBy = null) => new() { CleanExit = clean, EndedBy = endedBy };

    [Fact]
    public void Closed_for_another_program_it_is_started_again()
    {
        var d = RelaunchPlanner.Decide(Gone(true, ShellState.ClosedForAnotherProgram), relaunchAfterCrash: false, shuttingDown: false, Now);
        Assert.Equal(RelaunchKind.AfterCloseApp, d.Kind);
    }

    /// <summary>
    /// At each restart and shutdown Windows ended the process half-way through its stop (the state still read "not
    /// clean") and the watchdog started it again as after a crash (2026-10-13/14): the session's end is written first.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void After_the_session_ended_it_is_not_started_again_even_when_its_stop_was_cut_short(bool clean)
    {
        var d = RelaunchPlanner.Decide(Gone(clean, ShellState.SessionEnded), relaunchAfterCrash: true, shuttingDown: false, Now);
        Assert.Equal(RelaunchKind.None, d.Kind);
    }

    [Theory]
    [InlineData(true, ShellState.ClosedForAnotherProgram)]
    [InlineData(false, null)]
    public void Nothing_is_started_while_Windows_shuts_down(bool clean, string? endedBy)
    {
        var d = RelaunchPlanner.Decide(Gone(clean, endedBy), relaunchAfterCrash: true, shuttingDown: true, Now);
        Assert.Equal(RelaunchKind.None, d.Kind);
    }

    [Fact]
    public void A_quit_is_not_started_again_and_a_crash_is_when_the_setting_says_so()
    {
        Assert.Equal(RelaunchKind.None, RelaunchPlanner.Decide(Gone(true), relaunchAfterCrash: true, shuttingDown: false, Now).Kind);
        Assert.Equal(RelaunchKind.AfterCrash, RelaunchPlanner.Decide(Gone(false), relaunchAfterCrash: true, shuttingDown: false, Now).Kind);
        Assert.Equal(RelaunchKind.None, RelaunchPlanner.Decide(Gone(false), relaunchAfterCrash: false, shuttingDown: false, Now).Kind);
    }

    /// <summary>An installer that closes the shell again and again: three times in ten minutes, then it stays closed.</summary>
    [Fact]
    public void Closed_for_other_programs_three_times_in_ten_minutes_it_stays_closed()
    {
        var st = Gone(true, ShellState.ClosedForAnotherProgram);
        st.CloseAppRelaunches = [Now.AddMinutes(-9), Now.AddMinutes(-4), Now.AddMinutes(-1)];
        Assert.Equal(RelaunchKind.None, RelaunchPlanner.Decide(st, relaunchAfterCrash: true, shuttingDown: false, Now).Kind);
        st.CloseAppRelaunches = [Now.AddMinutes(-11), Now.AddMinutes(-4), Now.AddMinutes(-1)];
        Assert.Equal(RelaunchKind.AfterCloseApp, RelaunchPlanner.Decide(st, relaunchAfterCrash: true, shuttingDown: false, Now).Kind);
        Assert.Equal(3, CloseAppRelaunchPolicy.Record(st.CloseAppRelaunches, Now).Count); // the one of 11 minutes ago is dropped
    }

    /// <summary>The crash budget is not spent by closing for other programs, nor the other way round.</summary>
    [Fact]
    public void The_two_budgets_are_apart()
    {
        var st = Gone(false);
        st.CloseAppRelaunches = [Now.AddMinutes(-3), Now.AddMinutes(-2), Now.AddMinutes(-1)];
        Assert.Equal(RelaunchKind.AfterCrash, RelaunchPlanner.Decide(st, relaunchAfterCrash: true, shuttingDown: false, Now).Kind);
        st = Gone(true, ShellState.ClosedForAnotherProgram);
        st.Relaunches = [Now.AddMinutes(-3), Now.AddMinutes(-2), Now.AddMinutes(-1)];
        Assert.Equal(RelaunchKind.AfterCloseApp, RelaunchPlanner.Decide(st, relaunchAfterCrash: true, shuttingDown: false, Now).Kind);
    }
}
