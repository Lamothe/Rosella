using System.Diagnostics;
using System.Runtime.InteropServices;
using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

/// <summary>
/// Runs real, third-party Wayland clients (from their distro packages)
/// against the compositor. Skips silently when a client is not installed.
/// </summary>
public class RealClientTests
{
    [Fact]
    public void WaylandInfo_CompletesAgainstTheCompositor()
    {
        var waylandInfo = Find("wayland-info");
        if (waylandInfo is null)
        {
            return;
        }

        var scene = new Scene();
        var socketPath = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
        using var server = new DisplayServer(socketPath, handlerFactory: new CompositorHandlerFactory(scene));
        server.Start();

        var startInfo = new ProcessStartInfo(waylandInfo)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["XDG_RUNTIME_DIR"] = Path.GetDirectoryName(socketPath)!;
        startInfo.Environment["WAYLAND_DISPLAY"] = Path.GetFileName(socketPath);

        using var process = Process.Start(startInfo)!;
        Assert.True(process.WaitForExit(10000), "wayland-info did not finish");
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>
    /// A real GTK4 window minimised through its own CSD button: the request
    /// must take the window off the screen while its taskbar button stays.
    /// The toolkit is the point — a hand-written client would only prove the
    /// compositor agrees with itself.
    /// </summary>
    [Fact]
    public async Task Gtk4_Minimize_HidesTheWindowAndKeepsItsTaskButton()
    {
        if (!Gtk4Available(out var gjs))
        {
            return;
        }

        var scene = new Scene();
        var seat = new SeatState { HasKeyboardDevice = true, HasPointerDevice = true };
        var shell = new Shell();
        shell.Windows = new SceneWindowRegistry(scene, seat, "wayland-test");
        scene.Shell = shell;
        var socketPath = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
        var output = new OutputInfo(1280, 800, 300, 190, 60000, 1, "TEST", "1280x800", "TEST-1");
        using var server = new DisplayServer(socketPath, handlerFactory: new CompositorHandlerFactory(scene, seat, output));
        server.Start();

        // The scanout's loop: GTK's frame clock starves without frame
        // callbacks, and a client that cannot draw is not a fair test.
        using var frames = new CancellationTokenSource();
        var frameThread = new Thread(() =>
        {
            var buffer = new byte[1280 * 800 * 4];
            while (!frames.IsCancellationRequested)
            {
                scene.Render(buffer, 1280, 800, 1280 * 4, frame: 0);
                scene.CompleteFrame();
                Thread.Sleep(16);
            }
        }) { IsBackground = true };
        frameThread.Start();

        var path = Path.Combine(Path.GetTempPath(), $"rosella-gtk-{Guid.NewGuid():N}.js");
        await File.WriteAllTextAsync(path, """
            imports.gi.versions.Gtk = "4.0";
            const { GLib, Gtk } = imports.gi;
            Gtk.init();
            const loop = new GLib.MainLoop(null, false);
            const win = new Gtk.Window({ title: "Minimise Probe" });
            win.set_default_size(400, 300);
            win.set_child(new Gtk.Label({ label: "probe" }));
            win.present();
            GLib.timeout_add(GLib.PRIORITY_DEFAULT, 800, () => {
                win.minimize();
                return false;
            });
            GLib.timeout_add(GLib.PRIORITY_DEFAULT, 1600, () => {
                loop.quit();
                return false;
            });
            loop.run();
            """);

        var startInfo = new ProcessStartInfo(gjs!, [path])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["XDG_RUNTIME_DIR"] = Path.GetDirectoryName(socketPath)!;
        startInfo.Environment["WAYLAND_DISPLAY"] = Path.GetFileName(socketPath);
        startInfo.Environment["GDK_BACKEND"] = "wayland";

        using var process = Process.Start(startInfo)!;
        try
        {
            var surface = CompositorSessionTests.WaitFor(
                () => scene.Surfaces.FirstOrDefault(candidate => candidate.XdgRole?.Toplevel is not null),
                value => value is { IsMapped: true })!;

            CompositorSessionTests.WaitFor(() => surface.Hidden, value => value);
            var tasks = scene.TaskWindows();
            Assert.Single(tasks);
            Assert.True(tasks[0].Minimized, "the window lost its taskbar button");

            // Stop the loop before rendering from this thread: Scene.Render
            // owns shared per-render state and is a single-threaded caller.
            frames.Cancel();
            Assert.True(frameThread.Join(10000), "the frame loop did not stop");

            // The window's own pixels are gone: the desktop shows through.
            // Restore it and the same pixel comes back — hide and show, end
            // to end.
            var buffer = new byte[1280 * 800 * 4];
            scene.Render(buffer, 1280, 800, 1280 * 4, frame: 0);
            var hidden = MemoryMarshal.Read<uint>(buffer.AsSpan((100 * 1280 * 4) + (100 * 4)));

            scene.Restore(surface);
            Assert.False(surface.Hidden);
            scene.Render(buffer, 1280, 800, 1280 * 4, frame: 0);
            var shown = MemoryMarshal.Read<uint>(buffer.AsSpan((100 * 1280 * 4) + (100 * 4)));

            Assert.NotEqual(hidden, shown);
        }
        finally
        {
            frames.Cancel();
            frameThread.Join(5000);
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
            }

            File.Delete(path);
        }
    }

