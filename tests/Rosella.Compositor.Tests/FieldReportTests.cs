using System.Runtime.InteropServices;
using Rosella.Input;
using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

/// <summary>
/// Regression pins for the five field reports: DPI scaling per screen,
/// windows opening fullscreen, dark mode, the stray context-aware cursor
/// (set_cursor), and the black square around client-side decorations
/// (ARGB blending).
/// </summary>
public class FieldReportTests
{
    private static uint PixelAt(byte[] buffer, int stride, int x, int y) =>
        MemoryMarshal.Read<uint>(buffer.AsSpan((y * stride) + (x * 4)));

    // --- DPI scaling: mutter's formula, per screen ----------------------

    [Fact]
    public void ScalePolicy_FollowsTheMutterFormula()
    {
        // A 15" laptop panel: ~220 physical DPI against the 135 small-screen
        // target → 2x. (macOS "looks like 1440x900"; GNOME 200%.)
        Assert.Equal(2, ScalePolicy.For(2880, 1800, 344, 222));

        // A 34" ultrawide: ~110 physical DPI against the 110 large-screen
        // target → 1x. (This is exactly mercury's Dell.)
        Assert.Equal(1, ScalePolicy.For(3440, 1440, 797, 334));

        // A 55" 4K TV: ~80 DPI → 1x.
        Assert.Equal(1, ScalePolicy.For(3840, 2160, 1218, 685));

        // A 27" 4K desktop panel: ~163 DPI → 1x (1.48 rounds to 1).
        Assert.Equal(1, ScalePolicy.For(3840, 2160, 597, 336));
    }

    [Fact]
    public void ScalePolicy_FallsBackWhenTheSizeIsFabricated()
    {
        // simpledrm reports 762×476mm for this mode — exactly 96 DPI, the
        // placeholder firmware uses when it knows nothing. Trusting it would
        // force a HiDPI laptop panel to 1x; the resolution fallback says 2x,
        // which is what GNOME OOTB shows here.
        Assert.Equal(2, ScalePolicy.For(2880, 1800, 762, 476));

        // Missing size entirely: same fallback.
        Assert.Equal(2, ScalePolicy.For(2880, 1800, 0, 0));
        Assert.Equal(1, ScalePolicy.For(1920, 1080, 0, 0));
    }

    // --- Dark mode: every launched app inherits it ----------------------

    [Fact]
    public void LaunchedApps_AreDark()
    {
        var startInfo = Launcher.CreateStartInfo("nautilus", "wayland-1");

        Assert.Equal("wayland-1", startInfo.Environment["WAYLAND_DISPLAY"]);
        Assert.Equal("Adwaita:dark", startInfo.Environment["GTK_THEME"]);
        Assert.Equal("prefer-dark", startInfo.Environment["ADW_DEBUG_COLOR_SCHEME"]);
    }

    // --- Windows open fullscreen ----------------------------------------

    [Fact]
    public void Windows_OpenFullscreenToTheWorkArea()
    {
        using var harness = new Harness();
        var surface = harness.MapWindow(8, 11, 12);

        Assert.Equal((0, 0), surface.Position);
        Assert.Equal(harness.Scene.WorkArea.W, surface.Visible.W);
        Assert.Equal(harness.Scene.WorkArea.H, surface.Visible.H);
    }

    // --- The black square: ARGB shadows blend, never turn black ---------

    [Fact]
    public void ArgbMargins_BlendOverTheBackground_NotBlack()
    {
        using var harness = new Harness();

        // A 16×16 surface with a translucent CSD shadow margin: 4px of 50%
        // black (premultiplied) around an opaque red window. The old opaque
        // copy painted those margins black — "a black square".
        const int side = 16;
        const int margin = 4;
        var pixels = new uint[side * side];
        Array.Fill(pixels, 0x80000000u);
        for (var y = margin; y < side - margin; y++)
        {
            for (var x = margin; x < side - margin; x++)
            {
                pixels[(y * side) + x] = 0xFFFF0000u;
            }
        }

        harness.CommitRaw(8, side, side, pixels);
        harness.Router.PlaceCursor(500, 500); // park the arrow elsewhere

        // Role-less surfaces sit at the cascade slot (64, 64).
        const int origin = 64;
        var render = harness.RenderFrame();
        var stride = 1920 * 4;

        // Inside the window: the client's own colour.
        Assert.Equal(0xFFFF0000u, PixelAt(render, stride, origin + margin, origin + margin));

        // The shadow margin: the background showing through a half-black
        // veil — not the black square of an opaque copy.
        var expected = Blend(0x80000000u, Background.ColourAt(origin, 1080));
        Assert.Equal(expected, PixelAt(render, stride, origin, origin));
        Assert.NotEqual(0xFF000000u, PixelAt(render, stride, origin, origin));
    }

