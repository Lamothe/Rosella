using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

internal readonly record struct ViewportSource(double X, double Y, double Width, double Height);

internal readonly record struct ViewportState(ViewportSource? Source, (int Width, int Height)? Destination)
{
    internal int Width(int bufferWidth, int scale) => bufferWidth == 0 ? 0 :
        Destination?.Width ?? (int?)Source?.Width ?? bufferWidth / scale;

    internal int Height(int bufferHeight, int scale) => bufferHeight == 0 ? 0 :
        Destination?.Height ?? (int?)Source?.Height ?? bufferHeight / scale;

    internal void Validate(uint id, int width, int height, int scale)
    {
        if (Source is not { } source)
        {
            return;
        }
        if (Destination is null && (source.Width != Math.Truncate(source.Width) || source.Height != Math.Truncate(source.Height)))
        {
            throw new ProtocolErrorException(id, 1, "fractional viewport source size requires a destination");
        }
        if (width > 0 && height > 0 && ((source.X + source.Width) * scale > width || (source.Y + source.Height) * scale > height))
        {
            throw new ProtocolErrorException(id, 2, "viewport source extends outside the buffer");
        }
    }
}

internal sealed class ViewporterHandler : IRequestHandler
{
    internal const string Interface = "wp_viewporter";

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        if (HandlerSupport.Require(target, request).Name == "destroy")
        {
            session.DestroyObject(target);
            return;
        }

        var surfaceId = request.Arguments[1].AsObject();
        if (!session.TryGetObject(surfaceId, out var surface) || surface.Handler is not SurfaceHandler handler)
        {
            throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidObject, $"invalid viewport surface {surfaceId}");
        }
        if (handler.ViewportId != 0)
        {
            throw new ProtocolErrorException(target.Id, 0, "surface already has a viewport");
        }

        var id = request.Arguments[0].AsNewId();
        HandlerSupport.CreateChild(session, target, "wp_viewport", new Viewport(handler), id);
        handler.ViewportId = id;
    }

    private sealed class Viewport(SurfaceHandler surface) : IRequestHandler
    {
        public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
        {
            var method = HandlerSupport.Require(target, request).Name;
            if (method == "destroy")
            {
                surface.ViewportId = 0;
                surface.PendingViewport = default;
                session.DestroyObject(target);
                return;
            }
            if (surface.Gone)
            {
                throw new ProtocolErrorException(target.Id, 3, "viewport surface has been destroyed");
            }

            if (method == "set_source")
            {
                var x = request.Arguments[0].AsFixed().ToDouble();
                var y = request.Arguments[1].AsFixed().ToDouble();
                var width = request.Arguments[2].AsFixed().ToDouble();
                var height = request.Arguments[3].AsFixed().ToDouble();
                ViewportSource? source = null;
                if ((x, y, width, height) != (-1, -1, -1, -1))
                {
                    if (x < 0 || y < 0 || width <= 0 || height <= 0)
                    {
                        throw new ProtocolErrorException(target.Id, 0, "invalid viewport source rectangle");
                    }
                    source = new ViewportSource(x, y, width, height);
                }
                surface.PendingViewport = surface.PendingViewport with { Source = source };
            }
            else
            {
                var width = request.Arguments[0].AsInt32();
                var height = request.Arguments[1].AsInt32();
                (int, int)? destination = null;
                if ((width, height) != (-1, -1))
                {
                    if (width <= 0 || height <= 0)
                    {
                        throw new ProtocolErrorException(target.Id, 0, "invalid viewport destination size");
                    }
                    destination = (width, height);
                }
                surface.PendingViewport = surface.PendingViewport with { Destination = destination };
            }
        }
    }
}
