using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

/// <summary>
/// xdg_positioner placement as real toolkits use it: anchor and gravity pick
/// the point on the parent the popup hangs from, and constraint adjustment
/// keeps it on screen. The bug this file locks down: only the anchor
/// rectangle's top-left was honoured, so a Chrome menu anchored to the
/// bottom of a field (or the three-dot menu at the right edge) was placed
/// with its top-left at that point and ran off the screen.
/// </summary>
public class PopupPlacementTests
{
    // 1440x900 output, 32 px shell bar: the positioning boundary.
    private static readonly Rect Bounds = new(0, 0, 1440, 868);

    private static XdgPlacement Placement(
        (int X, int Y, int Width, int Height) anchorRect,
        uint anchor,
        uint gravity,
        (int X, int Y) offset,
        uint adjustment,
        int width = 200,
        int height = 100)
    {
        return new XdgPlacement(width, height, anchorRect, anchor, gravity, offset, adjustment);
    }

    [Fact]
    public void AnchorBottom_GravityBottom_PutsThePopupBelowTheAnchor()
    {
        // The autofill dropdown idiom: hang the popup under a widget.
        var placement = Placement((100, 300, 240, 30), anchor: 6 /* bottom_left */, gravity: 8 /* bottom_right */, (0, 0), adjustment: 0);
        var (x, y, width, height) = placement.Resolve((100, 300, 240, 30), Bounds);

        Assert.Equal(100, x);
        Assert.Equal(330, y); // the anchor rect's bottom edge, not its top
        Assert.Equal((200, 100), (width, height));
    }

    [Fact]
    public void AnchorTop_GravityTop_PutsThePopupAboveTheAnchor()
    {
        var placement = Placement((100, 300, 240, 30), anchor: 1 /* top */, gravity: 1 /* top */, (0, 0), adjustment: 0);
        var (_, y, _, _) = placement.Resolve((100, 300, 240, 30), Bounds);

        Assert.Equal(200, y); // popup bottom rests on the anchor's top edge
    }

    [Fact]
    public void AnchorNone_GravityNone_CentresThePopupOnTheAnchor()
    {
        // The spec's default: no anchor and no gravity centres the child.
        var placement = Placement((400, 400, 0, 0), anchor: 0, gravity: 0, (0, 0), adjustment: 0);
        var (x, y, _, _) = placement.Resolve((400, 400, 0, 0), Bounds);

        Assert.Equal(300, x); // 400 - 200/2
        Assert.Equal(350, y); // 400 - 100/2
    }

    [Fact]
    public void MenuAtTheBottomEdge_SlidesFullyOnScreen()
    {
        // A tall menu hung below a field near the bottom: without slide_y the
        // lower half is off screen — the reported Chrome symptom.
        var placement = Placement((100, 800, 240, 30), anchor: 6, gravity: 8, (0, 0), SlideY | SlideX, width: 240, height: 300);
        var (x, y, width, height) = placement.Resolve((100, 800, 240, 30), Bounds);

        Assert.Equal(100, x);
        Assert.Equal(868 - 300, y); // bottom edge on the work area's bottom
        Assert.Equal((240, 300), (width, height));
    }

    [Fact]
    public void MenuAtTheRightEdge_SlidesFullyOnScreen()
    {
        var placement = Placement((1380, 100, 40, 30), anchor: 5 /* top_left */, gravity: 8, (0, 0), SlideX | SlideY, width: 300, height: 200);
        var (x, y, width, height) = placement.Resolve((1380, 100, 40, 30), Bounds);

        Assert.Equal(1440 - 300, x);
        Assert.Equal(100, y); // the y axis was never constrained
        Assert.Equal((300, 200), (width, height));
    }

    [Fact]
    public void SlideOnOneAxis_LeavesTheOtherAxisWhereThePositionerPutIt()
    {
        // slide_x only: the popup's overflow at the bottom must remain.
        var placement = Placement((100, 800, 40, 30), anchor: 6, gravity: 8, (0, 0), SlideX, width: 200, height: 300);
        var (_, y, _, _) = placement.Resolve((100, 800, 40, 30), Bounds);

        Assert.Equal(830, y);
    }

