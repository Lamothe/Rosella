using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>A role object on an xdg_surface: the toplevel or popup half.</summary>
internal interface IXdgRoleObject
{
    /// <summary>Sends the role-specific half of a configure (before the serial).</summary>
    void SendRoleConfigure();

    /// <summary>The window size and, for popups, its fixed corner; a null corner means policy placement.</summary>
    (int Width, int Height, (int X, int Y)? Corner) GetPlacement(SurfaceHandler surface, (int X, int Y, int Width, int Height) geometry);
}

/// <summary>
/// Handles <c>xdg_surface</c> and drives the mapping state machine:
/// commit → configure → ack_configure → buffer commit = mapped. Content is
/// placed once per map by the window policy (toplevels) or the positioner
/// (popups); the window geometry shifts the content origin and is
/// double-buffered. Unmapping (a null-buffer commit) resets the surface to
/// its pre-initial-commit state, as the spec requires the mapping dance to
/// be repeated.
/// </summary>
internal sealed class XdgSurfaceHandler : IRequestHandler, ISurfaceRole, IDisposable
{
    private readonly Scene _scene;
    private readonly SurfaceHandler _surface;

    /// <summary>
    /// Guards the configure state machine: pending serials, the configure
    /// pair emission (so two configures never interleave on the wire), and
    /// activation. Session threads and input threads both touch it.
    /// </summary>
    private readonly object _configureGate = new();
    private readonly HashSet<uint> _pendingSerials = new();
    private ClientSession? _session;
    private ServerObject? _self;
    private ServerObject? _wmBase;
    private IXdgRoleObject? _roleObject;
    private XdgToplevelHandler? _toplevel;
    private XdgSurfaceHandler? _popupParent;
    private Action? _onEnded;
    private bool _constructed;
    private bool _destroyed;
    private bool _surfaceDefunct;
    private bool _initialConfigureSent;
    private bool _ackedConfigure;
    private bool _mapped;
    private uint _lastAckedSerial;

    /// <summary>The highest configure serial sent so far.</summary>
    private uint _lastSentSerial;

    /// <summary>Serials at or below this predate the last unmap: ackable, but not mapping consent.</summary>
    private uint _serialFloor;

    /// <summary>Highest serial evicted from the bounded pending set: ackable, never consent.</summary>
    private uint _evictedSerialFloor;
    private (int X, int Y, int Width, int Height) _geometry;
    private (int X, int Y, int Width, int Height)? _pendingGeometry;
    private (int X, int Y) _windowPosition;
    private (int Width, int Height) _policySize;

    public XdgSurfaceHandler(Scene scene, SurfaceHandler surface)
    {
        _scene = scene;
        _surface = surface;
    }

    public void Bind(ClientSession session, ServerObject self, ServerObject wmBase, Action onEnded)
    {
        _session = session;
        _self = self;
        _wmBase = wmBase;
        _onEnded = onEnded;
    }

    /// <summary>The window's visible corner sits at the work-area origin (maximised).</summary>
    internal (int X, int Y) WindowPosition => _windowPosition;

    /// <summary>The work area of the output this surface is on (logical pixels).</summary>
    internal (int Width, int Height) WorkArea
    {
        get
        {
            var area = _scene.WorkArea;
            return (area.W, area.H);
        }
    }

    /// <summary>The parent for popups: the window-geometry corner on screen.</summary>
    internal SurfaceHandler Surface => _surface;

    /// <summary>The xdg_wm_base object that created this surface, for its error enum.</summary>
    internal ServerObject WmBase => _wmBase!;

    /// <summary>True while a role object exists and the surface is not defunct.</summary>
    internal bool RoleIsAlive => _roleObject is not null && !_surfaceDefunct;

    /// <summary>The toplevel, for parent tracking and the shell.</summary>
    internal XdgToplevelHandler? Toplevel => _toplevel;

    /// <summary>The popup parent role, when this surface is a popup.</summary>
    internal XdgSurfaceHandler? PopupParent => _popupParent;

