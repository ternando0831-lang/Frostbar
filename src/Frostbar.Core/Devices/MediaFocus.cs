namespace Frostbar.Core.Devices;

/// <summary>
/// Which media session the panels show and send their buttons to. Windows makes another session its current one as
/// the current one pauses, when another plays: the Control Center followed it, and its play button then went to that
/// other app (2026-10-18, cc-media-other: 5 ms after the pause; cc-media had failed twice on 2026-10-17 at its second
/// press, when something else may have been playing). macOS's Now Playing keeps the app paused from it. So a session pressed from a panel is held: shown
/// while Windows' current session is one that already played at the press (or none); let go when it is gone, when
/// Windows' current session is it again, or when another session becomes current that did not play at the press
/// (something started).
/// </summary>
/// <typeparam name="T">A session; <paramref name="same"/> in the constructor says which are the same one.</typeparam>
public sealed class MediaFocus<T>(IEqualityComparer<T>? same = null) where T : class
{
    readonly IEqualityComparer<T> _same = same ?? EqualityComparer<T>.Default;
    readonly List<T> _playingAtPress = new();
    T? _held;

    /// <summary>The session held (pressed last from a panel), or none.</summary>
    public T? Held => _held;

    /// <summary>A panel's button pressed for <paramref name="session"/>; <paramref name="playing"/>: the sessions that played at that moment.</summary>
    public void Pressed(T session, IEnumerable<T> playing)
    {
        _held = session;
        _playingAtPress.Clear();
        _playingAtPress.AddRange(playing.Where(s => !_same.Equals(s, session)));
    }

    /// <summary>
    /// The session to show and act on, now that Windows' current session is <paramref name="current"/> (null: none)
    /// among <paramref name="all"/>.
    /// </summary>
    public T? Choose(T? current, IEnumerable<T> all)
    {
        if (_held is not { } held) { Why = "nothing held"; return current; }
        if (!all.Contains(held, _same)) { Why = "the held one is gone"; Let(); return current; }
        if (current != null && _same.Equals(current, held)) { Why = "the held one is current again"; Let(); return current; }
        if (current == null) { Why = "held: no current session"; return held; }
        if (_playingAtPress.Contains(current, _same)) { Why = "held: the current one played at the press"; return held; }
        Why = "another one started";
        Let();
        return current;
    }

    /// <summary>Why the last <see cref="Choose"/> chose so (diagnostics, the log).</summary>
    public string Why { get; private set; } = "";

    void Let()
    {
        _held = null;
        _playingAtPress.Clear();
    }
}
