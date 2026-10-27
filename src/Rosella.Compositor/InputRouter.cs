using Rosella.Input;

namespace Rosella.Compositor;

/// <summary>
/// The input policy, and deliberately the only policy there is: the cursor
/// moves and hit-tests windows, a click focuses and raises, keys go to the
/// keyboard focus, and the keybindings are fixed: Alt+F4 closes the focused
/// window, Super+Esc quits, Super alone toggles the Start menu. Anything
/// else typed or clicked while Super is held counts as a chord use (the
/// menu opens only on a bare Super tap). Nothing is configurable.
/// </summary>
internal sealed class InputRouter
{
    private readonly Scene _scene;
    private readonly SeatState _seat;
    private readonly Action _quit;
    private readonly Shell? _shell;
    private readonly object _gate = new();
    private readonly HashSet<int> _pressedKeys = new();
    private readonly HashSet<int> _consumedKeys = new();
    private int _cursorX;
    private int _cursorY;
    private bool _superChordUsed;
    private int _chromePressConsumedButton = -1;
    private SurfaceHandler? _buttonPressTarget;
    private SurfaceHandler? _focusBeforeLauncher;

    // Multitouch slot state (protocol B): slot -> last per-axis position
    // with per-axis seeding (the first coordinates after finger-down are a
    // placement, not a delta).
    private int _mtSlot;
    private readonly Dictionary<int, (int X, int Y, bool HasX, bool HasY)> _mtFingers = new();
    private bool _capsLocked;
    private bool _numLocked;
    private int _lastDepressed = -1;
    private int _lastLocked = -1;

    internal InputRouter(Scene scene, SeatState seat, Action quit, Shell? shell = null)
    {
        _scene = scene;
        _seat = seat;
        _quit = quit;
        _shell = shell;
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
                        MoveCursor(ScaleTouchpadX(e.Value - last.X), 0, e.TimeMs);
                    }

