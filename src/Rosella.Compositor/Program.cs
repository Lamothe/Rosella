using System.Runtime.InteropServices;
using Rosella.Compositor;
using Rosella.Drm;
using Rosella.Server;

// Rosella's entry point: the Wayland display server plus, from milestone 3,
// real scanout — the Windows 11 style desktop on the first connected output.

if (args.Contains("--probe", StringComparer.Ordinal))
{
    Probe.Run();
    return 0;
}

// The display server picks the first free socket (wayland-0 may already be
// taken by another session's compositor in the same runtime directory).
var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath();
var displayName = args.Length > 0 ? args[0] : Path.GetFileName(DisplayServer.FindFreeSocketPath(runtimeDirectory));
var socketPath = Path.Combine(runtimeDirectory, displayName);

var scene = new Scene();
var seat = new SeatState();
var shell = new Shell();
shell.Launcher.SocketName = displayName;
scene.Shell = shell;

// The device set is fixed before clients bind, so wl_seat capabilities are
// right from the first moment (no hot-plug in M6).
using var cancellation = new CancellationTokenSource();

// The output is detected first: its physical size, mode and computed
// scale are what clients hear over wl_output, so they must be fixed
// before the server accepts its first connection.
var detection = Scanout.Detect();
if (detection is null)
{
    Console.Error.WriteLine("rosella: no usable connected output found");
    return 1;
}

var output = detection.Value.Output;
var scale = ScalePolicy.For(output.Mode.HDisplay, output.Mode.VDisplay, output.Connector.MmWidth, output.Connector.MmHeight);

// Everything in the scene is logical pixels: the physical scanout is
// Scale times that. Windows, chrome and input all live in logical space.
scene.Scale = scale;
scene.OutputSize = (output.Mode.HDisplay / scale, output.Mode.VDisplay / scale);
var outputInfo = new OutputInfo(
    output.Mode.HDisplay,
    output.Mode.VDisplay,
    output.Connector.MmWidth,
    output.Connector.MmHeight,
    (int)(output.Mode.VRefresh * 1000),
    scale,
    output.Connector.TypeName,
    output.Mode.Name,
    $"{output.Connector.TypeName}-{output.Connector.TypeId}");

using var input = new InputThread(scene, seat, () => cancellation.Cancel(), shell);
input.Discover();

using var server = new DisplayServer(
    socketPath,
    handlerFactory: new CompositorHandlerFactory(scene, seat, outputInfo));
server.Start();
Console.WriteLine($"rosella: display server listening on {socketPath}");
Console.WriteLine($"rosella: output scale {scale} ({scene.OutputSize.Width}x{scene.OutputSize.Height} logical)");

input.Router.PlaceCursor(scene.OutputSize.Width / 2, scene.OutputSize.Height / 2);

// Signal handling is registered first so it stays alive through cleanup and
// keeps interrupting the flip loop while the display state is restored.
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
Console.WriteLine($"rosella: driving {output.Connector} at {output.Mode} via CRTC {output.CrtcId}");
input.Start();

var scanout = new Scanout(device, output, scene);
try
{
    scanout.Run(cancellation.Token);
}
catch (OperationCanceledException)
{
    // Shutdown requested: fall through to the restore below.
}
catch (Exception exception)
{
    // Whatever went wrong, the previous display state must be restored and
    // the session must exit so the greeter can recover.
    Console.Error.WriteLine($"rosella: scanout failed: {exception.Message}");
}

scanout.Dispose(); // restores the previous display state
Console.WriteLine("rosella: scanout stopped, previous display state restored");
return 0;
