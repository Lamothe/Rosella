using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

internal sealed class IdleInhibitHandler : IRequestHandler
{
    internal const string Interface = "zwp_idle_inhibit_manager_v1";

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var method = HandlerSupport.Require(target, request).Name;
        if (method == "destroy")
        {
            session.DestroyObject(target);
            return;
        }

        var id = request.Arguments[1].AsObject();
        if (!session.TryGetObject(id, out var surface) || surface.Handler is not SurfaceHandler handler)
        {
            throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidObject, $"invalid inhibitor surface {id}");
        }

        var inhibitor = new Inhibitor(handler);
        HandlerSupport.CreateChild(session, target, "zwp_idle_inhibitor_v1", inhibitor, request.Arguments[0].AsNewId());
        handler.WithScene(scene => scene.AddIdleInhibitor(handler));
    }

    private sealed class Inhibitor(SurfaceHandler surface) : IRequestHandler, IDisposable
    {
        private bool _disposed;

        public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
        {
            HandlerSupport.Require(target, request);
            Dispose();
            session.DestroyObject(target);
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _disposed = true;
                surface.WithScene(scene => scene.RemoveIdleInhibitor(surface));
            }
        }
    }
}