    /// <summary>True once the xdg_surface object is gone (session end or destroy).</summary>
    public bool IsDefunct => _destroyed;

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        switch (description.Name)
        {
            case "destroy":
                if (_roleObject is not null)
                {
                    throw new ProtocolErrorException(target.Id, XdgErrors.DefunctRoleObject, "xdg_surface destroyed before its role object");
                }

                End(target);
                break;

            case "get_toplevel":
                CreateRole(session, target, new XdgToplevelHandler(this), "xdg_toplevel", request.Arguments[0].AsNewId());
                break;

            case "get_popup":
                GetPopup(session, target, request);
                break;

            case "set_window_geometry":
            {
                var width = request.Arguments[2].AsInt32();
                var height = request.Arguments[3].AsInt32();
                if (width <= 0 || height <= 0)
                {
                    throw new ProtocolErrorException(target.Id, XdgErrors.InvalidSize, "window geometry must be positive");
                }

                // Double-buffered: applied at the next commit.
                _pendingGeometry = (request.Arguments[0].AsInt32(), request.Arguments[1].AsInt32(), width, height);
                break;
            }

            case "ack_configure":
            {
                var serial = request.Arguments[0].AsUInt32();
                lock (_configureGate)
                {
                    // Acking a serial consumes it and every older pending one.
                    // Invalid only per spec: never sent, or at/below the last
                    // ack (covers duplicate and out-of-order acks).
                    if (serial <= _lastAckedSerial)
                    {
                        throw new ProtocolErrorException(target.Id, XdgErrors.InvalidSerial, $"configure serial {serial} cannot be acked");
                    }

                    var fromPending = _pendingSerials.Remove(serial);
                    if (!fromPending && serial > _evictedSerialFloor)
                    {
                        // Not in the sent set and never evicted: never sent.
                        throw new ProtocolErrorException(target.Id, XdgErrors.InvalidSerial, $"configure serial {serial} cannot be acked");
                    }

                    _pendingSerials.RemoveWhere(s => s <= serial);
                    _lastAckedSerial = serial;

                    // Consent to map comes only from a configure sent after
                    // the last unmap (the mapping dance restarts there) and
                    // still in the live set — an evicted or pre-unmap serial
                    // is a legal ack, not a green light for a buffer.
                    _ackedConfigure |= fromPending && serial > _serialFloor;
                }

                break;
            }

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }

    /// <summary>The commit-time state machine (the wl_surface's role hook).</summary>
    public void OnCommit(SurfaceHandler surface, bool contentChanged, bool hasContent, int contentWidth, int contentHeight)
    {
        if (_destroyed || _surfaceDefunct)
        {
            return;
        }

        if (_roleObject is null)
        {
            if (!_constructed)
            {
                throw new ProtocolErrorException(_self!.Id, XdgErrors.NotConstructed, "xdg_surface committed without a role object");
            }

            // The role object is gone: the surface is permanently unmappable.
            if (contentChanged && hasContent)
            {
                throw new ProtocolErrorException(_self!.Id, XdgErrors.UnconfiguredBuffer, "buffer attached to a role-less xdg_surface");
            }

            return;
        }

        if (_pendingGeometry is { } geometry)
        {
            _pendingGeometry = null;
            _geometry = geometry;
            if (_mapped)
            {
                PlaceContent(surface, surface.ContentWidth, surface.ContentHeight);
            }
        }

        if (!_initialConfigureSent)
        {
            lock (_configureGate)
            {
                if (!_initialConfigureSent)
                {
                    SendConfigure();
                    _initialConfigureSent = true;
                }
            }
        }

        if (!contentChanged)
        {
            return;
        }

        if (hasContent)
        {
            if (!_ackedConfigure)
            {
                throw new ProtocolErrorException(_self!.Id, XdgErrors.UnconfiguredBuffer, "buffer attached before ack_configure");
            }

            Map(surface, contentWidth, contentHeight);
        }
        else
        {
            Unmap(surface);

            // A newly-unmapped surface must repeat the mapping dance. Sent
            // serials stay ackable across the unmap (spec: only a serial
            // that was never sent, or one at/below the last ack, is invalid)
            // — but an ack of a pre-unmap serial is not consent to map the
            // next buffer: only a configure sent after this floor counts.
            lock (_configureGate)
            {
                _initialConfigureSent = false;
                _ackedConfigure = false;
                _serialFloor = _lastSentSerial;
            }
        }
    }

