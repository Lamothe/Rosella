using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// Handles <c>wl_pointer</c>: relays the seat's pointer events.
/// <c>set_cursor</c> marks the named surface as the pointer cursor — drawn
/// at the pointer instead of the built-in arrow, so client cursor shapes
/// (text beam, hand, resize) appear where the pointer is. The surface is
/// never composited as a window.
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
                SetCursor(session, target, request);
                break;

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }

    /// <summary>
    /// wl_pointer.set_cursor: the surface becomes the cursor (hotspot at the
    /// given surface-local point). A null surface hides the pointer image.
    /// The surface keeps its own commit lifecycle — clients update cursor
    /// shapes by committing new content to it.
    /// </summary>
    private void SetCursor(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var serial = request.Arguments[0].AsUInt32();
        var surfaceId = request.Arguments[1].AsObject();
        if (surfaceId == 0)
        {
            // Hides the image (wayland.xml: "If surface is NULL, the pointer
            // image is hidden") — still serial- and focus-gated.
            _seat.TrySetCursor(session, target, serial, null, 0, 0);
            return;
        }

        if (!session.TryGetObject(surfaceId, out var surfaceObject) || surfaceObject.Handler is not SurfaceHandler surface)
        {
            throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidObject, $"invalid cursor surface {surfaceId}");
        }

        // wayland.xml: "This request gives the surface the role of a cursor.
        // If the surface already has another role, it raises a protocol
        // error." wl_pointer.error.role == 0.
        if (surface.HasXdgRole)
        {
            throw new ProtocolErrorException(target.Id, ErrorRole, $"wl_surface {surfaceId} already has another role");
        }

        // The role itself is unconditional (spec): the surface is never a
        // window again, even when the cursor change below is ignored —
        // clients commit a buffer around set_cursor with no reply to learn
        // otherwise, and must not leave a stray sprite window behind.
        surface.IsCursorSurface = true;

        // Only the image change is gated (spec: "The cursor actually changes
        // only if..."): a stale serial or foreign focus is silently ignored.
        _seat.TrySetCursor(session, target, serial, surface, request.Arguments[2].AsInt32(), request.Arguments[3].AsInt32());
    }

    /// <summary>wl_pointer.error.role: the named wl_surface already has another role.</summary>
    private const uint ErrorRole = 0;

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
