using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

public sealed class HangJudgeTests
{
    /// <summary>Rounds every 1.5 s from <paramref name="from"/> to <paramref name="to"/> (the watchdog's loop).</summary>
    static HangVerdict Rounds(HangJudge j, long from, long to, bool answered, Func<long, long?> beat)
    {
        var v = HangVerdict.Fine;
        for (long t = from; t <= to; t += 1500) v = j.Observe(t, answered, beat(t));
        return v;
    }

    [Fact]
    public void A_shell_that_answers_and_beats_is_fine()
    {
        var j = new HangJudge(0);
        Assert.Equal(HangVerdict.Fine, Rounds(j, 1500, 60_000, true, t => t - t % 3000));
    }

    /// <summary>
    /// The watchdog skips its ping while the beat is fresh and counts the round as answered at the beat (2026-11-08).
    /// A shell whose whole message loop stops right after a beat must still be told "does not answer", not "stuck in a
    /// wait": counted as answered at each skipping round instead, the last answer stayed 4 s after the beat and the
    /// stuck-beat rule always came first (Gemini's review).
    /// </summary>
    [Fact]
    public void Pings_skipped_on_a_fresh_beat_still_tell_a_window_that_does_not_answer()
    {
        var j = new HangJudge(0);
        const long beat = 0;
        long t = 1000;
        for (; t <= 4000; t += 1000) Assert.Equal(HangVerdict.Fine, j.Observe(t, true, beat, answeredAtMs: beat)); // fresh: no ping
        HangVerdict v = HangVerdict.Fine;
        for (; v == HangVerdict.Fine && t <= 20_000; t += 2000) v = j.Observe(t, false, beat); // pings time out after 1 s
        Assert.Equal(HangVerdict.NotAnswering, v);
    }

    /// <summary>The other case keeps its verdict: a window that answers but has not beaten is a UI thread stuck in a wait.</summary>
    [Fact]
    public void A_window_that_answers_without_beating_is_stuck_after_skipped_pings_too()
    {
        var j = new HangJudge(0);
        const long beat = 0;
        long t = 1000;
        for (; t <= 4000; t += 1000) j.Observe(t, true, beat, answeredAtMs: beat);
        HangVerdict v = HangVerdict.Fine;
        for (; v == HangVerdict.Fine && t <= 20_000; t += 1000) v = j.Observe(t, true, beat);
        Assert.Equal(HangVerdict.UiStuck, v);
    }

    /// <summary>
    /// A minute asleep: the first round after it sees a beat from before the sleep. That is a resume, not a stuck UI
    /// thread (before 2026-10-15 the watchdog put the taskbar back right after the resume).
    /// </summary>
    [Fact]
    public void A_beat_from_before_a_sleep_is_not_a_hang()
    {
        var j = new HangJudge(0);
        Assert.Equal(HangVerdict.Fine, j.Observe(1500, true, 0));
        Assert.Equal(HangVerdict.Resumed, j.Observe(61_500, true, 0));
        Assert.Equal(HangVerdict.Fine, j.Observe(63_000, true, 0));   // the shell has not beaten yet: counted from the resume
        Assert.Equal(HangVerdict.Fine, j.Observe(64_500, true, 63_500));
    }

    [Fact]
    public void A_ping_unanswered_before_a_sleep_is_not_a_hang()
    {
        var j = new HangJudge(0);
        Assert.Equal(HangVerdict.Fine, j.Observe(1500, false, 0));
        Assert.Equal(HangVerdict.Resumed, j.Observe(70_000, false, 0));
        Assert.Equal(HangVerdict.Fine, j.Observe(71_500, false, 0));
    }

    /// <summary>A real hang after the resume is still caught 10 s after the resume.</summary>
    [Fact]
    public void A_hang_after_a_resume_is_caught_ten_seconds_later()
    {
        var j = new HangJudge(0);
        j.Observe(1500, true, 0);
        Assert.Equal(HangVerdict.Resumed, j.Observe(60_000, true, 0));
        Assert.Equal(HangVerdict.Fine, Rounds(j, 61_500, 69_000, true, _ => 0));
        Assert.Equal(HangVerdict.UiStuck, Rounds(j, 70_500, 72_000, true, _ => 0));
    }

    [Fact]
    public void No_answer_for_ten_seconds_is_a_hang()
    {
        var j = new HangJudge(0);
        Assert.Equal(HangVerdict.Fine, Rounds(j, 1500, 9000, false, t => t));
        Assert.Equal(HangVerdict.NotAnswering, Rounds(j, 10_500, 12_000, false, t => t));
    }

    [Fact]
    public void Answering_with_a_stuck_UI_thread_is_a_hang()
    {
        var j = new HangJudge(0);
        Assert.Equal(HangVerdict.UiStuck, Rounds(j, 1500, 12_000, true, _ => 500));
    }

    /// <summary>Two hung pings of a second each make a round of about 2 s: not a sleep.</summary>
    [Fact]
    public void Slow_rounds_of_a_hung_shell_are_not_a_sleep()
    {
        var j = new HangJudge(0);
        var v = HangVerdict.Fine;
        for (long t = 2000; t <= 12_000; t += 2000) v = j.Observe(t, false, 0);
        Assert.Equal(HangVerdict.NotAnswering, v);
    }

    [Fact]
    public void Without_a_heartbeat_only_the_answers_count()
    {
        var j = new HangJudge(0);
        Assert.Equal(HangVerdict.Fine, Rounds(j, 1500, 30_000, true, _ => null));
    }
}
