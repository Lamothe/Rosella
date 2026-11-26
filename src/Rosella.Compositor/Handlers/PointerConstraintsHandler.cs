using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

internal sealed class PointerConstraintsHandler(SeatState seat) : IRequestHandler
{
    internal const string Interface = "zwp_pointer_constraints_v1";

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var method = HandlerSupport.Require(target, request).Name;
        if (method == "destroy")
        {
            session.DestroyObject(target);
            return;
        }

        var surfaceId = request.Arguments[1].AsObject();
        if (!session.TryGetObject(surfaceId, out var resource) || resource.Handler is not SurfaceHandler surface)
        {
            throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidObject, $"invalid constraint surface {surfaceId}");
        }
        var pointer = PointerHandler.Resolve(session, target, request.Arguments[2].AsObject());
        var region = ResolveRegion(session, target, request.Arguments[3].AsObject());
        var lifetime = request.Arguments[4].AsUInt32();
        if (lifetime is not (1 or 2))
        {
            throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, "invalid pointer constraint lifetime");
        }
        if (surface.PointerConstraint is { Finished: false })
        {
            throw new ProtocolErrorException(target.Id, 1, "surface already has a pointer constraint");
        }

        var locked = method == "lock_pointer";
        var constraint = new PointerConstraint(seat, surface, pointer, locked, lifetime == 1, region);
        var iface = locked ? "zwp_locked_pointer_v1" : "zwp_confined_pointer_v1";
        var child = HandlerSupport.CreateChild(session, target, iface, constraint, request.Arguments[0].AsNewId());
        constraint.Bind(session, child);
    }

    internal static RegionSnapshot? ResolveRegion(ClientSession session, ServerObject target, uint regionId)
    {
        if (regionId == 0)
        {
            return null;
        }
        if (session.TryGetObject(regionId, out var resource) && resource.Handler is RegionHandler region)
        {
            return region.Snapshot();
        }
        throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidObject, $"invalid pointer region {regionId}");
    }
}

internal sealed class PointerConstraint(SeatState seat, SurfaceHandler surface, ServerObject pointer,
    bool locked, bool oneShot, RegionSnapshot? region) : IRequestHandler, IDisposable
{
    private volatile RegionOperation[]? _region = region?.Operations;
    internal RegionSnapshot? PendingRegion { get; private set; } = region;
    internal SurfaceHandler Surface { get; } = surface;
    internal ServerObject Pointer { get; } = pointer;
    internal bool Locked { get; } = locked;
    internal bool OneShot { get; } = oneShot;
    internal volatile bool Finished;
    internal ClientSession Session { get; private set; } = null!;
    internal ServerObject Resource { get; private set; } = null!;

    internal void Bind(ClientSession session, ServerObject resource)
    {
        Session = session;
        Resource = resource;
        seat.RegisterConstraint(this);
    }

    internal void ApplyRegion(RegionSnapshot? next) => _region = next?.Operations;

    internal bool Contains(int x, int y) => _region is not { } operations || new RegionSnapshot(operations).Contains(x, y);

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        switch (HandlerSupport.Require(target, request).Name)
        {
            case "destroy":
                Dispose();
                session.DestroyObject(target);
                break;
            case "set_region":
                PendingRegion = PointerConstraintsHandler.ResolveRegion(session, target, request.Arguments[0].AsObject());
                break;
            case "set_cursor_position_hint":
                // The cursor stays at its last position on unlock.
                break;
        }
    }

    public void Dispose() => seat.UnregisterConstraint(this);
}
