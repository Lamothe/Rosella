using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// Handles <c>xdg_toplevel</c>: the window's identity (title, app id) and
/// its state requests. Rosella's M5 policy is opinionated: the client picks
/// its own size (configure 0×0), placement is the shell's (window policy),
/// and interactive move/resize/maximise need input (M6) so they are inert
/// but legal.
/// </summary>
internal sealed class XdgToplevelHandler : IRequestHandler, IXdgRoleObject
{
    private static readonly HashSet<uint> ValidResizeEdges = [0, 1, 2, 4, 5, 6, 8, 9, 10];

    private readonly XdgSurfaceHandler _surface;
    private ClientSession? _session;
    private ServerObject? _self;
    private XdgToplevelHandler? _parent;
    private bool _capabilitiesSent;
    private int _minWidth;
    private int _minHeight;
    private int _maxWidth;
    private int _maxHeight;

    public XdgToplevelHandler(XdgSurfaceHandler surface)
    {
        _surface = surface;
    }

    /// <summary>The window title, for the shell (M7).</summary>
    internal string Title { get; private set; } = "";

    /// <summary>The app id, for the shell (M7).</summary>
    internal string AppId { get; private set; } = "";

    /// <summary>Whether the window holds keyboard focus (xdg state 4, activated).</summary>
    internal bool Activated { get; set; }

    internal void Bind(ClientSession session, ServerObject self)
    {
        _session = session;
        _self = self;
    }

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        switch (description.Name)
        {
            case "destroy":
                _surface.OnRoleDestroyed();
                session.DestroyObject(target);
                break;

            case "set_parent":
            {
                var parentId = request.Arguments[0].AsObject();
                if (parentId == 0)
                {
                    _parent = null;
                    break;
                }

                if (!session.TryGetObject(parentId, out var parent) || parent.Handler is not XdgToplevelHandler toplevel || IsSelfOrAncestor(toplevel))
                {
                    throw new ProtocolErrorException(target.Id, XdgErrors.InvalidParent, $"invalid toplevel parent {parentId}");
                }

                _parent = toplevel;
                break;
            }

            case "set_title":
                Title = request.Arguments[0].AsNullableString() ?? "";
                break;

            case "set_app_id":
                AppId = request.Arguments[0].AsNullableString() ?? "";
                break;

            case "show_window_menu":
            case "move":
                // Interactive grabs need input (M6); legal to ignore.
                break;

            case "resize":
            {
                var edge = request.Arguments[1].AsUInt32();
                if (!ValidResizeEdges.Contains(edge))
                {
                    throw new ProtocolErrorException(target.Id, XdgErrors.InvalidResizeEdge, $"invalid resize edge {edge}");
                }

                break;
            }

            case "set_max_size":
            {
                var width = request.Arguments[0].AsInt32();
                var height = request.Arguments[1].AsInt32();
                ValidateSizeHint(target, width, height);
                _maxWidth = width;
                _maxHeight = height;
                ValidateSizeRange(target);
                break;
            }

            case "set_min_size":
            {
                var width = request.Arguments[0].AsInt32();
                var height = request.Arguments[1].AsInt32();
                ValidateSizeHint(target, width, height);
                _minWidth = width;
                _minHeight = height;
                ValidateSizeRange(target);
                break;
            }

            case "set_maximized":
            case "unset_maximized":
            case "set_fullscreen":
            case "unset_fullscreen":
            case "set_minimized":
                // State changes the shell will honour from M7; M5 keeps the
                // window in its policy slot.
                break;

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }

    /// <summary>Sends xdg_toplevel.configure: the client picks its own size.</summary>
    public void SendRoleConfigure()
    {
        if (!_capabilitiesSent && _self!.Version >= 5)
        {
            // Required once before the first xdg_surface.configure. Rosella
            // has no interactive capabilities yet: the array is honest.
            _capabilitiesSent = true;
            _session!.SendEvent(_self, "wm_capabilities", WireArgument.Array([]));
        }

        // xdg_toplevel.state.activated = 4 (pinned against xdg-shell.xml).
        var states = Activated ? BitConverter.GetBytes(4u) : [];
        _session!.SendEvent(_self!, "configure", WireArgument.Int32(0), WireArgument.Int32(0), WireArgument.Array(states));
    }

    /// <summary>Toplevels are placed by the window policy.</summary>
    public (int Width, int Height, (int X, int Y)? Corner) GetPlacement(SurfaceHandler surface, (int X, int Y, int Width, int Height) geometry)
    {
        return (geometry.Width, geometry.Height, null);
    }

    /// <summary>Asks the compositor to close the window (M6+ input, M7 shell).</summary>
    internal void Close()
    {
        _session!.SendEvent(_self!, "close");
    }

    private bool IsSelfOrAncestor(XdgToplevelHandler candidate)
    {
        for (var current = candidate; current is not null; current = current._parent)
        {
            if (ReferenceEquals(current, this))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateSizeHint(ServerObject target, int width, int height)
    {
        if (width < 0 || height < 0)
        {
            throw new ProtocolErrorException(target.Id, XdgErrors.ToplevelInvalidSize, "size hints must not be negative");
        }
    }

    private void ValidateSizeRange(ServerObject target)
    {
        if ((_maxWidth != 0 && _minWidth != 0 && _maxWidth < _minWidth)
            || (_maxHeight != 0 && _minHeight != 0 && _maxHeight < _minHeight))
        {
            throw new ProtocolErrorException(target.Id, XdgErrors.ToplevelInvalidSize, "maximum size must not be smaller than minimum size");
        }
    }
}
