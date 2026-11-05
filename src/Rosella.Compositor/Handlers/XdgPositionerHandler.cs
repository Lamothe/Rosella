using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// Which way a surface extends along one axis, from the anchor point:
/// negative is left (x) or up (y), positive is right (x) or down (y), and
/// none is centred. The xdg anchor and gravity enums decode to a pair of
/// these — see <see cref="Decode"/>.
/// </summary>
internal enum AxisEdge
{
    Negative,
    Centre,
    Positive,
}

/// <summary>
/// An immutable copy of a positioner's rules, taken when a popup uses it:
/// the spec requires later changes to the positioner to have no effect on
/// earlier usages.
/// </summary>
internal sealed record XdgPlacement(
    int Width,
    int Height,
    (int X, int Y, int Width, int Height) AnchorRect,
    uint Anchor,
    uint Gravity,
    (int X, int Y) Offset,
    uint ConstraintAdjustment)
{
    private const uint SlideX = 1;
    private const uint SlideY = 2;
    private const uint FlipX = 4;
    private const uint FlipY = 8;
    private const uint ResizeX = 16;
    private const uint ResizeY = 32;

    /// <summary>
    /// The popup's window position in scene coordinates and its size after
    /// constraint adjustment, per xdg_positioner: the anchor point on the
    /// anchor rectangle is aligned with the gravity point on the popup, the
    /// offset is added, and then flip, slide and resize are applied in that
    /// order until the popup fits inside <paramref name="bounds"/> (the work
    /// area).
    /// </summary>
    /// <param name="anchorRect">
    /// The anchor rectangle in scene coordinates: the parent's window origin
    /// plus the positioner's anchor rect (scaled the way the parent is).
    /// </param>
    internal (int X, int Y, int Width, int Height) Resolve((int X, int Y, int Width, int Height) anchorRect, Rect bounds)
    {
        var (anchorX, anchorY) = Decode(Anchor);
        var (gravityX, gravityY) = Decode(Gravity);

        var (x, width) = FitAxis(
            anchorX, gravityX, anchorRect.X, anchorRect.Width, Offset.X, Width,
            bounds.X, bounds.X + bounds.W,
            (ConstraintAdjustment & FlipX) != 0,
            (ConstraintAdjustment & SlideX) != 0,
            (ConstraintAdjustment & ResizeX) != 0);

        var (y, height) = FitAxis(
            anchorY, gravityY, anchorRect.Y, anchorRect.Height, Offset.Y, Height,
            bounds.Y, bounds.Y + bounds.H,
            (ConstraintAdjustment & FlipY) != 0,
            (ConstraintAdjustment & SlideY) != 0,
            (ConstraintAdjustment & ResizeY) != 0);

        return (x, y, width, height);
    }

    /// <summary>
    /// One axis of the placement. <paramref name="lo"/> and <paramref name="hi"/>
    /// are the inclusive start and exclusive end of the positioning boundary.
    /// </summary>
    private static (int Position, int Size) FitAxis(
        AxisEdge anchorEdge,
        AxisEdge gravityEdge,
        int rectStart,
        int rectLength,
        int offset,
        int size,
        int lo,
        int hi,
        bool flip,
        bool slide,
        bool resize)
    {
        var position = PlaceAxis(anchorEdge, gravityEdge, rectStart, rectLength, offset, size);

        // Precedence is flip, then slide, then resize (xdg-shell.xml,
        // constraint_adjustment): each step only runs while the popup is
        // still constrained on this axis.
        if (flip && !Fits(position, size, lo, hi))
        {
            var mirrored = PlaceAxis(Mirror(anchorEdge), Mirror(gravityEdge), rectStart, rectLength, offset, size);
            if (Fits(mirrored, size, lo, hi))
            {
                return (mirrored, size);
            }
        }

        if (slide && !Fits(position, size, lo, hi))
        {
            position = Clamp(position, size, lo, hi, gravityEdge);
        }

        if (resize && !Fits(position, size, lo, hi))
        {
            // The edge nearest the anchor point stays put and the far edge
            // is pulled in, so a tall menu keeps its first entries and a
            // tooltip above its anchor keeps its last ones.
            if (gravityEdge == AxisEdge.Negative)
            {
                var anchored = Math.Clamp(position + size, lo, hi);
                size = Math.Max(0, Math.Min(size, anchored - lo));
                position = anchored - size;
            }
            else if (gravityEdge == AxisEdge.Positive)
            {
                var anchored = Math.Clamp(position, lo, hi);
                size = Math.Max(0, Math.Min(size, hi - anchored));
                position = anchored;
            }
            else
            {
                size = Math.Max(0, Math.Min(size, hi - lo));
                position = Clamp(position, size, lo, hi, gravityEdge);
            }
        }

        return (position, size);
    }

    private static bool Fits(int position, int size, int lo, int hi) => position >= lo && position + size <= hi;

    private static int Clamp(int position, int size, int lo, int hi, AxisEdge gravity)
    {
        // When the popup cannot fit, keep the end the gravity points away
        // from: a dropdown extending down keeps its top, a tooltip extending
        // up keeps its bottom.
        if (gravity == AxisEdge.Negative)
        {
            if (position < lo)
            {
                position = lo;
            }

            if (position + size > hi)
            {
                position = hi - size;
            }
        }
        else
        {
            if (position + size > hi)
            {
                position = hi - size;
            }

            if (position < lo)
            {
                position = lo;
            }
        }

        return position;
    }

    /// <summary>
    /// The popup's start on one axis: its gravity point sits on the anchor
    /// point of the anchor rectangle, plus the offset.
    /// </summary>
    private static int PlaceAxis(AxisEdge anchorEdge, AxisEdge gravityEdge, int rectStart, int rectLength, int offset, int size)
    {
        var point = anchorEdge switch
        {
            AxisEdge.Negative => rectStart,
            AxisEdge.Positive => rectStart + rectLength,
            _ => rectStart + (rectLength / 2),
        };

        var position = gravityEdge switch
        {
            // Gravity negative: the popup extends towards the negative side,
            // so its far edge rests on the anchor point.
            AxisEdge.Negative => point - size,
            AxisEdge.Positive => point,
            _ => point - (size / 2),
        };

        return position + offset;
    }

    private static AxisEdge Mirror(AxisEdge edge) => edge switch
    {
        AxisEdge.Negative => AxisEdge.Positive,
        AxisEdge.Positive => AxisEdge.Negative,
        _ => AxisEdge.Centre,
    };

    /// <summary>
    /// The stable xdg anchor/gravity enum: none 0, top 1, bottom 2, left 3,
    /// right 4, top_left 5, bottom_left 6, top_right 7, bottom_right 8. Not
    /// a bitfield — left|right is not expressible, which is why the pairs are
    /// spelled out.
    /// </summary>
    internal static (AxisEdge X, AxisEdge Y) Decode(uint value) => value switch
    {
        1 => (AxisEdge.Centre, AxisEdge.Negative), // top
        2 => (AxisEdge.Centre, AxisEdge.Positive), // bottom
        3 => (AxisEdge.Negative, AxisEdge.Centre), // left
        4 => (AxisEdge.Positive, AxisEdge.Centre), // right
        5 => (AxisEdge.Negative, AxisEdge.Negative), // top_left
        6 => (AxisEdge.Negative, AxisEdge.Positive), // bottom_left
        7 => (AxisEdge.Positive, AxisEdge.Negative), // top_right
        8 => (AxisEdge.Positive, AxisEdge.Positive), // bottom_right
        _ => (AxisEdge.Centre, AxisEdge.Centre), // none
    };
}

