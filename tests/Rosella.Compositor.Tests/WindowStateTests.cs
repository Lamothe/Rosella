using System.Runtime.InteropServices;
using Rosella.Input;
using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

/// <summary>
/// xdg_toplevel's window-state requests: minimised windows leave the screen
/// but keep their taskbar button, and the state we decline (maximise,
/// fullscreen) still earns a configure — a client that waits for one never
/// hears back and its window sits in limbo.
/// </summary>
public class WindowStateTests
{
    private static uint PixelAt(byte[] buffer, int stride, int x, int y) =>
        MemoryMarshal.Read<uint>(buffer.AsSpan((y * stride) + (x * 4)));

    [Fact]
    public void SetMaximized_AnswersWithAConfigureSayingMaximized()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12);
        harness.DrainUntilRoundTrip();

        harness.Client.SendRequest(12, "xdg_toplevel", "set_maximized");

        var roleConfigure = harness.Client.ReadUntil(12, "configure");
        Assert.Equal(BitConverter.GetBytes(1u), roleConfigure.Arguments[2].AsArray().ToArray());
        Assert.Equal(harness.Scene.WorkArea.W, roleConfigure.Arguments[0].AsInt32());
        Assert.Equal(harness.Scene.WorkArea.H, roleConfigure.Arguments[1].AsInt32());

        // Every role configure is wrapped by an xdg_surface.configure serial.
        harness.Client.ReadUntil(11, "configure");
    }

    [Fact]
    public void UnsetMaximized_KeepsThePolicyState_ButStillAnswers()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12);
        var surface = harness.MappedSurface(8);
        var placed = (surface.Position, surface.Width, surface.Height);
        harness.DrainUntilRoundTrip();

        harness.Client.SendRequest(12, "xdg_toplevel", "unset_maximized");

        // Rosella has no floating state: the truthful answer is "still
        // maximised". The client gets a configure either way, so its pending
        // state settles instead of waiting forever.
        var roleConfigure = harness.Client.ReadUntil(12, "configure");
        Assert.Equal(BitConverter.GetBytes(1u), roleConfigure.Arguments[2].AsArray().ToArray());
        harness.Client.ReadUntil(11, "configure");

        Assert.Equal(placed, (surface.Position, surface.Width, surface.Height));
        Assert.False(surface.Hidden);
    }

    [Fact]
    public void SetFullscreen_IsAnsweredWithTheWorkArea()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12);
        harness.DrainUntilRoundTrip();

        harness.Client.SendRequest(12, "xdg_toplevel", "set_fullscreen", WireArgument.Object(0));
        var roleConfigure = harness.Client.ReadUntil(12, "configure");
        var states = roleConfigure.Arguments[2].AsArray().ToArray();

        // maximized, never fullscreen: the policy is one work-area state.
        Assert.Equal(BitConverter.GetBytes(1u), states);
        Assert.Equal(harness.Scene.WorkArea.W, roleConfigure.Arguments[0].AsInt32());
        harness.Client.ReadUntil(11, "configure");

        // Unfullscreening is answered too, with the same truth.
        harness.Client.SendRequest(12, "xdg_toplevel", "unset_fullscreen");
        var again = harness.Client.ReadUntil(12, "configure");
        Assert.Equal(BitConverter.GetBytes(1u), again.Arguments[2].AsArray().ToArray());
        harness.Client.ReadUntil(11, "configure");
    }

    [Fact]
    public void SetMinimized_BeforeMapping_ChangesNothing()
    {
        using var harness = new Harness();
        harness.CreateToplevel(8, 11, 12);

        // Nothing is on screen yet; there is nothing to hide.
        harness.Client.SendRequest(12, "xdg_toplevel", "set_minimized");
        harness.MapExisting(8, 11, 12);

        var surface = harness.MappedSurface(8);
        Assert.True(surface.IsMapped);
        Assert.False(surface.Hidden);

        harness.Router.PlaceCursor(500, 500);
        Assert.Equal(harness.ContentPixel, PixelAt(harness.RenderFrame(), 1920 * 4, 2, 2));
    }

    [Fact]
    public void SetMinimized_HidesTheWindowButKeepsItsTaskButton()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello");
        var surface = harness.MappedSurface(8);
        harness.ClickAt(2, 2);
        Assert.Equal(surface, harness.Seat.KeyboardFocus);

        harness.Client.SendRequest(12, "xdg_toplevel", "set_minimized");
        harness.RoundTrip();

        // Still mapped (the buffer stays current), but off the screen, out
        // of hit-testing, and off the keyboard.
        Assert.True(surface.IsMapped);
        Assert.True(surface.Hidden);
        Assert.Null(harness.Scene.HitTest(2, 2));
        Assert.NotEqual(surface, harness.Seat.KeyboardFocus);

        var task = Assert.Single(harness.Scene.TaskWindows());
        Assert.Equal("Hello", task.Label);
        Assert.True(task.Minimized);

        var render = harness.RenderFrame();
        Assert.NotEqual(harness.ContentPixel, PixelAt(render, 1920 * 4, 2, 2));
    }

    [Fact]
    public void TaskButton_RestoresAMinimisedWindow()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello");
        var surface = harness.MappedSurface(8);
        harness.Client.SendRequest(12, "xdg_toplevel", "set_minimized");
        harness.RoundTrip();
        Assert.True(surface.Hidden);

        var layout = Bar.ComputeLayout(1920, 1080, Bar.ClockString(), harness.Scene.TaskWindows());
        var rect = layout.Tasks.Single().Rect;
        harness.ClickAt(rect.X + (rect.W / 2), rect.Y + (rect.H / 2));

        Assert.False(surface.Hidden);
        Assert.Equal(surface, harness.Seat.KeyboardFocus);
        Assert.False(Assert.Single(harness.Scene.TaskWindows()).Minimized);

        var render = harness.RenderFrame();
        Assert.Equal(harness.ContentPixel, PixelAt(render, 1920 * 4, 2, 2));
    }

    [Fact]
    public void MinimisedWindow_StopsReceivingFrameCallbacks()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(8, "wl_surface", "frame", WireArgument.NewId(50));
        harness.Client.RegisterObject(50, "wl_callback");
        harness.Client.SendRequest(8, "wl_surface", "commit");
        harness.RoundTrip();

        harness.Client.SendRequest(12, "xdg_toplevel", "set_minimized");
        harness.RoundTrip();
        harness.Scene.CompleteFrame();

        // A hidden window's clock stops: the callback stays queued instead of
        // waking a client that cannot be seen.
        Assert.DoesNotContain(harness.CollectUntilRoundTrip(), entry => entry.ObjectId == 50);

        harness.Scene.Restore(harness.MappedSurface(8));
        harness.Scene.CompleteFrame();
        harness.Client.ReadUntil(50, "done");
    }

    [Fact]
    public void MinimisingTheFocusedWindow_HandsTheKeyboardToTheTopmostOther()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "First");
        harness.MapWindow(30, 31, 32, title: "Second");
        var first = harness.MappedSurface(8);
        var second = harness.MappedSurface(30);
        harness.Seat.SetKeyboardFocus(second, []);
        Assert.Equal(second, harness.Seat.KeyboardFocus);

        harness.Client.SendRequest(32, "xdg_toplevel", "set_minimized");
        harness.RoundTrip();

        Assert.True(second.Hidden);
        Assert.Equal(first, harness.Seat.KeyboardFocus);
    }

    [Fact]
    public void MinimisingTheOnlyWindow_ClearsTheFocus()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12);
        var surface = harness.MappedSurface(8);
        harness.Seat.SetKeyboardFocus(surface, []);

        harness.Client.SendRequest(12, "xdg_toplevel", "set_minimized");
        harness.RoundTrip();

        Assert.Null(harness.Seat.KeyboardFocus);
    }

    [Fact]
    public void MinimisingAnUnfocusedWindow_LeavesFocusAlone()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Bottom");
        harness.MapWindow(30, 31, 32, title: "Focused");
        harness.MapWindow(40, 41, 42, title: "Top");
        var bottom = harness.MappedSurface(8);
        var focused = harness.MappedSurface(30);

        // A third window mapped on top without taking the keyboard.
        harness.Seat.SetKeyboardFocus(focused, []);

        harness.Client.SendRequest(12, "xdg_toplevel", "set_minimized");
        harness.RoundTrip();

        Assert.True(bottom.Hidden);
        Assert.Equal(focused, harness.Seat.KeyboardFocus);
    }

    [Fact]
    public void MinimisingTheFocusedWindow_CarriesTheHeldKeys()
    {
        using var harness = new Harness();
        harness.OpenKeyboard();
        harness.MapWindow(8, 11, 12, title: "First");
        harness.MapWindow(30, 31, 32, title: "Second");
        var first = harness.MappedSurface(8);
        var second = harness.MappedSurface(30);
        harness.Seat.SetKeyboardFocus(first, [InputConstants.KeyA]);
        var enter = harness.Client.ReadUntil(21, "enter");
        Assert.Equal(first.Id, enter.Arguments[1].AsObject());

        harness.Client.SendRequest(12, "xdg_toplevel", "set_minimized");

        // The new focus hears about the key that is still down; otherwise the
        // release would land on a client that never saw the press.
        var next = harness.Client.ReadUntil(21, "enter");
        Assert.Equal(second.Id, next.Arguments[1].AsObject());
        Assert.Equal(BitConverter.GetBytes((uint)InputConstants.KeyA), next.Arguments[2].AsArray().ToArray());
    }

    [Fact]
    public void MinimisedWindow_CannotMapAPopup()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello");
        var window = harness.MappedSurface(8);
        harness.Client.SendRequest(12, "xdg_toplevel", "set_minimized");
        harness.RoundTrip();
        Assert.True(window.Hidden);
        Assert.Null(harness.Seat.KeyboardFocus);

        // A menu opened by a hidden window has nowhere to appear and no
        // business taking the keyboard. The commit still carries a buffer,
        // so the popup is hidden rather than drawn as a role-less surface.
        harness.MapPopup(11, 15, 16, 13);

        var popup = harness.Scene.Surfaces.First(surface => surface.Id == 15);
        Assert.True(popup.Hidden);
        Assert.Null(harness.Scene.HitTest(popup.Position.X + 1, popup.Position.Y + 1));
        Assert.Null(harness.Seat.KeyboardFocus);

        // A further commit changes nothing and, in particular, does not
        // repeat popup_done at the client.
        harness.Client.SendRequest(15, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
        harness.Client.SendRequest(15, "wl_surface", "commit");
        Assert.DoesNotContain(
            harness.CollectUntilRoundTrip(),
            entry => entry.ObjectId == 13 || entry.EventName == "popup_done");
    }

    [Fact]
    public void PopupHiddenByAMinimisedWindow_ReturnsWithTheWindow()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello");
        var window = harness.MappedSurface(8);
        harness.Client.SendRequest(12, "xdg_toplevel", "set_minimized");
        harness.RoundTrip();
        harness.MapPopup(11, 15, 16, 13);

        var popup = harness.Scene.Surfaces.First(surface => surface.Id == 15);
        Assert.True(popup.Hidden);

        // The window comes back and the client commits the popup again (it
        // ignored popup_done): the surface must not stay invisible for the
        // rest of its life.
        harness.Scene.Restore(window);
        harness.Client.SendRequest(15, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
        harness.Client.SendRequest(15, "wl_surface", "commit");
        harness.RoundTrip();

        Assert.False(popup.Hidden);
        Assert.Equal(harness.ContentPixel, PixelAt(harness.RenderFrame(), 1920 * 4, popup.Visible.X + 1, popup.Visible.Y + 1));
    }

    [Fact]
    public void EndingAGrab_DoesNotStealFocusFromAnotherWindow()
    {
        using var harness = new Harness();
        harness.OpenPointer();
        harness.MapWindow(8, 11, 12, title: "Menu host");
        harness.MapWindow(30, 31, 32, title: "Elsewhere");
        var other = harness.MappedSurface(30);

        var serial = harness.PressAt(2, 2);
        harness.MapPopup(11, 15, 16, 13, grab: true, grabSerial: serial, dismissed: false);

        // The user switches windows with Alt+Tab while the menu is open.
        harness.Seat.SetKeyboardFocus(other, []);
        harness.Scene.DismissPopupGrabs();

        Assert.Equal(other, harness.Seat.KeyboardFocus);
    }

    [Fact]
    public void MinimisingAWindowWithAnOpenMenu_CarriesTheHeldKeys()
    {
        using var harness = new Harness();
        harness.OpenKeyboard();
        harness.OpenPointer();
        harness.MapWindow(8, 11, 12, title: "First");
        harness.MapWindow(30, 31, 32, title: "Second");
        var first = harness.MappedSurface(8);
        var second = harness.MappedSurface(30);

        // A menu grabs the pointer and keyboard while a key is held: the
        // teardown of that grab is what used to wipe the key set.
        var serial = harness.PressAt(2, 2);
        harness.MapPopup(11, 15, 16, 13, grab: true, grabSerial: serial, dismissed: false);
        var popup = harness.MappedSurface(15);
        harness.Seat.SetKeyboardFocus(popup, [InputConstants.KeyA]);
        harness.Client.ReadUntil(21, "enter");

        harness.Client.SendRequest(12, "xdg_toplevel", "set_minimized");

        var enter = harness.Client.ReadUntil(21, "enter");
        Assert.Equal(second.Id, enter.Arguments[1].AsObject());
        Assert.Equal(BitConverter.GetBytes((uint)InputConstants.KeyA), enter.Arguments[2].AsArray().ToArray());
        Assert.Equal(second, harness.Seat.KeyboardFocus);

        // The handback is sent before the popup is unmapped, so the enter is
        // not the end of the request: sync before looking at the surfaces.
        harness.RoundTrip();
        Assert.True(first.Hidden);
        Assert.False(popup.IsMapped);
    }

    [Fact]
    public void PinnedLauncher_RestoresAMinimisedWindow()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello", appId: "hello");
        var surface = harness.MappedSurface(8);
        harness.Client.SendRequest(12, "xdg_toplevel", "set_minimized");
        harness.RoundTrip();
        Assert.True(surface.Hidden);

        var launcher = new PinnedLauncher("Hello", "hello.desktop", ["hello"], ["hello"], null, null);
        launcher.Click(harness.Registry);

        Assert.False(surface.Hidden);
        Assert.Equal(surface, harness.Seat.KeyboardFocus);
    }

    private sealed class Harness : IDisposable
    {
        private readonly DisplayServer _server;
        private readonly string _poolFile;

        public Harness()
        {
            Scene = new Scene();
            Seat = new SeatState { HasKeyboardDevice = true, HasPointerDevice = true };
            Shell = new Shell();
            Registry = new SceneWindowRegistry(Scene, Seat, "wayland-test");
            Shell.Windows = Registry;
            Scene.Shell = Shell;
            Router = new InputRouter(Scene, Seat, () => { }, Shell);
            var socketPath = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
            _server = new DisplayServer(socketPath, handlerFactory: new CompositorHandlerFactory(Scene, Seat));
            _server.Start();
            Client = new TestClient(_server.SocketPath);
            CompositorSessionTests.Handshake(Client);
            (Content, _poolFile) = CompositorSessionTests.SetupShm(Client);
            Client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(4), WireArgument.String("wl_seat"), WireArgument.UInt32(7), WireArgument.NewId(20));
            Client.RegisterObject(20, "wl_seat");
            Client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(7), WireArgument.String("xdg_wm_base"), WireArgument.UInt32(6), WireArgument.NewId(10));
            Client.RegisterObject(10, "xdg_wm_base");
        }

        public Scene Scene { get; }

        public SeatState Seat { get; }

        public Shell Shell { get; }

        public SceneWindowRegistry Registry { get; }

        public InputRouter Router { get; }

        public TestClient Client { get; }

        public byte[] Content { get; }

        public uint ContentPixel => MemoryMarshal.Read<uint>(Content);

        public void RoundTrip()
        {
            Client.SendRequest(1, "wl_display", "sync", WireArgument.NewId(90));
            Client.RegisterObject(90, "wl_callback");
            Client.ReadUntil(90, "done");
        }

        /// <summary>Reads everything the server has queued, up to the sync done.</summary>
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

        /// <summary>Round trip, keeping only which object got which event.</summary>
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

        public void CreateToplevel(uint surfaceId, uint xdgSurfaceId, uint toplevelId)
        {
            Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(surfaceId));
            Client.RegisterObject(surfaceId, "wl_surface");
            Client.SendRequest(10, "xdg_wm_base", "get_xdg_surface", WireArgument.NewId(xdgSurfaceId), WireArgument.Object(surfaceId));
            Client.RegisterObject(xdgSurfaceId, "xdg_surface");
            Client.SendRequest(xdgSurfaceId, "xdg_surface", "get_toplevel", WireArgument.NewId(toplevelId));
            Client.RegisterObject(toplevelId, "xdg_toplevel");
        }

        /// <summary>Runs the configure/ack/attach/commit dance on a created toplevel.</summary>
        public SurfaceHandler MapExisting(uint surfaceId, uint xdgSurfaceId, uint toplevelId)
        {
            Client.SendRequest(surfaceId, "wl_surface", "commit");
            Client.ReadUntil(toplevelId, "configure");
            var configure = Client.ReadUntil(xdgSurfaceId, "configure");
            Client.SendRequest(xdgSurfaceId, "xdg_surface", "ack_configure", WireArgument.UInt32(configure.Arguments[0].AsUInt32()));
            Client.SendRequest(surfaceId, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            Client.SendRequest(surfaceId, "wl_surface", "commit");
            return CompositorSessionTests.WaitFor(
                () => Scene.Surfaces.FirstOrDefault(s => s.Id == surfaceId),
                value => value is { IsMapped: true })!;
        }

        public void MapWindow(uint surfaceId, uint xdgSurfaceId, uint toplevelId, string? title = null, string? appId = null)
        {
            CreateToplevel(surfaceId, xdgSurfaceId, toplevelId);
            if (title is not null)
            {
                Client.SendRequest(toplevelId, "xdg_toplevel", "set_title", WireArgument.String(title));
            }

            if (appId is not null)
            {
                Client.SendRequest(toplevelId, "xdg_toplevel", "set_app_id", WireArgument.String(appId));
            }

            MapExisting(surfaceId, xdgSurfaceId, toplevelId);
        }

        /// <summary>Maps a popup on the parent's xdg_surface through the dance.</summary>
        public void MapPopup(uint parentXdgSurfaceId, uint popupSurfaceId, uint popupXdgSurfaceId, uint popupId, bool grab = false, uint grabSerial = 0, bool dismissed = true)
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
            if (grab)
            {
                Client.SendRequest(popupId, "xdg_popup", "grab", WireArgument.Object(20), WireArgument.UInt32(grabSerial));
            }

            Client.SendRequest(popupSurfaceId, "wl_surface", "commit");
            Client.ReadUntil(popupId, "configure");
            var configure = Client.ReadUntil(popupXdgSurfaceId, "configure");
            Client.SendRequest(popupXdgSurfaceId, "xdg_surface", "ack_configure", WireArgument.UInt32(configure.Arguments[0].AsUInt32()));
            Client.SendRequest(popupSurfaceId, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            Client.SendRequest(popupSurfaceId, "wl_surface", "commit");

            if (dismissed)
            {
                // A popup on a minimised window: popup_done, never mapped.
                Client.ReadUntil(popupId, "popup_done");
                return;
            }

            CompositorSessionTests.WaitFor(
                () => Scene.Surfaces.FirstOrDefault(surface => surface.Id == popupSurfaceId),
                value => value is { IsMapped: true });
        }

        /// <summary>Registers the wl_pointer.</summary>
        public void OpenPointer()
        {
            Client.SendRequest(20, "wl_seat", "get_pointer", WireArgument.NewId(22));
            Client.RegisterObject(22, "wl_pointer");
        }

        /// <summary>Presses at the point and returns the button serial the client received.</summary>
        public uint PressAt(int x, int y)
        {
            Router.PlaceCursor(x, y);
            Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.ButtonLeft, InputConstants.KeyPressed));
            var serial = Client.ReadUntil(22, "button").Arguments[0].AsUInt32();
            Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.ButtonLeft, InputConstants.KeyReleased));
            return serial;
        }

        /// <summary>Registers the wl_keyboard and waits for its keymap.</summary>
        public void OpenKeyboard()
        {
            Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(21));
            Client.RegisterObject(21, "wl_keyboard");
            Client.ReadUntil(21, "keymap");
        }

        public SurfaceHandler MappedSurface(uint surfaceId) =>
            Scene.Surfaces.First(s => s.Id == surfaceId);

        /// <summary>Moves the cursor to the point and clicks there (full click).</summary>
        public void ClickAt(int x, int y)
        {
            Router.PlaceCursor(x, y);
            Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.ButtonLeft, InputConstants.KeyPressed));
            Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.ButtonLeft, InputConstants.KeyReleased));
        }

        public byte[] RenderFrame()
        {
            var buffer = new byte[Scene.OutputSize.Width * Scene.OutputSize.Height * 4];
            Scene.Render(buffer, Scene.OutputSize.Width, Scene.OutputSize.Height, Scene.OutputSize.Width * 4, frame: 0);
            return buffer;
        }

        public void Dispose()
        {
            Client.Dispose();
            _server.Dispose();
            if (File.Exists(_poolFile))
            {
                File.Delete(_poolFile);
            }
        }
    }
}