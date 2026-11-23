using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// Handles <c>xdg_popup</c>: short-lived child windows (menus, tooltips)
/// positioned by their positioner. The positioner's rules are resolved in
/// full — anchor, gravity, offset and the flip/slide/resize constraint
/// adjustments against the work area — so a menu asked to hang below a
/// widget near the bottom edge comes back on screen. The rules are frozen
/// into an <see cref="XdgPlacement"/> when the popup uses them, as the spec
/// requires; the resulting position is re-resolved on every commit so
/// xdg_popup.reposition moves a live popup.
/// </summary>
internal sealed class XdgPopupHandler : IRequestHandler, IXdgRoleObject
{
    private readonly XdgSurfaceHandler _surface;
    private readonly XdgSurfaceHandler? _parent;
    private XdgPlacement _placement;
    private bool _grabbed;
    private uint _grabSerial;

    private ClientSession? _session;
    private ServerObject? _self;

    public XdgPopupHandler(XdgSurfaceHandler surface, XdgSurfaceHandler? parent, XdgPlacement placement)
    {
        _surface = surface;
        _parent = parent;
        _placement = placement;
    }

    internal void Bind(ClientSession session, ServerObject self)
    {
        _session = session;
        _self = self;
    }

    internal XdgPopupHandler? Parent => _parent?.PopupRole;

    internal SurfaceHandler? ParentSurface => _parent?.Surface;

    internal SurfaceHandler? RootParentSurface => _parent?.PopupRole?.RootParentSurface ?? _parent?.Surface;

    internal bool ParentIsPopup => _parent?.PopupRole is not null;

    internal SurfaceHandler Surface => _surface.Surface;

    internal bool IsGrabbed => _grabbed;

    internal uint GrabSerial => _grabSerial;

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        switch (description.Name)
        {
            case "destroy":
                if (_grabbed && _surface.Scene.TopmostPopupGrab != this)
                {
                    throw new ProtocolErrorException(_surface.WmBase.Id, XdgErrors.NotTheTopmostPopup, "only the topmost grabbed popup may be destroyed");
                }

                _surface.OnRoleDestroyed();
                session.DestroyObject(target);
                break;

            case "grab":
                Grab(request.Arguments[1].AsUInt32());
                break;

            case "reposition":
            {
                var positionerId = request.Arguments[0].AsObject();
                if (!session.TryGetObject(positionerId, out var positionerObject) || positionerObject.Handler is not XdgPositionerHandler positioner)
                {
                    throw new ProtocolErrorException(_surface.WmBase.Id, XdgErrors.InvalidPositioner, $"invalid positioner {positionerId}");
                }

                if (!positioner.IsComplete)
                {
                    throw new ProtocolErrorException(_surface.WmBase.Id, XdgErrors.InvalidPositioner, "positioner is incomplete (needs size and anchor rectangle)");
                }

                // repositioned comes before the configure pair, per the spec.
                _placement = positioner.Snapshot();
                var serial = request.Arguments[1].AsUInt32();
                _session!.SendEvent(_self!, "repositioned", WireArgument.UInt32(serial));
                _surface.SendConfigure();
                break;
            }

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }

    /// <summary>Sends xdg_popup.configure relative to the parent window geometry.</summary>
    public void SendRoleConfigure()
    {
        var (x, y, width, height) = ResolvePlacement();
        var origin = _parent?.WindowPosition ?? (0, 0);
        _session!.SendEvent(_self!, "configure", WireArgument.Int32(x - origin.X), WireArgument.Int32(y - origin.Y), WireArgument.Int32(width), WireArgument.Int32(height));
    }

    /// <summary>
    /// The popup's window position on screen (and its size after constraint
    /// adjustment), resolved from the parent's window geometry and the work
    /// area. Both the configure and the actual placement use this, so the
    /// client is told exactly what it gets.
    /// </summary>
    internal (int X, int Y, int Width, int Height) ResolvePlacement()
    {
        var origin = _parent?.WindowPosition ?? (0, 0);
        var (scaleX, scaleY) = _parent?.WindowScale ?? (1, 1);
        var anchor = _placement.AnchorRect;
        var sceneAnchor = (
            X: origin.X + (int)Math.Round(anchor.X * scaleX),
            Y: origin.Y + (int)Math.Round(anchor.Y * scaleY),
            Width: (int)Math.Round(anchor.Width * scaleX),
            Height: (int)Math.Round(anchor.Height * scaleY));
        return _placement.Resolve(sceneAnchor, _surface.Scene.WorkArea);
    }

