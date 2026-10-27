using System.Net.Sockets;
using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// The seat: device capabilities, the wl_seat/wl_keyboard/wl_pointer objects
/// across all clients, and keyboard/pointer focus. All sends happen under the
/// seat gate — serialised against focus teardown, object release and surface
/// death, so no event ever names an object after its delete_id. One dead
/// client's socket error never stops delivery to the rest.
/// </summary>
internal sealed class SeatState
{
    private readonly object _gate = new();
    private readonly List<(ClientSession Session, ServerObject Object)> _keyboards = new();
    private readonly List<(ClientSession Session, ServerObject Object)> _pointers = new();
    private readonly HashSet<int> _heldKeys = new();
    private SurfaceHandler? _keyboardFocus;
    private SurfaceHandler? _pointerFocus;
    private int _depressed;
    private int _locked;
    private int _pointerX;
    private int _pointerY;

    /// <summary>Set by the input thread before clients can bind.</summary>
    internal bool HasKeyboardDevice { get; set; }

    /// <summary>Set by the input thread before clients can bind.</summary>
    internal bool HasPointerDevice { get; set; }

    /// <summary>wl_seat.capabilities: pointer=1, keyboard=2, touch=4.</summary>
    internal uint Capabilities => (HasPointerDevice ? 1u : 0u) | (HasKeyboardDevice ? 2u : 0u);

    internal SurfaceHandler? KeyboardFocus
    {
        get
        {
            lock (_gate)
            {
                return _keyboardFocus;
            }
        }
    }

    internal SurfaceHandler? PointerFocus
    {
        get
        {
            lock (_gate)
            {
                return _pointerFocus;
            }
        }
    }

    /// <summary>
    /// Registers a keyboard. The handler has already sent keymap/repeat_info;
    /// if this session owns the keyboard focus, the new keyboard gets
    /// enter (+ the mandatory trailing modifiers) immediately.
    /// </summary>
    internal void RegisterKeyboard(ClientSession session, ServerObject keyboard)
    {
        lock (_gate)
        {
            _keyboards.Add((session, keyboard));
            if (_keyboardFocus is { Gone: false } focus && focus.Session == session)
            {
                SendEnter(session, keyboard, focus);
                SendModifiersOn(session, keyboard);
            }
        }
    }

    internal void UnregisterKeyboard(ClientSession session, ServerObject keyboard)
    {
        lock (_gate)
        {
            _keyboards.RemoveAll(entry => entry.Session == session && entry.Object == keyboard);
        }
    }

    /// <summary>
    /// Registers a pointer. If this session owns the pointer focus, the new
    /// pointer gets enter at the current cursor position immediately.
    /// </summary>
    internal void RegisterPointer(ClientSession session, ServerObject pointer)
    {
        lock (_gate)
        {
            _pointers.Add((session, pointer));
            if (_pointerFocus is { Gone: false } focus && focus.Session == session)
            {
                SendEnterOn(session, pointer, focus);
            }
        }
    }

    internal void UnregisterPointer(ClientSession session, ServerObject pointer)
    {
        lock (_gate)
        {
            _pointers.RemoveAll(entry => entry.Session == session && entry.Object == pointer);
        }
    }

    /// <summary>
    /// Moves keyboard focus: enter carries the keys already held and is
    /// always followed by a modifiers event (the protocol requires it), and
    /// the matching xdg_toplevel activation configure rounds follow.
    /// </summary>
    internal void SetKeyboardFocus(SurfaceHandler? focus, IEnumerable<int> heldKeys)
    {
        lock (_gate)
        {
            _heldKeys.Clear();
            _heldKeys.UnionWith(heldKeys);

            if (focus is { Gone: true })
            {
                focus = null;
            }

            var previous = _keyboardFocus;
            if (ReferenceEquals(previous, focus))
            {
                return;
            }

            _keyboardFocus = focus;

            if (previous is not null)
            {
                foreach (var (session, keyboard) in Of(_keyboards, previous))
                {
                    TrySend(session, keyboard, "leave", WireArgument.UInt32(session.NextSerial()), WireArgument.Object(previous.Id));
                }

                previous.XdgRole?.SetActivated(false);
            }

            if (focus is not null)
            {
                foreach (var (session, keyboard) in Of(_keyboards, focus))
                {
                    SendEnter(session, keyboard, focus);
                    SendModifiersOn(session, keyboard);
                }

                focus.XdgRole?.SetActivated(true);
            }
        }
    }

