using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The xdg-shell window state machine: the configure → ack → map dance,
/// mapping rules and their protocol errors, window policy placement, and
/// popup positioning — all over a real socket.
/// </summary>
public class XdgShellTests
{
    private const int Width = CompositorSessionTests.Width;
    private const int Height = CompositorSessionTests.Height;

    // Object ids: surface 8, xdg_surface 11, toplevel 12, popup 13,
    // positioner 14, popup surface 15, popup xdg_surface 16.
    // Policy: every toplevel opens maximised to the work area (the full
    // output here — this harness runs bare, without a shell).

    [Fact]
    public void Toplevel_FollowsTheConfigureAckMapDance()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);

        // Initial commit: the role's configure first, then the serial. The
        // configure carries the work-area size and the maximized state —
        // windows open fullscreen, always.
        harness.Client.SendRequest(8, "wl_surface", "commit");
        var roleConfigure = harness.Client.ReadUntil(12, "configure");
        Assert.Equal(1920, roleConfigure.Arguments[0].AsInt32());
        Assert.Equal(1080, roleConfigure.Arguments[1].AsInt32());
        var states = roleConfigure.Arguments[2].AsArray().ToArray();
        Assert.Equal(BitConverter.GetBytes(1u), states); // maximized; activated follows focus
        var configure = harness.Client.ReadUntil(11, "configure");
        var serial = configure.Arguments[0].AsUInt32();

        // ack + buffer commit = mapped, filling the work area.
        harness.Client.SendRequest(11, "xdg_surface", "ack_configure", WireArgument.UInt32(serial));
        harness.CommitBuffer(8);
        var surface = CompositorSessionTests.WaitFor(() => harness.Scene.Surfaces.FirstOrDefault(), value => value is { IsMapped: true })!;
        Assert.Equal((0, 0), surface.Position);
        Assert.Equal(1920, surface.Width);
        Assert.Equal(1080, surface.Height);
        Assert.Equal(harness.Content, surface.Content);
    }

    [Fact]
    public void Commit_WithoutRoleObject_ReportsNotConstructed()
    {
        using var harness = new Harness();
        harness.Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(8));
        harness.Client.RegisterObject(8, "wl_surface");
        harness.Client.SendRequest(10, "xdg_wm_base", "get_xdg_surface", WireArgument.NewId(11), WireArgument.Object(8));
        harness.Client.RegisterObject(11, "xdg_surface");
        harness.Client.SendRequest(8, "wl_surface", "commit");

        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(11u, error.Arguments[0].AsObject());
        Assert.Equal(XdgErrors.NotConstructed, error.Arguments[1].AsUInt32());
    }

    [Fact]
    public void BufferAttachedBeforeAck_ReportsUnconfiguredBuffer()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);
        harness.Client.SendRequest(8, "wl_surface", "commit");
        harness.Client.ReadUntil(11, "configure"); // never acked

        harness.CommitBuffer(8);

        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(11u, error.Arguments[0].AsObject());
        Assert.Equal(XdgErrors.UnconfiguredBuffer, error.Arguments[1].AsUInt32());
    }

    [Fact]
    public void SecondRoleObject_ReportsAlreadyConstructed()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);

        harness.Client.SendRequest(11, "xdg_surface", "get_toplevel", WireArgument.NewId(17));

        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(11u, error.Arguments[0].AsObject());
        Assert.Equal(XdgErrors.AlreadyConstructed, error.Arguments[1].AsUInt32());
    }

    [Fact]
    public void AckUnknownSerial_ReportsInvalidSerial()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);
        harness.Client.SendRequest(8, "wl_surface", "commit");
        harness.Client.ReadUntil(11, "configure");

        harness.Client.SendRequest(11, "xdg_surface", "ack_configure", WireArgument.UInt32(999));

        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(11u, error.Arguments[0].AsObject());
        Assert.Equal(XdgErrors.InvalidSerial, error.Arguments[1].AsUInt32());
    }

    [Fact]
    public void WindowGeometry_ShiftsTheContentOrigin()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);
        harness.Client.SendRequest(11, "xdg_surface", "set_window_geometry", WireArgument.Int32(4), WireArgument.Int32(6), WireArgument.Int32(20), WireArgument.Int32(20));

        var surface = harness.MapWindow(8, 11, 12);

        // The geometry rect (20×20 of the 32×32 content) fills the work
        // area: content scales by 1920/20 = 96 and 1080/20 = 54, and the
        // content origin is pulled back by the scaled geometry offset.
        Assert.Equal((0, 0), (surface.Visible.X, surface.Visible.Y));
        Assert.Equal((1920, 1080), (surface.Visible.W, surface.Visible.H));
        Assert.Equal((-4 * 96, -6 * 54), surface.Position);
        Assert.Equal((32 * 96, 32 * 54), (surface.Width, surface.Height));
    }

    [Fact]
    public void NullBufferCommit_UnmapsAndRestartsTheDance()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);
        var surface = harness.MapWindow(8, 11, 12);

        harness.Client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(0), WireArgument.Int32(0), WireArgument.Int32(0));
        harness.Client.SendRequest(8, "wl_surface", "commit");
        harness.RoundTrip();
        Assert.False(surface.IsMapped);

        // Remapping repeats the dance: the next commit brings a fresh configure.
        harness.Client.SendRequest(8, "wl_surface", "commit");
        var configure = harness.Client.ReadUntil(11, "configure");
        harness.Client.SendRequest(11, "xdg_surface", "ack_configure", WireArgument.UInt32(configure.Arguments[0].AsUInt32()));
        harness.CommitBuffer(8);
        CompositorSessionTests.WaitFor(() => surface.IsMapped, value => value);
    }

    [Fact]
    public void XdgSurfaceDestroyBeforeRole_ReportsDefunctRoleObject()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);

        harness.Client.SendRequest(11, "xdg_surface", "destroy");

        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(11u, error.Arguments[0].AsObject());
        Assert.Equal(XdgErrors.DefunctRoleObject, error.Arguments[1].AsUInt32());
    }

    [Fact]
    public void WmBaseDestroyBeforeSurfaces_ReportsDefunctSurfaces()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);

        harness.Client.SendRequest(10, "xdg_wm_base", "destroy");

        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(10u, error.Arguments[0].AsObject());
        Assert.Equal(XdgErrors.DefunctSurfaces, error.Arguments[1].AsUInt32());
    }

    [Fact]
    public void SecondWindow_IsRaisedAboveTheFirst()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);
        var first = harness.MapWindow(8, 11, 12);
        harness.CreateToplevel(20, 21, 22);
        var second = harness.MapWindow(20, 21, 22);

        // Both open maximised at the work-area origin; the newer one covers
        // the older, which is exactly how an always-fullscreen desktop
        // stacks. Order in the scene is the raise order.
        Assert.Equal(first.Position, second.Position);
        Assert.Equal((0, 0), second.Position);
        var surfaces = harness.Scene.Surfaces;
        Assert.Equal(8u, surfaces[surfaces.Count - 2].Id);
        Assert.Equal(20u, surfaces[^1].Id);
    }

    [Fact]
    public void Popup_PlacesAtAnchorRectPlusOffset()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);
        var parent = harness.MapWindow(8, 11, 12);

        // Positioner: 50×40 at the parent's (10, 20) plus offset (3, 5).
        harness.Client.SendRequest(10, "xdg_wm_base", "create_positioner", WireArgument.NewId(14));
        harness.Client.RegisterObject(14, "xdg_positioner");
        harness.Client.SendRequest(14, "xdg_positioner", "set_size", WireArgument.Int32(50), WireArgument.Int32(40));
        harness.Client.SendRequest(14, "xdg_positioner", "set_anchor_rect", WireArgument.Int32(10), WireArgument.Int32(20), WireArgument.Int32(1), WireArgument.Int32(1));
        harness.Client.SendRequest(14, "xdg_positioner", "set_offset", WireArgument.Int32(3), WireArgument.Int32(5));

        harness.Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(15));
        harness.Client.RegisterObject(15, "wl_surface");
        harness.Client.SendRequest(10, "xdg_wm_base", "get_xdg_surface", WireArgument.NewId(16), WireArgument.Object(15));
        harness.Client.RegisterObject(16, "xdg_surface");
        harness.Client.SendRequest(16, "xdg_surface", "get_popup", WireArgument.NewId(13), WireArgument.Object(11), WireArgument.Object(14));
        harness.Client.RegisterObject(13, "xdg_popup");
        harness.Client.SendRequest(15, "wl_surface", "commit");

        // xdg_popup.configure x/y is relative to the parent window geometry.
        var roleConfigure = harness.Client.ReadUntil(13, "configure");
        Assert.Equal(10 + 3, roleConfigure.Arguments[0].AsInt32());
        Assert.Equal(20 + 5, roleConfigure.Arguments[1].AsInt32());
        Assert.Equal(50, roleConfigure.Arguments[2].AsInt32());
        Assert.Equal(40, roleConfigure.Arguments[3].AsInt32());

        var configure = harness.Client.ReadUntil(16, "configure");
        harness.Client.SendRequest(16, "xdg_surface", "ack_configure", WireArgument.UInt32(configure.Arguments[0].AsUInt32()));
        harness.Client.SendRequest(15, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
        harness.Client.SendRequest(15, "wl_surface", "commit");
        var popup = CompositorSessionTests.WaitFor(() => harness.Scene.Surfaces.FirstOrDefault(s => s.Id == 15), value => value is { IsMapped: true })!;

        // Screen placement: parent window corner + anchor + offset.
        Assert.Equal((parent.Position.X + 10 + 3, parent.Position.Y + 20 + 5), popup.Position);
    }

    [Fact]
    public void RoleDestroy_UnmapsTheWindow()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);
        var surface = harness.MapWindow(8, 11, 12);
        Assert.True(surface.IsMapped);

        harness.Client.SendRequest(12, "xdg_toplevel", "destroy");
        harness.RoundTrip();

        Assert.False(surface.IsMapped);
        Assert.Null(surface.Content);
    }

    [Fact]
    public void SurfaceDestroyBeforeRole_ReportsDefunctRoleObject()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);

        harness.Client.SendRequest(8, "wl_surface", "destroy");

        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(8u, error.Arguments[0].AsObject());
        Assert.Equal(SurfaceErrors.DefunctRoleObject, error.Arguments[1].AsUInt32());
    }

    [Fact]
    public void WmCapabilities_ArrivesBeforeTheFirstConfigure()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);

        harness.Client.SendRequest(8, "wl_surface", "commit");

        // The first two toplevel events must be capabilities, then configure.
        var capabilities = harness.NextEventOn(12);
        Assert.Equal("wm_capabilities", harness.Client.EventName(capabilities));
        var roleConfigure = harness.NextEventOn(12);
        Assert.Equal("configure", harness.Client.EventName(roleConfigure));
    }

    [Fact]
    public void BufferlessCommitAfterRoleDestroy_IsInert()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);
        var surface = harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(12, "xdg_toplevel", "destroy");
        harness.RoundTrip();

        // Plain commits are inert; attaching a buffer is unconfigured.
        harness.Client.SendRequest(8, "wl_surface", "commit");
        harness.RoundTrip();
        Assert.False(surface.IsMapped);

        harness.CommitBuffer(8);
        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(11u, error.Arguments[0].AsObject());
        Assert.Equal(XdgErrors.UnconfiguredBuffer, error.Arguments[1].AsUInt32());
    }

    [Fact]
    public void AckOutOfOrder_ReportsInvalidSerial()
    {
        using var harness = new Harness();
        harness.CreatePopup(8, 11, 12, 13, 14, 15, 16);
        harness.Client.SendRequest(15, "wl_surface", "commit");
        var first = harness.Client.ReadUntil(16, "configure");

        // A reposition emits a second configure pair before any ack.
        harness.Client.SendRequest(13, "xdg_popup", "reposition", WireArgument.Object(14), WireArgument.UInt32(7));
        var second = harness.Client.ReadUntil(16, "configure");

        // Acking the newer serial consumes the older one with it.
        harness.Client.SendRequest(16, "xdg_surface", "ack_configure", WireArgument.UInt32(second.Arguments[0].AsUInt32()));
        harness.Client.SendRequest(16, "xdg_surface", "ack_configure", WireArgument.UInt32(first.Arguments[0].AsUInt32()));

        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(16u, error.Arguments[0].AsObject());
        Assert.Equal(XdgErrors.InvalidSerial, error.Arguments[1].AsUInt32());
    }

    [Fact]
    public void SizeHints_MaxSmallerThanMin_ReportsInvalidSize()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);

        harness.Client.SendRequest(12, "xdg_toplevel", "set_min_size", WireArgument.Int32(100), WireArgument.Int32(100));
        harness.Client.SendRequest(12, "xdg_toplevel", "set_max_size", WireArgument.Int32(50), WireArgument.Int32(50));

        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(12u, error.Arguments[0].AsObject());
        Assert.Equal(XdgErrors.ToplevelInvalidSize, error.Arguments[1].AsUInt32());
    }

    [Fact]
    public void SetParentToSelf_ReportsInvalidParent()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);

        harness.Client.SendRequest(12, "xdg_toplevel", "set_parent", WireArgument.Object(12));

        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(12u, error.Arguments[0].AsObject());
        Assert.Equal(XdgErrors.InvalidParent, error.Arguments[1].AsUInt32());
    }

    [Fact]
    public void IncompletePositioner_ReportsInvalidPositionerOnWmBase()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);
        harness.Client.SendRequest(10, "xdg_wm_base", "create_positioner", WireArgument.NewId(14));
        harness.Client.RegisterObject(14, "xdg_positioner");
        harness.Client.SendRequest(14, "xdg_positioner", "set_size", WireArgument.Int32(50), WireArgument.Int32(40));
        harness.Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(15));
        harness.Client.RegisterObject(15, "wl_surface");
        harness.Client.SendRequest(10, "xdg_wm_base", "get_xdg_surface", WireArgument.NewId(16), WireArgument.Object(15));
        harness.Client.RegisterObject(16, "xdg_surface");

        // No anchor rectangle: the positioner is incomplete.
        harness.Client.SendRequest(16, "xdg_surface", "get_popup", WireArgument.NewId(13), WireArgument.Object(11), WireArgument.Object(14));

        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(10u, error.Arguments[0].AsObject());
        Assert.Equal(XdgErrors.InvalidPositioner, error.Arguments[1].AsUInt32());
    }

    [Fact]
    public void Positioner_IsFrozenAtUseTime()
    {
        using var harness = new Harness();
        harness.CreatePopup(8, 11, 12, 13, 14, 15, 16);
        harness.MapWindow(8, 11, 12);

        // Mutating the positioner after get_popup must change nothing.
        harness.Client.SendRequest(14, "xdg_positioner", "set_anchor_rect", WireArgument.Int32(100), WireArgument.Int32(100), WireArgument.Int32(1), WireArgument.Int32(1));
        harness.Client.SendRequest(15, "wl_surface", "commit");

        var roleConfigure = harness.Client.ReadUntil(13, "configure");
        Assert.Equal(10 + 3, roleConfigure.Arguments[0].AsInt32());
        Assert.Equal(20 + 5, roleConfigure.Arguments[1].AsInt32());
    }

    /// <summary>A live compositor with shm and xdg_wm_base bound.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly DisplayServer _server;
        private readonly string _poolFile;

        public Harness()
        {
            Scene = new Scene();
            var socketPath = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
            _server = new DisplayServer(socketPath, handlerFactory: new CompositorHandlerFactory(Scene));
            _server.Start();
            Client = new TestClient(_server.SocketPath);
            CompositorSessionTests.Handshake(Client);
            (Content, _poolFile) = CompositorSessionTests.SetupShm(Client);

            Client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(7), WireArgument.String("xdg_wm_base"), WireArgument.UInt32(6), WireArgument.NewId(10));
            Client.RegisterObject(10, "xdg_wm_base");
        }

        public Scene Scene { get; }

        public TestClient Client { get; }

        public byte[] Content { get; }

        public void CreateToplevel(uint surfaceId, uint xdgSurfaceId, uint toplevelId)
        {
            Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(surfaceId));
            Client.RegisterObject(surfaceId, "wl_surface");
            Client.SendRequest(10, "xdg_wm_base", "get_xdg_surface", WireArgument.NewId(xdgSurfaceId), WireArgument.Object(surfaceId));
            Client.RegisterObject(xdgSurfaceId, "xdg_surface");
            Client.SendRequest(xdgSurfaceId, "xdg_surface", "get_toplevel", WireArgument.NewId(toplevelId));
            Client.RegisterObject(toplevelId, "xdg_toplevel");
        }

        /// <summary>A complete positioner (14) and a popup surface (15/16/13).</summary>
        public void CreatePopup(uint surfaceId, uint xdgSurfaceId, uint toplevelId, uint popupId, uint positionerId, uint popupSurfaceId, uint popupXdgSurfaceId)
        {
            CreateToplevel(surfaceId, xdgSurfaceId, toplevelId);
            Client.SendRequest(10, "xdg_wm_base", "create_positioner", WireArgument.NewId(positionerId));
            Client.RegisterObject(positionerId, "xdg_positioner");
            Client.SendRequest(positionerId, "xdg_positioner", "set_size", WireArgument.Int32(50), WireArgument.Int32(40));
            Client.SendRequest(positionerId, "xdg_positioner", "set_anchor_rect", WireArgument.Int32(10), WireArgument.Int32(20), WireArgument.Int32(1), WireArgument.Int32(1));
            Client.SendRequest(positionerId, "xdg_positioner", "set_offset", WireArgument.Int32(3), WireArgument.Int32(5));
            Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(popupSurfaceId));
            Client.RegisterObject(popupSurfaceId, "wl_surface");
            Client.SendRequest(10, "xdg_wm_base", "get_xdg_surface", WireArgument.NewId(popupXdgSurfaceId), WireArgument.Object(popupSurfaceId));
            Client.RegisterObject(popupXdgSurfaceId, "xdg_surface");
            Client.SendRequest(popupXdgSurfaceId, "xdg_surface", "get_popup", WireArgument.NewId(popupId), WireArgument.Object(xdgSurfaceId), WireArgument.Object(positionerId));
            Client.RegisterObject(popupId, "xdg_popup");
        }

        /// <summary>Runs the full dance and returns the mapped window.</summary>
        public SurfaceHandler MapWindow(uint surfaceId, uint xdgSurfaceId, uint toplevelId)
        {
            Client.SendRequest(surfaceId, "wl_surface", "commit");
            Client.ReadUntil(toplevelId, "configure");
            var configure = Client.ReadUntil(xdgSurfaceId, "configure");
            Client.SendRequest(xdgSurfaceId, "xdg_surface", "ack_configure", WireArgument.UInt32(configure.Arguments[0].AsUInt32()));
            CommitBuffer(surfaceId);

            return CompositorSessionTests.WaitFor(
                () => Scene.Surfaces.FirstOrDefault(s => s.Id == surfaceId),
                value => value is { IsMapped: true })!;
        }

        /// <summary>Attaches the shared 32×32 buffer and commits.</summary>
        public void CommitBuffer(uint surfaceId)
        {
            Client.SendRequest(surfaceId, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            Client.SendRequest(surfaceId, "wl_surface", "commit");
        }

        /// <summary>Reads events until one on the object, keeping order.</summary>
        public DecodedMessage NextEventOn(uint objectId)
        {
            while (true)
            {
                var message = Client.ReadEvent();
                if (message.ObjectId == objectId)
                {
                    return message;
                }
            }
        }

        /// <summary>A sync round trip, so the server has processed prior requests.</summary>
        public void RoundTrip()
        {
            Client.SendRequest(1, "wl_display", "sync", WireArgument.NewId(100));
            Client.RegisterObject(100, "wl_callback");
            Client.ReadUntil(100, "done");
        }

        public void Dispose()
        {
            Client.Dispose();
            _server.Dispose();
            File.Delete(_poolFile);
        }
    }
}
