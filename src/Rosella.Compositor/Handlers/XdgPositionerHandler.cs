using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// An immutable copy of a positioner's rules, taken when a popup uses it:
/// the spec requires later changes to the positioner to have no effect on
/// earlier usages.
/// </summary>
internal sealed record XdgPlacement(int Width, int Height, int AnchorX, int AnchorY, int OffsetX, int OffsetY)
{
    /// <summary>Where the popup goes, given the parent window's corner.</summary>
    internal (int X, int Y) PlaceAt((int X, int Y) parentWindowCorner)
    {
        return (parentWindowCorner.X + AnchorX + OffsetX, parentWindowCorner.Y + AnchorY + OffsetY);
    }

    /// <summary>The xdg_popup.configure x/y: relative to the parent window geometry.</summary>
    internal (int X, int Y) RelativeToParent => (AnchorX + OffsetX, AnchorY + OffsetY);
}

/// <summary>
/// Handles <c>xdg_positioner</c>: collects the placement hints a popup is
/// created with. Rosella's opinionated placement uses the anchor rectangle
/// and offset; anchor corners, gravity, and constraint adjustments are
/// parsed but not honoured.
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

    internal uint ConstraintAdjustment { get; private set; }

    /// <summary>Freezes the rules for one popup usage.</summary>
    internal XdgPlacement Snapshot()
    {
        return new XdgPlacement(Size.Width, Size.Height, AnchorRect.X, AnchorRect.Y, Offset.X, Offset.Y);
    }
}
