using Rosella.Input;
using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

/// <summary>Alt+Tab focus and keyboard events over a real Wayland socket.</summary>
public class AltTabFocusTests
{
    [Theory]
    [InlineData(InputConstants.ButtonSide, InputConstants.ButtonExtra)]
    [InlineData(InputConstants.ButtonBack, InputConstants.ButtonForward)]
    public void SideButtonsRouteBothInputDevicesBetweenIndependentDisplays(ushort down, ushort up)
    {
        using var sony = new Harness();
        using var dell = new Harness();
        sony.Scene.OutputSize = (640, 480);
        dell.Scene.OutputSize = (800, 600);
        using var input = new InputThread(sony.Scene, sony.Seat, () => { }, sony.Scene.Shell,
            secondary: (dell.Router, dell.Seat));
        sony.Scene.ToplevelMapped = input.PrimaryRouter.FocusNewWindow;
        dell.Scene.ToplevelMapped = dell.Router.FocusNewWindow;
        input.PrimaryRouter.PlaceCursor(12, 15);
        dell.Router.PlaceCursor(75, 80);
        sony.OpenKeyboard();
        dell.OpenKeyboard();
        sony.OpenPointer();
        dell.OpenPointer();
        sony.MapWindow(8, 11, 12);
        dell.MapWindow(8, 11, 12);
        CompositorSessionTests.WaitFor(() => sony.Seat.KeyboardFocus?.Id, id => id == 8);
        Assert.Null(dell.Seat.KeyboardFocus);
        sony.Drain();
        dell.Drain();

        void Key(ushort code, int value) => input.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, code, value));
        void Move(int x) => input.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeX, x));

        Move(0);
        Assert.Equal(8u, sony.Seat.PointerFocus?.Id);
        Key(InputConstants.KeyLeftAlt, InputConstants.KeyPressed);
        Key(down, InputConstants.KeyPressed);
        Key(down, InputConstants.KeyReleased);
        Key(InputConstants.KeyLeftAlt, InputConstants.KeyReleased);
        Assert.Same(dell.Router, input.Router);
        Assert.Equal((75, 80), input.Router.Cursor);
        Assert.Null(sony.Seat.KeyboardFocus);
        Assert.Null(sony.Seat.PointerFocus);
        Assert.Equal(8u, dell.Seat.KeyboardFocus?.Id);
        Assert.Equal(8u, dell.Seat.PointerFocus?.Id);

        var outgoing = sony.Drain();
        Assert.Contains(outgoing, e => e.ObjectId == 21 && sony.Client.EventName(e) == "leave");
        Assert.Contains(outgoing, e => e.ObjectId == 22 && sony.Client.EventName(e) == "leave");
        Assert.Contains(outgoing, e => e.ObjectId == 21 && sony.Client.EventName(e) == "key" &&
            e.Arguments[2].AsUInt32() == InputConstants.KeyLeftAlt && e.Arguments[3].AsUInt32() == 0);
        var incoming = dell.Drain();
        var enter = Assert.Single(incoming, e => e.ObjectId == 21 && dell.Client.EventName(e) == "enter");
        Assert.True(enter.Arguments[2].AsArray().IsEmpty);
        Assert.DoesNotContain(incoming, e => dell.Client.EventName(e) is "button" or "key");

        Key(InputConstants.KeyA, InputConstants.KeyPressed);
        Key(InputConstants.KeyA, InputConstants.KeyReleased);
        var key = dell.Client.ReadUntil(21, "key");
        Assert.Equal((uint)InputConstants.KeyA, key.Arguments[2].AsUInt32());
        Move(4000);
        Assert.Same(dell.Router, input.Router);
        Assert.Equal((799, 80), input.Router.Cursor);
        Assert.Equal((12, 15), input.PrimaryRouter.Cursor);
        Key(up, InputConstants.KeyPressed);
        Assert.Same(input.PrimaryRouter, input.Router);
        Assert.Equal((12, 15), input.Router.Cursor);
        Assert.Equal(8u, sony.Seat.KeyboardFocus?.Id);
        Assert.Null(dell.Seat.KeyboardFocus);
    }

    [Fact]
    public void NewlyMappedWindowTakesFocus_AndAltTabReturnsToPreviousWindow()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12);
        harness.OpenPointer();
        harness.OpenKeyboard();
        harness.ClickAt(2, 2);
        Assert.Equal(8u, harness.Seat.KeyboardFocus?.Id);
        harness.Scene.ToplevelMapped = harness.Router.FocusNewWindow;
        harness.MapWindow(30, 31, 32);
        Assert.Equal(30u, harness.Seat.KeyboardFocus?.Id);
        Assert.Contains(harness.Drain(), e => e.ObjectId == 21 && harness.Client.EventName(e) == "enter" && e.Arguments[1].AsObject() == 30);

        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyReleased);
        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyReleased);
        var events = harness.Drain();

        Assert.Equal(8u, harness.Seat.KeyboardFocus?.Id);
        Assert.Contains(events, e => e.ObjectId == 21 && harness.Client.EventName(e) == "enter" && e.Arguments[1].AsObject() == 8);
        harness.Key(InputConstants.KeyA, InputConstants.KeyPressed);
        var key = harness.Client.ReadUntil(21, "key");
        Assert.Equal((uint)InputConstants.KeyA, key.Arguments[2].AsUInt32());
    }

    [Fact]
    public void SwitchingAwayFromGrabbedPopup_DismissesGrabAndKeepsKeyboardOnSelectedWindow()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12);
        harness.MapWindow(30, 31, 32);
        harness.OpenPointer();
        harness.OpenKeyboard();
        var serial = harness.ClickAt(2, 2);
        harness.MapGrabbedPopup(31, 15, 16, 13, serial);
        harness.ClickAt(12, 22);
        var selected = harness.Scene.Surfaces.Single(surface => surface.Id == 8);
        Assert.Equal(15u, harness.Seat.KeyboardFocus?.Id);
        Assert.NotNull(harness.Scene.TopmostPopupGrab);
        harness.Drain();

        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyReleased);
        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyReleased);
        var events = harness.Drain();

        Assert.Null(harness.Scene.TopmostPopupGrab);
        Assert.Contains(events, e => e.ObjectId == 13 && harness.Client.EventName(e) == "popup_done");
        Assert.Same(selected, harness.Seat.KeyboardFocus);
        var enter = Assert.Single(events, e => e.ObjectId == 21 && harness.Client.EventName(e) == "enter" && e.Arguments[1].AsObject() == 8);
        Assert.Empty(enter.Arguments[2].AsArray().ToArray());
        var enterIndex = events.IndexOf(enter);
        Assert.Equal("modifiers", harness.Client.EventName(events[enterIndex + 1]));
        Assert.Equal(0u, events[enterIndex + 1].Arguments[1].AsUInt32());
        Assert.Contains(events.Take(enterIndex), e => e.ObjectId == 21 && harness.Client.EventName(e) == "key" && e.Arguments[2].AsUInt32() == InputConstants.KeyLeftAlt && e.Arguments[3].AsUInt32() == 0);
        Assert.DoesNotContain(events, e => e.ObjectId == 21 && harness.Client.EventName(e) == "key" && e.Arguments[2].AsUInt32() == InputConstants.KeyTab);

        harness.ClickAt(2, 2);
        Assert.Same(selected, harness.Seat.KeyboardFocus);
        harness.Key(InputConstants.KeyA, InputConstants.KeyPressed);
        var key = harness.Client.ReadUntil(21, "key");
        Assert.Equal((uint)InputConstants.KeyA, key.Arguments[2].AsUInt32());
    }

    [Fact]
    public void CancellingSwitch_LeavesPopupGrabAndKeyboardFocusAlone()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12);
        harness.OpenPointer();
        harness.OpenKeyboard();
        var serial = harness.ClickAt(2, 2);
        harness.MapGrabbedPopup(11, 15, 16, 13, serial);
        harness.ClickAt(12, 22);
        var focus = harness.Seat.KeyboardFocus;
        Assert.Equal(15u, focus?.Id);
        Assert.NotNull(harness.Scene.TopmostPopupGrab);
        harness.Drain();

        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyReleased);
        harness.Key(InputConstants.KeyEsc, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyEsc, InputConstants.KeyReleased);
        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyReleased);
        var events = harness.Drain();

        Assert.NotNull(harness.Scene.TopmostPopupGrab);
        Assert.Same(focus, harness.Seat.KeyboardFocus);
        Assert.DoesNotContain(events, e => e.ObjectId == 13 && harness.Client.EventName(e) == "popup_done");
    }

    [Fact]
    public void ReturningToPopupParent_DismissesGrabBeforeFocusingParent()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12);
        harness.OpenPointer();
        harness.OpenKeyboard();
        var serial = harness.ClickAt(2, 2);
        harness.MapGrabbedPopup(11, 15, 16, 13, serial);
        harness.ClickAt(12, 22);
        Assert.Equal(15u, harness.Seat.KeyboardFocus?.Id);
        harness.Drain();

        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyReleased);
        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyReleased);
        var events = harness.Drain();

        Assert.Null(harness.Scene.TopmostPopupGrab);
        Assert.Equal(8u, harness.Seat.KeyboardFocus?.Id);
        Assert.Contains(events, e => e.ObjectId == 13 && harness.Client.EventName(e) == "popup_done");
        harness.Key(InputConstants.KeyA, InputConstants.KeyPressed);
        var key = harness.Client.ReadUntil(21, "key");
        Assert.Equal((uint)InputConstants.KeyA, key.Arguments[2].AsUInt32());
    }

    [Fact]
    public void SelectedWindowUnmappedDuringSwitch_DoesNotTakeKeyboard()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12);
        harness.MapWindow(30, 31, 32);
        harness.OpenPointer();
        harness.OpenKeyboard();
        harness.ClickAt(2, 2);
        harness.Drain();
        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyReleased);

        harness.Client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(0), WireArgument.Int32(0), WireArgument.Int32(0));
        harness.Client.SendRequest(8, "wl_surface", "commit");
        harness.Drain();
        Assert.False(harness.Scene.Surfaces.Single(s => s.Id == 8).IsMapped);
        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyReleased);

        Assert.Equal(30u, harness.Seat.KeyboardFocus?.Id);
        harness.Drain();
        harness.Key(InputConstants.KeyA, InputConstants.KeyPressed);
        var key = harness.Client.ReadUntil(21, "key");
        Assert.Equal((uint)InputConstants.KeyA, key.Arguments[2].AsUInt32());
    }

    [Fact]
    public void InitialShiftTab_SelectsOldestOfThreeWindows()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12);
        harness.MapWindow(30, 31, 32);
        harness.MapWindow(40, 41, 42);
        harness.OpenPointer();
        harness.OpenKeyboard();
        harness.ClickAt(2, 2);
        harness.Drain();
        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyLeftShift, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyReleased);
        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyReleased);
        Assert.Equal(8u, harness.Seat.KeyboardFocus?.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StaleSelectedWindow_SkipsToNextLiveWindow(bool destroyRole)
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12);
        harness.MapWindow(30, 31, 32);
        harness.MapWindow(40, 41, 42);
        harness.OpenPointer();
        harness.OpenKeyboard();
        harness.ClickAt(2, 2);
        harness.Drain();
        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyReleased);
        if (destroyRole)
        {
            harness.Client.SendRequest(32, "xdg_toplevel", "destroy");
        }
        else
        {
            harness.Client.SendRequest(30, "wl_surface", "attach", WireArgument.Object(0), WireArgument.Int32(0), WireArgument.Int32(0));
            harness.Client.SendRequest(30, "wl_surface", "commit");
        }
        harness.Drain();
        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyReleased);
        Assert.Equal(8u, harness.Seat.KeyboardFocus?.Id);
    }

    [Fact]
    public void TabStep_SkipsUnmappedSnapshotWindow()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12);
        harness.MapWindow(30, 31, 32);
        harness.MapWindow(40, 41, 42);
        harness.OpenPointer();
        harness.OpenKeyboard();
        harness.ClickAt(2, 2);
        harness.Drain();
        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyReleased);
        harness.Client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(0), WireArgument.Int32(0), WireArgument.Int32(0));
        harness.Client.SendRequest(8, "wl_surface", "commit");
        harness.Drain();
        harness.Key(InputConstants.KeyTab, InputConstants.KeyPressed);
        harness.Key(InputConstants.KeyTab, InputConstants.KeyReleased);
        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyReleased);
        Assert.Equal(40u, harness.Seat.KeyboardFocus?.Id);
    }

    [Fact]
    public void NoLiveSnapshotSelection_DoesNotDismissPopupGrab()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12);
        harness.MapWindow(30, 31, 32);
        harness.OpenPointer();
        harness.OpenKeyboard();
        var serial = harness.ClickAt(2, 2);
        harness.MapGrabbedPopup(31, 15, 16, 13, serial);
        harness.ClickAt(12, 22);
        harness.Drain();
        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyPressed);
        var stale = harness.Scene.TaskWindows().Where(task => task.Surface.Id == 8).ToArray();
        harness.Switcher.Open(stale);
        harness.Client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(0), WireArgument.Int32(0), WireArgument.Int32(0));
        harness.Client.SendRequest(8, "wl_surface", "commit");
        harness.Drain();
        harness.Key(InputConstants.KeyLeftAlt, InputConstants.KeyReleased);
        var events = harness.Drain();
        Assert.NotNull(harness.Scene.TopmostPopupGrab);
        Assert.Equal(15u, harness.Seat.KeyboardFocus?.Id);
        Assert.DoesNotContain(events, e => e.ObjectId == 13 && harness.Client.EventName(e) == "popup_done");
    }

    private sealed class Harness : IDisposable
    {
        private readonly DisplayServer _server;
        private readonly string _poolFile;

        public Harness()
        {
            Scene = new Scene();
            Seat = new SeatState { HasKeyboardDevice = true, HasPointerDevice = true };
            var shell = new Shell { Windows = new SceneWindowRegistry(Scene, Seat, "wayland-test") };
            Switcher = shell.Switcher;
            Scene.Shell = shell;
            Router = new InputRouter(Scene, Seat, () => { }, shell);
            var socketPath = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
            _server = new DisplayServer(socketPath, handlerFactory: new CompositorHandlerFactory(Scene, Seat));
            _server.Start();
            Client = new TestClient(_server.SocketPath);
            CompositorSessionTests.Handshake(Client);
            (_, _poolFile) = CompositorSessionTests.SetupShm(Client);
            Client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(4), WireArgument.String("wl_seat"), WireArgument.UInt32(7), WireArgument.NewId(20));
            Client.RegisterObject(20, "wl_seat");
            Client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(7), WireArgument.String("xdg_wm_base"), WireArgument.UInt32(6), WireArgument.NewId(10));
            Client.RegisterObject(10, "xdg_wm_base");
        }

        public TaskSwitcher Switcher { get; }
        public Scene Scene { get; }
        public SeatState Seat { get; }
        public InputRouter Router { get; }
        public TestClient Client { get; }

        public void OpenPointer()
        {
            Client.SendRequest(20, "wl_seat", "get_pointer", WireArgument.NewId(22));
            Client.RegisterObject(22, "wl_pointer");
        }

        public void OpenKeyboard()
        {
            Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(21));
            Client.RegisterObject(21, "wl_keyboard");
            Client.ReadUntil(21, "keymap");
        }

        public void MapWindow(uint surface, uint xdg, uint toplevel)
        {
            Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(surface));
            Client.RegisterObject(surface, "wl_surface");
            Client.SendRequest(10, "xdg_wm_base", "get_xdg_surface", WireArgument.NewId(xdg), WireArgument.Object(surface));
            Client.RegisterObject(xdg, "xdg_surface");
            Client.SendRequest(xdg, "xdg_surface", "get_toplevel", WireArgument.NewId(toplevel));
            Client.RegisterObject(toplevel, "xdg_toplevel");
            Map(surface, xdg, toplevel);
        }

        public void MapGrabbedPopup(uint parent, uint surface, uint xdg, uint popup, uint serial)
        {
            Client.SendRequest(10, "xdg_wm_base", "create_positioner", WireArgument.NewId(14));
            Client.RegisterObject(14, "xdg_positioner");
            Client.SendRequest(14, "xdg_positioner", "set_size", WireArgument.Int32(50), WireArgument.Int32(40));
            Client.SendRequest(14, "xdg_positioner", "set_anchor_rect", WireArgument.Int32(10), WireArgument.Int32(20), WireArgument.Int32(1), WireArgument.Int32(1));
            Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(surface));
            Client.RegisterObject(surface, "wl_surface");
            Client.SendRequest(10, "xdg_wm_base", "get_xdg_surface", WireArgument.NewId(xdg), WireArgument.Object(surface));
            Client.RegisterObject(xdg, "xdg_surface");
            Client.SendRequest(xdg, "xdg_surface", "get_popup", WireArgument.NewId(popup), WireArgument.Object(parent), WireArgument.Object(14));
            Client.RegisterObject(popup, "xdg_popup");
            Client.SendRequest(popup, "xdg_popup", "grab", WireArgument.Object(20), WireArgument.UInt32(serial));
            Map(surface, xdg, popup);
        }

        private void Map(uint surface, uint xdg, uint role)
        {
            Client.SendRequest(surface, "wl_surface", "commit");
            Client.ReadUntil(role, "configure");
            var configure = Client.ReadUntil(xdg, "configure");
            Client.SendRequest(xdg, "xdg_surface", "ack_configure", WireArgument.UInt32(configure.Arguments[0].AsUInt32()));
            Client.SendRequest(surface, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            Client.SendRequest(surface, "wl_surface", "commit");
            CompositorSessionTests.WaitFor(() => Scene.Surfaces.FirstOrDefault(s => s.Id == surface), value => value is { IsMapped: true });
        }

        public uint ClickAt(int x, int y)
        {
            Router.PlaceCursor(x, y);
            Key(InputConstants.ButtonLeft, InputConstants.KeyPressed);
            var serial = Client.ReadUntil(22, "button").Arguments[0].AsUInt32();
            Key(InputConstants.ButtonLeft, InputConstants.KeyReleased);
            return serial;
        }

        public void Key(ushort code, int value) => Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, code, value));

        public List<DecodedMessage> Drain()
        {
            Client.SendRequest(1, "wl_display", "sync", WireArgument.NewId(90));
            Client.RegisterObject(90, "wl_callback");
            var events = new List<DecodedMessage>();
            while (true)
            {
                var message = Client.ReadEvent();
                if (message.ObjectId == 90 && Client.EventName(message) == "done")
                {
                    return events;
                }

                events.Add(message);
            }
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
