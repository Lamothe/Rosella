using System.Runtime.InteropServices;
using Rosella.Input;
using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The M6 input pipeline over a real socket: seat binding and the keymap,
/// click-to-focus with raise and activation, key routing, the two hardcoded
/// keybindings, pointer enter/leave/axis, cursor clamping and rendering,
/// and focus teardown on unmap.
/// </summary>
public class InputTests
{
    [Fact]
    public void SeatBind_SendsNameThenCapabilities()
    {
        using var harness = new Harness();

        // wayland.xml: name first, then capabilities. Read in arrival order —
        // ReadUntil would discard the skipped one.
        var name = harness.NextEventOn(20);
        Assert.Equal("name", harness.Client.EventName(name));
        Assert.Equal("seat0", name.Arguments[0].AsString());
        var capabilities = harness.NextEventOn(20);
        Assert.Equal("capabilities", harness.Client.EventName(capabilities));
        Assert.Equal(3u, capabilities.Arguments[0].AsUInt32()); // pointer | keyboard
    }

    [Fact]
    public void GetKeyboard_SendsTheKeymapFileAndRepeatInfo()
    {
        using var harness = new Harness();
        harness.Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(21));
        harness.Client.RegisterObject(21, "wl_keyboard");

        var keymap = harness.Client.ReadUntil(21, "keymap");
        Assert.Equal(1u, keymap.Arguments[0].AsUInt32()); // XKB_V1
        var size = keymap.Arguments[2].AsUInt32();
        Assert.True(size > 1000, $"keymap size {size} is too small");

        var fd = keymap.Arguments[1].AsFileDescriptor();
        var text = File.ReadAllBytes($"/proc/self/fd/{fd}");
        Assert.StartsWith("xkb_keymap", System.Text.Encoding.UTF8.GetString(text));

