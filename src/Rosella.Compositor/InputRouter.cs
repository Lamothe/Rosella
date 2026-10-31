using Rosella.Input;

namespace Rosella.Compositor;

/// <summary>
/// The input policy, and deliberately the only policy there is: the cursor
/// moves and hit-tests windows, a click focuses and raises, keys go to the
/// keyboard focus, and the keybindings are fixed: Alt+F4 closes the focused
/// window, Super+Esc quits. The Super key by itself is unbound. On the
/// touchpad, one finger points, a quick tap clicks (left, right or middle by
/// finger count), two fingers scroll and a tap then a quick second touch
/// drags; a physical click always beats a pending tap. Anything else typed
/// or clicked is ordinary input. Nothing is configurable.
/// </summary>
internal sealed class InputRouter
{
    private readonly Scene _scene;
    private readonly SeatState _seat;
    private readonly Action _quit;
    private readonly Action<int>? _switchVt;
    private readonly Shell? _shell;
    private readonly ScreenPower? _screenPower;
    private readonly object _gate = new();
    private readonly HashSet<int> _pressedKeys = new();
    private readonly HashSet<int> _consumedKeys = new();
    private int _cursorX;
    private int _cursorY;

    /// <summary>Button codes pressed over the shell chrome and consumed there.</summary>
    private readonly HashSet<int> _chromePressConsumedButtons = new();

    /// <summary>
    /// Where each button's press landed (the implicit grab, lite): its
    /// release goes back there and nowhere else. Per-button, so a tap-drag
    /// and a physical click cannot steal each other's releases.
    /// </summary>
    private readonly Dictionary<int, SurfaceHandler> _buttonPressTargets = new();
    private int _moveRemainderX;
    private int _moveRemainderY;

    // Touchpad gestures own the multitouch slot state (tap-to-click, scroll,
    // tap-drag); the router supplies the pointer, axis and button plumbing.
    private readonly TouchpadGestures _gestures;
    private bool _numLocked;
    private int _lastDepressed = -1;
    private int _lastLocked = -1;

    internal InputRouter(Scene scene, SeatState seat, Action quit, Shell? shell = null, ScreenPower? screenPower = null, Action<int>? switchVt = null)
    {
        _scene = scene;
        _seat = seat;
        _quit = quit;
        _shell = shell;
        _screenPower = screenPower;
        _switchVt = switchVt;
        _gestures = new TouchpadGestures(
            move: (dx, dy, time) => MoveCursor(
                dx == 0 ? 0 : ScaleTouchpadX(dx),
                dy == 0 ? 0 : ScaleTouchpadY(dy),
                time),
            scroll: SendTouchpadScroll,
            button: (code, pressed, time) =>
            {
                HandleButton(code, pressed, time);
                _scene.MarkDirty();
            });
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
        if (Suspended)
        {
            return;
        }

        lock (_gate)
        {
            _cursorX = x;
            _cursorY = y;
            _scene.Cursor = (x, y);
        }
    }

    /// <summary>
    /// While another VT owns the display, injected events are dropped: the
    /// session is invisible and must not move windows nobody can see.
    /// </summary>
    internal volatile bool Suspended;

    /// <summary>
    /// The VT-switch reset: everything pressed or consumed right now is
    /// gone — the matching releases arrive while the devices belong to
    /// another session and are dropped. Without this, the chord that
    /// triggered the switch (Ctrl+Alt) would read as still held on return.
    /// </summary>
    internal void ResetInputState()
    {
        lock (_gate)
        {
            // Gestures first: a tap-drag's release must route through the
            // still-set press target before the fields below are cleared.
            _gestures.Reset();
            _seat.ReleaseAllHeld();
            _pressedKeys.Clear();
            _consumedKeys.Clear();
            _chromePressConsumedButtons.Clear();
            _buttonPressTargets.Clear();
            _lastDepressed = -1;
            _lastLocked = -1;
        }
    }

