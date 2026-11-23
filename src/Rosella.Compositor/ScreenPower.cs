namespace Rosella.Compositor;

/// <summary>What the display should do about power on the next loop turn.</summary>
internal enum ScreenPowerAction
{
    /// <summary>No change.</summary>
    None,

    /// <summary>Blank this display and start its standby transition.</summary>
    Blank,

    /// <summary>Wake this display.</summary>
    Unblank,
}

/// <summary>
/// Tracks one desktop's input and idle inhibition. The scanout owns the
/// display's power controls and reconciles them with this policy.
/// </summary>
internal sealed class ScreenPower
{
    /// <summary>How long this desktop must sit idle before its display blanks.</summary>
    internal static readonly TimeSpan IdleBeforeBlank = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly long _idleMilliseconds = (long)IdleBeforeBlank.TotalMilliseconds;
    private long _lastActivity;
    private bool _blanked;
    private bool _inhibited;

    internal ScreenPower(Func<long>? tickCount = null)
    {
        _tickCount = tickCount ?? (() => Environment.TickCount64);
        _lastActivity = _tickCount();
    }

    private readonly Func<long> _tickCount;

    /// <summary>Whether the panel is currently unlit.</summary>
    internal bool IsBlanked
    {
        get
        {
            lock (_gate)
            {
                return _blanked;
            }
        }
    }

    /// <summary>
    /// How long until the idle policy would blank the panel (zero once it is
    /// due). The render loop sleeps no longer than this, so going dark is
    /// never late — the loop's other idle bound is the clock face, which can
    /// be most of a minute away.
    /// </summary>
    internal TimeSpan? UntilIdle
    {
        get
        {
            lock (_gate)
            {
                if (_blanked)
                {
                    return TimeSpan.Zero;
                }

                if (_inhibited)
                {
                    return null;
                }

                var remaining = _idleMilliseconds - (_tickCount() - _lastActivity);
                return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(remaining);
            }
        }
    }

    /// <summary>
    /// Records that a person touched something. Returns <see
    /// cref="ScreenPowerAction.Unblank"/> when this woke the screen, so the
    /// caller knows to redraw immediately.
    /// </summary>
    internal ScreenPowerAction MarkActivity()
    {
        lock (_gate)
        {
            _lastActivity = _tickCount();
            if (!_blanked)
            {
                return ScreenPowerAction.None;
            }

            _blanked = false;
            return ScreenPowerAction.Unblank;
        }
    }

    /// <summary>
    /// Decides what to do at this instant. Called once per render-loop turn;
    /// the caller performs the resulting power change and, for a blank, stops
    /// compositing until activity returns.
    /// </summary>
    internal ScreenPowerAction Tick(bool inhibited = false)
    {
        lock (_gate)
        {
            var now = _tickCount();
            if (_inhibited != inhibited)
            {
                _inhibited = inhibited;
                _lastActivity = now;
            }

            if (_blanked || _inhibited)
            {
                return ScreenPowerAction.None;
            }

            if (now - _lastActivity >= _idleMilliseconds)
            {
                _blanked = true;
                return ScreenPowerAction.Blank;
            }

            return ScreenPowerAction.None;
        }
    }

    /// <summary>
    /// Forces the screen dark (or lit) regardless of idleness — the control
    /// socket's hand, so blanking can be exercised without waiting out the
    /// idle period. Returns the change that actually happened.
    /// </summary>
    internal ScreenPowerAction Force(bool blank)
    {
        lock (_gate)
        {
            if (_blanked == blank)
            {
                return ScreenPowerAction.None;
            }

            _blanked = blank;

            // A forced wake must survive the very next Tick: the machine is
            // still idle, so without this the idle clock would re-blank it
            // immediately and the wake would be silently undone.
            _lastActivity = _tickCount();
            return blank ? ScreenPowerAction.Blank : ScreenPowerAction.Unblank;
        }
    }
}
