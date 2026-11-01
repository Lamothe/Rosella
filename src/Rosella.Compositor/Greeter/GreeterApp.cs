using System.Net.Sockets;
using System.Runtime.InteropServices;
using Rosella.Drm;

namespace Rosella.Compositor.Greeter;

/// <summary>
/// The --greeter entry point: Rosella as greetd's login screen.
///
/// It is the same compositor — Scene, ScalePolicy, the evdev seat and
/// Scanout — with the desktop replaced by one full-frame overlay and the
/// greetd socket replacing the display server. It deliberately does not open
/// a Wayland socket or the control socket: a greeter owns the screen only
/// until it hands the VT to the authenticated session.
/// </summary>
internal static class GreeterApp
{
    internal static int Run()
    {
        var socketPath = Environment.GetEnvironmentVariable("GREETD_SOCK");
        if (string.IsNullOrEmpty(socketPath))
        {
            Console.Error.WriteLine("rosella: --greeter must be started by greetd (GREETD_SOCK is not set)");
            return 1;
        }

        var detection = Scanout.Detect();
        if (detection is null)
        {
            Console.Error.WriteLine("rosella: greeter: no usable connected output found");
            return 1;
        }

        var output = detection.Value.Output;

        // The same one scale policy as a session: on silver's 2880x1800 panel
        // this is 2, so the login card and its fields are drawn at 2x and the
        // 8-pixel password field GDM produced cannot happen.
        var scale = ScalePolicy.For(
            output.Mode.HDisplay,
            output.Mode.VDisplay,
            output.Connector.MmWidth,
            output.Connector.MmHeight);

        var scene = new Scene
        {
            Scale = scale,
            OutputSize = (output.Mode.HDisplay / scale, output.Mode.VDisplay / scale),
        };

        var sessions = GreeterSessions.Load(GreeterSessions.DefaultDirectory);
        var defaultUser = GreeterUser.Default();
        var state = new GreeterState(sessions, defaultUser);
        var screen = new GreeterScreen(state);
        state.Dirty = scene.MarkDirty;
        scene.Overlay = screen;

        GreetdConnection connection;
        try
        {
            connection = GreetdConnection.Connect(socketPath);
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
            Console.Error.WriteLine($"rosella: greeter: cannot connect to greetd at {socketPath}: {exception.Message}");
            return 1;
        }

        using var cancellation = new CancellationTokenSource();
        using var controller = new GreeterController(state, connection, cancellation.Cancel);

        var seat = new SeatState();
        var screenPower = new ScreenPower();
        using var input = new InputThread(
            scene,
            seat,
            () => cancellation.Cancel(),
            screenPower: screenPower,
            greeterKey: controller.Key);

        // greetd owns the VT, so there is no SessionSwitch here: the greeter user
        // cannot take VT_PROCESS. The evdev grab this process holds means
        // Ctrl+Alt+Fn never reaches the kernel console handler, and an
        // external VT switch (chvt over ssh) does not release the grab — the
        // install-time trial must stop greetd to hand input back. A seat
        // manager (libseat/logind) or VT-state polling is the follow-up that
        // makes switching work from this keyboard.
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });
        using var sighup = PosixSignalRegistration.Create(PosixSignal.SIGHUP, context =>
        {
            context.Cancel = true;
            cancellation.Cancel();
        });
        Console.CancelKeyPress += (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };

        using var device = detection.Value.Device;
        using var scanout = new Scanout(device, output, scene, screenPower);

        // Discover before Start: the reader threads exist per device, and the
        // hot-plug watcher only adds devices that appear later. Without this
        // the login screen would have no keyboard at all.
        input.Discover();
        input.Start();

        Console.WriteLine($"rosella: greeter on {output.Connector} at {output.Mode}, scale {scale} ({scene.OutputSize.Width}x{scene.OutputSize.Height} logical)");
        Console.WriteLine($"rosella: greeter: {sessions.Count} session(s) from {GreeterSessions.DefaultDirectory}");
        Console.WriteLine(defaultUser is null
            ? "rosella: greeter: asking for the username"
            : $"rosella: greeter: signing in as {defaultUser}");
        controller.Start();

        try
        {
            scanout.Run(cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown (SIGTERM from greetd, or a session was started).
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"rosella: greeter: scanout failed: {exception.Message}");
            return 1;
        }

        return controller.ExitCode;
    }
}