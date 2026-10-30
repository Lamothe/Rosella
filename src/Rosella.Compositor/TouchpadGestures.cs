using Rosella.Input;

namespace Rosella.Compositor;

/// <summary>
/// Touchpad gestures over multitouch (protocol B) contacts — one fixed,
/// Windows-like policy with no settings: one finger moves the pointer, a
/// quick tap clicks (left; right with two fingers; middle with three), two
/// or more fingers scroll instead of moving the pointer, and a tap followed
/// by a quick second touch that moves drags with the button held. A real
/// button press always beats a pending tap.
///
/// Part of the router's fixed input policy. Not thread-safe by itself — the
/// router serialises every call under its input gate.
/// </summary>
internal sealed class TouchpadGestures
{
    /// <summary>A touch shorter than this, with little travel, is a tap.</summary>
    internal const uint TapTimeoutMs = 300;

    /// <summary>A new touch this soon after a left tap may drag.</summary>
    internal const uint DragWindowMs = 300;

    /// <summary>Travel (device units) beyond which a touch is movement, not a tap.</summary>
    internal const int TapSlop = 40;

    private sealed class Finger
    {
        public int X;

        public int Y;

        public bool HasX;

        public bool HasY;
    }

    private readonly Action<int, int, uint> _move;
    private readonly Action<int, int, uint> _scroll;
    private readonly Action<int, bool, uint> _button;

    private readonly Dictionary<int, Finger> _fingers = new();
    private readonly HashSet<int> _heldPhysical = new();
    private int _slot;
    private int _maxFingers;
    private int _travel;
    private uint _startMs;
    private bool _scrolled;
    private bool _physical;
    private bool _consumed;
    private bool _dragPending;
    private bool _dragging;
    private int _dragSlot = -1;
    private bool _dragArmed;
    private uint _dragArmedMs;
    private int _scrollCarryX;
    private int _scrollCarryY;

    internal TouchpadGestures(Action<int, int, uint> move, Action<int, int, uint> scroll, Action<int, bool, uint> button)
    {
        _move = move;
        _scroll = scroll;
        _button = button;
    }

    /// <summary>ABS_MT_SLOT: selects the finger the next events describe.</summary>
    internal void Slot(int slot) => _slot = slot;

    /// <summary>ABS_MT_TRACKING_ID: zero or above lands a finger, −1 lifts it.</summary>
    internal void TrackingId(int id, uint timeMs)
    {
        if (id < 0)
        {
            Lift(timeMs);
        }
        else
        {
            Land(timeMs);
        }
    }

    internal void PositionX(int value, uint timeMs) => Slide(value, horizontal: true, timeMs);

    internal void PositionY(int value, uint timeMs) => Slide(value, horizontal: false, timeMs);

    /// <summary>
    /// A real button press or release: hardware beats any pending tap. A
    /// press during a held drag hands the drag over to the hardware (the
    /// synthesised button is released first), and anything still physically
    /// held suppresses synthesised clicks until it lifts.
    /// </summary>
    internal void NotePhysicalButton(int code, bool pressed, uint timeMs)
    {
        if (pressed)
        {
            _physical = true;
            _dragArmed = false;
            _dragPending = false;
            _heldPhysical.Add(code);
            if (_dragging)
            {
                _dragging = false;
                _dragSlot = -1;
                _consumed = true;
                _button(InputConstants.ButtonLeft, false, timeMs);
            }
        }
        else
        {
            _heldPhysical.Remove(code);
        }
    }

    /// <summary>VT switch: every held or pending gesture is abandoned.</summary>
    internal void Reset()
    {
        if (_dragging)
        {
            _dragging = false;
            _button(InputConstants.ButtonLeft, false, 0);
        }

        _fingers.Clear();
        _maxFingers = 0;
        _travel = 0;
        _scrolled = false;
        _physical = false;
        _consumed = false;
        _dragPending = false;
        _heldPhysical.Clear();
        _dragSlot = -1;
        _dragArmed = false;
        _scrollCarryX = 0;
        _scrollCarryY = 0;
    }

