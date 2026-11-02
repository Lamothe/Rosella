namespace Rosella.Compositor;

/// <summary>
/// Recognises a deliberate shake of the pointer, the "where is my cursor?"
/// gesture: a few fast back-and-forth strokes in quick succession, which no
/// ordinary pointing produces. Each axis is tracked on its own, so a
/// horizontal shake over a page counts even when the hand adds a little
/// vertical wander.
/// <para>
/// Thresholds are logical pixels of travel per stroke and milliseconds of
/// event time (evdev's clock, so a test drives it directly). A pause longer
/// than <see cref="PauseMilliseconds"/> starts over: clicking around must
/// never add up to a shake.
/// </para>
/// </summary>
internal sealed class CursorShake
{
    /// <summary>Travel in one direction before the turn back counts as a stroke.</summary>
    internal const int MinimumLeg = 40;

    /// <summary>Strokes needed before the gesture fires (two full shakes).</summary>
    internal const int ReversalsToTrigger = 3;

    /// <summary>The strokes must happen inside this window.</summary>
    internal const uint WindowMilliseconds = 700;

    /// <summary>A gap this long means the person stopped; counters restart.</summary>
    internal const uint PauseMilliseconds = 400;

    private int _signX;
    private int _legX;
    private int _reversalsX;
    private uint _firstX;

    private int _signY;
    private int _legY;
    private int _reversalsY;
    private uint _firstY;

    private uint _last;
    private bool _started;

    /// <summary>
    /// Adds one motion sample (logical pixels) and reports whether it
    /// completed a shake. The caller resets nothing: a fired gesture starts
    /// over, so holding the shake does not retrigger on every stroke.
    /// </summary>
    internal bool Add(int dx, int dy, uint time)
    {
        if (!_started || unchecked(time - _last) > PauseMilliseconds)
        {
            Reset();
            _started = true;
        }

        _last = time;
        var shake = Track(dx, ref _signX, ref _legX, ref _reversalsX, ref _firstX, time)
            | Track(dy, ref _signY, ref _legY, ref _reversalsY, ref _firstY, time);
        if (shake)
        {
            Reset();
        }

        return shake;
    }

    private static bool Track(int delta, ref int sign, ref int leg, ref int reversals, ref uint first, uint time)
    {
        if (delta == 0)
        {
            return false;
        }

        var direction = Math.Sign(delta);
        if (direction == sign)
        {
            leg += Math.Abs(delta);
            return false;
        }

        // A turn only counts once the previous leg was long enough to be a
        // stroke rather than a wobble.
        var counted = sign != 0 && leg >= MinimumLeg;
        sign = direction;
        leg = Math.Abs(delta);
        if (!counted)
        {
            return false;
        }

        if (reversals == 0 || unchecked(time - first) > WindowMilliseconds)
        {
            reversals = 1;
            first = time;
        }
        else
        {
            reversals++;
        }

        return reversals >= ReversalsToTrigger;
    }

    /// <summary>Forgets the strokes so far; a click or a pause starts over.</summary>
    internal void Reset()
    {
        _signX = 0;
        _legX = 0;
        _reversalsX = 0;
        _firstX = 0;
        _signY = 0;
        _legY = 0;
        _reversalsY = 0;
        _firstY = 0;
    }
}