/// <summary>
/// Handles <c>xdg_positioner</c>: collects the placement rules a popup is
/// created with. The whole rule set is honoured — anchor, gravity, offset
/// and constraint adjustment — because dropping any of them puts real menus
/// off the edge of the screen.
/// </summary>
internal sealed class XdgPositionerHandler : IRequestHandler
{
    private bool _hasAnchorRect;

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        switch (description.Name)
        {
            case "destroy":
                session.DestroyObject(target);
                break;

            case "set_size":
            {
                var width = request.Arguments[0].AsInt32();
                var height = request.Arguments[1].AsInt32();
                if (width <= 0 || height <= 0)
                {
                    throw new ProtocolErrorException(target.Id, XdgErrors.InvalidInput, "positioner size must be positive");
                }

                Size = (width, height);
                HasSize = true;
                break;
            }

            case "set_anchor_rect":
            {
                var width = request.Arguments[2].AsInt32();
                var height = request.Arguments[3].AsInt32();
                if (width < 0 || height < 0)
                {
                    throw new ProtocolErrorException(target.Id, XdgErrors.InvalidInput, "anchor rectangle must not be negative");
                }

                AnchorRect = (request.Arguments[0].AsInt32(), request.Arguments[1].AsInt32(), width, height);
                _hasAnchorRect = true;
                break;
            }

            case "set_anchor":
            case "set_gravity":
            {
                var value = request.Arguments[0].AsUInt32();
                if (value > 8)
                {
                    throw new ProtocolErrorException(target.Id, XdgErrors.InvalidInput, "invalid anchor or gravity");
                }

                if (description.Name == "set_anchor")
                {
                    Anchor = value;
                }
                else
                {
                    Gravity = value;
                }

                break;
            }

            case "set_constraint_adjustment":
                ConstraintAdjustment = request.Arguments[0].AsUInt32();
                break;

            case "set_offset":
                Offset = (request.Arguments[0].AsInt32(), request.Arguments[1].AsInt32());
                break;

            case "set_reactive":
            case "set_parent_size":
            case "set_parent_configure":
                // Stored for later milestones; reactive placement needs the
                // shell to watch parent state.
                break;

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }

    /// <summary>False until set_size has been called with a positive size.</summary>
    internal bool HasSize { get; private set; }

    /// <summary>False until set_anchor_rect has been called; both complete a positioner.</summary>
    internal bool IsComplete => HasSize && _hasAnchorRect;

    internal (int Width, int Height) Size { get; private set; }

    internal (int X, int Y, int Width, int Height) AnchorRect { get; private set; }

    internal (int X, int Y) Offset { get; private set; }

    internal uint Anchor { get; private set; }

    internal uint Gravity { get; private set; }

    internal uint ConstraintAdjustment { get; private set; }

    /// <summary>Freezes the rules for one popup usage.</summary>
    internal XdgPlacement Snapshot()
    {
        return new XdgPlacement(Size.Width, Size.Height, AnchorRect, Anchor, Gravity, Offset, ConstraintAdjustment);
    }
}