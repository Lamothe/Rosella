using System.Runtime.InteropServices;
using Rosella.Input;
using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The Windows 11 style shell over a real socket: windows land above the
/// taskbar, the taskbar's pinned and task buttons are clickable,
/// binding keys never leak to clients, and windows wear no chrome of their
/// own. The Super key by itself does nothing.
/// </summary>
public class ShellTests
{
    private static uint PixelAt(byte[] buffer, int stride, int x, int y) =>
        MemoryMarshal.Read<uint>(buffer.AsSpan((y * stride) + (x * 4)));

    private static PinnedLauncher Pin(string name, string[] matchKeys, string[]? argv = null) =>
        new(name, $"{name.ToLowerInvariant()}.desktop", matchKeys, argv ?? matchKeys, null, null);

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
    public void FocusedWindow_WearsNoChrome()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello");
        var surface = harness.MappedSurface(8);
        harness.ClickAt(surface.Position.X + 2, surface.Position.Y + 2);

        // Focused or not, the window's content is all there is: no border
        // or frame is drawn over or around it. The sample point sits on the
        // window's left edge, where the old accent border used to live.
        var render = harness.RenderFrame();
        Assert.Equal(harness.ContentPixel, PixelAt(render, 1920 * 4, 0, surface.Position.Y + 2));
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
    public void GrabbedPopupPress_KeepsKeyboardFocusOnTheParentWindow()
    {
        // A mouse press on a menu item must not move keyboard focus off the
        // toplevel: that deactivates the window and the client closes its menu
        // before the release arrives. Keyboard Enter works because focus stays.
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Menu host");
        var parent = harness.MappedSurface(8);
        harness.OpenPointer();
        harness.CollectUntilRoundTrip();

        // The grab needs a real press serial the client received (the menu
        // button press that opens it).
        harness.ClickAt(950, 530);
        var pressSerial = harness.Client.ReadUntil(22, "button").Arguments[0].AsUInt32();
        harness.MapPopup(parentXdgSurfaceId: 11, popupSurfaceId: 15, popupXdgSurfaceId: 16, popupId: 13, grab: true, grabSerial: pressSerial);
        var popup = harness.MappedSurface(15);
        Assert.Same(parent, harness.Seat.KeyboardFocus);

        var centre = (popup.Visible.X + popup.Visible.W / 2, popup.Visible.Y + popup.Visible.H / 2);
        harness.Router.PlaceCursor(centre.Item1, centre.Item2);
        harness.Press(InputConstants.ButtonLeft);

        // The popup takes the press and keyboard focus, but its parent window
        // is never deactivated: the menu must survive until the release.
        Assert.Same(popup, harness.Seat.KeyboardFocus);
        Assert.True(parent.XdgRole!.Toplevel!.Activated);
        harness.Release(InputConstants.ButtonLeft);

        // Leaving the menu for an unrelated window deactivates the parent
        // window too, not just the popup's (non-toplevel) role.
        harness.MapWindow(30, 31, 32, title: "Other");
        harness.Seat.SetKeyboardFocus(harness.MappedSurface(30), []);
        Assert.False(parent.XdgRole!.Toplevel!.Activated);
    }

    [Fact]
    public void UngrabbedPopupClick_KeepsKeyboardFocusOnTheWindow()
    {
        // A plain menu popup takes no grab, so it must not take keyboard
        // focus either: the keyboard leave that follows reaches the toplevel,
        // which Chrome reads as the window losing focus and closes the menu
        // before the click's release arrives.
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Host");
        harness.MapPopup(parentXdgSurfaceId: 11, popupSurfaceId: 15, popupXdgSurfaceId: 16, popupId: 13);
        var window = harness.MappedSurface(8);
        var popup = harness.MappedSurface(15);
        harness.OpenKeyboard();
        harness.OpenPointer();
        harness.CollectUntilRoundTrip();

        harness.Router.PlaceCursor(window.Visible.X + 5, window.Visible.Y + 5);
        harness.Press(InputConstants.ButtonLeft);
        harness.Release(InputConstants.ButtonLeft);
        harness.CollectUntilRoundTrip();
        Assert.Equal(8u, harness.Seat.KeyboardFocus?.Id);

        harness.Router.PlaceCursor(popup.Visible.X + (popup.Visible.W / 2), popup.Visible.Y + (popup.Visible.H / 2));
        harness.Scene.Raise(popup);
        harness.Press(InputConstants.ButtonLeft);
        Assert.Equal(15u, harness.Seat.PointerFocus?.Id);

        Assert.Equal(8u, harness.Seat.KeyboardFocus?.Id);
        Assert.DoesNotContain(
            harness.DrainUntilRoundTrip(),
            e => e.ObjectId == 21 && harness.Client.EventName(e) is "enter" or "leave");
    }