    /// <summary>Moves pointer focus: leave/enter with surface-local coordinates.</summary>
    internal void SetPointerFocus(SurfaceHandler? focus, int surfaceX, int surfaceY)
    {
        lock (_gate)
        {
            _pointerX = surfaceX;
            _pointerY = surfaceY;

            if (focus is { Gone: true })
            {
                focus = null;
            }

            var previous = _pointerFocus;
            if (ReferenceEquals(previous, focus))
            {
                return;
            }

            _pointerFocus = focus;

            if (previous is not null)
            {
                foreach (var (session, pointer) in Of(_pointers, previous))
                {
                    TrySend(session, pointer, "leave", WireArgument.UInt32(session.NextSerial()), WireArgument.Object(previous.Id));
                }
            }

            if (focus is not null)
            {
                foreach (var (session, pointer) in Of(_pointers, focus))
                {
                    SendEnterOn(session, pointer, focus);
                }
            }
        }
    }

    /// <summary>Sends wl_keyboard.key and keeps the held-key snapshot fresh.</summary>
    internal void SendKey(SurfaceHandler target, int code, bool pressed, uint time)
    {
        lock (_gate)
        {
            if (pressed)
            {
                _heldKeys.Add(code);
            }
            else
            {
                _heldKeys.Remove(code);
            }

            foreach (var (session, keyboard) in Of(_keyboards, target))
            {
                TrySend(
                    session,
                    keyboard,
                    "key",
                    WireArgument.UInt32(session.NextSerial()),
                    WireArgument.UInt32(time),
                    WireArgument.UInt32((uint)code),
                    WireArgument.UInt32(pressed ? 1u : 0u));
            }
        }
    }

    /// <summary>
    /// Sends wl_keyboard.modifiers to the focus surface's keyboards. The
    /// mask is tracked even unfocused (so a click while Shift is held sends
    /// the right state after enter).
    /// </summary>
    internal void SendModifiers(SurfaceHandler? target, int depressed, int locked)
    {
        lock (_gate)
        {
            _depressed = depressed;
            _locked = locked;
            if (target is not null)
            {
                foreach (var (session, keyboard) in Of(_keyboards, target))
                {
                    SendModifiersOn(session, keyboard);
                }
            }
        }
    }

    /// <summary>Sends wl_pointer.motion to the focus surface's pointers.</summary>
    internal void SendMotion(SurfaceHandler target, int surfaceX, int surfaceY, uint time)
    {
        lock (_gate)
        {
            foreach (var (session, pointer) in Of(_pointers, target))
            {
                TrySend(
                    session,
                    pointer,
                    "motion",
                    WireArgument.UInt32(time),
                    WireArgument.FixedPoint(Protocol.Fixed.FromRaw(surfaceX << 8)),
                    WireArgument.FixedPoint(Protocol.Fixed.FromRaw(surfaceY << 8)));
            }
        }
    }

    /// <summary>Sends wl_pointer.button to the focus surface's pointers.</summary>
    internal void SendButton(SurfaceHandler target, int button, bool pressed, uint time)
    {
        lock (_gate)
        {
            foreach (var (session, pointer) in Of(_pointers, target))
            {
                TrySend(
                    session,
                    pointer,
                    "button",
                    WireArgument.UInt32(session.NextSerial()),
                    WireArgument.UInt32(time),
                    WireArgument.UInt32((uint)button),
                    WireArgument.UInt32(pressed ? 1u : 0u));
            }
        }
    }

    /// <summary>Sends wl_pointer.axis to the focus surface's pointers.</summary>
    internal void SendAxis(SurfaceHandler target, int axis, int value, uint time)
    {
        lock (_gate)
        {
            foreach (var (session, pointer) in Of(_pointers, target))
            {
                TrySend(
                    session,
                    pointer,
                    "axis",
                    WireArgument.UInt32(time),
                    WireArgument.UInt32((uint)axis),
                    WireArgument.FixedPoint(Protocol.Fixed.FromRaw(value << 8)));
            }
        }
    }

