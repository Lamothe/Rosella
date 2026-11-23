using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// Handles <c>wl_compositor</c>: creates surfaces and regions.
/// </summary>
internal sealed class CompositorHandler : IRequestHandler
{
    private readonly Scene _scene;

    public CompositorHandler(Scene scene)
    {
        _scene = scene;
    }

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        switch (description.Name)
        {
            case "create_surface":
            {
                var handler = new SurfaceHandler(_scene);
                var created = HandlerSupport.CreateChild(session, target, "wl_surface", handler, request.Arguments[0].AsNewId());
                handler.Bind(session, created);
                _scene.Register(handler);
                break;
            }

            case "create_region":
                HandlerSupport.CreateChild(session, target, "wl_region", new RegionHandler(), request.Arguments[0].AsNewId());
                break;

            case "release": // destructor, since 7
                session.DestroyObject(target);
                break;

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }
}