    [Fact]
    public void ParentlessPopupPress_LeavesKeyboardFocusAlone()
    {
        // xdg-shell allows get_popup with a null parent, and such a popup can
        // never be grabbed (a grab needs a root toplevel). Pressing it must
        // leave keyboard focus where it was rather than moving focus onto the
        // popup, which is the same failure as the ungrabbed-menu click.
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Host");
        harness.OpenKeyboard();
        harness.OpenPointer();
        harness.CollectUntilRoundTrip();

        harness.ClickAt(950, 530);
        harness.CollectUntilRoundTrip();
        Assert.Equal(8u, harness.Seat.KeyboardFocus?.Id);

        harness.MapPopup(parentXdgSurfaceId: 0, popupSurfaceId: 15, popupXdgSurfaceId: 16, popupId: 13);
        var popup = harness.MappedSurface(15);
        harness.Router.PlaceCursor(popup.Visible.X + (popup.Visible.W / 2), popup.Visible.Y + (popup.Visible.H / 2));
        harness.Scene.Raise(popup);
        harness.Press(InputConstants.ButtonLeft);

        Assert.Equal(15u, harness.Seat.PointerFocus?.Id);
        Assert.Equal(8u, harness.Seat.KeyboardFocus?.Id);
        Assert.DoesNotContain(
            harness.DrainUntilRoundTrip(),
            e => e.ObjectId == 21 && harness.Client.EventName(e) is "enter" or "leave");
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

    // --- The Super key remains unbound ----------------------------------

    [Fact]
    public void SuperAlone_DoesNothing()
    {
        using var harness = new Harness();

        harness.Press(InputConstants.KeyLeftMeta);
        harness.Release(InputConstants.KeyLeftMeta);
        harness.Press(InputConstants.KeyRightMeta);
        harness.Release(InputConstants.KeyRightMeta);

        Assert.False(harness.Shell.Settings.IsOpen);
        Assert.False(harness.Quit);
    }

    [Fact]
    public void SuperWithAKey_IsOrdinaryInputNotABinding()
    {
        using var harness = new Harness();

        harness.Press(InputConstants.KeyLeftMeta);
        harness.Press(InputConstants.KeyA);
        harness.Release(InputConstants.KeyA);
        harness.Release(InputConstants.KeyLeftMeta);

        Assert.False(harness.Shell.Settings.IsOpen);
        Assert.False(harness.Quit);
    }

    // --- Clock-anchored settings panel ----------------------------------

    [Fact]
    public void ClockButton_TogglesSettingsAndLeavesStartButtonAvailable()
    {
        using var harness = new Harness();
        var layout = Bar.ComputeLayout(1920, 1080, Bar.ClockString(), []);

        harness.ClickAt(layout.Clock.X + (layout.Clock.W / 2), layout.Clock.Y + (layout.Clock.H / 2));
        Assert.True(harness.Shell.Settings.IsOpen);

        harness.ClickAt(layout.Clock.X + (layout.Clock.W / 2), layout.Clock.Y + (layout.Clock.H / 2));
        Assert.False(harness.Shell.Settings.IsOpen);

        harness.ClickAt(layout.Start.X + (layout.Start.W / 2), layout.Start.Y + (layout.Start.H / 2));
        Assert.False(harness.Shell.Settings.IsOpen);
    }

    [Fact]
    public void SettingsAction_ClickRunsAndClosesPanel()
    {
        using var harness = new Harness();
        var layout = Bar.ComputeLayout(1920, 1080, Bar.ClockString(), []);
        harness.ClickAt(layout.Clock.X + (layout.Clock.W / 2), layout.Clock.Y + (layout.Clock.H / 2));

        var panel = harness.Shell.Settings.PanelRect(layout.Clock, 1920, 1080);
        var row = SettingsPanel.ActionRect(panel, 1);
        harness.ClickAt(row.X + (row.W / 2), row.Y + (row.H / 2));

        Assert.False(harness.Shell.Settings.IsOpen);
        Assert.Equal(["Restart"], harness.Actions.Calls);
    }

    [Fact]
    public void SettingsPanel_EscCloses()
    {
        using var harness = new Harness();
        var clock = Bar.ComputeLayout(1920, 1080, Bar.ClockString(), []).Clock;
        harness.ClickAt(clock.X + (clock.W / 2), clock.Y + (clock.H / 2));

        harness.Press(InputConstants.KeyEsc);
        harness.Release(InputConstants.KeyEsc);
        Assert.False(harness.Shell.Settings.IsOpen);
    }

    // --- Pinned launchers: focus-or-launch and window claims ------------

    [Fact]
    public void PinnedButton_FocusesTheMatchingWindow()
    {
        using var harness = new Harness();
        harness.Shell.Pinned = [Pin("Test", ["test-app"])];
        harness.MapWindow(8, 11, 12, appId: "test-app");
        var surface = harness.MappedSurface(8);

        var layout = Bar.ComputeLayout(1920, 1080, Bar.ClockString(), harness.Scene.TaskWindows(), harness.Shell.Pinned);
        var rect = layout.Pinned[0].Rect;
        harness.ClickAt(rect.X + (rect.W / 2), rect.Y + (rect.H / 2));

        Assert.Equal(surface, harness.Seat.KeyboardFocus);
    }

    [Fact]
    public void PinnedButton_LaunchesWhenNoWindowMatches()
    {
        using var harness = new Harness();
        var fake = new FakeWindowRegistry();
        harness.Shell.Windows = fake;
        harness.Shell.Pinned = [Pin("Test", ["test-app"], ["test-app", "--flag"])];

        var layout = Bar.ComputeLayout(1920, 1080, Bar.ClockString(), harness.Scene.TaskWindows(), harness.Shell.Pinned);
        var rect = layout.Pinned[0].Rect;
        harness.ClickAt(rect.X + (rect.W / 2), rect.Y + (rect.H / 2));

        Assert.Single(fake.Launched);
        Assert.Equal(["test-app", "--flag"], fake.Launched[0]);
    }

    [Fact]
    public void MostRecentMatching_PicksTheNewestMatchingWindow()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, appId: "test-app");
        harness.MapWindow(30, 31, 32, appId: "TEST-APP");

