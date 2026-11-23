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
    public void MapWindow_FillsTheOutput()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello");

        var surface = harness.MappedSurface(8);
        Assert.Equal((0, 0), surface.Position);
        Assert.True(surface.Position.Y + surface.Height <= harness.Scene.OutputSize.Height,
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void UngrabbedPopupChildPress_PreservesWindowFocus(bool parentless)
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Host");
        harness.OpenKeyboard();
        harness.OpenPointer();
        harness.ClickAt(950, 530);
        harness.CollectUntilRoundTrip();
        harness.MapPopup(parentless ? 0u : 11u, 15, 16, 13);
        var client = harness.Client;
        client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(2), WireArgument.String("wl_subcompositor"), WireArgument.UInt32(1), WireArgument.NewId(40));
        client.RegisterObject(40, "wl_subcompositor");
        client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(41));
        client.RegisterObject(41, "wl_surface");
        client.SendRequest(40, "wl_subcompositor", "get_subsurface", WireArgument.NewId(42), WireArgument.Object(41), WireArgument.Object(15));
        client.RegisterObject(42, "wl_subsurface");
        client.SendRequest(41, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
        client.SendRequest(41, "wl_surface", "commit");
        client.SendRequest(15, "wl_surface", "commit");
        harness.CollectUntilRoundTrip();
        var child = harness.MappedSurface(41);
        harness.Router.PlaceCursor(child.Position.X, child.Position.Y);
        harness.Press(InputConstants.ButtonLeft);
        Assert.Same(child, harness.Seat.PointerFocus);
        Assert.Equal(8u, harness.Seat.KeyboardFocus?.Id);
        Assert.DoesNotContain(harness.DrainUntilRoundTrip(), e => e.ObjectId == 21 && harness.Client.EventName(e) is "enter" or "leave");
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
        harness.RevealDock();
        harness.Router.PlaceCursor(harness.Scene.OutputSize.Width / 2,
            harness.Scene.OutputSize.Height - (Shell.BarHeight / 2));
        harness.Press(InputConstants.ButtonRight);
        harness.Release(InputConstants.ButtonLeft);

        var buttons = harness.DrainUntilRoundTrip()
            .Where(e => harness.Client.EventName(e) == "button")
            .ToList();
        Assert.Equal(2, buttons.Count);
        Assert.All(buttons, e => Assert.Equal((uint)InputConstants.ButtonLeft, e.Arguments[2].AsUInt32()));
    }

    [Fact]
    public void DockMenu_MovesAnExistingWindowBetweenDisplaysAndKeepsItsConnection()
    {
        using var source = new Harness();
        using var destination = new Harness(source.Seat);
        source.Scene.OtherDesktop = destination.Scene;
        destination.Scene.OtherDesktop = source.Scene;
        destination.Scene.OutputSize = (960, 540);
        var pin = new PinnedLauncher("Test App", "org.example.Test.desktop", ["org.example.Test"], [], null, null);
        destination.Shell.Pinned = [pin];
        source.Shell.Pinned = [pin];
        source.MapWindow(8, 11, 12, appId: "org.example.Test");
        source.MapWindow(30, 31, 32, appId: "org.example.Other");
        var moving = source.MappedSurface(8);
        var staying = source.MappedSurface(30);
        source.OpenKeyboard();
        source.OpenPointer();
        source.Router.SetDesktopActive(false);

        destination.RevealDock();
        destination.RenderFrame();
        var icon = Assert.Single(destination.Shell.LastBarLayout!.Pinned).Rect;
        destination.Router.PlaceCursor(icon.X + 2, icon.Y + 2);
        destination.Press(InputConstants.ButtonRight);
        destination.Release(InputConstants.ButtonRight);
        var menu = Assert.IsType<DockMenu>(destination.Shell.DockMenu);
        Assert.Same(moving, menu.MoveWindow);
        Assert.Empty(menu.CloseWindows);
        destination.ClickAt(menu.Bounds.X + 3, menu.Bounds.Y + 3);

        Assert.Same(staying, Assert.Single(source.Scene.TaskWindows()).Surface);
        Assert.Same(moving, Assert.Single(destination.Scene.TaskWindows()).Surface);
        Assert.Same(moving, destination.Seat.KeyboardFocus);
        Assert.Equal((0, 0, 960, 540), moving.Visible);
        var configure = source.Client.ReadUntil(12, "configure");
        Assert.Equal(960, configure.Arguments[0].AsInt32());
        Assert.Equal(540, configure.Arguments[1].AsInt32());
        var enter = source.Client.ReadUntil(21, "enter");
        Assert.Equal(8u, enter.Arguments[1].AsObject());
        destination.Press(InputConstants.KeyA);
        Assert.Equal((uint)InputConstants.KeyA, source.Client.ReadUntil(21, "key").Arguments[2].AsUInt32());
        destination.Release(InputConstants.KeyA);
        destination.ClickAt(100, 100);
        Assert.Equal(8u, source.Client.ReadUntil(22, "enter").Arguments[1].AsObject());

        source.Client.SendRequest(8, "wl_surface", "frame", WireArgument.NewId(40));
        source.Client.RegisterObject(40, "wl_callback");
        source.Client.SendRequest(8, "wl_surface", "commit");
        source.CollectUntilRoundTrip();
        destination.RenderFrame();
        destination.Scene.CompleteFrame();
        source.Client.ReadUntil(40, "done");

        source.MapPopup(11, 15, 16, 13, display: destination.Scene);
        var popup = Assert.Single(destination.Scene.Surfaces, surface => surface.Id == 15);
        Assert.Same(moving, popup.XdgRole!.RootToplevelSurface);
        Assert.DoesNotContain(popup, source.Scene.Surfaces);

        destination.Router.SetDesktopActive(false);
        source.Router.SetDesktopActive(true);
        Assert.Same(staying, source.Seat.KeyboardFocus);
        Assert.True(source.Scene.TryMoveWindowHere(moving));
        Assert.Contains(source.Scene.TaskWindows(), task => task.Surface == moving);
        Assert.Empty(destination.Scene.TaskWindows());
        Assert.False(popup.IsMapped);
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
        Assert.Equal((800, 600), (scene.WorkArea.W, scene.WorkArea.H));
    }

    [Fact]
    public void StaleTaskButton_DoesNotFocusAGhostWindow()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Hello");
        harness.RevealDock();
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
    public void ReloadTaskbarFromClock_ReplacesCodeAndKeepsWindowAndFocus()
    {
        using var harness = new Harness(taskbarPath: Path.Combine(AppContext.BaseDirectory, TaskbarGeneration.AssemblyFile));
        harness.MapWindow(8, 11, 12);
        harness.ClickAt(500, 500);
        var window = harness.MappedSurface(8);
        var first = harness.Shell.Taskbar;
        Assert.NotNull(first.Context);

        harness.RevealDock();
        harness.RenderFrame();
        var clock = harness.Shell.LastBarLayout!.Clock;
        harness.Router.PlaceCursor(clock.X + clock.W / 2, clock.Y + clock.H / 2);
        harness.Press(InputConstants.ButtonRight);
        harness.Release(InputConstants.ButtonRight);
        var menu = Assert.IsType<DockMenu>(harness.Shell.DockMenu);
        Assert.True(menu.ReloadTaskbar);
        harness.ClickAt(menu.Bounds.X + menu.Bounds.W / 2, menu.Bounds.Y + menu.Bounds.H / 2);
        harness.RenderFrame();

        var current = harness.Shell.Taskbar;
        Assert.NotNull(current.Context);
        Assert.Equal(first.BuildId, current.BuildId);
        Assert.NotSame(first.Context, current.Context);
        Assert.NotSame(first.Module.GetType().Assembly, current.Module.GetType().Assembly);
        Assert.Same(window, harness.Scene.TaskWindows().Single().Surface);
        Assert.Same(window, harness.Seat.KeyboardFocus);
        Assert.Contains("Same build reloaded", harness.Shell.DescribeModules());

        harness.ClickAt(clock.X + clock.W / 2, clock.Y + clock.H / 2);
        Assert.True(current.Settings.IsOpen);
        harness.RenderFrame();
        current.Settings.Close();
    }

    [Fact]
    public void ModulesSubmenu_ShowsTheActiveBuildAfterSuccessAndFailure()
    {
        var directory = Directory.CreateTempSubdirectory("rosella-modules-");
        var modulePath = Path.Combine(directory.FullName, TaskbarGeneration.AssemblyFile);
        try
        {
            using var harness = new Harness(taskbarPath: modulePath);
            harness.MapWindow(8, 11, 12);
            harness.ClickAt(500, 500);
            var window = harness.MappedSurface(8);
            harness.RevealDock();
            harness.RenderFrame();
            var clock = harness.Shell.LastBarLayout!.Clock;
            void Click(Rect rect) => harness.ClickAt(rect.X + rect.W / 2, rect.Y + rect.H / 2);
            Rect Panel() => harness.Shell.Settings.PanelRect(clock, 1920, 1080);

            Click(clock);
            Click(SettingsPanel.ModulesRow(Panel(), showResolution: false));
            Assert.True(harness.Shell.Settings.ModulesOpen);
            Assert.Equal(("Built-in", null), harness.Shell.Settings.Modules!.ReadStatus());

            File.Copy(Path.Combine(AppContext.BaseDirectory, TaskbarGeneration.AssemblyFile), modulePath);
            Click(ModulePicker.ReloadButton(Panel()));
            var active = harness.Shell.Taskbar;
            Assert.NotNull(active.Context);
            Assert.NotNull(active.BuildId);
            Assert.True(harness.Shell.Settings.ModulesOpen);
            Assert.Equal((active.BuildLabel, "New build loaded"), harness.Shell.Settings.Modules!.ReadStatus());
            Assert.Contains($"  active: {active.BuildId}\n  installed: {active.BuildId}", harness.Shell.DescribeModules());
            Assert.Same(window, harness.Seat.KeyboardFocus);
            harness.RenderFrame();

            File.WriteAllText(modulePath, "invalid module");
            Click(ModulePicker.ReloadButton(Panel()));
            Assert.Same(active, harness.Shell.Taskbar);
            Assert.True(harness.Shell.Settings.ModulesOpen);
            Assert.Equal((active.BuildLabel, "Reload failed · active build kept"),
                harness.Shell.Settings.Modules!.ReadStatus());
            harness.RenderFrame();

            Click(ModulePicker.Back(Panel()));
            Assert.True(harness.Shell.Settings.IsOpen);
            Assert.False(harness.Shell.Settings.ModulesOpen);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void ReloadTaskbarWithInvalidAssembly_KeepsWorkingGeneration()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.Copy(Path.Combine(AppContext.BaseDirectory, TaskbarGeneration.AssemblyFile), path, overwrite: true);
            var shell = new Shell(taskbarPath: path);
            var first = shell.Taskbar;
            Assert.NotNull(first.Context);

            File.WriteAllText(path, "invalid module");
            Assert.Throws<InvalidOperationException>(shell.ReloadTaskbar);

            Assert.Same(first, shell.Taskbar);
            Assert.True(shell.ComputeBarLayout(1920, 1080, []).Clock.W > 0);
            first.Context.Unload();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ClockButton_TogglesSettings()
    {
        using var harness = new Harness();
        var layout = Bar.ComputeLayout(1920, 1080, Bar.ClockString(), []);

        harness.ClickAt(layout.Clock.X + (layout.Clock.W / 2), layout.Clock.Y + (layout.Clock.H / 2));
        Assert.True(harness.Shell.Settings.IsOpen);

        harness.ClickAt(layout.Clock.X + (layout.Clock.W / 2), layout.Clock.Y + (layout.Clock.H / 2));
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

    [Fact]
    public void VTReset_ReleasesASettingsSliderDrag()
    {
        using var harness = new Harness();
        var layout = Bar.ComputeLayout(1920, 1080, Bar.ClockString(), []);
        harness.ClickAt(layout.Clock.X + (layout.Clock.W / 2), layout.Clock.Y + (layout.Clock.H / 2));
        var panel = harness.Shell.Settings.PanelRect(layout.Clock, 1920, 1080);
        var track = Assert.IsType<SettingsPanel>(harness.Shell.Settings).MicrophoneTrackRect(panel);

        harness.Router.PlaceCursor(track.X + (track.W / 2), track.Y);
        harness.Press(InputConstants.ButtonLeft);
        Assert.True(harness.Shell.Settings.IsDraggingSlider);

        harness.Router.ResetInputState();

        Assert.False(harness.Shell.Settings.IsDraggingSlider);
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

        harness.RevealDock();
        harness.RenderFrame();
        var layout = harness.Shell.LastBarLayout!;

        Assert.True(layout.Pinned[0].Running);
        Assert.Equal("Two", Assert.Single(layout.Tasks).Task.Label);
    }

    [Theory]
    [InlineData(900, 1)]
    [InlineData(900, 2)]
    [InlineData(600, 1)]
    public void TooManyPins_KeepClockAndRunningWindowAccessible(int physicalWidth, int scale)
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Last", appId: "app-31");
        var pins = Enumerable.Range(0, 32).Select(i => Pin($"App {i}", [$"app-{i}"])).ToArray();
        var width = physicalWidth / scale;
        var layout = Bar.ComputeLayout(width, 700 / scale, Bar.ClockString(), harness.Scene.TaskWindows(), pins);
        Assert.True(layout.Pinned.Length < pins.Length);
        Assert.All(layout.Pinned, button => Assert.True(button.Rect.X >= layout.Bounds.X && button.Rect.X + button.Rect.W <= layout.Clock.X));
        Assert.Equal("Last", Assert.Single(layout.Tasks).Task.Label);
    }

    [Fact]
    public void FittingPins_DoNotDisappearWhenTheirWindowOpens()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, appId: "app-0");
        var pins = Enumerable.Range(0, 6).Select(i => Pin($"App {i}", [$"app-{i}"])).ToArray();
        var layout = Bar.ComputeLayout(650, 700, Bar.ClockString(), harness.Scene.TaskWindows(), pins);
        Assert.Equal(6, layout.Pinned.Length);
        Assert.Empty(layout.Tasks);
    }

    [Fact]
    public void OverflowingPins_DoNotOscillateWhenEvictingARunningPin()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Last", appId: "app-6");
        var pins = Enumerable.Range(0, 8).Select(i => Pin($"App {i}", [$"app-{i}"])).ToArray();
        var clockWidth = ShellText.Measure(Bar.ClockString(), ShellText.BarEm).Width + Bar.Pad + Bar.EdgePad;
        var width = 24 + (2 * Bar.EdgePad) + (6 * (Bar.IconBoxSize + Bar.PinGap)) + clockWidth;
        var layout = Bar.ComputeLayout(width, 700, Bar.ClockString(), harness.Scene.TaskWindows(), pins);
        Assert.True(layout.Pinned.Length < 6);
        Assert.Equal("Last", Assert.Single(layout.Tasks).Task.Label);
    }

    [Fact]
    public void NarrowBar_PrefersAVisibleWindowWhenOnlyTheTaskFits()
    {
        using var harness = new Harness();
        harness.MapWindow(8, 11, 12, title: "Window", appId: "other");
        var pins = Enumerable.Range(0, 10).Select(i => Pin($"App {i}", [$"app-{i}"])).ToArray();
        var clockWidth = ShellText.Measure(Bar.ClockString(), ShellText.BarEm).Width + Bar.Pad + Bar.EdgePad;
        var width = 24 + (2 * Bar.EdgePad) + clockWidth + Bar.IconBoxSize + Bar.PinGap;
        var layout = Bar.ComputeLayout(width, 700, Bar.ClockString(), harness.Scene.TaskWindows(), pins);
        Assert.Empty(layout.Pinned);
        Assert.Equal("Window", Assert.Single(layout.Tasks).Task.Label);
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

        public int? Launch(IReadOnlyList<string> argv) { Launched.Add([.. argv]); return null; }
    }

    private sealed class Harness : IDisposable
    {
        private readonly DisplayServer _server;
        private readonly string _poolFile;

        public Harness(SeatState? seat = null, string? taskbarPath = null)
        {
            Scene = new Scene();
            Seat = seat ?? new SeatState { HasKeyboardDevice = true, HasPointerDevice = true };
            Actions = new FakeSystemActions();
            Shell = new Shell(Actions, taskbarPath: taskbarPath) { SocketName = "wayland-test" };
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
        public void RevealDock() =>
            Router.PlaceCursor(Scene.OutputSize.Width / 2, Scene.OutputSize.Height - 1);

        public void ClickAt(int x, int y)
        {
            if (y >= Scene.OutputSize.Height - Rosella.Compositor.Shell.BarHeight)
            {
                RevealDock();
            }

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
        public void MapPopup(uint parentXdgSurfaceId, uint popupSurfaceId, uint popupXdgSurfaceId, uint popupId,
            bool grab = false, uint grabSerial = 0, Scene? display = null)
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
                () => (display ?? Scene).Surfaces.FirstOrDefault(s => s.Id == popupSurfaceId),
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
            Shell.Settings.Close();
            Shell.Taskbar.Context?.Unload();
            _server.Dispose();
            if (File.Exists(_poolFile))
            {
                File.Delete(_poolFile);
            }
        }
    }
}
