using System.Runtime.InteropServices;
using Rosella.Input;
using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The Windows 11 style shell over a real socket: windows land above the
/// taskbar, Super toggles the Start menu, the taskbar's Start button and
/// task buttons are clickable, binding keys never leak to clients, and the
/// focused window wears the accent border.
/// </summary>
public class ShellTests
{
    private static uint PixelAt(byte[] buffer, int stride, int x, int y) =>
        MemoryMarshal.Read<uint>(buffer.AsSpan((y * stride) + (x * 4)));

    [Fact]
    public void MapWindow_FillsTheWorkArea()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello");

        var surface = harness.MappedSurface(8);
        Assert.Equal((0, 0), surface.Position);
        Assert.True(surface.Position.Y + surface.Height <= harness.Scene.OutputSize.Height - Shell.BarHeight,
            $"window bottom at y={surface.Position.Y + surface.Height}");

        // Park the cursor away: its sprite would otherwise overwrite the
        // sampled pixel now that windows start at the origin.
        harness.Router.PlaceCursor(500, 500);
        var render = harness.RenderFrame();
        Assert.Equal(harness.ContentPixel, PixelAt(render, 1920 * 4, surface.Position.X + 2, surface.Position.Y + 2));
    }

    [Fact]
    public void FocusedWindow_WearsTheAccentBorder()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello");
        var surface = harness.MappedSurface(8);
        harness.ClickAt(surface.Position.X + 2, surface.Position.Y + 2);

        // The border hugs the visible rect: its left edge is at x = 0 for a
        // maximised window, drawn over the content.
        var render = harness.RenderFrame();
        Assert.Equal(0xFF7B86E8, PixelAt(render, 1920 * 4, 0, surface.Position.Y + 2));
    }

    [Fact]
    public void BarTask_ShowsTheWindowTitle()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello");
        harness.MapWindow(30, 31, 32);

        var tasks = harness.Scene.TaskWindows();
        Assert.Equal(["Hello", "Window"], tasks.Select(t => t.Label));
    }

    [Fact]
    public void SuperAlone_TogglesTheLauncher()
    {
        using var harness = new Harness();

        harness.Press(InputConstants.KeyLeftMeta);
        harness.Release(InputConstants.KeyLeftMeta);
        Assert.True(harness.Shell.Launcher.IsOpen);

        harness.Press(InputConstants.KeyLeftMeta);
        harness.Release(InputConstants.KeyLeftMeta);
        Assert.False(harness.Shell.Launcher.IsOpen);
    }

    [Fact]
    public void StartButton_TogglesTheLauncher()
    {
        using var harness = new Harness();
        var start = Bar.ComputeLayout(1920, 1080, Bar.ClockString(), []).Start;
        var cx = start.X + (start.W / 2);
        var cy = start.Y + (start.H / 2);

        harness.ClickAt(cx, cy);
        Assert.True(harness.Shell.Launcher.IsOpen);

        harness.ClickAt(cx, cy);
        Assert.False(harness.Shell.Launcher.IsOpen);
    }

    [Fact]
    public void TaskButton_FocusesAndRaisesTheWindow()
    {
        using var harness = new Harness();

        harness.MapWindow(8, 11, 12, title: "Hello");
        var surface = harness.MappedSurface(8);

        var layout = Bar.ComputeLayout(1920, 1080, Bar.ClockString(), harness.Scene.TaskWindows());
        var rect = layout.Tasks.Single().Rect;
        harness.ClickAt(rect.X + (rect.W / 2), rect.Y + (rect.H / 2));

        Assert.Equal(surface, harness.Seat.KeyboardFocus);
    }

    [Fact]
    public void LauncherEnter_SpawnsAndClosesThroughTheRouter()
    {
        using var harness = new Harness();
        var spawned = new List<string>();
        harness.Shell.Launcher.SpawnOverride = (command, socket) =>
        {
            spawned.Add(command);
            return true;
        };

        harness.Press(InputConstants.KeyLeftMeta);
        harness.Release(InputConstants.KeyLeftMeta);
        Assert.True(harness.Shell.Launcher.IsOpen);

        harness.Press(InputConstants.KeyDown);
        harness.Press(InputConstants.KeyEnter);

        Assert.Equal([Launcher.Apps[1].Command], spawned);
        Assert.False(harness.Shell.Launcher.IsOpen);
    }

    [Fact]
    public void LauncherOpen_StealsKeysFromTheFocusedWindow()
    {
        using var harness = new Harness();

        harness.MapWindow(8, 11, 12, title: "Hello");
        var surface = harness.MappedSurface(8);
        harness.ClickAt(surface.Position.X + 2, surface.Position.Y + 2);
        harness.OpenKeyboard();

        harness.Press(InputConstants.KeyLeftMeta);
        harness.Release(InputConstants.KeyLeftMeta);
        Assert.True(harness.Shell.Launcher.IsOpen);

        // Drain what the focus handover sent: the meta press key and the
        // leave are legitimate, they predate the launcher's opening.
        harness.CollectUntilRoundTrip();

        // Focus left the window for the launcher: typing reaches nobody.
        Assert.Null(harness.Seat.KeyboardFocus);
        harness.Press(InputConstants.KeyA);
        harness.Release(InputConstants.KeyA);
        Assert.DoesNotContain(harness.CollectUntilRoundTrip(), e => e.EventName == "key");
    }

    [Fact]
    public void AltF4_ClosesTheWindowWithoutLeakingTheKey()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello");
        var surface = harness.MappedSurface(8);
        harness.ClickAt(surface.Position.X + 2, surface.Position.Y + 2);
        harness.OpenKeyboard();
        harness.CollectUntilRoundTrip();

        harness.Press(InputConstants.KeyLeftAlt);
        harness.Press(InputConstants.KeyF4);
        harness.Release(InputConstants.KeyF4);
        harness.Release(InputConstants.KeyLeftAlt);

        var events = harness.DrainUntilRoundTrip();
        Assert.DoesNotContain(events, e => harness.Client.EventName(e) == "key" && e.Arguments[2].AsUInt32() is 62 or 70);

        // Closing is the client's move: the compositor asks, the client obeys.
        Assert.Contains(events, e => e.ObjectId == 12 && harness.Client.EventName(e) == "close");
    }

    [Fact]
    public void ChromePressAndRelease_NeverReachAWindow()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello");
        harness.OpenPointer();
        harness.CollectUntilRoundTrip();

        // A full click on a task button: consumed both ways.
        var layout = Bar.ComputeLayout(1920, 1080, Bar.ClockString(), harness.Scene.TaskWindows());
        var rect = layout.Tasks.Single().Rect;
        harness.ClickAt(rect.X + (rect.W / 2), rect.Y + (rect.H / 2));
        Assert.DoesNotContain(harness.CollectUntilRoundTrip(), e => e.EventName == "button");
    }

    [Fact]
    public void LauncherClick_OutsideClosesIt()
    {
        using var harness = new Harness();
        harness.Press(InputConstants.KeyLeftMeta);
        harness.Release(InputConstants.KeyLeftMeta);
        Assert.True(harness.Shell.Launcher.IsOpen);

        harness.ClickAt(0, 200);
        Assert.False(harness.Shell.Launcher.IsOpen);
    }

    [Fact]
    public void LauncherEscRestoresKeyboardFocus()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello");
        var surface = harness.MappedSurface(8);
        harness.ClickAt(surface.Position.X + 2, surface.Position.Y + 2);

        harness.Press(InputConstants.KeyLeftMeta);
        harness.Release(InputConstants.KeyLeftMeta);
        Assert.Null(harness.Seat.KeyboardFocus);

        harness.Press(InputConstants.KeyEsc);
        Assert.False(harness.Shell.Launcher.IsOpen);
        Assert.Equal(surface, harness.Seat.KeyboardFocus);
    }

    [Fact]
    public void LauncherEnterRestoresKeyboardFocus()
    {
        using var harness = new Harness();
        harness.Shell.Launcher.SpawnOverride = (_, _) => true;
        harness.MapWindow(8, 11, 12, title: "Hello");
        var surface = harness.MappedSurface(8);
        harness.ClickAt(surface.Position.X + 2, surface.Position.Y + 2);

        harness.Press(InputConstants.KeyLeftMeta);
        harness.Release(InputConstants.KeyLeftMeta);
        harness.Press(InputConstants.KeyEnter);

        Assert.False(harness.Shell.Launcher.IsOpen);
        Assert.Equal(surface, harness.Seat.KeyboardFocus);
    }

    [Fact]
    public void SecondMetaDoesNotResetAUsedChord()
    {
        using var harness = new Harness();
        var start = Bar.ComputeLayout(1920, 1080, Bar.ClockString(), []).Start;

        harness.Press(InputConstants.KeyLeftMeta);
        harness.ClickAt(start.X + (start.W / 2), start.Y + (start.H / 2));
        Assert.True(harness.Shell.Launcher.IsOpen);

        harness.Press(InputConstants.KeyRightMeta);
        harness.Release(InputConstants.KeyLeftMeta);
        harness.Release(InputConstants.KeyRightMeta);

        Assert.True(harness.Shell.Launcher.IsOpen);
    }

    [Fact]
    public void ClickDismissWhileMetaHeld_DoesNotReopenOnRelease()
    {
        using var harness = new Harness();

        harness.Press(InputConstants.KeyLeftMeta);
        harness.Release(InputConstants.KeyLeftMeta);
        Assert.True(harness.Shell.Launcher.IsOpen);

        harness.Press(InputConstants.KeyLeftMeta);
        harness.ClickAt(0, 200);
        Assert.False(harness.Shell.Launcher.IsOpen);

        harness.Release(InputConstants.KeyLeftMeta);
        Assert.False(harness.Shell.Launcher.IsOpen);
    }

    [Fact]
    public void DismissingPopups_UnmapsThemAndSendsPopupDone()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello");
        harness.MapPopup(parentXdgSurfaceId: 11, popupSurfaceId: 15, popupXdgSurfaceId: 16, popupId: 13);
        var surface = harness.MappedSurface(8);

        harness.Scene.DismissDescendantPopups(surface);

        Assert.False(harness.MappedSurface(15).IsMapped);
        Assert.Equal("popup_done", harness.Client.EventName(harness.Client.ReadUntil(13, "popup_done")));
    }

    [Fact]
    public void TaskbarDismissWhileMetaHeld_DoesNotReopenOnRelease()
    {
        using var harness = new Harness();
        harness.Press(InputConstants.KeyLeftMeta);
        harness.Release(InputConstants.KeyLeftMeta);
        Assert.True(harness.Shell.Launcher.IsOpen);

        harness.Press(InputConstants.KeyLeftMeta);
        harness.ClickAt(400, harness.Scene.OutputSize.Height - (Shell.BarHeight / 2));
        Assert.False(harness.Shell.Launcher.IsOpen);

        harness.Release(InputConstants.KeyLeftMeta);
        Assert.False(harness.Shell.Launcher.IsOpen);
    }

    [Fact]
    public void SuperPlusKey_IsAChordNotALauncherToggle()
    {
        using var harness = new Harness();

        harness.Press(InputConstants.KeyLeftMeta);
        harness.Press(InputConstants.KeyA);
        harness.Release(InputConstants.KeyA);
        harness.Release(InputConstants.KeyLeftMeta);

        Assert.False(harness.Shell.Launcher.IsOpen);
    }

    [Fact]
    public void ChromePress_SwallowsOnlyItsOwnButtonRelease()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello");
        var surface = harness.MappedSurface(8);
        harness.OpenPointer();
        harness.CollectUntilRoundTrip();

        // Left press on the window (delivered), right press on the taskbar
        // (consumed), then the left release: it still belongs to the window.
        harness.Router.PlaceCursor(surface.Position.X + 2, surface.Position.Y + 2);
        harness.Press(InputConstants.ButtonLeft);
        harness.Router.PlaceCursor(400, harness.Scene.OutputSize.Height - (Shell.BarHeight / 2));
        harness.Press(InputConstants.ButtonRight);
        harness.Release(InputConstants.ButtonLeft);

        var buttons = harness.DrainUntilRoundTrip()
            .Where(e => harness.Client.EventName(e) == "button")
            .ToList();
        Assert.Equal(2, buttons.Count);
        Assert.All(buttons, e => Assert.Equal((uint)InputConstants.ButtonLeft, e.Arguments[2].AsUInt32()));
    }

    [Fact]
    public void PlaceWindow_OpensAtTheWorkAreaOrigin()
    {
        var scene = new Scene { Shell = new Shell() };
        scene.OutputSize = (800, 600);

        // Every toplevel opens maximised to the work area: the policy
        // position is the work-area origin whatever the window size.
        var surface = new SurfaceHandler(scene);
        var (x, y) = scene.PlaceWindow(surface, 700, 300);
        Assert.Equal((0, 0), (x, y));
        Assert.Equal((0, 0), (scene.WorkArea.X, scene.WorkArea.Y));
        Assert.Equal((800, 600 - Shell.BarHeight), (scene.WorkArea.W, scene.WorkArea.H));
    }

    [Fact]
    public void StaleTaskButton_DoesNotFocusAGhostWindow()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello");
        harness.RenderFrame();
        var surface = harness.MappedSurface(8);

        // The window unmaps after the frame that built the taskbar layout.
        harness.Client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(0), WireArgument.Int32(0), WireArgument.Int32(0));
        harness.Client.SendRequest(8, "wl_surface", "commit");
        CompositorSessionTests.WaitFor(() => surface, value => !value.IsMapped);

        var rect = harness.Shell.LastBarLayout!.Tasks.Single().Rect;
        harness.ClickAt(rect.X + (rect.W / 2), rect.Y + (rect.H / 2));
        Assert.Null(harness.Seat.KeyboardFocus);
    }

    private sealed class Harness : IDisposable
    {
        private readonly DisplayServer _server;
        private readonly string _poolFile;

        public Harness()
        {
            Scene = new Scene();
            Seat = new SeatState { HasKeyboardDevice = true, HasPointerDevice = true };
            Shell = new Shell { Launcher = { SocketName = "wayland-test" } };
            Scene.Shell = Shell;
            Router = new InputRouter(Scene, Seat, () => Quit = true, Shell);
            var socketPath = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
            _server = new DisplayServer(socketPath, handlerFactory: new CompositorHandlerFactory(Scene, Seat));
            _server.Start();
            Client = new TestClient(_server.SocketPath);
            CompositorSessionTests.Handshake(Client);
            (Content, _poolFile) = CompositorSessionTests.SetupShm(Client);
            Client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(4), WireArgument.String("wl_seat"), WireArgument.UInt32(7), WireArgument.NewId(20));
            Client.RegisterObject(20, "wl_seat");
            BindXdg();
        }

        public Scene Scene { get; }

        public SeatState Seat { get; }

        public Shell Shell { get; }

        public InputRouter Router { get; }

        public TestClient Client { get; }

        public byte[] Content { get; }

        public bool Quit { get; private set; }

        /// <summary>What a blitted content pixel looks like in the render span.</summary>
        public uint ContentPixel => MemoryMarshal.Read<uint>(Content);

        public void BindXdg()
        {
            Client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(7), WireArgument.String("xdg_wm_base"), WireArgument.UInt32(6), WireArgument.NewId(10));
            Client.RegisterObject(10, "xdg_wm_base");
        }

        /// <summary>Maps a window through the full dance, optionally titled.</summary>
        public void MapWindow(uint surfaceId, uint xdgSurfaceId, uint toplevelId, string? title = null)
        {
            Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(surfaceId));
            Client.RegisterObject(surfaceId, "wl_surface");
            Client.SendRequest(10, "xdg_wm_base", "get_xdg_surface", WireArgument.NewId(xdgSurfaceId), WireArgument.Object(surfaceId));
            Client.RegisterObject(xdgSurfaceId, "xdg_surface");
            Client.SendRequest(xdgSurfaceId, "xdg_surface", "get_toplevel", WireArgument.NewId(toplevelId));
            Client.RegisterObject(toplevelId, "xdg_toplevel");
            if (title is not null)
            {
                Client.SendRequest(toplevelId, "xdg_toplevel", "set_title", WireArgument.String(title));
            }

            Client.SendRequest(surfaceId, "wl_surface", "commit");
            Client.ReadUntil(toplevelId, "configure");
            var configure = Client.ReadUntil(xdgSurfaceId, "configure");
            Client.SendRequest(xdgSurfaceId, "xdg_surface", "ack_configure", WireArgument.UInt32(configure.Arguments[0].AsUInt32()));
            Client.SendRequest(surfaceId, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            Client.SendRequest(surfaceId, "wl_surface", "commit");
            CompositorSessionTests.WaitFor(
                () => Scene.Surfaces.FirstOrDefault(s => s.Id == surfaceId),
                value => value is { IsMapped: true });
        }

        public SurfaceHandler MappedSurface(uint surfaceId) =>
            Scene.Surfaces.First(s => s.Id == surfaceId);

        /// <summary>Renders one frame into a span sized for the scene's output.</summary>
        public byte[] RenderFrame()
        {
            var buffer = new byte[Scene.OutputSize.Width * Scene.OutputSize.Height * 4];
            Scene.Render(buffer, Scene.OutputSize.Width, Scene.OutputSize.Height, Scene.OutputSize.Width * 4, frame: 0);
            return buffer;
        }

        public void Press(ushort code) =>
            Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, code, InputConstants.KeyPressed));

        public void Release(ushort code) =>
            Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, code, InputConstants.KeyReleased));

        /// <summary>Moves the cursor to the point and clicks there (full click).</summary>
        public void ClickAt(int x, int y)
        {
            Router.PlaceCursor(x, y);
            Press(InputConstants.ButtonLeft);
            Release(InputConstants.ButtonLeft);
        }

        /// <summary>Registers the wl_keyboard and waits for its keymap.</summary>
        public void OpenKeyboard()
        {
            Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(21));
            Client.RegisterObject(21, "wl_keyboard");
            Client.ReadUntil(21, "keymap");
        }

        /// <summary>Registers the wl_pointer.</summary>
        public void OpenPointer()
        {
            Client.SendRequest(20, "wl_seat", "get_pointer", WireArgument.NewId(22));
            Client.RegisterObject(22, "wl_pointer");
        }

        /// <summary>Maps a popup on the parent's xdg_surface through the dance.</summary>
        public void MapPopup(uint parentXdgSurfaceId, uint popupSurfaceId, uint popupXdgSurfaceId, uint popupId)
        {
            Client.SendRequest(10, "xdg_wm_base", "create_positioner", WireArgument.NewId(14));
            Client.RegisterObject(14, "xdg_positioner");
            Client.SendRequest(14, "xdg_positioner", "set_size", WireArgument.Int32(50), WireArgument.Int32(40));
            Client.SendRequest(14, "xdg_positioner", "set_anchor_rect", WireArgument.Int32(10), WireArgument.Int32(20), WireArgument.Int32(1), WireArgument.Int32(1));
            Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(popupSurfaceId));
            Client.RegisterObject(popupSurfaceId, "wl_surface");
            Client.SendRequest(10, "xdg_wm_base", "get_xdg_surface", WireArgument.NewId(popupXdgSurfaceId), WireArgument.Object(popupSurfaceId));
            Client.RegisterObject(popupXdgSurfaceId, "xdg_surface");
            Client.SendRequest(popupXdgSurfaceId, "xdg_surface", "get_popup", WireArgument.NewId(popupId), WireArgument.Object(parentXdgSurfaceId), WireArgument.Object(14));
            Client.RegisterObject(popupId, "xdg_popup");
            Client.SendRequest(popupSurfaceId, "wl_surface", "commit");
            Client.ReadUntil(popupId, "configure");
            var configure = Client.ReadUntil(popupXdgSurfaceId, "configure");
            Client.SendRequest(popupXdgSurfaceId, "xdg_surface", "ack_configure", WireArgument.UInt32(configure.Arguments[0].AsUInt32()));
            Client.SendRequest(popupSurfaceId, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            Client.SendRequest(popupSurfaceId, "wl_surface", "commit");
            CompositorSessionTests.WaitFor(
                () => Scene.Surfaces.FirstOrDefault(s => s.Id == popupSurfaceId),
                value => value is { IsMapped: true });
        }

        /// <summary>Drains events until the sync done, keeping the messages.</summary>
        public List<DecodedMessage> DrainUntilRoundTrip()
        {
            Client.SendRequest(1, "wl_display", "sync", WireArgument.NewId(92));
            Client.RegisterObject(92, "wl_callback");
            var seen = new List<DecodedMessage>();
            while (true)
            {
                var message = Client.ReadEvent();
                if (message.ObjectId == 92 && Client.EventName(message) == "done")
                {
                    return seen;
                }

                seen.Add(message);
            }
        }

        /// <summary>Drains events until the sync done, keeping what arrived.</summary>
        public List<(uint ObjectId, string EventName)> CollectUntilRoundTrip()
        {
            Client.SendRequest(1, "wl_display", "sync", WireArgument.NewId(91));
            Client.RegisterObject(91, "wl_callback");
            var seen = new List<(uint, string)>();
            while (true)
            {
                var message = Client.ReadEvent();
                if (message.ObjectId == 91 && Client.EventName(message) == "done")
                {
                    return seen;
                }

                seen.Add((message.ObjectId, Client.EventName(message)));
            }
        }

        public void Dispose()
        {
            _server.Dispose();
            if (File.Exists(_poolFile))
            {
                File.Delete(_poolFile);
            }
        }
    }
}
