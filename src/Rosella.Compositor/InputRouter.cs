using Rosella.Input;

namespace Rosella.Compositor;

/// <summary>
/// The input policy, and deliberately the only policy there is: the cursor
/// moves and hit-tests windows, a click focuses and raises, keys go to the
/// keyboard focus, and two keybindings exist (Alt+F4 = close window,
/// Super+Esc = quit). Nothing is configurable.
/// </summary>
internal sealed class InputRouter
{
    private readonly Scene _scene;
    private readonly SeatState _seat;
    private readonly Action _quit;
    private readonly object _gate = new();
    private readonly HashSet<int> _pressedKeys = new();
    private int _cursorX;
    private int _cursorY;

    // Multitouch slot state (protocol B): slot -> last per-axis position
    // with per-axis seeding (the first coordinates after finger-down are a
    // placement, not a delta).
    private int _mtSlot;
    private readonly Dictionary<int, (int X, int Y, bool HasX, bool HasY)> _mtFingers = new();
    private bool _capsLocked;
    private bool _numLocked;
    private int _lastDepressed = -1;
    private int _lastLocked = -1;

    internal InputRouter(Scene scene, SeatState seat, Action quit)
    {
        _scene = scene;
        _seat = seat;
        _quit = quit;
    }

    /// <summary>Where the arrow's hotspot sits.</summary>
    internal (int X, int Y) Cursor
    {
        get
        {
            lock (_gate)
            {
                return (_cursorX, _cursorY);
            }
        }
    }

    /// <summary>Places the cursor outright (startup centring).</summary>
    internal void PlaceCursor(int x, int y)
    {
        lock (_gate)
        {
            _cursorX = x;
            _cursorY = y;
            _scene.Cursor = (x, y);
        }
    }

    /// <summary>Processes one evdev event (thread-safe; called by device readers).</summary>
    internal void ProcessEvent(InputEvent e)
    {
        lock (_gate)
        {
            switch (e.Type)
            {
                case InputConstants.EventRelative when e.Code == InputConstants.RelativeX:
                    MoveCursor(e.Value, 0, e.TimeMs);
                    break;

                case InputConstants.EventRelative when e.Code == InputConstants.RelativeY:
                    MoveCursor(0, e.Value, e.TimeMs);
                    break;

                case InputConstants.EventRelative when e.Code == InputConstants.RelativeWheel:
                    SendAxis(axis: 0, value: -e.Value * 40, e.TimeMs);
                    break;

                case InputConstants.EventRelative when e.Code == InputConstants.RelativeHWheel:
                    SendAxis(axis: 1, value: e.Value * 40, e.TimeMs);
                    break;

                case InputConstants.EventAbsolute:
                    HandleTouchpadAbs(e);
                    break;

                case InputConstants.EventKey when e.Code is InputConstants.ButtonLeft
                    or InputConstants.ButtonRight
                    or InputConstants.ButtonMiddle:
                    HandleButton(e);
                    break;

                case InputConstants.EventKey when e.Code >= 0x100:
                    // Other BTN_* codes (BTN_TOOL_FINGER, BTN_TOUCH, ...) are
                    // touch state, not keys or buttons: ignored.
                    break;

                case InputConstants.EventKey:
                    HandleKey(e);
                    break;
            }
        }
    }

    /// <summary>
    /// Multitouch (protocol B) trackpad: ABS_MT_POSITION updates of the
    /// active finger become relative pointer motion (deltas between
    /// consecutive positions), matching how a touchpad moves a cursor.
    /// </summary>
    private void HandleTouchpadAbs(InputEvent e)
    {
        switch (e.Code)
        {
            case InputConstants.AbsMtSlot:
                _mtSlot = e.Value;
                return;

            case InputConstants.AbsMtTrackingId:
                if (e.Value == InputConstants.TrackingIdUp)
                {
                    _mtFingers.Remove(_mtSlot);
                }
                else
                {
                    // Down: nothing seeded, so the first coordinates of both
                    // axes place the finger without moving the cursor.
                    _mtFingers[_mtSlot] = (0, 0, false, false);
                }

                return;

            case InputConstants.AbsMtPositionX or InputConstants.AbsMtPositionY:
                if (!_mtFingers.TryGetValue(_mtSlot, out var last))
                {
                    // No tracking id seen for this slot: ignore stray coords.
                    return;
                }

                if (e.Code == InputConstants.AbsMtPositionX)
                {
                    if (last.HasX)
                    {
                        MoveCursor(e.Value - last.X, 0, e.TimeMs);
                    }

                    _mtFingers[_mtSlot] = (e.Value, last.Y, true, last.HasY);
                }
                else
                {
                    if (last.HasY)
                    {
                        MoveCursor(0, e.Value - last.Y, e.TimeMs);
                    }

                    _mtFingers[_mtSlot] = (last.X, e.Value, last.HasX, true);
                }

                return;
        }
    }

