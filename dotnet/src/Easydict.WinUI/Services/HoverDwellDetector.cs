namespace Easydict.WinUI.Services;

/// <summary>
/// Pure "pointer rest" (dwell) detector used by hover word lookup.
/// Tracks the point where the pointer came to rest and reports, at most once per rest,
/// when the pointer has stayed there for <see cref="DwellMs"/> while the trigger condition
/// (e.g. the configured modifier key is held) is satisfied.
/// All timing is passed in explicitly so the class is testable without timers or Win32.
/// </summary>
public sealed class HoverDwellDetector
{
    /// <summary>
    /// Time the pointer has to rest on a spot before a lookup fires.
    /// </summary>
    public const int DwellMs = 350;

    /// <summary>
    /// Movement (in pixels) that is treated as hand jitter and does not restart the rest clock.
    /// </summary>
    public const int JitterTolerancePx = 3;

    /// <summary>
    /// The pointer has to move at least this far away from the last fired point before the
    /// detector fires again, so a single word does not trigger repeated lookups.
    /// </summary>
    public const int RearmDistancePx = 8;

    private MouseHookService.POINT _restPoint;
    private long _restStartTicks;
    private bool _hasRest;
    private bool _armed;
    private bool _hasFired;
    private MouseHookService.POINT _lastFiredPoint;

    /// <summary>Whether the pointer currently rests somewhere (a rest point is tracked).</summary>
    public bool IsResting => _hasRest;

    /// <summary>Whether the current rest can still fire.</summary>
    public bool IsArmed => _hasRest && _armed;

    /// <summary>The point where the pointer currently rests.</summary>
    public MouseHookService.POINT RestPoint => _restPoint;

    /// <summary>
    /// Feed a pointer movement. Movement within <see cref="JitterTolerancePx"/> of the current
    /// rest point keeps the rest; anything larger starts a new rest at <paramref name="pt"/>.
    /// </summary>
    public void OnMouseMove(MouseHookService.POINT pt, long nowTicks)
    {
        if (_hasRest && DistanceSquared(pt, _restPoint) <= JitterTolerancePx * JitterTolerancePx)
        {
            return;
        }

        _restPoint = pt;
        _restStartTicks = nowTicks;
        _hasRest = true;
        _armed = !_hasFired || DistanceSquared(pt, _lastFiredPoint) >= (long)RearmDistancePx * RearmDistancePx;
    }

    /// <summary>
    /// Evaluate the detector. Fires (once per rest) when the pointer has rested for at least
    /// <see cref="DwellMs"/> and <paramref name="triggerSatisfied"/> is true. The caller checks
    /// the trigger key's minimum hold duration before passing true.
    /// </summary>
    public DwellResult Tick(long nowTicks, bool triggerSatisfied)
    {
        if (!_hasRest || !_armed || !triggerSatisfied)
        {
            return default;
        }

        if (nowTicks - _restStartTicks < DwellMs)
        {
            return default;
        }

        _armed = false;
        _hasFired = true;
        _lastFiredPoint = _restPoint;
        return new DwellResult(true, _restPoint);
    }

    /// <summary>
    /// Allow the current rest to fire again (e.g. after the popup was dismissed or the trigger
    /// key was released and pressed again over the same word).
    /// </summary>
    public void Rearm()
    {
        _armed = _hasRest;
    }

    /// <summary>
    /// Forget the current rest and fired state.
    /// </summary>
    public void Reset()
    {
        _hasRest = false;
        _armed = false;
        _hasFired = false;
        _restPoint = default;
        _lastFiredPoint = default;
        _restStartTicks = 0;
    }

    private static long DistanceSquared(MouseHookService.POINT a, MouseHookService.POINT b)
    {
        long dx = a.x - b.x;
        long dy = a.y - b.y;
        return dx * dx + dy * dy;
    }
}

/// <summary>
/// Result of <see cref="HoverDwellDetector.Tick"/>.
/// </summary>
public readonly record struct DwellResult(bool Fired, MouseHookService.POINT Point);
