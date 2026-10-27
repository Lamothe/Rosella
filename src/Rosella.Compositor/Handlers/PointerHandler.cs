using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// Handles <c>wl_pointer</c>: relays the seat's pointer events.
/// <c>set_cursor</c> is deliberately ignored — Rosella draws one built-in
/// cursor, always (heavily opinionated).
/// </summary>
internal sealed class PointerHandler : IRequestHandler, IDisposable
{
    private readonly SeatState _seat;
    private ClientSession? _session;
    private ServerObject? _self;

    internal PointerHandler(SeatState seat)
    {
        _seat = seat;
    }

    /// <summary>Registers the pointer with the seat.</summary>
    internal void Bind(ClientSession session, ServerObject self)
    {
        _session = session;
        _self = self;
        _seat.RegisterPointer(session, self);
    }

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        switch (description.Name)
        {
            case "release":
                _seat.UnregisterPointer(session, target);
                _session = null;
                _self = null;
                session.DestroyObject(target);
                break;

            case "set_cursor":
                // Rosella's M6 policy: one cursor, built in. Ignoring is legal.
                break;

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }

    public void Dispose()
    {
        if (_session is not null && _self is not null)
        {
            _seat.UnregisterPointer(_session, _self);
            _session = null;
            _self = null;
        }
    }
}
