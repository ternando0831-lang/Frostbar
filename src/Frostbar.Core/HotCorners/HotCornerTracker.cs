namespace Frostbar.Core.HotCorners;

public enum Corner { None, TopLeft, TopRight, BottomLeft, BottomRight }

/// <summary>A screen's edges in pixels (right and bottom exclusive).</summary>
public readonly record struct ScreenEdges(int Left, int Top, int Right, int Bottom);

/// <summary>
/// Hot corners (M7): a corner fires once the pointer has stayed in it for the dwell time — not while a mouse button is
/// down (a window dragged into the corner), not while a fullscreen app is in front, and only with the modifier held
/// when one is set. After firing it does not fire again until the pointer has gone a little way off (so resting in the
/// corner does not repeat it). The pointer is "in" a corner within <see cref="Reach"/> pixels of its last pixel.
/// </summary>
public sealed class HotCornerTracker
{
    public const int Reach = 2, RearmDistance = 20;

    readonly int _dwellMs;
    Corner _in = Corner.None, _fired = Corner.None;
    long _since, _lastLook = long.MinValue;

    public HotCornerTracker(int dwellMs) => _dwellMs = Math.Max(0, dwellMs);

    /// <summary>The corner the pointer is waiting in (diagnostics), and since when.</summary>
    public (Corner corner, long since) Waiting => (_in, _since);

    /// <summary>Nothing pending: no corner being waited in, none waiting to be re-armed (a look far from the corners can be skipped).</summary>
    public bool Idle => _in == Corner.None && _fired == Corner.None;

    /// <summary>A look that found the pointer far from every corner while <see cref="Idle"/> (cheaper than <see cref="Update"/>):
    /// the pointer's arrival in a corner is not dated before it.</summary>
    public void Away(long nowMs) => _lastLook = nowMs;

    /// <summary>
    /// One look at the pointer. Returns the corner that fires now, or <see cref="Corner.None"/>.
    /// <paramref name="active"/> tells which corners have something to do. <paramref name="enteredMs"/>, when known, is
    /// when the pointer got there (the last input's time): the dwell counts from then, not from this look, so looking
    /// rarely does not lengthen it (2026-10-12: looks 100 ms apart made it 330 ms) — but never from before the last look,
    /// which saw the pointer elsewhere.
    /// </summary>
    public Corner Update(int x, int y, ScreenEdges s, long nowMs, bool buttonDown, bool modifierOk, bool fullScreen, Func<Corner, bool> active,
        long? enteredMs = null)
    {
        long lastLook = _lastLook;
        _lastLook = nowMs;
        // A corner that fired waits until the pointer has gone far enough from it.
        if (_fired != Corner.None)
        {
            var (fx, fy) = Point(_fired, s);
            if (Math.Max(Math.Abs(x - fx), Math.Abs(y - fy)) < RearmDistance) { _in = Corner.None; return Corner.None; }
            _fired = Corner.None;
        }
        var c = At(x, y, s);
        if (c == Corner.None || !active(c) || buttonDown || fullScreen || !modifierOk)
        {
            _in = Corner.None;
            return Corner.None;
        }
        if (c != _in)
        {
            _in = c;
            _since = enteredMs is long e ? Math.Clamp(e, lastLook == long.MinValue ? e : lastLook, nowMs) : nowMs;
        }
        if (nowMs - _since < _dwellMs) return Corner.None;
        _fired = c;
        _in = Corner.None;
        return c;
    }

    /// <summary>The corner (<paramref name="x"/>, <paramref name="y"/>) is in, if any.</summary>
    public static Corner At(int x, int y, ScreenEdges s)
    {
        bool left = x < s.Left + Reach, right = x >= s.Right - Reach, top = y < s.Top + Reach, bottom = y >= s.Bottom - Reach;
        return (left, right, top, bottom) switch
        {
            (true, _, true, _) => Corner.TopLeft,
            (_, true, true, _) => Corner.TopRight,
            (true, _, _, true) => Corner.BottomLeft,
            (_, true, _, true) => Corner.BottomRight,
            _ => Corner.None,
        };
    }

    /// <summary>The corner's own pixel.</summary>
    public static (int x, int y) Point(Corner c, ScreenEdges s) => c switch
    {
        Corner.TopLeft => (s.Left, s.Top),
        Corner.TopRight => (s.Right - 1, s.Top),
        Corner.BottomLeft => (s.Left, s.Bottom - 1),
        _ => (s.Right - 1, s.Bottom - 1),
    };
}
