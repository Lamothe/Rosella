using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// Handles <c>xdg_toplevel</c>: the window's identity (title, app id) and
/// its state requests. Ordinary windows fill the work area. Dialogs can choose
/// their size and are centered by the shell.
/// </summary>
internal sealed class XdgToplevelHandler : IRequestHandler, IXdgRoleObject
{
    private static readonly HashSet<uint> ValidResizeEdges = [0, 1, 2, 4, 5, 6, 8, 9, 10];

    private readonly XdgSurfaceHandler _surface;
    private ClientSession? _session;
    private ServerObject? _self;
    private XdgToplevelHandler? _parent;
    private bool _capabilitiesSent;
    private volatile bool _fullscreen;
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

    // The login prompt is a standalone GTK toplevel, with its own app ID.
    private bool IsKeyringPrompt => AppId is "gcr-prompter" or "org.gnome.keyring.SystemPrompter" or "org.gnome.keyring.PrivatePrompter";

    // Zenity prompts often have no parent because another process launched them.
    private bool UsesNaturalSize => IsKeyringPrompt || (!_fullscreen &&
        (_parent is { IsMapped: true } || AppId is "zenity" or "org.gnome.Zenity"));

    /// <summary>Whether the window holds keyboard focus (xdg state 4, activated).</summary>
    internal bool Activated { get; set; }

    internal SurfaceHandler Surface => _surface.Surface;

    internal bool IsMapped => _surface.IsMapped;

    internal void ResetFullscreen() => _fullscreen = false;

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
                var usedNaturalSize = UsesNaturalSize;
                var parentId = request.Arguments[0].AsObject();
                if (parentId == 0)
                {
                    _parent = null;
                }
                else
                {
                    if (!session.TryGetObject(parentId, out var parent) || parent.Handler is not XdgToplevelHandler toplevel || IsSelfOrAncestor(toplevel))
                    {
                        throw new ProtocolErrorException(target.Id, XdgErrors.InvalidParent, $"invalid toplevel parent {parentId}");
                    }

                    Scene.AttachToParent(_surface.Surface, toplevel._surface.Surface, _ => _parent = toplevel);
                }

                if (usedNaturalSize != UsesNaturalSize)
                {
                    _surface.SendStateConfigure();
                }
                break;
            }

            case "set_title":
                Title = request.Arguments[0].AsNullableString() ?? "";
                _surface.MarkSceneDirty();
                break;

            case "set_app_id":
            {
                var usedNaturalSize = UsesNaturalSize;
                AppId = request.Arguments[0].AsNullableString() ?? "";
                if (usedNaturalSize != UsesNaturalSize)
                {
                    _surface.SendStateConfigure();
                }
                _surface.MarkSceneDirty();
                break;
            }

            case "show_window_menu":
            case "move":
                // Interactive grabs need input (M6); legal to ignore.
                break;

            case "resize":
            {
                var edge = request.Arguments[2].AsUInt32();
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
                break;
            }

            case "set_min_size":
            {
                var width = request.Arguments[0].AsInt32();
                var height = request.Arguments[1].AsInt32();
                ValidateSizeHint(target, width, height);
                _minWidth = width;
                _minHeight = height;
                break;
            }

            case "set_maximized":
            case "unset_maximized":
                // The normal window policy remains maximised.
                _surface.SendStateConfigure();
                break;

            case "set_fullscreen":
            case "unset_fullscreen":
                // There is one output, so it is used for any fullscreen request.
                _fullscreen = description.Name == "set_fullscreen";
                _surface.SendStateConfigure();
                break;

            case "set_minimized":
                // Compositor-side hide; the taskbar is the way back (the
                // protocol has no "unminimize" request).
                _surface.Minimize();
                break;

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }

    /// <summary>Sends the window state and the size chosen by the window policy.</summary>
    public void SendRoleConfigure()
    {
        if (!_capabilitiesSent && _self!.Version >= 5)
        {
            _capabilitiesSent = true;
            _session!.SendEvent(_self, "wm_capabilities",
                WireArgument.Array([.. BitConverter.GetBytes(3u), .. BitConverter.GetBytes(4u)]));
        }

        var (workWidth, workHeight) = _surface.WorkArea;
        var states = new List<byte>();
        // xdg_toplevel.state: fullscreen = 2, maximized = 1.
        if (UsesNaturalSize)
        {
            (workWidth, workHeight) = (0, 0);
        }
        else
        {
            states.AddRange(BitConverter.GetBytes(_fullscreen ? 2u : 1u));
        }
        if (Activated)
        {
            states.AddRange(BitConverter.GetBytes(4u));
        }

        _session!.SendEvent(_self!, "configure", WireArgument.Int32(workWidth), WireArgument.Int32(workHeight), WireArgument.Array([.. states]));
    }

    /// <summary>
    /// Places normal windows at the work-area origin and centers dialogs within it.
    /// </summary>
    public (int Width, int Height, (int X, int Y)? Corner) GetPlacement(SurfaceHandler surface, (int X, int Y, int Width, int Height) geometry)
    {
        var (workWidth, workHeight) = _surface.WorkArea;
        if (UsesNaturalSize)
        {
            var factor = Math.Min(1d, Math.Min(workWidth / (double)Math.Max(1, geometry.Width),
                workHeight / (double)Math.Max(1, geometry.Height)));
            var width = Math.Max(1, (int)(geometry.Width * factor));
            var height = Math.Max(1, (int)(geometry.Height * factor));
            return (width, height, ((workWidth - width) / 2, (workHeight - height) / 2));
        }

        return (workWidth, workHeight, null);
    }

    /// <summary>Asks the compositor to close the window (M6+ input, M7 shell).</summary>
    internal void Close()
    {
        if (_session is { } session && _self is { } self)
        {
            session.TrySendEvent(self, "close");
        }
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

    internal SurfaceHandler RootSurface
    {
        get
        {
            var root = this;
            while (root._parent is { } parent && parent._surface.IsMapped)
            {
                root = parent;
            }
            return root._surface.Surface;
        }
    }

    private static void ValidateSizeHint(ServerObject target, int width, int height)
    {
        if (width < 0 || height < 0)
        {
            throw new ProtocolErrorException(target.Id, XdgErrors.ToplevelInvalidSize, "size hints must not be negative");
        }
    }

    // Both hints are double-buffered. A pair of requests may temporarily
    // cross while replacing both bounds before a single surface commit.
    internal void ValidateSizeRange()
    {
        if ((_maxWidth != 0 && _minWidth != 0 && _maxWidth < _minWidth)
            || (_maxHeight != 0 && _minHeight != 0 && _maxHeight < _minHeight))
        {
            throw new ProtocolErrorException(_self!.Id, XdgErrors.ToplevelInvalidSize, "maximum size must not be smaller than minimum size");
        }
    }
}