    /// <summary>Ends one pointer event batch (wl_pointer.frame, v5+).</summary>
    internal void SendPointerFrame(SurfaceHandler target)
    {
        lock (_gate)
        {
            foreach (var (session, pointer) in Of(_pointers, target))
            {
                if (pointer.Version >= 5)
                {
                    TrySend(session, pointer, "frame");
                }
            }
        }
    }

    /// <summary>
    /// A surface is going away or hiding. Clears it as keyboard/pointer
    /// focus, sending leave only while the surface id is still valid (unmap),
    /// never for destroyed surfaces or ended sessions.
    /// </summary>
    internal void OnSurfaceHidden(SurfaceHandler surface, bool canSendLeave)
    {
        lock (_gate)
        {
            var keyboardHadFocus = ReferenceEquals(_keyboardFocus, surface);
            var pointerHadFocus = ReferenceEquals(_pointerFocus, surface);
            if (keyboardHadFocus)
            {
                _keyboardFocus = null;
            }

            if (pointerHadFocus)
            {
                _pointerFocus = null;
            }

            if (!canSendLeave)
            {
                return;
            }

            if (keyboardHadFocus)
            {
                foreach (var (session, keyboard) in Of(_keyboards, surface))
                {
                    TrySend(session, keyboard, "leave", WireArgument.UInt32(session.NextSerial()), WireArgument.Object(surface.Id));
                }

                surface.XdgRole?.SetActivated(false);
            }

            if (pointerHadFocus)
            {
                foreach (var (session, pointer) in Of(_pointers, surface))
                {
                    TrySend(session, pointer, "leave", WireArgument.UInt32(session.NextSerial()), WireArgument.Object(surface.Id));
                }
            }
        }
    }

    private static List<(ClientSession Session, ServerObject Object)> Of(
        List<(ClientSession Session, ServerObject Object)> source,
        SurfaceHandler target)
    {
        if (target.Session is not { } session)
        {
            return [];
        }

        return source.Where(entry => entry.Session == session).ToList();
    }

    private void SendEnter(ClientSession session, ServerObject keyboard, SurfaceHandler focus)
    {
        var keys = _heldKeys.SelectMany(code => BitConverter.GetBytes((uint)code)).ToArray();
        TrySend(
            session,
            keyboard,
            "enter",
            WireArgument.UInt32(session.NextSerial()),
            WireArgument.Object(focus.Id),
            WireArgument.Array(keys));
    }

    private void SendEnterOn(ClientSession session, ServerObject pointer, SurfaceHandler focus)
    {
        TrySend(
            session,
            pointer,
            "enter",
            WireArgument.UInt32(session.NextSerial()),
            WireArgument.Object(focus.Id),
            WireArgument.FixedPoint(Protocol.Fixed.FromRaw(_pointerX << 8)),
            WireArgument.FixedPoint(Protocol.Fixed.FromRaw(_pointerY << 8)));
    }

    private void SendModifiersOn(ClientSession session, ServerObject keyboard)
    {
        // The modifiers event is mandatory after every enter; the mask is
        // taken from the seat's current state.
        TrySend(
            session,
            keyboard,
            "modifiers",
            WireArgument.UInt32(session.NextSerial()),
            WireArgument.UInt32((uint)_depressed),
            WireArgument.UInt32(0),
            WireArgument.UInt32((uint)_locked),
            WireArgument.UInt32(0));
    }

    /// <summary>One dead client's socket error must not poison the fan-out.</summary>
    private static void TrySend(ClientSession session, ServerObject target, string eventName, params WireArgument[] arguments)
    {
        try
        {
            session.SendEvent(target, eventName, arguments);
        }
        catch (SocketException)
        {
            // The peer vanished mid-send; its session teardown cleans up.
        }
        catch (IOException)
        {
            // Same: torn connection.
        }
    }
}