    private void MoveCursor(int dx, int dy, uint time)
    {
        var (outputWidth, outputHeight) = _scene.OutputSize;
        _cursorX = Math.Clamp(_cursorX + dx, 0, Math.Max(0, outputWidth - 1));
        _cursorY = Math.Clamp(_cursorY + dy, 0, Math.Max(0, outputHeight - 1));
        _scene.Cursor = (_cursorX, _cursorY);

        var target = _scene.HitTest(_cursorX, _cursorY);
        _seat.SetPointerFocus(target, LocalX(target), LocalY(target));
        if (target is not null)
        {
            _seat.SendMotion(target, LocalX(target), LocalY(target), time);
            _seat.SendPointerFrame(target);
        }
    }

    private void HandleButton(InputEvent e)
    {
        var pressed = e.Value != InputConstants.KeyReleased;

        // The window under the cursor is authoritative even if the cursor
        // never moved since it appeared (click-to-focus, raise on press).
        var target = _scene.HitTest(_cursorX, _cursorY);
        _seat.SetPointerFocus(target, LocalX(target), LocalY(target));
        if (pressed && target is not null)
        {
            _scene.Raise(target);
            _seat.SetKeyboardFocus(target, _pressedKeys);
        }

        if (_seat.PointerFocus is { } pointerTarget)
        {
            _seat.SendButton(pointerTarget, e.Code, pressed, e.TimeMs);
            _seat.SendPointerFrame(pointerTarget);
        }
    }

    private void HandleKey(InputEvent e)
    {
        // Kernel autorepeat is not forwarded: clients repeat on their own
        // schedule via repeat_info, and re-sending press would violate the
        // "no release without a press" rule for focused-mid-type clients.
        if (e.Value == InputConstants.KeyRepeated)
        {
            return;
        }

        var pressed = e.Value != InputConstants.KeyReleased;

        if (pressed)
        {
            _pressedKeys.Add(e.Code);
        }
        else
        {
            _pressedKeys.Remove(e.Code);
        }

        if (e.Value == InputConstants.KeyPressed)
        {
            if (e.Code == InputConstants.KeyF4 && AltDown)
            {
                _seat.KeyboardFocus?.XdgRole?.Close();
            }

            if (e.Code == InputConstants.KeyEsc && SuperDown)
            {
                _quit();
            }

            if (e.Code == InputConstants.KeyCapsLock)
            {
                _capsLocked = !_capsLocked;
            }

            if (e.Code == InputConstants.KeyNumLock)
            {
                _numLocked = !_numLocked;
            }
        }

        if (_seat.KeyboardFocus is { } focus)
        {
            _seat.SendKey(focus, e.Code, pressed, e.TimeMs);
        }

        // The mask is tracked even unfocused; sent when there is a focus.
        SendModifiers(_seat.KeyboardFocus);
    }

    private bool AltDown => _pressedKeys.Contains(InputConstants.KeyLeftAlt) || _pressedKeys.Contains(InputConstants.KeyRightAlt);

    private bool SuperDown => _pressedKeys.Contains(InputConstants.KeyLeftMeta) || _pressedKeys.Contains(InputConstants.KeyRightMeta);

    /// <summary>Sends wl_keyboard.modifiers when the mask changed.</summary>
    private void SendModifiers(SurfaceHandler? target)
    {
        var depressed = 0;
        if (_pressedKeys.Contains(InputConstants.KeyLeftShift) || _pressedKeys.Contains(InputConstants.KeyRightShift))
        {
            depressed |= 1 << 0;
        }

        if (_pressedKeys.Contains(InputConstants.KeyLeftCtrl) || _pressedKeys.Contains(InputConstants.KeyRightCtrl))
        {
            depressed |= 1 << 2;
        }

        if (AltDown)
        {
            depressed |= 1 << 3;
        }

        if (SuperDown)
        {
            depressed |= 1 << 6;
        }

        var locked = 0;
        if (_capsLocked)
        {
            locked |= 1 << 1;
        }

        if (_numLocked)
        {
            locked |= 1 << 4;
        }

        if (depressed == _lastDepressed && locked == _lastLocked)
        {
            return;
        }

        _lastDepressed = depressed;
        _lastLocked = locked;
        _seat.SendModifiers(target, depressed, locked);
    }

    private void SendAxis(int axis, int value, uint time)
    {
        if (_seat.PointerFocus is { } target)
        {
            _seat.SendAxis(target, axis, value, time);
            _seat.SendPointerFrame(target);
        }
    }

    private int LocalX(SurfaceHandler? target) => target is null ? 0 : _cursorX - target.Position.X;

    private int LocalY(SurfaceHandler? target) => target is null ? 0 : _cursorY - target.Position.Y;
}