        var repeat = harness.Client.ReadUntil(21, "repeat_info");
        Assert.Equal(25, repeat.Arguments[0].AsInt32());
        Assert.Equal(600, repeat.Arguments[1].AsInt32());
    }

    [Fact]
    public void Click_FocusesRaisesAndActivatesTheWindow()
    {
        using var harness = new Harness();
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(21));
        harness.Client.RegisterObject(21, "wl_keyboard");
        harness.RoundTrip();
        harness.Client.SendRequest(20, "wl_seat", "get_pointer", WireArgument.NewId(22));
        harness.Client.RegisterObject(22, "wl_pointer");
        harness.RoundTrip();

        // Click window A (the only window): it activates.
        harness.ClickAt(950, 530);
        var enter = harness.Client.ReadUntil(21, "enter");
        Assert.Equal(8u, enter.Arguments[1].AsObject());
        var activated = harness.Client.ReadUntil(12, "configure");
        Assert.Equal([.. BitConverter.GetBytes(1u), .. BitConverter.GetBytes(4u)], activated.Arguments[2].AsArray().ToArray());

        // Mapping B puts it on top (always-fullscreen windows stack); a
        // click anywhere lands on B: A deactivates, B activates.
        harness.MapWindow(30, 31, 32);
        harness.ClickAt(1000, 580);
        var deactivated = harness.NextEventOn(12);
        Assert.Equal("configure", harness.Client.EventName(deactivated));
        Assert.Equal(BitConverter.GetBytes(1u), deactivated.Arguments[2].AsArray().ToArray());
        var bActivated = harness.NextEventOn(32);
        Assert.Equal("configure", harness.Client.EventName(bActivated));
        Assert.Equal([.. BitConverter.GetBytes(1u), .. BitConverter.GetBytes(4u)], bActivated.Arguments[2].AsArray().ToArray());

        CompositorSessionTests.WaitFor(
            () => harness.Scene.Surfaces.ToArray(),
            surfaces => surfaces.Length == 2 && surfaces[^1].Id == 30);
    }

    [Fact]
    public void ChromePresses_SwallowEveryOwnRelease()
    {
        var shell = new Shell();
        using var harness = new Harness(shell: shell);
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(20, "wl_seat", "get_pointer", WireArgument.NewId(22));
        harness.Client.RegisterObject(22, "wl_pointer");
        harness.RoundTrip();

        // The cursor sits on the open system-menu panel, just off its rows:
        // a press there is consumed by the chrome and the panel stays open.
        shell.Menu.Open();
        var panel = shell.Menu.PanelRect(1920, 1080);
        harness.Router.PlaceCursor(panel.X + 2, panel.Y + 2);
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.ButtonLeft, InputConstants.KeyPressed));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.ButtonRight, InputConstants.KeyPressed));

        // Both presses were the chrome's, so both releases are the chrome's
        // too: each consumed button swallows its own release, and no button
        // event may reach the window under the cursor.
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.ButtonLeft, InputConstants.KeyReleased));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.ButtonRight, InputConstants.KeyReleased));
        foreach (var (objectId, eventName) in harness.CollectUntilRoundTrip())
        {
            Assert.NotEqual((22u, "button"), (objectId, eventName));
        }
    }

    [Fact]
    public void Keys_RouteToTheKeyboardFocus()
    {
        using var harness = new Harness();
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(21));
        harness.Client.RegisterObject(21, "wl_keyboard");
        harness.RoundTrip();
        harness.ClickAt(950, 530);
        harness.Client.ReadUntil(21, "enter");

        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyA, InputConstants.KeyPressed));
        var key = harness.Client.ReadUntil(21, "key");
        Assert.Equal(30u, key.Arguments[2].AsUInt32());
        Assert.Equal(1u, key.Arguments[3].AsUInt32());

        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyA, InputConstants.KeyReleased));
        var released = harness.Client.ReadUntil(21, "key");
        Assert.Equal(0u, released.Arguments[3].AsUInt32());
    }

    [Fact]
    public void Shift_SendsTheModifiersEvent()
    {
        using var harness = new Harness();
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(21));
        harness.Client.RegisterObject(21, "wl_keyboard");
        harness.RoundTrip();
        harness.ClickAt(950, 530);
        harness.Client.ReadUntil(21, "enter");
        harness.Client.ReadUntil(21, "modifiers"); // the click's (0, 0) state

        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyLeftShift, InputConstants.KeyPressed));
        var modifiers = harness.Client.ReadUntil(21, "modifiers");
        Assert.Equal(1u, modifiers.Arguments[1].AsUInt32()); // shift = depressed bit 0
        Assert.Equal(0u, modifiers.Arguments[3].AsUInt32());
    }

    [Fact]
    public void AltF4_SendsCloseToTheFocusedWindow()
    {
        using var harness = new Harness();
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(21));
        harness.Client.RegisterObject(21, "wl_keyboard");
        harness.RoundTrip();
        harness.ClickAt(950, 530);
        harness.Client.ReadUntil(21, "enter");

        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyLeftAlt, InputConstants.KeyPressed));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyF4, InputConstants.KeyPressed));

        harness.Client.ReadUntil(12, "close");
    }

    [Fact]
    public void SuperEsc_RequestsQuit()
    {
        using var harness = new Harness();
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);

        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyLeftMeta, InputConstants.KeyPressed));
        Assert.False(harness.Quit);
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyEsc, InputConstants.KeyPressed));

        Assert.True(harness.Quit);
    }

    [Theory]
    [InlineData(InputConstants.KeyF1, 1)]
    [InlineData(InputConstants.KeyF2, 2)]
    [InlineData(InputConstants.KeyF10, 10)]
    [InlineData(InputConstants.KeyF11, 11)]
    [InlineData(InputConstants.KeyF12, 12)]
    public void CtrlAltFn_SwitchesToThatVt(ushort key, int vt)
    {
        using var harness = new Harness();

        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyLeftCtrl, InputConstants.KeyPressed));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyLeftAlt, InputConstants.KeyPressed));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, key, InputConstants.KeyPressed));

        Assert.Equal([vt], harness.VtSwitches);
    }

    [Fact]
    public void CtrlAltFn_IsConsumed_NotSentToClients()
    {
        using var harness = new Harness();
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(21));
        harness.Client.RegisterObject(21, "wl_keyboard");
        harness.RoundTrip();
        harness.ClickAt(950, 530);
        harness.Client.ReadUntil(21, "enter");

        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyLeftCtrl, InputConstants.KeyPressed));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyLeftAlt, InputConstants.KeyPressed));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyF2, InputConstants.KeyPressed));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyF2, InputConstants.KeyReleased));

        Assert.Equal([2], harness.VtSwitches);

        // Nothing of the chord reached the client: collect every key it
        // sees until the ordinary key pressed after it — no F2 anywhere.
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyA, InputConstants.KeyPressed));
        var seen = new List<uint>();
        while (true)
        {
            var message = harness.Client.ReadUntil(21, "key");
            var code = message.Arguments[2].AsUInt32();
            if (code == InputConstants.KeyA)
            {
                break;
            }

            seen.Add(code);
        }

        Assert.DoesNotContain((uint)InputConstants.KeyF2, seen);
        Assert.Contains((uint)InputConstants.KeyLeftCtrl, seen);
    }

    [Fact]
    public void AltF4_Alone_DoesNotSwitchVt()
    {
        using var harness = new Harness();

        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyLeftAlt, InputConstants.KeyPressed));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyF4, InputConstants.KeyPressed));

        Assert.Empty(harness.VtSwitches);
    }

    [Fact]
    public void Suspended_RouterDropsEvents()
    {
        using var harness = new Harness();
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);
        harness.ClickAt(950, 530);

        harness.Router.Suspended = true;
        harness.Router.PlaceCursor(10, 10);
        Assert.Equal((950, 530), harness.Router.Cursor);
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeX, 50));
        Assert.Equal((950, 530), harness.Router.Cursor);

        harness.Router.Suspended = false;
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeX, 50));
        Assert.NotEqual((950, 530), harness.Router.Cursor);
    }

    [Fact]
    public void VtSwitch_ClearsHeldKeys_SoTheChordCannotStick()
    {
        using var harness = new Harness();
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(21));
        harness.Client.RegisterObject(21, "wl_keyboard");
        harness.RoundTrip();
        harness.ClickAt(950, 530);
        harness.Client.ReadUntil(21, "enter");

        // The chord that triggers a VT switch is down when the devices are
        // handed over; its releases are lost in the other session.
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyLeftCtrl, InputConstants.KeyPressed));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyLeftAlt, InputConstants.KeyPressed));
        harness.Router.ResetInputState();

        // Back from the switch: F4 alone must NOT read as Alt+F4, and the
        // client must see clean releases for everything that was held.
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyF4, InputConstants.KeyPressed));
        Assert.Empty(harness.VtSwitches);

        var released = new List<uint>();
        harness.Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(22));
        harness.Client.RegisterObject(22, "wl_keyboard");
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyF4, InputConstants.KeyReleased));
        while (true)
        {
            var message = harness.Client.ReadUntil(21, "key");
            if (message.Arguments[3].AsUInt32() == 0)
            {
                released.Add(message.Arguments[2].AsUInt32());
            }
            else if (message.Arguments[2].AsUInt32() == InputConstants.KeyF4)
            {
                break;
            }
        }

        Assert.Contains((uint)InputConstants.KeyLeftCtrl, released);
        Assert.Contains((uint)InputConstants.KeyLeftAlt, released);
    }

    [Fact]
    public void Motion_SendsEnterMotionFrameAndLeave()
    {
        using var harness = new Harness();

        // A role-less surface keeps its natural size (window policy only
        // governs xdg toplevels), so the pointer can enter and leave it.
        harness.Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(8));
        harness.Client.RegisterObject(8, "wl_surface");
        harness.Client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
        harness.Client.SendRequest(8, "wl_surface", "commit");
        CompositorSessionTests.WaitFor(() => harness.Scene.Surfaces.FirstOrDefault(), value => value is { IsMapped: true });

        harness.Client.SendRequest(20, "wl_seat", "get_pointer", WireArgument.NewId(22));
        harness.Client.RegisterObject(22, "wl_pointer");
        harness.RoundTrip();

        // Move onto the surface from outside: enter then motion then frame.
        harness.Router.PlaceCursor(40, 70);
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeX, 30));
        var enter = harness.Client.ReadUntil(22, "enter");
        Assert.Equal(8u, enter.Arguments[1].AsObject());
        harness.Client.ReadUntil(22, "motion");
        harness.Client.ReadUntil(22, "frame");

        // Move off the surface: leave.
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeX, 4000));
        harness.Client.ReadUntil(22, "leave");
    }

    [Fact]
    public void Wheel_SendsAxisWithSurfaceCoordinates()
    {
        using var harness = new Harness();
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(20, "wl_seat", "get_pointer", WireArgument.NewId(22));
        harness.Client.RegisterObject(22, "wl_pointer");
        harness.RoundTrip();
        harness.Router.PlaceCursor(950, 530);
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeX, 0));
        harness.Client.ReadUntil(22, "enter");

        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeWheel, 1));
        var axis = harness.Client.ReadUntil(22, "axis");
        Assert.Equal(0u, axis.Arguments[1].AsUInt32()); // vertical
        Assert.Equal(-40 << 8, axis.Arguments[2].AsFixed().RawValue);
        harness.Client.ReadUntil(22, "frame");
    }

    [Fact]
    public void Cursor_ClampsAtOutputEdges()
    {
        using var harness = new Harness();
        harness.Router.PlaceCursor(5, 5);

        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeX, -100));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeY, -100));
        Assert.Equal((0, 0), harness.Router.Cursor);

        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeX, 99999));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeY, 99999));
        Assert.Equal((1919, 1079), harness.Router.Cursor);
    }

    [Fact]
    public void Render_DrawsTheCursorOnTop()
    {
        using var harness = new Harness();
        var frame = new byte[1920 * 1080 * 4];

        // With the arrow at (100, 100), its top-left pixel is black.
        harness.Router.PlaceCursor(100, 100);
        harness.Scene.Render(frame, 1920, 1080, 1920 * 4, 0);
        Assert.Equal(0xFF000000u, PixelAt(frame, 1920, 100, 100));
        Assert.Equal(0xFFFFFFFFu, PixelAt(frame, 1920, 101, 102)); // the white fill

        // With the arrow elsewhere, (100, 100) is the background again.
        harness.Router.PlaceCursor(500, 500);
        harness.Scene.Render(frame, 1920, 1080, 1920 * 4, 0);
        Assert.Equal(Background.ColourAt(100, 1080), PixelAt(frame, 1920, 100, 100));
        Assert.Equal(0xFF000000u, PixelAt(frame, 1920, 500, 500));
    }

    [Fact]
    public void Unmap_SendsKeyboardLeaveAndDropsFocus()
    {
        using var harness = new Harness();
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(21));
        harness.Client.RegisterObject(21, "wl_keyboard");
        harness.RoundTrip();
        harness.ClickAt(950, 530);
        harness.Client.ReadUntil(21, "enter");

        // Unmap: attach a null buffer and commit.
        harness.Client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(0), WireArgument.Int32(0), WireArgument.Int32(0));
        harness.Client.SendRequest(8, "wl_surface", "commit");

        var leave = harness.Client.ReadUntil(21, "leave");
        Assert.Equal(8u, leave.Arguments[1].AsObject());
        Assert.Null(harness.Seat.KeyboardFocus);
    }

    [Fact]
    public void ShiftHeldAtClick_SendsStateAfterEnter()
    {
        using var harness = new Harness();
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(21));
        harness.Client.RegisterObject(21, "wl_keyboard");
        harness.RoundTrip();

        // Shift goes down with no focus: state only. Then click.
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyLeftShift, InputConstants.KeyPressed));
        harness.ClickAt(950, 530);

        harness.Client.ReadUntil(21, "enter");
        var modifiers = harness.NextEventOn(21);
        Assert.Equal("modifiers", harness.Client.EventName(modifiers));
        Assert.Equal(1u, modifiers.Arguments[1].AsUInt32()); // shift held at enter
    }

    [Fact]
    public void Enter_CarriesKeysAlreadyHeld()
    {
        using var harness = new Harness();
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(21));
        harness.Client.RegisterObject(21, "wl_keyboard");
        harness.RoundTrip();

        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyA, InputConstants.KeyPressed));
        harness.ClickAt(950, 530);

        var enter = harness.Client.ReadUntil(21, "enter");
        Assert.Equal(BitConverter.GetBytes(30u), enter.Arguments[2].AsArray().ToArray());
    }

    [Fact]
    public void Release_UnregistersTheKeyboard()
    {
        using var harness = new Harness();
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(21));
        harness.Client.RegisterObject(21, "wl_keyboard");
        harness.RoundTrip();
        harness.ClickAt(950, 530);
        harness.Client.ReadUntil(21, "enter");

        harness.Client.SendRequest(21, "wl_keyboard", "release");
        harness.RoundTrip();

        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.KeyA, InputConstants.KeyPressed));
        foreach (var (objectId, eventName) in harness.CollectUntilRoundTrip())
        {
            Assert.NotEqual((21u, "key"), (objectId, eventName));
        }
    }

    [Fact]
    public void Destroy_ClearsFocusAndNeverNamesDeadObjects()
    {
        using var harness = new Harness();
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(21));
        harness.Client.RegisterObject(21, "wl_keyboard");
        harness.RoundTrip();
        harness.ClickAt(950, 530);
        harness.Client.ReadUntil(21, "enter");

        // Role objects first (wayland.xml order), then the surface.
        harness.Client.SendRequest(12, "xdg_toplevel", "destroy");
        harness.Client.SendRequest(11, "xdg_surface", "destroy");
        harness.Client.SendRequest(8, "wl_surface", "destroy");

        // The safety property: an event naming surface 8 after its delete_id
        // would kill a real libwayland client. A leave is fine (and expected)
        // while the id is still valid — just never after the delete_id.
        var events = harness.CollectUntilRoundTrip();
        var deleteIndex = events.FindIndex(e => e == (1u, "delete_id"));
        foreach (var e in events.Where(e => e.EventName is "enter" or "leave"))
        {
            Assert.True(
                deleteIndex < 0 || events.IndexOf(e) < deleteIndex,
                $"{e.EventName} arrived after the surface's delete_id");
        }

        Assert.Null(harness.Seat.KeyboardFocus);
    }

    [Fact]
    public void PointerFrame_IsSuppressedBeforeVersion5()
    {
        using var harness = new Harness(seatVersion: 4);
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(20, "wl_seat", "get_pointer", WireArgument.NewId(22));
        harness.Client.RegisterObject(22, "wl_pointer");
        harness.RoundTrip();

        harness.Router.PlaceCursor(930, 530);
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeX, 20));
        harness.Client.ReadUntil(22, "motion");

        foreach (var (objectId, eventName) in harness.CollectUntilRoundTrip())
        {
            Assert.NotEqual((22u, "frame"), (objectId, eventName));
        }
    }

    [Fact]
    public void Touchpad_FirstFramePlacesWithoutMoving()
    {
        using var harness = new Harness();
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtSlot, 0));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtTrackingId, 7));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtPositionX, 500));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtPositionY, 300));
        Assert.Equal((0, 0), harness.Router.Cursor);
    }

    [Fact]
    public void Touchpad_SecondFrameMovesByDelta()
    {
        using var harness = new Harness();
        harness.Router.PlaceCursor(100, 100); // room below the top edge
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtSlot, 0));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtTrackingId, 7));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtPositionX, 500));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtPositionY, 300));

        // 20 units of finger travel = 7 cursor pixels (the 7/20 scale).
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtPositionX, 520));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtPositionY, 280));
        Assert.Equal((107, 93), harness.Router.Cursor);
    }

    [Fact]
    public void Touchpad_FingerUpStopsMotionAndStrayCoordsAreIgnored()
    {
        using var harness = new Harness();
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtSlot, 0));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtTrackingId, 7));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtPositionX, 500));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtPositionY, 300));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtTrackingId, -1));

        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtPositionX, 900));
        Assert.Equal((0, 0), harness.Router.Cursor);
    }

    [Fact]
    public void Touchpad_SlotsTrackSeparateFingers()
    {
        using var harness = new Harness();
        // Finger A on slot 0 at (500, 300); finger B on slot 1 at (10, 10).
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtSlot, 0));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtTrackingId, 7));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtPositionX, 500));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtPositionY, 300));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtSlot, 1));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtTrackingId, 8));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtPositionX, 10));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtPositionY, 10));

        // Slot 0 moves +20 units (= 7 pixels); slot 1 stays. Then slot 1
        // moves +20. The scale remainder is per-axis and shared across fingers.
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtSlot, 0));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtPositionX, 520));
        Assert.Equal((7, 0), harness.Router.Cursor);
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtSlot, 1));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventAbsolute, InputConstants.AbsMtPositionY, 30));
        Assert.Equal((7, 7), harness.Router.Cursor);
    }

    [Fact]
    public void TouchButtonCodes_AreNotKeyboardKeys()
    {
        using var harness = new Harness();
        harness.BindXdg();
        harness.MapWindow(8, 11, 12);
        harness.Client.SendRequest(20, "wl_seat", "get_keyboard", WireArgument.NewId(21));
        harness.Client.RegisterObject(21, "wl_keyboard");
        harness.RoundTrip();
        harness.ClickAt(950, 530);
        harness.Client.ReadUntil(21, "enter");

        // BTN_TOOL_FINGER (325) and BTN_TOUCH (330) are touch state.
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, 325, InputConstants.KeyPressed));
        harness.Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, 330, InputConstants.KeyPressed));
        foreach (var (objectId, eventName) in harness.CollectUntilRoundTrip())
        {
            Assert.NotEqual((21u, "key"), (objectId, eventName));
        }
    }

    private static uint PixelAt(byte[] frame, int width, int x, int y)
    {
        return MemoryMarshal.Cast<byte, uint>(frame.AsSpan())[(y * width) + x];
    }

    private sealed class Harness : IDisposable
    {
        private readonly DisplayServer _server;
        private readonly string _poolFile;

        public Harness(uint seatVersion = 7, Shell? shell = null)
        {
            Scene = new Scene();
            Seat = new SeatState { HasKeyboardDevice = true, HasPointerDevice = true };
            Router = new InputRouter(Scene, Seat, () => Quit = true, shell, switchVt: vt => VtSwitches.Add(vt));
            var socketPath = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
            _server = new DisplayServer(socketPath, handlerFactory: new CompositorHandlerFactory(Scene, Seat));
            _server.Start();
            Client = new TestClient(_server.SocketPath);
            CompositorSessionTests.Handshake(Client);
            (Content, _poolFile) = CompositorSessionTests.SetupShm(Client);
            Client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(4), WireArgument.String("wl_seat"), WireArgument.UInt32(seatVersion), WireArgument.NewId(20));
            Client.RegisterObject(20, "wl_seat");
        }

        public Scene Scene { get; }

        public SeatState Seat { get; }

        public InputRouter Router { get; }

        public TestClient Client { get; }

        public byte[] Content { get; }

        public bool Quit { get; private set; }

        public List<int> VtSwitches { get; } = [];

        public void BindXdg()
        {
            Client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(7), WireArgument.String("xdg_wm_base"), WireArgument.UInt32(6), WireArgument.NewId(10));
            Client.RegisterObject(10, "xdg_wm_base");
        }

        /// <summary>Maps a window through the full dance (policy slot by map order).</summary>
        public void MapWindow(uint surfaceId, uint xdgSurfaceId, uint toplevelId)
        {
            Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(surfaceId));
            Client.RegisterObject(surfaceId, "wl_surface");
            Client.SendRequest(10, "xdg_wm_base", "get_xdg_surface", WireArgument.NewId(xdgSurfaceId), WireArgument.Object(surfaceId));
            Client.RegisterObject(xdgSurfaceId, "xdg_surface");
            Client.SendRequest(xdgSurfaceId, "xdg_surface", "get_toplevel", WireArgument.NewId(toplevelId));
            Client.RegisterObject(toplevelId, "xdg_toplevel");
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

        /// <summary>Moves the cursor to the point and clicks there.</summary>
        public void ClickAt(int x, int y)
        {
            Router.PlaceCursor(x, y);
            Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.ButtonLeft, InputConstants.KeyPressed));
        }

        /// <summary>A sync round trip, so the server has created pending objects.</summary>
        public void RoundTrip()
        {
            Client.SendRequest(1, "wl_display", "sync", WireArgument.NewId(90));
            Client.RegisterObject(90, "wl_callback");
            Client.ReadUntil(90, "done");
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
                    return seen.Select(entry => (entry.Item1, entry.Item2)).ToList();
                }

                seen.Add((message.ObjectId, Client.EventName(message)));
            }
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

        public void Dispose()
        {
            Client.Dispose();
            _server.Dispose();
            File.Delete(_poolFile);
        }
    }
}
