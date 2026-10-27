using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// Handles <c>xdg_popup</c>: short-lived child windows (menus, tooltips)
/// positioned by their positioner. Rosella's opinionated placement resolves
/// the anchor rectangle (relative to the parent's window geometry) plus the
/// offset; constraint adjustment (flipping/sliding to stay on screen) is
/// parsed but not honoured. The placement is frozen at use time, as the
/// spec requires.
/// </summary>
internal sealed class XdgPopupHandler : IRequestHandler, IXdgRoleObject
{
    private readonly XdgSurfaceHandler _surface;
    private readonly XdgSurfaceHandler? _parent;
    private XdgPlacement _placement;
    private ClientSession? _session;
    private ServerObject? _self;

    public XdgPopupHandler(XdgSurfaceHandler surface, XdgSurfaceHandler? parent, XdgPlacement placement)
    {
        _surface = surface;
        _parent = parent;
        _placement = placement;
    }

    internal void Bind(ClientSession session, ServerObject self)
    {
        _session = session;
        _self = self;
    }

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        switch (description.Name)
        {
            case "destroy":
                _surface.OnRoleDestroyed();
                session.DestroyObject(target);
                break;

            case "grab":
                // Pointer grabs need input routing (M6); legal to ignore.
                break;

            case "reposition":
            {
                var positionerId = request.Arguments[0].AsObject();
                if (!session.TryGetObject(positionerId, out var positionerObject) || positionerObject.Handler is not XdgPositionerHandler positioner)
                {
                    throw new ProtocolErrorException(_surface.WmBase.Id, XdgErrors.InvalidPositioner, $"invalid positioner {positionerId}");
                }

                if (!positioner.IsComplete)
                {
                    throw new ProtocolErrorException(_surface.WmBase.Id, XdgErrors.InvalidPositioner, "positioner is incomplete (needs size and anchor rectangle)");
                }

                // repositioned comes before the configure pair, per the spec.
                _placement = positioner.Snapshot();
                var serial = request.Arguments[1].AsUInt32();
                _session!.SendEvent(_self!, "repositioned", WireArgument.UInt32(serial));
                _surface.SendConfigure();
                break;
            }

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }

    /// <summary>Sends xdg_popup.configure relative to the parent window geometry.</summary>
    public void SendRoleConfigure()
    {
        var (x, y) = _placement.RelativeToParent;
        _session!.SendEvent(_self!, "configure", WireArgument.Int32(x), WireArgument.Int32(y), WireArgument.Int32(_placement.Width), WireArgument.Int32(_placement.Height));
    }

    /// <summary>Popups go where the placement says, not the window policy.</summary>
    public (int Width, int Height, (int X, int Y)? Corner) GetPlacement(SurfaceHandler surface, (int X, int Y, int Width, int Height) geometry)
    {
        var corner = _placement.PlaceAt(_parent?.WindowPosition ?? (0, 0));
        return (_placement.Width, _placement.Height, corner);
    }

    /// <summary>Tells the client the popup was dismissed (M6+ input).</summary>
    internal void Done()
    {
        _session!.SendEvent(_self!, "popup_done");
    }
}