    /// <summary>Source-over with premultiplied source, as the blitter does.</summary>
    private static uint Blend(uint source, uint destination)
    {
        var alpha = source >> 24;
        if (alpha == 255)
        {
            return source;
        }

        var inverse = 255 - alpha;
        var red = ((source >> 16) & 0xFF) + ((((destination >> 16) & 0xFF) * inverse) / 255);
        var green = ((source >> 8) & 0xFF) + ((((destination >> 8) & 0xFF) * inverse) / 255);
        var blue = (source & 0xFF) + (((destination & 0xFF) * inverse) / 255);
        return 0xFF000000u | (red << 16) | (green << 8) | blue;
    }

    // --- The stray cursor: set_cursor surfaces are the cursor, not a window

    [Fact]
    public void SetCursorSurface_RendersAtThePointer_NotAsAWindow()
    {
        using var harness = new Harness();

        // Two raw surfaces: 20 holds the pointer focus (like a window would),
        // 21 is named as the cursor. The serial set_cursor must carry comes
        // from a real wl_pointer.enter — read off the wire.
        var white = new byte[32 * 32 * 4];
        MemoryMarshal.Cast<byte, uint>(white.AsSpan()).Fill(0xFFFFFFFFu);
        harness.CommitRaw(20, 32, 32, white);
        harness.CommitRaw(21, 32, 32, white);
        harness.BindSeat(40, 41);
        var focusSurface = harness.Scene.Surfaces.First(s => s.Id == 20);
        var cursorSurface = harness.Scene.Surfaces.First(s => s.Id == 21);

        harness.Seat.SetPointerFocus(focusSurface, 5, 5);
        var enter = harness.Client.ReadUntil(41, "enter");
        var serial = enter.Arguments[0].AsUInt32();
        harness.Client.SendRequest(41, "wl_pointer", "set_cursor", WireArgument.UInt32(serial), WireArgument.Object(21), WireArgument.Int32(2), WireArgument.Int32(3));
        harness.RoundTrip();
        CompositorSessionTests.WaitFor(() => harness.Scene.Surfaces.FirstOrDefault(s => s.Id == 21), value => value is { IsCursorSurface: true });

        // Park the pointer away from the cascade slots: the cursor content
        // lands at (pointer − hotspot).
        harness.Router.PlaceCursor(200, 100);
        var render = harness.RenderFrame();
        var stride = 1920 * 4;
        Assert.Equal(0xFFFFFFFFu, PixelAt(render, stride, 200 - 2, 100 - 3));

        // It is not a window: its own slot shows the background again, and
        // it is neither hit-testable nor taskbar material.
        Assert.Equal(Background.ColourAt(136, 1080), PixelAt(render, stride, 136, 136));
        Assert.Null(harness.Scene.HitTest(136, 136));
        Assert.Equal(20u, harness.Scene.HitTest(70, 70)!.Id);
        Assert.DoesNotContain(harness.Scene.TaskWindows(), t => t.Surface.Id == 21);

        // The built-in arrow returns the moment pointer focus leaves the
        // client that owns the cursor (spec: scoped to the owner's focus).
        harness.Seat.SetPointerFocus(null, 0, 0);
        render = harness.RenderFrame();
        Assert.Equal(0xFF000000u, PixelAt(render, stride, 200, 100)); // the arrow's tip
    }

