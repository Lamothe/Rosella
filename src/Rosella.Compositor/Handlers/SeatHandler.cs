using Rosella.Protocol;
using Rosella.Server;
using Rosella.Server.Handlers;

namespace Rosella.Compositor;

/// <summary>
/// Handles <c>wl_seat</c>: advertises the devices the compositor found and
/// hands out <c>wl_keyboard</c>/<c>wl_pointer</c> objects. Requesting a
/// capability the seat never had is missing_capability (0), per wayland.xml —
/// that includes touch: Rosella is opinionated and has no touch support.
/// </summary>
internal sealed class SeatHandler : IRequestHandler, IBindEvents, IDisposable
{
    private const uint ErrorMissingCapability = 0;

    private readonly SeatState _seat;
    private ServerObject? _bound;

    internal SeatHandler(SeatState seat)
    {
        _seat = seat;
    }

    public void SendBindEvents(ClientSession session, ServerObject bound)
    {
        _bound = bound;

        // Registered and announced under the seat gate, so a hot-plug
        // broadcast can neither interleave nor miss this seat. wayland.xml:
        // the name "should be sent before announcing capabilities".
        _seat.RegisterSeat(session, bound, () =>
        {
            if (bound.Version >= 2)
            {
                session.SendEvent(bound, "name", WireArgument.String("seat0"));
            }

            session.SendEvent(bound, "capabilities", WireArgument.UInt32(_seat.Capabilities));
        });
    }

    /// <summary>Session end: stop the capability broadcast list.</summary>
    public void Dispose()
    {
        if (_bound is not null)
        {
            _seat.UnregisterSeat(_bound);
            _bound = null;
        }
    }

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        switch (description.Name)
        {
            case "get_keyboard":
            {
                RequireCapability(target, _seat.HasKeyboardDevice, "keyboard");
                var keyboard = HandlerSupport.CreateChild(session, target, "wl_keyboard", new KeyboardHandler(_seat), request.Arguments[0].AsNewId());
                ((KeyboardHandler)keyboard.Handler).Bind(session, keyboard);
                break;
            }

            case "get_pointer":
            {
                RequireCapability(target, _seat.HasPointerDevice, "pointer");
                var pointer = HandlerSupport.CreateChild(session, target, "wl_pointer", new PointerHandler(_seat), request.Arguments[0].AsNewId());
                ((PointerHandler)pointer.Handler).Bind(session, pointer);
                break;
            }

            case "get_touch":
                throw new ProtocolErrorException(target.Id, ErrorMissingCapability, "the seat has no touch capability");

            case "release":
                // Unregister before destroy: after delete_id the id may be
                // recycled and a broadcast must never name it again.
                Dispose();
                session.DestroyObject(target);
                break;

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }

    private static void RequireCapability(ServerObject target, bool available, string capability)
    {
        if (!available)
        {
            throw new ProtocolErrorException(target.Id, ErrorMissingCapability, $"the seat has no {capability} capability");
        }
    }
}