    /// <summary>The wl_surface died underneath the role: the role is defunct.</summary>
    public void OnSurfaceDestroyed()
    {
        Unmap(_surface);
        _surfaceDefunct = true;
    }

    /// <summary>The role object was destroyed: the surface is unmapped for good.</summary>
    internal void OnRoleDestroyed()
    {
        Unmap(_surface);

        // Same gate as the activation/configure path: the input thread
        // dereferences _roleObject under it and must never see a torn pair.
        lock (_configureGate)
        {
            _roleObject = null;
            _toplevel = null;
        }
    }

    /// <summary>Activation round-trip for input focus changes.</summary>
    internal void SetActivated(bool activated)
    {
        lock (_configureGate)
        {
            if (_roleObject is XdgToplevelHandler toplevel && toplevel.Activated != activated)
            {
                toplevel.Activated = activated;
                SendConfigure();
            }
        }
    }

    /// <summary>Asks the window to close (Alt+F4 in the input policy).</summary>
    internal void Close()
    {
        if (_roleObject is XdgToplevelHandler toplevel)
        {
            toplevel.Close();
        }
    }

    /// <summary>Sends xdg_surface.configure around the role's payload.</summary>
    internal void SendConfigure()
    {
        lock (_configureGate)
        {
            if (_roleObject is not { } roleObject)
            {
                return;
            }

            roleObject.SendRoleConfigure();
            var serial = _session!.NextSerial();
            _lastSentSerial = serial;
            _pendingSerials.Add(serial);
            if (_pendingSerials.Count > MaxPendingSerials)
            {
                // Bounded against a client that spams configure cycles
                // without acking. Evicted serials were still *sent*, so they
                // stay ackable (via _evictedSerialFloor) — they just never
                // count as mapping consent.
                var drop = _pendingSerials.OrderBy(s => s).Take(_pendingSerials.Count - MaxPendingSerials).ToArray();
                foreach (var old in drop)
                {
                    _pendingSerials.Remove(old);
                }

                _evictedSerialFloor = Math.Max(_evictedSerialFloor, drop[^1]);
            }

            _session.SendEvent(_self!, "configure", WireArgument.UInt32(serial));
        }
    }

    /// <summary>How many sent-but-unacked configure serials to remember.</summary>
    private const int MaxPendingSerials = 64;

    /// <summary>Session end: stop the wm_base surface count without sending.</summary>
    public void Dispose()
    {
        if (!_destroyed)
        {
            _destroyed = true;
            _onEnded?.Invoke();
        }
    }

    private void CreateRole(ClientSession session, ServerObject target, IXdgRoleObject role, string interfaceName, uint newId)
    {
        if (_constructed)
        {
            throw new ProtocolErrorException(target.Id, XdgErrors.AlreadyConstructed, "xdg_surface already has a role object");
        }

        _constructed = true;
        _roleObject = role;
        switch (role)
        {
            case XdgToplevelHandler toplevel:
                _toplevel = toplevel;
                toplevel.Bind(session, HandlerSupport.CreateChild(session, target, interfaceName, toplevel, newId));
                break;

            case XdgPopupHandler popup:
                popup.Bind(session, HandlerSupport.CreateChild(session, target, interfaceName, popup, newId));
                break;
        }
    }

