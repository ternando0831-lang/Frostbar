namespace Frostbar.Core.Shell;

/// <summary>
/// When the menu bar asks the app in front for its input mode (two messages to another process, up to 100 ms each when
/// that app hangs). The mode changes with input (the 半角/全角 key, the IME's own menu), so it asks once input has
/// stopped for <see cref="SettleMs"/>; while input has gone on for <see cref="BusyMs"/> or more, at most once every
/// <see cref="BusyMs"/>; at once when told (the app in front changed); and every <see cref="IdleMs"/> without input as
/// a safety. A question asked before the input settled does not count for it: the mode may change after the key.
/// Until 2026-10-15 it asked every 300 ms after any input and every 5 s without any: 37 questions in 3 idle minutes,
/// the ticker's largest job.
/// </summary>
public sealed class ImeCheck
{
    public const long SettleMs = 100, BusyMs = 1000, IdleMs = 30_000;

    uint _input;
    bool _seen, _pending, _askedOnce;
    long _pendingSince, _asked;

    /// <param name="now">The clock (Environment.TickCount64).</param>
    /// <param name="lastInput">The last input's time stamp (GetLastInputInfo).</param>
    /// <param name="sinceInput">Milliseconds since that input.</param>
    /// <param name="force">Asked to look now (the app in front changed).</param>
    /// <returns>Whether to ask the app now; when true, the question counts as asked.</returns>
    public bool Due(long now, uint lastInput, long sinceInput, bool force)
    {
        if (!_seen || lastInput != _input)
        {
            if (_seen && !_pending) { _pending = true; _pendingSince = now; } // the first stamp read is not new input
            _input = lastInput;
            _seen = true;
        }
        bool settled = _pending && sinceInput >= SettleMs;
        bool busy = _pending && now - _pendingSince >= BusyMs && now - _asked >= BusyMs;
        bool due = force || !_askedOnce || settled || busy || now - _asked >= IdleMs;
        if (!due) return false;
        _asked = now;
        _askedOnce = true;
        if (sinceInput >= SettleMs) _pending = false; // asked after the input settled: that input is answered
        return true;
    }
}
