using Frostbar.Core.Shell;

namespace Frostbar.Core.Tests;

public sealed class ImeCheckTests
{
    /// <summary>Drives the rule like the ticker: every <paramref name="stepMs"/>, with input at the given times.</summary>
    static List<long> Asked(long untilMs, long stepMs, Func<long, bool> inputAt, long start = 0)
    {
        var check = new ImeCheck();
        var asked = new List<long>();
        uint stamp = 7;
        long lastInput = -1_000_000;
        for (long t = start; t <= untilMs; t += stepMs)
        {
            if (inputAt(t)) { stamp++; lastInput = t; }
            if (check.Due(t, stamp, t - lastInput, force: false)) asked.Add(t);
        }
        return asked;
    }

    [Fact]
    public void The_first_look_asks()
    {
        var check = new ImeCheck();
        Assert.True(check.Due(1000, 5, 60_000, force: false));
    }

    /// <summary>2026-10-15: it asked every 5 s with nobody at the PC (37 questions in 3 minutes).</summary>
    [Fact]
    public void Without_input_it_asks_every_30_s_only()
    {
        var asked = Asked(180_000, 200, _ => false);
        Assert.Equal([0L, 30_000, 60_000, 90_000, 120_000, 150_000, 180_000], asked.ToArray());
    }

    [Fact]
    public void A_key_is_followed_by_one_question_once_input_has_settled()
    {
        // A key at 1000 ms; the ticker looks every 200 ms.
        var asked = Asked(5000, 200, t => t == 1000);
        Assert.Equal([0L, 1200], asked.ToArray()); // 200 ms after the key: the first look once it settled (SettleMs)
    }

    [Fact]
    public void Typing_goes_on_with_at_most_one_question_a_second()
    {
        // Keys every 100 ms from 1000 to 4900 ms, then nothing.
        var asked = Asked(6000, 100, t => t >= 1000 && t < 5000);
        var during = asked.Where(t => t >= 1000 && t < 5000).ToList();
        Assert.InRange(during.Count, 3, 4); // once a second while typing
        for (int i = 1; i < during.Count; i++) Assert.True(during[i] - during[i - 1] >= ImeCheck.BusyMs);
        Assert.Contains(asked, t => t >= 5000 && t <= 5200); // and once more after the last key, when it settled
    }

    [Fact]
    public void Told_to_look_it_asks_at_once()
    {
        var check = new ImeCheck();
        Assert.True(check.Due(0, 3, 50_000, force: false));
        Assert.False(check.Due(500, 3, 50_500, force: false));
        Assert.True(check.Due(600, 3, 50_600, force: true));
    }

    /// <summary>A question at the moment of a key reads the mode before the IME changed it: one more once it settled.</summary>
    [Fact]
    public void A_question_asked_while_typing_does_not_answer_that_input()
    {
        var check = new ImeCheck();
        Assert.True(check.Due(0, 1, 60_000, force: false));
        Assert.True(check.Due(1000, 2, 0, force: true)); // the app in front changed, a key just down
        Assert.True(check.Due(1200, 2, 200, force: false)); // settled: asked again
        Assert.False(check.Due(1400, 2, 400, force: false));
    }

    [Fact]
    public void The_same_input_is_not_asked_about_twice()
    {
        var check = new ImeCheck();
        Assert.True(check.Due(0, 3, 10_000, force: false));
        Assert.True(check.Due(1000, 4, 200, force: false)); // new input, settled
        Assert.False(check.Due(1200, 4, 400, force: false));
        Assert.False(check.Due(9000, 4, 8200, force: false));
    }
}