    private void GetPopup(ClientSession session, ServerObject target, DecodedMessage request)
    {
        XdgSurfaceHandler? parent = null;
        var parentId = request.Arguments[1].AsObject();
        if (parentId != 0)
        {
            if (!session.TryGetObject(parentId, out var parentObject) || parentObject.Handler is not XdgSurfaceHandler parentSurface || !parentSurface.RoleIsAlive)
            {
                throw new ProtocolErrorException(_wmBase!.Id, XdgErrors.InvalidPopupParent, $"invalid popup parent {parentId}");
            }

            parent = parentSurface;
        }

        var positionerId = request.Arguments[2].AsObject();
        if (!session.TryGetObject(positionerId, out var positionerObject) || positionerObject.Handler is not XdgPositionerHandler positioner)
        {
            throw new ProtocolErrorException(_wmBase!.Id, XdgErrors.InvalidPositioner, $"invalid positioner {positionerId}");
        }

        if (!positioner.IsComplete)
        {
            throw new ProtocolErrorException(_wmBase!.Id, XdgErrors.InvalidPositioner, "positioner is incomplete (needs size and anchor rectangle)");
        }

        CreateRole(session, target, new XdgPopupHandler(this, parent, positioner.Snapshot()), "xdg_popup", request.Arguments[0].AsNewId());
        _popupParent = parent;
    }

    private void Map(SurfaceHandler surface, int contentWidth, int contentHeight)
    {
        if (!_mapped)
        {
            var geometry = _geometry.Width > 0 ? _geometry : (0, 0, contentWidth, contentHeight);
            var (width, height, corner) = _roleObject!.GetPlacement(surface, geometry);
            _windowPosition = corner ?? _scene.PlaceWindow(surface, width, height);
            _policySize = (width, height);
            _mapped = true;
        }

        PlaceContent(surface, contentWidth, contentHeight);
    }

    /// <summary>
    /// Places the content around the window geometry: popups keep their
    /// natural size at their corner; toplevels are maximised — the geometry
    /// rect fills the policy rect (the work area), and the content (CSD
    /// shadow margins and all) scales with it. Every commit re-places, so
    /// resizes and geometry changes track.
    /// </summary>
    private void PlaceContent(SurfaceHandler surface, int contentWidth, int contentHeight)
    {
        (int X, int Y, int Width, int Height) geometry =
            _geometry.Width > 0 ? _geometry : (0, 0, contentWidth, contentHeight);
        if (_roleObject is XdgPopupHandler)
        {
            surface.Place(_windowPosition.X - geometry.X, _windowPosition.Y - geometry.Y, contentWidth, contentHeight);
            surface.SetVisible(_windowPosition.X, _windowPosition.Y, geometry.Width, geometry.Height);
            return;
        }

        // Toplevels are maximised: the window geometry rect fills the policy
        // rect (the work area), and the content (CSD shadow margins and all)
        // scales with it.
        var (rectWidth, rectHeight) = _policySize;
        var placeWidth = geometry.Width > 0 ? (int)(((long)contentWidth * rectWidth) / geometry.Width) : contentWidth;
        var placeHeight = geometry.Height > 0 ? (int)(((long)contentHeight * rectHeight) / geometry.Height) : contentHeight;
        var placeX = _windowPosition.X - (geometry.Width > 0 ? (int)(((long)geometry.X * rectWidth) / geometry.Width) : geometry.X);
        var placeY = _windowPosition.Y - (geometry.Height > 0 ? (int)(((long)geometry.Y * rectHeight) / geometry.Height) : geometry.Y);
        surface.Place(placeX, placeY, placeWidth, placeHeight);
        surface.SetVisible(_windowPosition.X, _windowPosition.Y, rectWidth, rectHeight);
    }

    /// <summary>Dismisses this popup: popup_done to the client, then unmap.</summary>
    internal void DismissPopup()
    {
        if (_roleObject is XdgPopupHandler popup)
        {
            popup.Done();
            Unmap(_surface);
        }
    }

    private void Unmap(SurfaceHandler surface)
    {
        if (_mapped)
        {
            _mapped = false;
            surface.UnmapContent();
        }
    }

    private void End(ServerObject target)
    {
        if (_destroyed)
        {
            return;
        }

        _destroyed = true;
        _onEnded?.Invoke();
        _session!.DestroyObject(target);
    }
}