    [Fact]
    public void SetCursor_WithAStaleSerial_IsIgnored()
    {
        using var harness = new Harness();
        var white = new byte[32 * 32 * 4];
        MemoryMarshal.Cast<byte, uint>(white.AsSpan()).Fill(0xFFFFFFFFu);
        harness.CommitRaw(20, 32, 32, white);
        harness.CommitRaw(21, 32, 32, white);
        harness.BindSeat(40, 41);
        var surface21 = harness.Scene.Surfaces.First(s => s.Id == 21);
        harness.Seat.SetPointerFocus(harness.Scene.Surfaces.First(s => s.Id == 20), 5, 5);
        harness.Client.ReadUntil(41, "enter");

        // The spec: a serial that is not the latest enter leaves the cursor
        // unchanged — but the role is still given unconditionally ("This
        // request gives the surface the role of a cursor"), so the client's
        // committed buffer must not pop up as a stray window either.
        harness.Client.SendRequest(41, "wl_pointer", "set_cursor", WireArgument.UInt32(999), WireArgument.Object(21), WireArgument.Int32(2), WireArgument.Int32(3));
        harness.RoundTrip();
        Assert.True(surface21.IsCursorSurface);
        Assert.Null(harness.Seat.CurrentCursor().Surface);

        // No image applied: the built-in arrow draws, and the surface's own
        // slot shows the desktop, never a window.
        harness.Router.PlaceCursor(200, 100);
        var render = harness.RenderFrame();
        var stride = 1920 * 4;
        Assert.Equal(0xFF000000u, PixelAt(render, stride, 200, 100));
        Assert.Equal(Background.ColourAt(136, 1080), PixelAt(render, stride, 136, 136));
    }

    [Fact]
    public void SetCursor_WithoutPointerFocus_IsIgnored()
    {
        using var harness = new Harness();
        var white = new byte[32 * 32 * 4];
        MemoryMarshal.Cast<byte, uint>(white.AsSpan()).Fill(0xFFFFFFFFu);
        harness.CommitRaw(20, 32, 32, white);
        harness.CommitRaw(21, 32, 32, white);
        harness.BindSeat(40, 41);
        var surface21 = harness.Scene.Surfaces.First(s => s.Id == 21);
        harness.Seat.SetPointerFocus(harness.Scene.Surfaces.First(s => s.Id == 20), 5, 5);
        var enter = harness.Client.ReadUntil(41, "enter");
        var serial = enter.Arguments[0].AsUInt32();

        // Focus goes elsewhere; the matching serial alone must not let a
        // background client claim the global cursor.
        harness.Seat.SetPointerFocus(null, 0, 0);
        harness.Client.SendRequest(41, "wl_pointer", "set_cursor", WireArgument.UInt32(serial), WireArgument.Object(21), WireArgument.Int32(2), WireArgument.Int32(3));
        harness.RoundTrip();
        Assert.True(surface21.IsCursorSurface);   // the role is given
        Assert.Null(harness.Seat.CurrentCursor().Surface); // the image is not
    }

    [Fact]
    public void SetCursor_NullSurfaceHidesThePointerImage()
    {
        using var harness = new Harness();
        var white = new byte[32 * 32 * 4];
        MemoryMarshal.Cast<byte, uint>(white.AsSpan()).Fill(0xFFFFFFFFu);
        harness.CommitRaw(20, 32, 32, white);
        harness.BindSeat(40, 41);
        harness.Seat.SetPointerFocus(harness.Scene.Surfaces.First(s => s.Id == 20), 5, 5);
        var enter = harness.Client.ReadUntil(41, "enter");
        var serial = enter.Arguments[0].AsUInt32();

        // wayland.xml: "If surface is NULL, the pointer image is hidden."
        harness.Client.SendRequest(41, "wl_pointer", "set_cursor", WireArgument.UInt32(serial), WireArgument.Object(0), WireArgument.Int32(0), WireArgument.Int32(0));
        harness.RoundTrip();
        Assert.True(harness.Seat.CurrentCursor().Hidden);

        // Nothing draws at the pointer: neither an arrow nor a surface.
        harness.Router.PlaceCursor(200, 100);
        var render = harness.RenderFrame();
        Assert.Equal(Background.ColourAt(100, 1080), PixelAt(render, 1920 * 4, 200, 100));
    }