                    _mtFingers[_mtSlot] = (e.Value, last.Y, true, last.HasY);
                }
                else
                {
                    if (last.HasY)
                    {
                        MoveCursor(0, ScaleTouchpadY(e.Value - last.Y), e.TimeMs);
                    }

                    _mtFingers[_mtSlot] = (last.X, e.Value, last.HasX, true);
                }

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

        // Any click while Super is held counts as a chord use.
        if (pressed && SuperDown)
        {
            _superChordUsed = true;
        }

        // A press eaten by the shell chrome swallows its own release: no
        // client may see a release for a press it never received, and no
        // press may lose the release it is owed.
        if (!pressed && e.Code == _chromePressConsumedButton)
        {
            _chromePressConsumedButton = -1;
            return;
        }

        // Shell chrome eats clicks first: the Start menu (it draws over the
        // taskbar on small outputs, so what you see is what you click), the
        // taskbar strip, then the click-outside dismissal. None may leak
        // into client windows underneath.
        if (pressed && _shell is { } shell)
        {
            var (outputWidth, outputHeight) = _scene.OutputSize;
            if (shell.Launcher.IsOpen && shell.Launcher.PanelRect(outputWidth, outputHeight).Contains(_cursorX, _cursorY))
            {
                shell.Launcher.HandleClick(_cursorX, _cursorY, outputWidth, outputHeight);
                _chromePressConsumedButton = e.Code;
                RestoreLauncherFocusIfClosed();
                return;
            }

            if (_cursorY >= outputHeight - Shell.BarHeight)
            {
                HandleTaskbarClick(shell, outputWidth, outputHeight, _cursorX, _cursorY);
                _chromePressConsumedButton = e.Code;
                RestoreLauncherFocusIfClosed();
                return;
            }

            if (shell.Launcher.IsOpen)
            {
                // A click outside the panel dismisses it.
                ToggleLauncher();
                _chromePressConsumedButton = e.Code;
                RestoreLauncherFocusIfClosed();
                return;
            }
        }

        // The window under the cursor is authoritative for a press (click-to-
        // focus, raise on press). Its release is owed back to the surface
        // that received the press — an implicit grab, lite — wherever the
        // cursor wandered to since.
        var target = pressed ? _scene.HitTest(_cursorX, _cursorY) : _buttonPressTarget ?? _scene.HitTest(_cursorX, _cursorY);
        _seat.SetPointerFocus(target, LocalX(target), LocalY(target));
        if (pressed && target is not null)
        {
            _buttonPressTarget = target;
            _scene.Raise(target);
            _seat.SetKeyboardFocus(target, _pressedKeys);
        }
        else if (!pressed)
        {
            _buttonPressTarget = null;
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

        var superWasDown = SuperDown;
        if (pressed)
        {
            _pressedKeys.Add(e.Code);
        }
        else
        {
            _pressedKeys.Remove(e.Code);
        }

        // Only the Super up->down transition arms a fresh chord: a second
        // Meta must not reset one already made.
        if (pressed && !superWasDown && (e.Code is InputConstants.KeyLeftMeta or InputConstants.KeyRightMeta))
        {
            _superChordUsed = false;
        }

        // Any other key while Super is held is a chord use: typing under a
        // held Super must not open the Start menu on its release.
        if (pressed && SuperDown && e.Code is not (InputConstants.KeyLeftMeta or InputConstants.KeyRightMeta))
        {
            _superChordUsed = true;
        }

        if (pressed && e.Code == InputConstants.KeyCapsLock)
        {
            _capsLocked = !_capsLocked;
        }

        if (pressed && e.Code == InputConstants.KeyNumLock)
        {
            _numLocked = !_numLocked;
        }

        // Super released with no chord: the launcher toggles.
        if (!pressed
            && (e.Code is InputConstants.KeyLeftMeta or InputConstants.KeyRightMeta)
            && !SuperDown && !_superChordUsed)
        {
            ToggleLauncher();
        }

        // The launcher, when open, eats its navigation keys outright.
        if (pressed && _shell is { Launcher.IsOpen: true } openShell && openShell.Launcher.HandleKey(e.Code))
        {
            // Any consumed key counts as a chord while Super may still be
            // held: its release must not also toggle the launcher.
            _superChordUsed = true;
            RestoreLauncherFocusIfClosed();
            SendModifiers(_seat.KeyboardFocus);
            return;
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
        if (code == InputConstants.KeyF4 && AltDown)
        {
            _seat.KeyboardFocus?.XdgRole?.Close();
            return true;
        }

        if (code == InputConstants.KeyEsc && SuperDown)
        {
            _superChordUsed = true;
            _quit();
            return true;
        }

        return false;
    }

    private void ToggleLauncher()
    {
        if (_shell is not { } shell)
        {
            return;
        }

        // Every open/close counts as a chord: if Super is still held when
        // the launcher closes some other way, its release must not re-open.
        _superChordUsed = true;
        if (shell.Launcher.IsOpen)
        {
            CloseLauncher();
        }
        else
        {
            _focusBeforeLauncher = _seat.KeyboardFocus;
            shell.Launcher.Open();
            _seat.SetKeyboardFocus(null, _pressedKeys);
        }
    }

    /// <summary>Closes the launcher and hands the keyboard back.</summary>
    private void CloseLauncher()
    {
        // Any dismissal while Super is held is a chord use: its release
        // must not re-open the menu.
        if (SuperDown)
        {
            _superChordUsed = true;
        }

        _shell?.Launcher.Close();
        RestoreLauncherFocus();
    }

    /// <summary>Restores the pre-launcher focus once the launcher is shut.</summary>
    private void RestoreLauncherFocusIfClosed()
    {
        if (_shell is not { Launcher.IsOpen: true })
        {
            RestoreLauncherFocus();
        }
    }

    private void RestoreLauncherFocus()
    {
        if (_focusBeforeLauncher is null)
        {
            return;
        }

        var previous = _focusBeforeLauncher is { Gone: false } stale && stale.IsMapped ? stale : null;
        _focusBeforeLauncher = null;
        _seat.SetKeyboardFocus(previous, _pressedKeys);
    }

    private void HandleTaskbarClick(Shell shell, int outputWidth, int outputHeight, int x, int y)
    {
        // The cached layout is exactly what the scanout drew, so drawn and
        // hit rectangles cannot disagree; without a frame yet, compute one.
        var layout = shell.LastBarLayout ?? Bar.ComputeLayout(outputWidth, outputHeight, Bar.ClockString(), _scene.TaskWindows());

        // Any taskbar click closes the Start menu first, except a click on
        // Start itself, which toggles it.
        if (shell.Launcher.IsOpen && !layout.Start.Contains(x, y))
        {
            CloseLauncher();
        }

        if (layout.Start.Contains(x, y))
        {
            ToggleLauncher();
            return;
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