        var registry = new SceneWindowRegistry(harness.Scene, harness.Seat, "wayland-test");
        Assert.Equal(harness.MappedSurface(30), registry.MostRecentMatching(["test-app"]));
    }

    [Fact]
    public void TaskButtons_SkipWindowsClaimedByPinnedLaunchers()
    {
        using var harness = new Harness();
        harness.Shell.Pinned = [Pin("Test", ["test-app"])];
        harness.MapWindow(8, 11, 12, title: "One", appId: "test-app");
        harness.MapWindow(30, 31, 32, title: "Two");

        harness.RenderFrame();
        var layout = harness.Shell.LastBarLayout!;

        Assert.True(layout.Pinned[0].Running);
        Assert.Equal("Two", Assert.Single(layout.Tasks).Task.Label);
    }

    private sealed class FakeSystemActions : ISystemActions
    {
        public List<string> Calls { get; } = [];

        public void LogOut() => Calls.Add("LogOut");

        public void Restart() => Calls.Add("Restart");

        public void ShutDown() => Calls.Add("ShutDown");
    }

    private sealed class FakeWindowRegistry : IWindowRegistry
    {
        public List<string[]> Launched { get; } = [];

        public SurfaceHandler? MostRecentMatching(IReadOnlyList<string> matchKeys) => null;

        public void RaiseAndFocus(SurfaceHandler surface, IReadOnlyList<int> heldKeys)
        {
        }

        public void Launch(IReadOnlyList<string> argv) => Launched.Add([.. argv]);
    }

    private sealed class Harness : IDisposable
    {
        private readonly DisplayServer _server;
        private readonly string _poolFile;

        public Harness()
        {
            Scene = new Scene();
            Seat = new SeatState { HasKeyboardDevice = true, HasPointerDevice = true };
            Actions = new FakeSystemActions();
            Shell = new Shell(Actions) { SocketName = "wayland-test" };
            Shell.Windows = new SceneWindowRegistry(Scene, Seat, "wayland-test");
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

        public FakeSystemActions Actions { get; }

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

        /// <summary>Maps a window through the full dance, optionally titled and app-id'd.</summary>
        public void MapWindow(uint surfaceId, uint xdgSurfaceId, uint toplevelId, string? title = null, string? appId = null)
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

            if (appId is not null)
            {
                Client.SendRequest(toplevelId, "xdg_toplevel", "set_app_id", WireArgument.String(appId));
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
        public void MapPopup(uint parentXdgSurfaceId, uint popupSurfaceId, uint popupXdgSurfaceId, uint popupId, bool grab = false, uint grabSerial = 0)
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