    private void Land(uint timeMs)
    {
        if (_fingers.Count == 0)
        {
            _startMs = timeMs;
            _maxFingers = 0;
            _travel = 0;
            _scrolled = false;
            _physical = false;
            _consumed = false;
            _scrollCarryX = 0;
            _scrollCarryY = 0;

            // A fresh touch this soon after a left tap may drag with the
            // button held. The press waits for real movement: resting a
            // finger must not click, and a quick second tap is just the
            // second click of a double-tap.
            _dragPending = _dragArmed && Elapsed(_dragArmedMs, timeMs) <= DragWindowMs;
            _dragSlot = _dragPending ? _slot : -1;
            _dragArmed = false;
        }
        else if (_dragPending)
        {
            // A second finger joins the pending drag: let the gesture become
            // ordinary multi-finger business (scroll, or a two-finger tap).
            _dragPending = false;
            _dragSlot = -1;
        }

        _fingers[_slot] = new Finger();
        _maxFingers = Math.Max(_maxFingers, _fingers.Count);
    }

    private void Slide(int value, bool horizontal, uint timeMs)
    {
        if (!_fingers.TryGetValue(_slot, out var finger))
        {
            return; // stray coordinates after the finger is gone
        }

        if (horizontal ? !finger.HasX : !finger.HasY)
        {
            // The first coordinates place the finger; only later ones move.
            if (horizontal)
            {
                finger.X = value;
                finger.HasX = true;
            }
            else
            {
                finger.Y = value;
                finger.HasY = true;
            }

            return;
        }

        int delta;
        if (horizontal)
        {
            delta = value - finger.X;
            finger.X = value;
        }
        else
        {
            delta = value - finger.Y;
            finger.Y = value;
        }

        _travel += Math.Abs(delta);
        if (delta == 0)
        {
            return;
        }

        if (_dragPending && _slot == _dragSlot && _travel > TapSlop)
        {
            // Real movement turns the pending drag into a held button: past
            // the tap slop this is a drag, not a second click. The press
            // hit-tests where the pointer already sits (the tap point).
            _dragPending = false;
            _dragging = true;
            _consumed = true;
            _button(InputConstants.ButtonLeft, true, timeMs);
        }

        if (_dragging)
        {
            if (_slot == _dragSlot)
            {
                _move(horizontal ? delta : 0, horizontal ? 0 : delta, timeMs);
            }

            return;
        }

        if (_fingers.Count == 1)
        {
            _move(horizontal ? delta : 0, horizontal ? 0 : delta, timeMs);
            return;
        }

        // Two or more fingers: the contact pair scrolls at the mean finger
        // rate, so fingers travelling together scroll at their own speed and
        // the pointer stays exactly where it is.
        _scrolled = true;
        var count = _fingers.Count;
        if (horizontal)
        {
            var total = delta + _scrollCarryX;
            var mean = total / count;
            _scrollCarryX = total - (mean * count);
            if (mean != 0)
            {
                _scroll(mean, 0, timeMs);
            }
        }
        else
        {
            var total = delta + _scrollCarryY;
            var mean = total / count;
            _scrollCarryY = total - (mean * count);
            if (mean != 0)
            {
                _scroll(0, mean, timeMs);
            }
        }
    }

    private void Lift(uint timeMs)
    {
        if (!_fingers.Remove(_slot))
        {
            return; // stray up
        }

        if (_dragging)
        {
            if (_slot == _dragSlot)
            {
                _dragging = false;
                _dragSlot = -1;
                _button(InputConstants.ButtonLeft, false, timeMs);
            }

            return; // the drag's press and release were the click
        }

        if (_dragPending && _slot == _dragSlot)
        {
            // Lifted before moving: the drag never happened, and the tap
            // check below decides (a quick one is the second click).
            _dragPending = false;
            _dragSlot = -1;
        }

        if (_fingers.Count > 0)
        {
            return;
        }

        TryTap(timeMs);
    }

    private void TryTap(uint timeMs)
    {
        if (_physical
            || _consumed
            || _scrolled
            || _heldPhysical.Count > 0
            || _travel > TapSlop
            || Elapsed(_startMs, timeMs) > TapTimeoutMs)
        {
            return;
        }

        var code = _maxFingers switch
        {
            1 => InputConstants.ButtonLeft,
            2 => InputConstants.ButtonRight,
            3 => InputConstants.ButtonMiddle,
            _ => (ushort)0,
        };

        if (code == 0)
        {
            return;
        }

        _button(code, true, timeMs);
        _button(code, false, timeMs);
        if (code == InputConstants.ButtonLeft)
        {
            // The next touch this soon may drag with the button held.
            _dragArmed = true;
            _dragArmedMs = timeMs;
        }
    }

    /// <summary>Wrap-safe milliseconds between two kernel timestamps.</summary>
    private static uint Elapsed(uint fromMs, uint toMs) => unchecked(toMs - fromMs);
}