    /// <summary>Processes one evdev event (thread-safe; called by device readers).</summary>
    internal void ProcessEvent(InputEvent e)
    {
        if (Suspended)
        {
            return;
        }

        // Any real input keeps the screen alive, and wakes it if it had
        // blanked. Deliberately outside _gate: the power object has its own
        // lock and the render loop must be woken even if this event goes no
        // further. The event itself is still delivered — the first move after
        // a blank both lights the panel and moves the cursor.
        if (_screenPower is not null && _screenPower.MarkActivity() == ScreenPowerAction.Unblank)
        {
            _scene.MarkDirty();
        }

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
                    // The hardware button beats any pending tap: a physical
                    // click during a touch must never double as one.
                    _gestures.NotePhysicalButton(e.Code, e.Value != InputConstants.KeyReleased, e.TimeMs);
                    HandleButton(e.Code, e.Value != InputConstants.KeyReleased, e.TimeMs);
                    _scene.MarkDirty();
                    break;

                case InputConstants.EventKey when e.Code >= 0x100:
                    // Other BTN_* codes (BTN_TOOL_FINGER, BTN_TOUCH, ...) are
                    // touch state, not keys or buttons: ignored.
                    break;

                case InputConstants.EventKey:
                    HandleKey(e);
                    _scene.MarkDirty();
                    break;
            }
        }
    }

    /// <summary>
    /// Multitouch (protocol B) trackpad events feed the gesture engine: one
    /// finger moves the pointer, a quick tap clicks (left/right/middle by
    /// finger count), two or more fingers scroll, and a tap followed by a
    /// quick second touch drags with the button held.
    /// </summary>
    private void HandleTouchpadAbs(InputEvent e)
    {
        switch (e.Code)
        {
            case InputConstants.AbsMtSlot:
                _gestures.Slot(e.Value);
                return;

            case InputConstants.AbsMtTrackingId:
                _gestures.TrackingId(e.Value, e.TimeMs);
                return;

            case InputConstants.AbsMtPositionX:
                _gestures.PositionX(e.Value, e.TimeMs);
                return;

            case InputConstants.AbsMtPositionY:
                _gestures.PositionY(e.Value, e.TimeMs);
                return;
        }
    }

    /// <summary>
    /// Trackpad sensitivity: touchpad units are roughly three times coarser
    /// than screen pixels should be. Scaled by 7/20 with the fraction
    /// carried, so slow drags still move the cursor steadily.
    /// </summary>
    private const int TouchpadNumerator = 7;
    private const int TouchpadDenominator = 20;

    private int _touchpadRemainderX;
    private int _touchpadRemainderY;

    private int ScaleTouchpadX(int raw)
    {
        var total = (raw * TouchpadNumerator) + _touchpadRemainderX;
        _touchpadRemainderX = total % TouchpadDenominator;
        return total / TouchpadDenominator;
    }

    private int ScaleTouchpadY(int raw)
    {
        var total = (raw * TouchpadNumerator) + _touchpadRemainderY;
        _touchpadRemainderY = total % TouchpadDenominator;
        return total / TouchpadDenominator;
    }

    private void MoveCursor(int dx, int dy, uint time)
    {
        // Device deltas are physical pixels; the scene is logical (physical /
        // output scale). Dividing keeps the tuned pointer feel identical at
        // every scale — the cursor crosses the same fraction of the screen
        // per swipe. The remainder carries, so slow motion still moves.
        var scale = Math.Max(1, _scene.Scale);
        var moveX = (dx + _moveRemainderX) / scale;
        _moveRemainderX = (dx + _moveRemainderX) % scale;
        var moveY = (dy + _moveRemainderY) / scale;
        _moveRemainderY = (dy + _moveRemainderY) % scale;

        var (outputWidth, outputHeight) = _scene.OutputSize;
        _cursorX = Math.Clamp(_cursorX + moveX, 0, Math.Max(0, outputWidth - 1));
        _cursorY = Math.Clamp(_cursorY + moveY, 0, Math.Max(0, outputHeight - 1));
        _scene.Cursor = (_cursorX, _cursorY);

        var target = _scene.HitTest(_cursorX, _cursorY);
        _seat.SetPointerFocus(target, LocalX(target), LocalY(target));
        if (target is not null)
        {
            _seat.SendMotion(target, LocalX(target), LocalY(target), time);
            _seat.SendPointerFrame(target);
        }
    }

    private void HandleButton(int code, bool pressed, uint time)
    {
        // A press eaten by the shell chrome swallows its own release: no
        // client may see a release for a press it never received, and no
        // press may lose the release it is owed. Consumed buttons are a set
        // of codes, so a second chrome press with another button cannot
        // orphan the first button's release.
        if (!pressed)
        {
            if (_shell is { } openShell)
            {
                openShell.PressedPin = -1;
            }

            if (_chromePressConsumedButtons.Remove(code))
            {
                return;
            }
        }

        // Shell chrome eats clicks first: the system menu panel (it draws
        // over the taskbar on small outputs, so what you see is what you
        // click), the taskbar strip, then the click-outside dismissal. None
        // may leak into client windows underneath.
        if (pressed && _shell is { } shell)
        {
            var (outputWidth, outputHeight) = _scene.OutputSize;
            if (shell.Menu.IsOpen && shell.Menu.PanelRect(outputWidth, outputHeight).Contains(_cursorX, _cursorY))
            {
                shell.Menu.HandleClick(_cursorX, _cursorY, outputWidth, outputHeight);
                _chromePressConsumedButtons.Add(code);
                return;
            }

            if (_cursorY >= outputHeight - Shell.BarHeight)
            {
                HandleTaskbarClick(shell, outputWidth, outputHeight, _cursorX, _cursorY);
                _chromePressConsumedButtons.Add(code);
                return;
            }

            if (shell.Menu.IsOpen)
            {
                // A click outside the panel dismisses it.
                shell.Menu.Close();
                _chromePressConsumedButtons.Add(code);
                return;
            }
        }

        // The window under the cursor is authoritative for a press (click-to-
        // focus, raise on press). Its release is owed back to the surface
        // that received the press — an implicit grab, lite — wherever the
        // cursor wandered to since; a release whose press never reached a
        // surface is dropped (no client may see a release without a press).
        if (pressed)
        {
            var target = _scene.HitTest(_cursorX, _cursorY);
            _seat.SetPointerFocus(target, LocalX(target), LocalY(target));
            if (target is not null)
            {
                _buttonPressTargets[code] = target;
                _scene.Raise(target);
                _seat.SetKeyboardFocus(target, _pressedKeys);
            }
        }
        else if (_buttonPressTargets.Remove(code, out var pressedTarget))
        {
            _seat.SetPointerFocus(pressedTarget, LocalX(pressedTarget), LocalY(pressedTarget));
            if (_seat.PointerFocus is { } pointerTarget)
            {
                _seat.SendButton(pointerTarget, code, pressed: false, time);
                _seat.SendPointerFrame(pointerTarget);
            }

            return;
        }
        else
        {
            return; // release with no press behind it
        }

        if (_seat.PointerFocus is { } target2)
        {
            _seat.SendButton(target2, code, pressed, time);
            _seat.SendPointerFrame(target2);
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

        // The release of a binding-consumed key is swallowed with its press,
        // so a consumed key never appears in enter frames or at a client.
        if (!pressed && _consumedKeys.Remove(e.Code))
        {
            SendModifiers(_seat.KeyboardFocus);
            return;
        }

        // Bindings see the raw key before it joins the pressed set, so the
        // key that triggered one is never part of a later enter frame.
        if (e.Value == InputConstants.KeyPressed && TryHandleBinding(e.Code))
        {
            _consumedKeys.Add(e.Code);
            SendModifiers(_seat.KeyboardFocus);
            return;
        }

        // The system menu, when open, eats Esc: it closes the menu, and the
        // key never reaches a client (press and release alike).
        if (pressed && _shell is { Menu.IsOpen: true } menuShell && menuShell.Menu.HandleKey(e.Code))
        {
            _consumedKeys.Add(e.Code);
            SendModifiers(_seat.KeyboardFocus);
            return;
        }

        if (pressed)
        {
            _pressedKeys.Add(e.Code);
        }
        else
        {
            _pressedKeys.Remove(e.Code);
        }

        if (pressed && e.Code == InputConstants.KeyNumLock)
        {
            _numLocked = !_numLocked;
        }

        if (_seat.KeyboardFocus is { } focus)
        {
            _seat.SendKey(focus, e.Code, pressed, e.TimeMs);
        }

        // The mask is tracked even unfocused; sent when there is a focus.
        SendModifiers(_seat.KeyboardFocus);
    }

    /// <summary>The fixed keybindings. True = the key is consumed outright.</summary>
    private bool TryHandleBinding(int code)
    {
        // Ctrl+Alt+Fn switches VT — the kernel's own handling never fires
        // because this session owns the input devices exclusively, so the
        // display server performs the switch itself.
        if (CtrlDown && AltDown && VtForKey(code) is { } vt && _switchVt is { } switchVt)
        {
            switchVt(vt);
            return true;
        }

        if (code == InputConstants.KeyF4 && AltDown)
        {
            _seat.KeyboardFocus?.XdgRole?.Close();
            return true;
        }

        if (code == InputConstants.KeyEsc && SuperDown)
        {
            _quit();
            return true;
        }

        return false;
    }

    /// <summary>The VT number a function key selects (F1..F12 → VT 1..12).</summary>
    internal static int? VtForKey(int code) => code switch
    {
        >= InputConstants.KeyF1 and <= InputConstants.KeyF10 => code - InputConstants.KeyF1 + 1,
        InputConstants.KeyF11 => 11,
        InputConstants.KeyF12 => 12,
        _ => null,
    };

    private void HandleTaskbarClick(Shell shell, int outputWidth, int outputHeight, int x, int y)
    {
        // The cached layout is exactly what the scanout drew, so drawn and
        // hit rectangles cannot disagree; without a frame yet, compute one.
        var layout = shell.LastBarLayout ?? Bar.ComputeLayout(outputWidth, outputHeight, Bar.ClockString(), _scene.TaskWindows(), shell.Pinned);

        // Any taskbar click closes the system menu first, except a click on
        // the apple button itself, which toggles it.
        if (shell.Menu.IsOpen && !layout.Start.Contains(x, y))
        {
            shell.Menu.Close();
        }

        if (layout.Start.Contains(x, y))
        {
            shell.Menu.Toggle();
            return;
        }

        for (var i = 0; i < layout.Pinned.Length; i++)
        {
            if (layout.Pinned[i].Rect.Contains(x, y))
            {
                shell.PressedPin = i;
                if (shell.Windows is { } windows)
                {
                    layout.Pinned[i].Launcher.Click(windows, [.. _pressedKeys]);
                }

                return;
            }
        }

        foreach (var (rect, task) in layout.Tasks)
        {
            if (rect.Contains(x, y))
            {
                // The layout is a snapshot: the window may be gone since it
                // was drawn. Ghosts get no focus.
                if (task.Surface is { IsMapped: true, Gone: false } target)
                {
                    _scene.Raise(target);
                    _seat.SetKeyboardFocus(target, _pressedKeys);
                }

                return;
            }
        }
    }

    private bool AltDown => _pressedKeys.Contains(InputConstants.KeyLeftAlt) || _pressedKeys.Contains(InputConstants.KeyRightAlt);

    private bool CtrlDown => _pressedKeys.Contains(InputConstants.KeyLeftCtrl) || _pressedKeys.Contains(InputConstants.KeyRightCtrl);

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

        // No caps-lock bit: the keymap maps the key to nothing (caps:none),
        // so no client ever computes Lock from it. Num Lock stays.
        var locked = 0;
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

    /// <summary>
    /// Two-finger scroll: mean finger travel becomes axis deltas at the same
    /// rate the pointer moves. Signs follow the wheel mapping (fingers down
    /// or right scroll down or right), and the pointer stays exactly put.
    /// </summary>
    private void SendTouchpadScroll(int dx, int dy, uint time)
    {
        // Scrolling needs a focus too, and the cursor may never have moved
        // (a first gesture can be a scroll): hit-test here like motion does.
        var target = _scene.HitTest(_cursorX, _cursorY);
        _seat.SetPointerFocus(target, LocalX(target), LocalY(target));

        if (dy != 0)
        {
            SendAxis(axis: 0, ScaleTouchpadY(dy), time);
        }

        if (dx != 0)
        {
            SendAxis(axis: 1, ScaleTouchpadX(dx), time);
        }
    }

    private int LocalX(SurfaceHandler? target) => target is null ? 0 : target.LocalX(_cursorX);

    private int LocalY(SurfaceHandler? target) => target is null ? 0 : target.LocalY(_cursorY);
}
