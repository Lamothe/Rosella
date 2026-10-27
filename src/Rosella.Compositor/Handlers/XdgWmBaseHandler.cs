using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// Handles <c>xdg_wm_base</c>: creates xdg surfaces and positioners, and
/// refuses to die while surfaces it created are still alive.
/// </summary>
internal sealed class XdgWmBaseHandler : IRequestHandler, IDisposable
{
    private readonly Scene _scene;
    private int _liveSurfaces;

    public XdgWmBaseHandler(Scene scene)
    {
        _scene = scene;
    }

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        switch (description.Name)
        {
            case "destroy":
                if (_liveSurfaces > 0)
                {
                    throw new ProtocolErrorException(target.Id, XdgErrors.DefunctSurfaces, "xdg_wm_base destroyed before its xdg_surfaces");
                }

                session.DestroyObject(target);
                break;

            case "create_positioner":
                HandlerSupport.CreateChild(session, target, "xdg_positioner", new XdgPositionerHandler(), request.Arguments[0].AsNewId());
                break;

            case "get_xdg_surface":
                GetXdgSurface(session, target, request);
                break;

            case "pong":
                // Keepalive reply; Rosella never pings yet.
                break;

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }

    /// <summary>Session end: the surfaces go with the connection.</summary>
    public void Dispose()
    {
        _liveSurfaces = 0;
    }

    private void GetXdgSurface(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var surfaceId = request.Arguments[1].AsObject();
        if (!session.TryGetObject(surfaceId, out var surfaceObject) || surfaceObject.Handler is not SurfaceHandler surface)
        {
            throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidObject, $"invalid wl_surface {surfaceId}");
        }

        var handler = new XdgSurfaceHandler(_scene, surface);
        if (!surface.TryAssignRole(handler))
        {
            // A surface's role never changes: xdg-shell cannot adopt it.
            throw new ProtocolErrorException(target.Id, XdgErrors.Role, $"wl_surface {surfaceId} already has a role");
        }

        var created = HandlerSupport.CreateChild(session, target, "xdg_surface", handler, request.Arguments[0].AsNewId());
        handler.Bind(session, created, target, OnSurfaceEnded);
        _liveSurfaces++;
    }

    private void OnSurfaceEnded()
    {
        _liveSurfaces--;
    }
}