    /// <summary>Popups go where the positioner says, not the window policy.</summary>
    public (int Width, int Height, (int X, int Y)? Corner) GetPlacement(SurfaceHandler surface, (int X, int Y, int Width, int Height) geometry)
    {
        var (x, y, width, height) = ResolvePlacement();
        return (width, height, (x, y));
    }

    private void Grab(uint serial)
    {
        if (_surface.Surface.IsMapped)
        {
            throw new ProtocolErrorException(_self!.Id, InvalidGrab, "xdg_popup.grab requested after the popup was mapped");
        }

        if (_session is null || _surface.Scene.Seat?.IsValidInputSerial(_session, serial) != true)
        {
            _surface.DismissPopup();
            return;
        }

        if (RootParentSurface?.XdgRole is not { IsMapped: true } || RootParentSurface.Hidden)
        {
            // A grab on a hidden window would hand the keyboard to a popup
            // the user cannot see.
            _surface.DismissPopup();
            return;
        }

        var popupStack = _surface.Scene.PopupGrabs;
        if (ParentIsPopup && (Parent is not { } parent || !parent.IsGrabbed || !popupStack.Contains(parent)))
        {
            if (_surface.IsMapped)
            {
                _surface.DismissPopup();
                return;
            }

            throw new ProtocolErrorException(_surface.WmBase.Id, XdgErrors.InvalidPopupParent, "a popup parent must have an active grab");
        }

        if (_surface.Scene.TryGrabPopup(this))
        {
            _grabbed = true;
            _grabSerial = serial;
            if (_surface.IsMapped)
            {
                _surface.Scene.Seat?.SetKeyboardFocus(Surface, []);
            }
        }
        else
        {
            _surface.DismissPopup();
        }
    }

    /// <summary>Tells the client the popup was dismissed and releases its grab.</summary>
    internal void Done()
    {
        if (_grabbed)
        {
            _surface.Scene.ReleasePopupGrab(this);
            _grabbed = false;
            RestoreFocusAfterGrab();
        }

        _session!.SendEvent(_self!, "popup_done");
    }

    /// <summary>
    /// Hands the keyboard back after a grab ends, and only then: if the user
    /// has moved the keyboard since the grab opened (Alt+Tab, a click
    /// elsewhere), ending the grab must not pull it back. The target is the
    /// next popup grab, else the window — and never a window that is hidden
    /// or no longer on screen. The held keys are passed through instead of
    /// cleared: a grab ending is not a key-up.
    /// </summary>
    private void RestoreFocusAfterGrab()
    {
        var scene = _surface.Scene;
        if (scene.Seat is not { } seat)
        {
            return;
        }

        var root = _surface.RootToplevelSurface;
        var ours = root is not null
            ? seat.KeyboardFocusIsWithin(root)
            : ReferenceEquals(seat.KeyboardFocus, Surface);
        if (!ours)
        {
            // The keyboard has moved on since the grab opened: leave it be.
            return;
        }

        var target = root is null
            ? scene.TopmostVisible()
            : scene.TopmostPopupGrab?.Surface ?? root;
        if (target is null || target.Hidden || !target.IsMapped || target.Gone)
        {
            target = scene.TopmostVisible();
        }

        seat.SetKeyboardFocus(target, seat.HeldKeys);
    }

    internal void DismissFromCompositor()
    {
        if (_grabbed)
        {
            _surface.DismissPopup();
        }
    }

    internal void RoleDestroyed()
    {
        if (_grabbed)
        {
            _surface.Scene.ReleasePopupGrab(this);
            _grabbed = false;
            RestoreFocusAfterGrab();
        }
    }

    internal void ParentDismissed()
    {
        if (_grabbed)
        {
            _surface.DismissPopup();
        }
    }

    private const uint InvalidGrab = 0;
}
