using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// Stores the ordered union and subtraction operations of a Wayland region.
/// </summary>
internal sealed class RegionHandler : IRequestHandler
{
    private readonly List<RegionOperation> _operations = [];

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        switch (description.Name)
        {
            case "destroy": // destructor
                session.DestroyObject(target);
                break;

            case "add":
            case "subtract":
            {
                var x = request.Arguments[0].AsInt32();
                var y = request.Arguments[1].AsInt32();
                var width = request.Arguments[2].AsInt32();
                var height = request.Arguments[3].AsInt32();

                if (width > 0 && height > 0)
                {
                    _operations.Add(new RegionOperation(new DamageRect(x, y, width, height), description.Name == "add"));
                }

                break;
            }

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }

    /// <summary>The region as it stands now (surface requests copy it).</summary>
    internal RegionSnapshot Snapshot() => new([.. _operations]);
}

internal readonly record struct RegionOperation(DamageRect Rect, bool Add);

/// <summary>
/// A region frozen at the moment a surface took it. Coordinates are
/// surface-local; <see cref="Covers"/> answers whether a single rectangle
/// covers a whole surface of that size, which is the only case a renderer
/// may treat as certainly opaque.
/// </summary>
internal readonly record struct RegionSnapshot(RegionOperation[] Operations)
{
    internal bool Contains(long x, long y)
    {
        // At a given point the last operation covering it determines membership.
        for (var i = Operations.Length - 1; i >= 0; i--)
        {
            var (rect, add) = Operations[i];
            if (x >= rect.X && x < (long)rect.X + rect.W && y >= rect.Y && y < (long)rect.Y + rect.H)
            {
                return add;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether one rectangle covers the surface a buffer of this size
    /// describes. The comparison is in buffer pixels on purpose: a buffer
    /// width that is not a multiple of the scale would otherwise be
    /// truncated (33 px at scale 2 is 16.5 surface units) and a region that
    /// stops short of the last pixel would be believed.
    /// </summary>
    internal bool Covers(int bufferWidth, int bufferHeight, int scale)
    {
        if (bufferWidth <= 0 || bufferHeight <= 0)
        {
            return false;
        }

        scale = Math.Max(1, scale);
        var covers = false;
        foreach (var (rect, add) in Operations)
        {
            if (!add)
            {
                covers = false;
                continue;
            }

            if (rect.X <= 0 && rect.Y <= 0
                && ((long)rect.X + rect.W) * scale >= bufferWidth
                && ((long)rect.Y + rect.H) * scale >= bufferHeight)
            {
                covers = true;
            }
        }

        return covers;
    }
}