    /// <summary>
    /// A real GTK4 popover opened from a widget at the bottom of the window:
    /// GTK anchors it below the button and asks the compositor for flip,
    /// slide and resize. The whole popup must end up on screen — the
    /// reported Chrome bug was a menu placed with its top-left at the anchor
    /// point and its lower half off the bottom edge.
    /// </summary>
    [Fact]
    public async Task Gtk4_PopoverAtTheBottomEdge_StaysOnScreen()
    {
        if (!Gtk4Available(out var gjs))
        {
            return;
        }

        var scene = new Scene { OutputSize = (1280, 800) };
        var seat = new SeatState { HasKeyboardDevice = true, HasPointerDevice = true };
        var shell = new Shell();
        shell.Windows = new SceneWindowRegistry(scene, seat, "wayland-test");
        scene.Shell = shell;
        var socketPath = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
        var output = new OutputInfo(1280, 800, 300, 190, 60000, 1, "TEST", "1280x800", "TEST-1");
        using var server = new DisplayServer(socketPath, handlerFactory: new CompositorHandlerFactory(scene, seat, output));
        server.Start();

        using var frames = new CancellationTokenSource();
        var frameThread = new Thread(() =>
        {
            var buffer = new byte[1280 * 800 * 4];
            while (!frames.IsCancellationRequested)
            {
                scene.Render(buffer, 1280, 800, 1280 * 4, frame: 0);
                scene.CompleteFrame();
                Thread.Sleep(16);
            }
        }) { IsBackground = true };
        frameThread.Start();

        var path = Path.Combine(Path.GetTempPath(), $"rosella-gtk-{Guid.NewGuid():N}.js");
        await File.WriteAllTextAsync(path, """
            imports.gi.versions.Gtk = "4.0";
            const { GLib, Gtk } = imports.gi;
            Gtk.init();
            const loop = new GLib.MainLoop(null, false);
            const win = new Gtk.Window({ title: "Popover Probe" });
            win.set_default_size(500, 600);
            const box = new Gtk.Box({ orientation: Gtk.Orientation.VERTICAL });
            const spacer = new Gtk.Box();
            spacer.set_vexpand(true);
            const button = new Gtk.Button({ label: "bottom of the window" });
            box.append(spacer);
            box.append(button);
            win.set_child(box);
            const popover = new Gtk.Popover({ autohide: false });
            popover.set_size_request(320, 520);
            popover.set_child(new Gtk.Label({ label: "menu" }));
            popover.set_parent(button);
            win.present();
            GLib.timeout_add(GLib.PRIORITY_DEFAULT, 900, () => {
                popover.popup();
                return false;
            });
            GLib.timeout_add(GLib.PRIORITY_DEFAULT, 2400, () => {
                loop.quit();
                return false;
            });
            loop.run();
            """);

        var startInfo = new ProcessStartInfo(gjs!, [path])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["XDG_RUNTIME_DIR"] = Path.GetDirectoryName(socketPath)!;
        startInfo.Environment["WAYLAND_DISPLAY"] = Path.GetFileName(socketPath);
        startInfo.Environment["GDK_BACKEND"] = "wayland";

        using var process = Process.Start(startInfo)!;
        try
        {
            var popup = CompositorSessionTests.WaitFor(
                () => scene.Surfaces.FirstOrDefault(candidate => candidate.XdgRole?.PopupRole is not null && candidate.IsMapped),
                value => value is not null)!;

            // GTK's own constraint request: the toolkit drew the popup where
            // the compositor told it, and that place is fully on screen.
            var area = scene.WorkArea;
            var visible = popup.Visible;
            Assert.True(visible.X >= area.X && visible.Y >= area.Y, $"popup at {visible.X},{visible.Y} starts outside {area}");
            Assert.True(visible.X + visible.W <= area.X + area.W, $"popup right edge {visible.X + visible.W} past the work area {area.W}");
            Assert.True(visible.Y + visible.H <= area.Y + area.H, $"popup bottom {visible.Y + visible.H} past the work area {area.H}");

            // And it is still anchored to the widget at the bottom: GTK asks
            // for flip_y, so the popup sits above the button rather than
            // being slid up over it. Its bottom edge lands near the button,
            // not near the top of the screen.
            Assert.True(visible.Y + visible.H >= (area.Y + area.H) * 9 / 10, $"popup spans {visible.Y}..{visible.Y + visible.H}, not anchored at the bottom");
        }
        finally
        {
            frames.Cancel();
            frameThread.Join(5000);
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
            }

            File.Delete(path);
        }
    }

    /// <summary>
    /// Whether gjs can import GTK4, asked of gjs itself: the typelib lives
    /// in a different directory on every distro, and a hardcoded path would
    /// turn this test into a silent skip where it matters most.
    /// </summary>
    private static bool Gtk4Available(out string? gjs)
    {
        gjs = Find("gjs");
        if (gjs is null)
        {
            return false;
        }

        var startInfo = new ProcessStartInfo(gjs, ["-c", "imports.gi.versions.Gtk = '4.0'; imports.gi.Gtk;"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var probe = Process.Start(startInfo)!;
        var drained = Task.WhenAll(probe.StandardOutput.ReadToEndAsync(), probe.StandardError.ReadToEndAsync());
        if (!probe.WaitForExit(5000))
        {
            probe.Kill(entireProcessTree: true);
            return false;
        }

        // The pipes must be drained even on success, or a full buffer can
        // hold the child open on a loaded machine.
        drained.Wait(1000);
        return probe.ExitCode == 0;
    }

    private static string? Find(string name)
    {
        foreach (var directory in new[] { "/usr/bin", "/usr/local/bin", "/bin" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