    [Fact]
    public void FlipY_MovesThePopupAboveTheAnchorWhenBelowDoesNotFit()
    {
        // flip_y: a menu that would hang off the bottom is flipped above the
        // anchor rect instead of slid over it.
        var placement = Placement((100, 800, 240, 30), anchor: 6, gravity: 8, (0, 0), FlipY, width: 240, height: 300);
        var (_, y, _, _) = placement.Resolve((100, 800, 240, 30), Bounds);

        // Flipped: anchor becomes top, gravity becomes top, so the popup's
        // bottom rests on the anchor rect's top edge.
        Assert.Equal(800 - 300, y);
    }

    [Fact]
    public void FlipIsRejectedWhenTheFlippedPositionDoesNotFitEither()
    {
        // Nothing fits: the spec says keep the pre-flip position when the
        // flipped one is constrained too.
        var placement = Placement((100, 400, 240, 30), anchor: 6, gravity: 8, (0, 0), FlipY, width: 240, height: 900);
        var (_, y, _, _) = placement.Resolve((100, 400, 240, 30), Bounds);

        Assert.Equal(430, y);
    }

    [Fact]
    public void ResizeY_ShrinksAPopupTallerThanTheWorkArea()
    {
        // resize_y without slide or flip: the popup keeps its anchored top
        // edge and is shrunk so its bottom lands on the boundary.
        var placement = Placement((100, 100, 240, 30), anchor: 6, gravity: 8, (0, 0), ResizeY, width: 240, height: 2000);
        var (x, y, width, height) = placement.Resolve((100, 100, 240, 30), Bounds);

        Assert.Equal(100, x);
        Assert.Equal(130, y);
        Assert.Equal(240, width);
        Assert.Equal(Bounds.H - 130, height);

        // A popup taller than the whole boundary still fits, at the top.
        var whole = Placement((100, 0, 240, 0), anchor: 5, gravity: 8, (0, 0), ResizeY, width: 240, height: 2000);
        var (_, wholeY, _, wholeHeight) = whole.Resolve((100, 0, 240, 0), Bounds);
        Assert.Equal(0, wholeY);
        Assert.Equal(Bounds.H, wholeHeight);
    }

    [Fact]
    public void ResizeX_ShrinksAPopupWiderThanTheWorkArea()
    {
        var placement = Placement((100, 100, 240, 30), anchor: 0, gravity: 0, (0, 0), ResizeX, width: 2000, height: 100);
        var (x, _, width, _) = placement.Resolve((100, 100, 240, 30), Bounds);

        Assert.Equal(Bounds.W, width);
        Assert.InRange(x, Bounds.X, Bounds.X + Bounds.W - width);
    }

    [Fact]
    public void OffsetMovesThePopupFromTheAnchorPoint()
    {
        var placement = Placement((100, 300, 240, 30), anchor: 6, gravity: 8, (7, 11), adjustment: 0);
        var (x, y, _, _) = placement.Resolve((100, 300, 240, 30), Bounds);

        Assert.Equal(107, x);
        Assert.Equal(341, y);
    }

    [Fact]
    public void PopupAtTheLeftEdge_IsSlidRightRatherThanClipped()
    {
        // A popup extending leftwards from an anchor near the left edge
        // hangs off it: slide_x brings the whole popup in.
        var placement = Placement((10, 300, 40, 30), anchor: 3 /* left */, gravity: 3 /* left */, (0, 0), SlideX, width: 300, height: 100);
        var (x, _, _, _) = placement.Resolve((10, 300, 40, 30), Bounds);

        Assert.Equal(0, x);
    }

    [Fact]
    public void OversizedPopupKeepsTheEdgeTheGravityPointsAwayFrom()
    {
        // Extending downwards and too tall: keep the top (the first menu
        // entries), not the bottom.
        var down = Placement((100, 100, 40, 30), anchor: 6, gravity: 8, (0, 0), SlideY, width: 200, height: 1000);
        Assert.Equal(0, down.Resolve((100, 100, 40, 30), Bounds).Y);

        // Extending upwards and too tall: keep the bottom.
        var up = Placement((100, 600, 40, 30), anchor: 5, gravity: 5, (0, 0), SlideY, width: 200, height: 1000);
        Assert.Equal(Bounds.H - 1000, up.Resolve((100, 600, 40, 30), Bounds).Y);
    }

