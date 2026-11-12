namespace Frostbar.Core.Shell;

public enum HangVerdict
{
    /// <summary>The shell answers and its UI thread beats.</summary>
    Fine,
    /// <summary>The PC slept (or this process did not run): counting starts again now.</summary>
    Resumed,
    /// <summary>No answer to the pings for <see cref="HangJudge.HangMs"/>.</summary>
    NotAnswering,
    /// <summary>It answers, but its UI thread has not beaten for <see cref="HangJudge.HangMs"/> (stuck in a CLR wait).</summary>
    UiStuck,
}

/// <summary>
/// The watchdog's judgement of the shell, one observation per round of its loop (a 1 s wait, then a ping of up to
/// 1 s). The clock (Environment.TickCount64) goes on while the PC sleeps: a round that comes more than
/// <see cref="SleepGapMs"/> after the last means neither process ran, and the counting starts again from now, so a
/// beat from before the sleep is not taken for a stuck UI thread. Before 2026-10-15 a sleep longer than 10 s could
/// make the watchdog put the taskbar back right after the resume (and the shell hide it again a second later).
/// A real hang after the resume is still caught 10 s after it.
/// </summary>
public sealed class HangJudge
{
    public const long HangMs = 10_000, SleepGapMs = 5_000;
    long _lastRound, _lastAnswer, _resumedAt = long.MinValue;

    public HangJudge(long nowMs)
    {
        _lastRound = nowMs;
        _lastAnswer = nowMs;
    }

    /// <param name="beatMs">The UI thread's last beat (same clock), or null when the shell has no beat to read.</param>
    /// <param name="answeredAtMs">
    /// When the answer came, if not now: a round that skipped its ping on a fresh beat counts as answered at that beat.
    /// </param>
    public HangVerdict Observe(long nowMs, bool answered, long? beatMs, long? answeredAtMs = null)
    {
        if (nowMs - _lastRound > SleepGapMs)
        {
            _lastRound = _lastAnswer = _resumedAt = nowMs;
            return HangVerdict.Resumed;
        }
        _lastRound = nowMs;
        if (answered) _lastAnswer = Math.Max(_lastAnswer, answeredAtMs ?? nowMs);
        long beat = Math.Max(beatMs ?? nowMs, _resumedAt); // a beat from before the sleep counts from the resume
        if (nowMs - _lastAnswer > HangMs) return HangVerdict.NotAnswering;
        if (nowMs - beat > HangMs) return HangVerdict.UiStuck;
        return HangVerdict.Fine;
    }

    /// <summary>Nothing to judge for now (no heartbeat window yet, or replaced by another instance): counted from now.</summary>
    public void Skip(long nowMs)
    {
        _lastRound = nowMs;
        _lastAnswer = nowMs;
    }
}
