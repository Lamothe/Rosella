using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// Handles <c>wl_region</c>: the rectangles a client adds and subtracts.
/// A surface copies the region when it is given one (set_opaque_region),
/// so the interesting operation here is <see cref="Snapshot"/>. A
/// subtraction is not modelled — the region is then reported as complex,
/// and a consumer that needs certainty answers no rather than claiming
/// coverage it cannot prove.
/// </summary>
internal sealed class RegionHandler : IRequestHandler
{
    private readonly List<DamageRect> _rects = [];
    private bool _subtracted;

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        switch (description.Name)
        {
            case "destroy": // destructor
                session.DestroyObject(target);
                break;

            case "add":
            {
                var x = request.Arguments[0].AsInt32();
                var y = request.Arguments[1].AsInt32();
                var width = request.Arguments[2].AsInt32();
                var height = request.Arguments[3].AsInt32();

                // An empty rectangle adds nothing; a negative one is a
                // client bug and treating it as empty keeps the region
                // conservative rather than inventing coverage.
                if (width > 0 && height > 0)
                {
                    _rects.Add(new DamageRect(x, y, width, height));
                }

                break;
            }

            case "subtract":
                // Not modelled: remember that the region is no longer a
                // plain union, so Covers() will refuse to claim it.
                _subtracted = true;
                break;

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }

    /// <summary>The region as it stands now (surface requests copy it).</summary>
    internal RegionSnapshot Snapshot() => new([.. _rects], _subtracted);
}

/// <summary>
/// A region frozen at the moment a surface took it. Coordinates are
/// surface-local; <see cref="Covers"/> answers whether a single rectangle
/// covers a whole surface of that size, which is the only case a renderer
/// may treat as certainly opaque.
/// </summary>
internal readonly record struct RegionSnapshot(DamageRect[] Rects, bool Complex)
{
    /// <summary>
    /// Whether one rectangle covers the surface a buffer of this size
    /// describes. The comparison is in buffer pixels on purpose: a buffer
    /// width that is not a multiple of the scale would otherwise be
    /// truncated (33 px at scale 2 is 16.5 surface units) and a region that
    /// stops short of the last pixel would be believed.
    /// </summary>
    internal bool Covers(int bufferWidth, int bufferHeight, int scale)
    {
        if (Complex || bufferWidth <= 0 || bufferHeight <= 0)
        {
            return false;
        }

        scale = Math.Max(1, scale);
        foreach (var rect in Rects)
        {
            if (rect.X <= 0 && rect.Y <= 0
                && (long)rect.W * scale >= bufferWidth
                && (long)rect.H * scale >= bufferHeight)
            {
                return true;
            }
        }

        return false;
    }
}