    /// <summary>End to end: the wire configure and the placed position agree.</summary>
    [Fact]
    public void ChromeStyleAutofillPopup_ConfigureAndPlacementBothFit()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);
        var parent = harness.MapWindow(8, 11, 12);

        // Chrome's autofill dropdown at the bottom of the work area:
        // anchor bottom_left of the field, gravity bottom_right, slide+flip
        // on y, and more entries than fit.
        const int fieldY = 820;
        harness.CreatePositioner(14, origin: (100, fieldY), size: (240, 30), popup: (240, 300),
            anchor: 6, gravity: 8, adjustment: SlideY | SlideX);
        harness.CreatePopupSurface(15, 16, 13, 14);

        var roleConfigure = harness.Client.ReadUntil(13, "configure");
        var configureX = roleConfigure.Arguments[0].AsInt32();
        var configureY = roleConfigure.Arguments[1].AsInt32();
        var configureWidth = roleConfigure.Arguments[2].AsInt32();
        var configureHeight = roleConfigure.Arguments[3].AsInt32();

        var configure = harness.Client.ReadUntil(16, "configure");
        harness.Client.SendRequest(16, "xdg_surface", "ack_configure", WireArgument.UInt32(configure.Arguments[0].AsUInt32()));
        harness.Attach(15);
        var popup = CompositorSessionTests.WaitFor(() => harness.Scene.Surfaces.FirstOrDefault(s => s.Id == 15), value => value is { IsMapped: true })!;

        // The configured position is where it actually goes, and the whole
        // popup is inside the work area.
        Assert.Equal(configureX, popup.Position.X - parent.Position.X);
        Assert.Equal(configureY, popup.Position.Y - parent.Position.Y);
        Assert.True(popup.Position.Y + popup.Visible.H <= harness.Scene.WorkArea.H, $"popup bottom {popup.Position.Y + popup.Visible.H} exceeds the work area {harness.Scene.WorkArea.H}");
        Assert.Equal((240, 300), (configureWidth, configureHeight));
    }

    [Fact]
    public void Reposition_MovesAMappedPopup()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);
        var parent = harness.MapWindow(8, 11, 12);

        harness.CreatePositioner(14, origin: (100, 200), size: (240, 30), popup: (200, 100), anchor: 6, gravity: 8, adjustment: 0);
        harness.CreatePopupSurface(15, 16, 13, 14);
        var configure = harness.Client.ReadUntil(16, "configure");
        harness.Client.SendRequest(16, "xdg_surface", "ack_configure", WireArgument.UInt32(configure.Arguments[0].AsUInt32()));
        harness.Attach(15);
        var popup = CompositorSessionTests.WaitFor(() => harness.Scene.Surfaces.FirstOrDefault(s => s.Id == 15), value => value is { IsMapped: true })!;
        Assert.Equal(230, popup.Position.Y - parent.Position.Y); // 200 + 30

        // Reposition to a lower anchor: the live popup must move.
        harness.CreatePositioner(17, origin: (300, 600), size: (240, 30), popup: (200, 100), anchor: 6, gravity: 8, adjustment: 0);
        harness.Client.SendRequest(13, "xdg_popup", "reposition", WireArgument.Object(17), WireArgument.UInt32(7));
        harness.Client.ReadUntil(13, "repositioned");
        var moved = harness.Client.ReadUntil(13, "configure");
        var reserial = harness.Client.ReadUntil(16, "configure");
        harness.Client.SendRequest(16, "xdg_surface", "ack_configure", WireArgument.UInt32(reserial.Arguments[0].AsUInt32()));
        harness.Attach(15);

        Assert.Equal(300, moved.Arguments[0].AsInt32());
        Assert.Equal(630, moved.Arguments[1].AsInt32());
        CompositorSessionTests.WaitFor(() => popup.Position.Y - parent.Position.Y, value => value == 630);
    }

    /// <summary>
    /// A submenu anchored inside a popup that the constraint solver resized:
    /// the anchor rectangle is in the parent's own coordinates, so it must
    /// not be scaled by the ratio between the size the compositor granted
    /// and the size the parent drew itself at. Scaling there put every
    /// submenu of a resized menu in the wrong place.
    /// </summary>
    [Fact]
    public void SubmenuOfAResizedPopup_IsAnchoredInTheParentsOwnCoordinates()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);
        harness.MapWindow(8, 11, 12);

        // Parent popup: 600 tall, anchored near the bottom edge, resize_y
        // only, so the solver grants it just 70 px.
        harness.CreatePositioner(14, origin: (100, 800), size: (40, 30), popup: (200, 600), anchor: 2, gravity: 2, adjustment: ResizeY);
        harness.CreatePopupSurface(15, 16, 13, 14);
        var first = harness.Client.ReadUntil(13, "configure");
        Assert.Equal(70, first.Arguments[3].AsInt32());
        var serial = harness.Client.ReadUntil(16, "configure");

        // Declaring a 600x600 window geometry makes the granted 70 px and the
        // geometry disagree, which is what used to distort the anchor below.
        harness.Client.SendRequest(16, "xdg_surface", "set_window_geometry", WireArgument.Int32(0), WireArgument.Int32(0), WireArgument.Int32(200), WireArgument.Int32(600));
        harness.Client.SendRequest(16, "xdg_surface", "ack_configure", WireArgument.UInt32(serial.Arguments[0].AsUInt32()));
        harness.Attach(15);
        var parentPopup = CompositorSessionTests.WaitFor(() => harness.Scene.Surfaces.FirstOrDefault(s => s.Id == 15), value => value is { IsMapped: true })!;
        var parentTop = parentPopup.Position.Y;

        // Child popup anchored at (0, 400) in the parent's coordinates.
        harness.CreatePositioner(17, origin: (0, 400), size: (10, 10), popup: (120, 80), anchor: 5, gravity: 8, adjustment: 0);
        harness.CreatePopupSurface(18, 19, 20, 17, parentXdgSurfaceId: 16);
        var childConfigure = harness.Client.ReadUntil(20, "configure");
        var childSerial = harness.Client.ReadUntil(19, "configure");
        harness.Client.SendRequest(19, "xdg_surface", "ack_configure", WireArgument.UInt32(childSerial.Arguments[0].AsUInt32()));
        harness.Attach(18);
        var child = CompositorSessionTests.WaitFor(() => harness.Scene.Surfaces.FirstOrDefault(s => s.Id == 18), value => value is { IsMapped: true })!;

        Assert.Equal(400, childConfigure.Arguments[1].AsInt32());
        Assert.Equal(parentTop + 400, child.Position.Y);
    }

    /// <summary>
    /// A toplevel that never called set_window_geometry is stretched to the
    /// work area (PlaceContent falls back to the content size), so its own
    /// coordinates are the content's, not the scene's. The anchor rectangle
    /// has to be stretched with it or the menu is placed at the unstretched
    /// point — hundreds of pixels from the widget on a small window.
    /// </summary>
    [Fact]
    public void AnchorRectOfAStretchedToplevelIsScaledWithTheWindow()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);
        var parent = harness.MapWindow(8, 11, 12, declareGeometry: false);

        // 32x32 content stretched to the 1440x900 work area: 45x in each axis.
        Assert.Equal(45, parent.Visible.W / 32);
        harness.CreatePositioner(14, origin: (16, 16), size: (1, 1), popup: (100, 50), anchor: 5, gravity: 8, adjustment: 0);
        harness.CreatePopupSurface(15, 16, 13, 14);

        var configure = harness.Client.ReadUntil(13, "configure");
        Assert.Equal(720, configure.Arguments[0].AsInt32());
        Assert.Equal(450, configure.Arguments[1].AsInt32());

        var serial = harness.Client.ReadUntil(16, "configure");
        harness.Client.SendRequest(16, "xdg_surface", "ack_configure", WireArgument.UInt32(serial.Arguments[0].AsUInt32()));
        harness.Attach(15);
        var popup = CompositorSessionTests.WaitFor(() => harness.Scene.Surfaces.FirstOrDefault(s => s.Id == 15), value => value is { IsMapped: true })!;

        Assert.Equal(parent.Position.X + 720, popup.Position.X);
        Assert.Equal(parent.Position.Y + 450, popup.Position.Y);
    }

    private const uint SlideX = 1;
    private const uint SlideY = 2;
    private const uint FlipX = 4;
    private const uint FlipY = 8;
    private const uint ResizeX = 16;
    private const uint ResizeY = 32;

    private sealed class Harness : IDisposable
    {
        private readonly DisplayServer _server;
        private readonly string _poolFile;

        public Harness()
        {
            Scene = new Scene();
            Scene.OutputSize = (1440, 900);
            var socketPath = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
            _server = new DisplayServer(socketPath, handlerFactory: new CompositorHandlerFactory(Scene));
            _server.Start();
            Client = new TestClient(_server.SocketPath);
            CompositorSessionTests.Handshake(Client);
            (_, _poolFile) = CompositorSessionTests.SetupShm(Client);
            Client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(7), WireArgument.String("xdg_wm_base"), WireArgument.UInt32(6), WireArgument.NewId(10));
            Client.RegisterObject(10, "xdg_wm_base");
        }

        public Scene Scene { get; }

        public TestClient Client { get; }

        public void CreateToplevel(uint surfaceId, uint xdgSurfaceId, uint toplevelId)
        {
            Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(surfaceId));
            Client.RegisterObject(surfaceId, "wl_surface");
            Client.SendRequest(10, "xdg_wm_base", "get_xdg_surface", WireArgument.NewId(xdgSurfaceId), WireArgument.Object(surfaceId));
            Client.RegisterObject(xdgSurfaceId, "xdg_surface");
            Client.SendRequest(xdgSurfaceId, "xdg_surface", "get_toplevel", WireArgument.NewId(toplevelId));
            Client.RegisterObject(toplevelId, "xdg_toplevel");
        }

        /// <summary>
        /// Maps the toplevel the way a real maximised client does: it acks
        /// the configure and declares the window geometry it was given, so
        /// its own coordinates map one-to-one onto the work area.
        /// </summary>
        public SurfaceHandler MapWindow(uint surfaceId, uint xdgSurfaceId, uint toplevelId, bool declareGeometry = true)
        {
            Client.SendRequest(surfaceId, "wl_surface", "commit");
            Client.ReadUntil(toplevelId, "configure");
            var configure = Client.ReadUntil(xdgSurfaceId, "configure");
            if (declareGeometry)
            {
                Client.SendRequest(xdgSurfaceId, "xdg_surface", "set_window_geometry", WireArgument.Int32(0), WireArgument.Int32(0), WireArgument.Int32(Scene.WorkArea.W), WireArgument.Int32(Scene.WorkArea.H));
            }

            Client.SendRequest(xdgSurfaceId, "xdg_surface", "ack_configure", WireArgument.UInt32(configure.Arguments[0].AsUInt32()));
            Attach(surfaceId);
            return CompositorSessionTests.WaitFor(() => Scene.Surfaces.FirstOrDefault(s => s.Id == surfaceId), value => value is { IsMapped: true })!;
        }

        public void CreatePositioner(uint id, (int X, int Y) origin, (int Width, int Height) size, (int Width, int Height) popup, uint anchor, uint gravity, uint adjustment)
        {
            Client.SendRequest(10, "xdg_wm_base", "create_positioner", WireArgument.NewId(id));
            Client.RegisterObject(id, "xdg_positioner");
            Client.SendRequest(id, "xdg_positioner", "set_size", WireArgument.Int32(popup.Width), WireArgument.Int32(popup.Height));
            Client.SendRequest(id, "xdg_positioner", "set_anchor_rect", WireArgument.Int32(origin.X), WireArgument.Int32(origin.Y), WireArgument.Int32(size.Width), WireArgument.Int32(size.Height));
            Client.SendRequest(id, "xdg_positioner", "set_anchor", WireArgument.UInt32(anchor));
            Client.SendRequest(id, "xdg_positioner", "set_gravity", WireArgument.UInt32(gravity));
            Client.SendRequest(id, "xdg_positioner", "set_constraint_adjustment", WireArgument.UInt32(adjustment));
        }

        public void CreatePopupSurface(uint popupSurfaceId, uint popupXdgSurfaceId, uint popupId, uint positionerId, uint parentXdgSurfaceId = 11)
        {
            Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(popupSurfaceId));
            Client.RegisterObject(popupSurfaceId, "wl_surface");
            Client.SendRequest(10, "xdg_wm_base", "get_xdg_surface", WireArgument.NewId(popupXdgSurfaceId), WireArgument.Object(popupSurfaceId));
            Client.RegisterObject(popupXdgSurfaceId, "xdg_surface");
            Client.SendRequest(popupXdgSurfaceId, "xdg_surface", "get_popup", WireArgument.NewId(popupId), WireArgument.Object(parentXdgSurfaceId), WireArgument.Object(positionerId));
            Client.RegisterObject(popupId, "xdg_popup");
            Client.SendRequest(popupSurfaceId, "wl_surface", "commit");
        }

        public void Attach(uint surfaceId)
        {
            Client.SendRequest(surfaceId, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            Client.SendRequest(surfaceId, "wl_surface", "commit");
        }

        public void Dispose()
        {
            Client.Dispose();
            _server.Dispose();
            File.Delete(_poolFile);
        }
    }
}