    [Fact]
    public void SetCursor_OnARoledSurface_ReportsRoleError()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12); // surface 8 carries the xdg role
        harness.BindSeat(40, 41);
        var window = harness.Scene.Surfaces.First(s => s.Id == 8);
        harness.Seat.SetPointerFocus(window, 5, 5);
        var enter = harness.Client.ReadUntil(41, "enter");
        var serial = enter.Arguments[0].AsUInt32();

        // wayland.xml: a surface with another role raises wl_pointer.error.role.
        harness.Client.SendRequest(41, "wl_pointer", "set_cursor", WireArgument.UInt32(serial), WireArgument.Object(8), WireArgument.Int32(0), WireArgument.Int32(0));

        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(41u, error.Arguments[0].AsObject());
        Assert.Equal(0u, error.Arguments[1].AsUInt32()); // wl_pointer.error.role
    }

    [Fact]
    public void XdgSurface_OnACursorSurface_ReportsRoleError()
    {
        using var harness = new Harness();
        var white = new byte[32 * 32 * 4];
        MemoryMarshal.Cast<byte, uint>(white.AsSpan()).Fill(0xFFFFFFFFu);
        harness.CommitRaw(20, 32, 32, white);
        harness.CommitRaw(21, 32, 32, white);
        harness.BindSeat(40, 41);
        harness.Seat.SetPointerFocus(harness.Scene.Surfaces.First(s => s.Id == 20), 5, 5);
        var enter = harness.Client.ReadUntil(41, "enter");
        var serial = enter.Arguments[0].AsUInt32();
        harness.Client.SendRequest(41, "wl_pointer", "set_cursor", WireArgument.UInt32(serial), WireArgument.Object(21), WireArgument.Int32(0), WireArgument.Int32(0));
        harness.RoundTrip();

        // The cursor role is taken: xdg-shell cannot adopt the surface.
        harness.Client.SendRequest(10, "xdg_wm_base", "get_xdg_surface", WireArgument.NewId(30), WireArgument.Object(21));

        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(10u, error.Arguments[0].AsObject());
        Assert.Equal(XdgErrors.Role, error.Arguments[1].AsUInt32());
    }

    // --- HiDPI clients: buffer scale divides out ------------------------

    [Fact]
    public void BufferScale_MapsBufferPixelsToLogicalSize()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);

        // A 2x client: a 64×64 buffer with buffer_scale 2 is a 32×32
        // logical surface.
        harness.Client.SendRequest(8, "wl_surface", "set_buffer_scale", WireArgument.Int32(2));
        harness.Client.SendRequest(11, "xdg_surface", "set_window_geometry", WireArgument.Int32(0), WireArgument.Int32(0), WireArgument.Int32(32), WireArgument.Int32(32));
        harness.EnsureBuffer(new byte[64 * 64 * 4], 64, 64);
        var surface = harness.MapExisting(8, 11);

        Assert.Equal(2, surface.BufferScale);
        Assert.Equal(32, surface.ContentWidth);
        Assert.Equal(32, surface.ContentHeight);
    }

    // --- The whole logical→physical conversion at scale 2 ---------------

    [Fact]
    public void Render_AtScale2_MapsLogicalToPhysical()
    {
        using var harness = new Harness();
        harness.Scene.Scale = 2;
        harness.Scene.OutputSize = (960, 540); // logical: 1920×1080 physical

        // A raw 16×16 red surface at the cascade slot (64, 64) logical:
        // physical (128, 128)–(160, 160).
        var red = new byte[16 * 16 * 4];
        MemoryMarshal.Cast<byte, uint>(red.AsSpan()).Fill(0xFFFF0000u);
        harness.CommitRaw(20, 16, 16, red);
        harness.Router.PlaceCursor(300, 300); // park the arrow off the sample

        var render = harness.RenderPhysical(1920, 1080);
        var stride = 1920 * 4;
        Assert.Equal(0xFFFF0000u, PixelAt(render, stride, 128, 128));
        Assert.Equal(0xFFFF0000u, PixelAt(render, stride, 159, 159));
        Assert.Equal(Background.ColourAt(128, 1080), PixelAt(render, stride, 127, 128));
        Assert.Equal(Background.ColourAt(128, 1080), PixelAt(render, stride, 160, 128));
    }

    [Fact]
    public void PointerDeltas_AtScale2_DivideWithRemainderCarry()
    {
        using var harness = new Harness();
        harness.Scene.Scale = 2;
        harness.Scene.OutputSize = (960, 540);
        harness.Router.PlaceCursor(100, 100);

        // 5 physical pixels is 2 logical with 1 carried; the next 5 make 3.
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeX, 5));
        Assert.Equal(102, harness.Scene.Cursor.X);
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeX, 5));
        Assert.Equal(105, harness.Scene.Cursor.X);
    }

    [Fact]
    public void Arrow_AtScale2_LandsAtTheScaledPointer()
    {
        using var harness = new Harness();
        harness.Scene.Scale = 2;
        harness.Scene.OutputSize = (960, 540);
        harness.Router.PlaceCursor(200, 100);

        var render = harness.RenderPhysical(1920, 1080);
        Assert.Equal(0xFF000000u, PixelAt(render, 1920 * 4, 400, 200)); // the arrow's tip
    }

    [Fact]
    public void Taskbar_AtScale2_CoversTheBottomEdge_OnANonDivisibleMode()
    {
        using var harness = new Harness();
        harness.Scene.Scale = 2;
        harness.Scene.OutputSize = (960, 540);
        harness.Scene.Shell = new Shell();
        harness.Router.PlaceCursor(10, 400);

        // 1921×1081 physical: scale 2 does not divide evenly. The clip must
        // come from the true buffer extent and the full-bleed rects must
        // round up, or the last row and column show the desktop through the
        // taskbar (the seam the review flagged).
        var render = harness.RenderPhysical(1921, 1081);
        var stride = 1921 * 4;
        Assert.Equal(0xFF16171Eu, PixelAt(render, stride, 1920, 1080)); // bottom-right corner
        Assert.Equal(0xFF16171Eu, PixelAt(render, stride, 0, 1080));    // bottom-left corner
        Assert.Equal(0xFF16171Eu, PixelAt(render, stride, 1920, 1000)); // right edge inside the bar
        Assert.Equal(Background.ColourAt(980, 1081), PixelAt(render, stride, 10, 980)); // just above the bar
    }

    // --- wl_output tells clients the real screen ------------------------

    [Fact]
    public void OutputEvents_ReportTheRealGeometryModeAndScale()
    {
        var info = new OutputInfo(2880, 1800, 344, 222, 59940, 2, "eDP", "2880x1800", "eDP-1");
        using var harness = new Harness(info);
        harness.Client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(5), WireArgument.String("wl_output"), WireArgument.UInt32(4), WireArgument.NewId(50));
        harness.Client.RegisterObject(50, "wl_output");

        var geometry = harness.Client.ReadUntil(50, "geometry");
        Assert.Equal(344, geometry.Arguments[2].AsInt32());  // physical width, mm
        Assert.Equal(222, geometry.Arguments[3].AsInt32());

        // Read the rest strictly in send order: done closes the batch, so
        // name/description must precede it (wayland.xml: done is "sent after
        // all other properties"; name/description are "followed by a done").
        var mode = harness.Client.ReadEvent();
        Assert.Equal(2880, mode.Arguments[1].AsInt32());
        Assert.Equal(1800, mode.Arguments[2].AsInt32());
        Assert.Equal(59940, mode.Arguments[3].AsInt32());     // milli-Hz

        var scale = harness.Client.ReadEvent();
        Assert.Equal(2, scale.Arguments[0].AsInt32());

        // name is the connector's identity, as DRM tools show it.
        var name = harness.Client.ReadEvent();
        Assert.Equal("eDP-1", name.Arguments[0].AsString());

        var description = harness.Client.ReadEvent();
        Assert.Equal("eDP 2880x1800", description.Arguments[0].AsString());

        harness.Client.ReadEvent(); // done — the batch closer, sent last
    }

    // --- The mapping dance across unmap (xdg-shell serial rules) --------

    [Fact]
    public void Ack_AfterUnmap_IsLegal_ButDoesNotMap()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);
        harness.EnsureBuffer(new byte[32 * 32 * 4], 32, 32);

        // The initial commit brings configure serial S1 — never acked.
        harness.Client.SendRequest(8, "wl_surface", "commit");
        var configure = harness.Client.ReadUntil(11, "configure");
        var serial = configure.Arguments[0].AsUInt32();

        // A null-buffer commit unmaps and restarts the dance.
        harness.Client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(0), WireArgument.Int32(0), WireArgument.Int32(0));
        harness.Client.SendRequest(8, "wl_surface", "commit");
        harness.RoundTrip();

        // Spec: only a serial that was never sent is invalid — this one was,
        // so the late ack is legal and must not disconnect the client.
        harness.Client.SendRequest(11, "xdg_surface", "ack_configure", WireArgument.UInt32(serial));
        harness.RoundTrip();

        // But it is not consent to map: the pre-unmap serial is at the
        // floor, so the buffer commit is refused and the dance must repeat.
        harness.Client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
        harness.Client.SendRequest(8, "wl_surface", "commit");

        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(11u, error.Arguments[0].AsObject());
        Assert.Equal(XdgErrors.UnconfiguredBuffer, error.Arguments[1].AsUInt32());
    }

    [Fact]
    public void EvictedConfigureSerials_StayAckable()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);
        harness.Client.SendRequest(8, "wl_surface", "commit");
        var configure = harness.Client.ReadUntil(11, "configure");
        var firstSerial = configure.Arguments[0].AsUInt32();
        var surface = harness.Scene.Surfaces.First(s => s.Id == 8);

        // Activation toggles each emit a configure: 70 pushes past the
        // 64-serial bound, evicting the oldest.
        for (var i = 0; i < 70; i++)
        {
            surface.XdgRole!.SetActivated(i % 2 == 0);
        }

        // The evicted serial was still *sent*, so acking it is legal — a
        // memory bound must not disconnect a well-behaved client.
        harness.Client.SendRequest(11, "xdg_surface", "ack_configure", WireArgument.UInt32(firstSerial));
        harness.RoundTrip(); // completes: no invalid_serial teardown
    }

    // --- Cursor surface teardown ----------------------------------------

    [Fact]
    public void CursorSurfaceTeardown_RestoresTheArrow()
    {
        using var harness = new Harness();
        var white = new byte[32 * 32 * 4];
        MemoryMarshal.Cast<byte, uint>(white.AsSpan()).Fill(0xFFFFFFFFu);
        harness.CommitRaw(20, 32, 32, white);
        harness.CommitRaw(21, 32, 32, white);
        harness.BindSeat(40, 41);
        harness.Seat.SetPointerFocus(harness.Scene.Surfaces.First(s => s.Id == 20), 5, 5);
        var enter = harness.Client.ReadUntil(41, "enter");
        var serial = enter.Arguments[0].AsUInt32();
        harness.Client.SendRequest(41, "wl_pointer", "set_cursor", WireArgument.UInt32(serial), WireArgument.Object(21), WireArgument.Int32(2), WireArgument.Int32(3));
        harness.RoundTrip();
        Assert.NotNull(harness.Seat.CurrentCursor().Surface);

        // The cursor surface dies: the built-in arrow takes over again.
        harness.Client.SendRequest(21, "wl_surface", "destroy");
        harness.RoundTrip();
        Assert.Null(harness.Seat.CurrentCursor().Surface);
        harness.Router.PlaceCursor(200, 100);
        var render = harness.RenderFrame();
        Assert.Equal(0xFF000000u, PixelAt(render, 1920 * 4, 200, 100));
    }

    private sealed class Harness : IDisposable
    {
        private readonly DisplayServer _server;
        private bool _hasBuffer;

        public Harness(OutputInfo? output = null)
        {
            Scene = new Scene();
            Seat = new SeatState { HasKeyboardDevice = true, HasPointerDevice = true };
            Router = new InputRouter(Scene, Seat, () => { });
            var socketPath = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
            _server = new DisplayServer(socketPath, handlerFactory: new CompositorHandlerFactory(Scene, Seat, output));
            _server.Start();
            Client = new TestClient(_server.SocketPath);
            CompositorSessionTests.Handshake(Client);
            Client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(7), WireArgument.String("xdg_wm_base"), WireArgument.UInt32(6), WireArgument.NewId(10));
            Client.RegisterObject(10, "xdg_wm_base");
        }

        public Scene Scene { get; }

        public SeatState Seat { get; }

        public InputRouter Router { get; }

        public TestClient Client { get; }

        /// <summary>A sync round trip, so the server has processed pending requests.</summary>
        public void RoundTrip()
        {
            Client.SendRequest(1, "wl_display", "sync", WireArgument.NewId(90));
            Client.RegisterObject(90, "wl_callback");
            Client.ReadUntil(90, "done");
        }

        /// <summary>Creates pool (6) + buffer (7) with exact content, once.</summary>
        public void EnsureBuffer(byte[] content, int width, int height)
        {
            if (_hasBuffer)
            {
                return;
            }

            _hasBuffer = true;
            CompositorSessionTests.SetupShmBuffer(Client, content, width, height);
        }

        /// <summary>Binds wl_seat and opens one pointer on it.</summary>
        public void BindSeat(uint seatId, uint pointerId)
        {
            Client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(4), WireArgument.String("wl_seat"), WireArgument.UInt32(7), WireArgument.NewId(seatId));
            Client.RegisterObject(seatId, "wl_seat");
            Client.SendRequest(seatId, "wl_seat", "get_pointer", WireArgument.NewId(pointerId));
            Client.RegisterObject(pointerId, "wl_pointer");
            RoundTrip();
        }

        public void CreateToplevel(uint surfaceId, uint xdgSurfaceId, uint toplevelId)
        {
            Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(surfaceId));
            Client.RegisterObject(surfaceId, "wl_surface");
            Client.SendRequest(10, "xdg_wm_base", "get_xdg_surface", WireArgument.NewId(xdgSurfaceId), WireArgument.Object(surfaceId));
            Client.RegisterObject(xdgSurfaceId, "xdg_surface");
            Client.SendRequest(xdgSurfaceId, "xdg_surface", "get_toplevel", WireArgument.NewId(toplevelId));
            Client.RegisterObject(toplevelId, "xdg_toplevel");
        }

        /// <summary>Commits exact pixels on a raw (role-less) surface.</summary>
        public void CommitRaw(uint surfaceId, int width, int height, byte[] content)
        {
            EnsureBuffer(content, width, height);
            Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(surfaceId));
            Client.RegisterObject(surfaceId, "wl_surface");
            Client.SendRequest(surfaceId, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            Client.SendRequest(surfaceId, "wl_surface", "commit");
            CompositorSessionTests.WaitFor(() => Scene.Surfaces.FirstOrDefault(s => s.Id == surfaceId), value => value is { IsMapped: true });
        }

        public void CommitRaw(uint surfaceId, int width, int height, uint[] pixels)
        {
            var content = new byte[pixels.Length * 4];
            pixels.AsSpan().CopyTo(MemoryMarshal.Cast<byte, uint>(content.AsSpan()));
            CommitRaw(surfaceId, width, height, content);
        }

        /// <summary>Maps an xdg window through the dance, committing buffer 7.</summary>
        public SurfaceHandler MapExisting(uint surfaceId, uint xdgSurfaceId)
        {
            Client.SendRequest(surfaceId, "wl_surface", "commit");
            Client.ReadUntil(xdgSurfaceId + 1, "configure");
            var configure = Client.ReadUntil(xdgSurfaceId, "configure");
            Client.SendRequest(xdgSurfaceId, "xdg_surface", "ack_configure", WireArgument.UInt32(configure.Arguments[0].AsUInt32()));
            Client.SendRequest(surfaceId, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            Client.SendRequest(surfaceId, "wl_surface", "commit");
            return CompositorSessionTests.WaitFor(() => Scene.Surfaces.FirstOrDefault(s => s.Id == surfaceId), value => value is { IsMapped: true })!;
        }

        /// <summary>Creates and maps an xdg window over the shared 32×32 buffer.</summary>
        public SurfaceHandler MapWindow(uint surfaceId, uint xdgSurfaceId, uint toplevelId)
        {
            CreateToplevel(surfaceId, xdgSurfaceId, toplevelId);
            EnsureBuffer(new byte[CompositorSessionTests.Width * CompositorSessionTests.Height * 4], CompositorSessionTests.Width, CompositorSessionTests.Height);
            return MapExisting(surfaceId, xdgSurfaceId);
        }

        public byte[] RenderFrame()
        {
            var buffer = new byte[Scene.OutputSize.Width * Scene.OutputSize.Height * 4];
            Scene.Render(buffer, Scene.OutputSize.Width, Scene.OutputSize.Height, Scene.OutputSize.Width * 4, frame: 0);
            return buffer;
        }

        /// <summary>Renders into a physical-size buffer (the scanout's, not the logical size).</summary>
        public byte[] RenderPhysical(int physicalWidth, int physicalHeight)
        {
            var buffer = new byte[physicalWidth * physicalHeight * 4];
            Scene.Render(buffer, physicalWidth, physicalHeight, physicalWidth * 4, frame: 0);
            return buffer;
        }

        public void Dispose()
        {
            Client.Dispose();
            _server.Dispose();
        }
    }
}
