using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

internal sealed class RelativePointerHandler(SeatState seat) : IRequestHandler
{
    internal const string Interface = "zwp_relative_pointer_manager_v1";

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        if (HandlerSupport.Require(target, request).Name == "destroy")
        {
            session.DestroyObject(target);
            return;
        }

        var pointer = PointerHandler.Resolve(session, target, request.Arguments[1].AsObject());
        var relative = new RelativePointer(seat);
        var resource = HandlerSupport.CreateChild(session, target, "zwp_relative_pointer_v1", relative, request.Arguments[0].AsNewId());
        relative.Bind(session, pointer, resource);
    }

    private sealed class RelativePointer(SeatState seat) : IRequestHandler, IDisposable
    {
        private ServerObject? _resource;

        internal void Bind(ClientSession session, ServerObject pointer, ServerObject resource)
        {
            _resource = resource;
            seat.RegisterRelativePointer(session, pointer, resource);
        }

        public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
        {
            HandlerSupport.Require(target, request);
            Dispose();
            session.DestroyObject(target);
        }

        public void Dispose()
        {
            if (_resource is { } resource)
            {
                seat.UnregisterRelativePointer(resource);
                _resource = null;
            }
        }
    }
}
