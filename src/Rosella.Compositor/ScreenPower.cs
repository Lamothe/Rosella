namespace Rosella.Compositor;

/// <summary>What the display should do about power on the next loop turn.</summary>
internal enum ScreenPowerAction
{
    /// <summary>No change.</summary>
    None,

    /// <summary>Stop driving the panel (DPMS off).</summary>
    Blank,

    /// <summary>Resume driving the panel (DPMS on).</summary>
    Unblank,
}

/// <summary>
/// Screen blanking: the display stops being driven after a fixed idle
/// period, and any input brings it straight back. This is a real power
/// state (DPMS off), not a black picture — the panel is not being lit, so
/// it is saved from burn-in and the machine draws less power.
///
/// The policy is deliberately dumb and has no settings: input keeps the
/// screen alive, silence blanks it. It holds no DRM handles — the render
/// loop owns those and acts on what this class decides, so every mode-set
/// still happens on exactly one thread.
/// </summary>
internal sealed class ScreenPower
{
    /// <summary>How long the machine must sit idle before the screen blanks.</summary>
    internal static readonly TimeSpan IdleBeforeBlank = TimeSpan.FromMinutes(5);

    private readonly object _gate = new();
    private readonly long _idleMilliseconds = (long)IdleBeforeBlank.TotalMilliseconds;
    private long _lastActivity;
    private bool _blanked;

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
    internal ScreenPowerAction Tick()
    {
        lock (_gate)
        {
            if (_blanked)
            {
                return ScreenPowerAction.None;
            }

            if (_tickCount() - _lastActivity >= _idleMilliseconds)
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
