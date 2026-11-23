using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

internal sealed class SubcompositorHandler : IRequestHandler
{
    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        if (description.Name == "destroy")
        {
            session.DestroyObject(target);
            return;
        }

        var child = Resolve(session, target, request.Arguments[1].AsObject());
        var parent = Resolve(session, target, request.Arguments[2].AsObject());
        Scene.AttachToParent(child, parent, scene => scene.AddSubsurface(session, target, request.Arguments[0].AsNewId(), child, parent));
    }

    internal static SurfaceHandler Resolve(ClientSession session, ServerObject target, uint id)
    {
        if (!session.TryGetObject(id, out var obj) || obj.Handler is not SurfaceHandler surface)
        {
            throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidObject, $"invalid surface {id}");
        }

        return surface;
    }
}

internal sealed class SubsurfaceHandler(SurfaceHandler surface, SurfaceHandler parent) : IRequestHandler, ISurfaceRole, IDisposable
{
    internal SurfaceHandler Surface { get; } = surface;
    internal SurfaceHandler? Parent { get; set; } = parent;
    internal (int X, int Y) Position { get; set; }
    internal (int X, int Y) PendingPosition { get; set; }
    internal bool Sync { get; set; } = true;
    public bool IsDefunct { get; private set; }
    internal bool EffectiveSync => !IsDefunct && (Sync || (Parent?.Subsurface?.EffectiveSync ?? false));

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        switch (HandlerSupport.Require(target, request).Name)
        {
            case "destroy":
                Dispose();
                session.DestroyObject(target);
                break;
            case "set_position":
                Surface.WithScene(scene => scene.SetSubsurfacePosition(this, (request.Arguments[0].AsInt32(), request.Arguments[1].AsInt32())));
                break;
            case "place_above":
            case "place_below":
                var reference = SubcompositorHandler.Resolve(session, target, request.Arguments[0].AsObject());
                Surface.WithScene(scene => scene.RestackSubsurface(this, reference, request.Opcode == 2, target));
                break;
            case "set_sync":
                Surface.WithScene(scene => scene.SetSubsurfaceSync(this, true));
                break;
            case "set_desync":
                Surface.WithScene(scene => scene.SetSubsurfaceSync(this, false));
                break;
        }
    }

    public void Dispose()
    {
        if (!IsDefunct)
        {
            IsDefunct = true;
            Surface.WithScene(scene => scene.RemoveSubsurface(this));
        }
    }

    public void OnCommit(SurfaceHandler surface, bool contentChanged, bool hasContent, int contentWidth, int contentHeight) { }
    public void OnSurfaceDestroyed() => Dispose();
}
