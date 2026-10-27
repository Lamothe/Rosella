using System.Runtime.InteropServices;
using Rosella.Compositor;
using Rosella.Drm;
using Rosella.Server;

// Rosella's entry point: the Wayland display server plus, from milestone 3,
// real scanout of an opinionated test pattern on the first connected output.

if (args.Contains("--probe", StringComparer.Ordinal))
{
    Probe.Run();
    return 0;
}

var displayName = args.Length > 0 ? args[0] : "wayland-0";
var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath();
var socketPath = Path.Combine(runtimeDirectory, displayName);

using var server = new DisplayServer(socketPath);
server.Start();
Console.WriteLine($"rosella: display server listening on {socketPath}");

var detection = Scanout.Detect();
if (detection is null)
{
    Console.Error.WriteLine("rosella: no usable connected output found");
    return 1;
}

using var cancellation = new CancellationTokenSource();

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
var output = detection.Value.Output;
Console.WriteLine($"rosella: driving {output.Connector} at {output.Mode} via CRTC {output.CrtcId}");

var scanout = new Scanout(device, output);
try
{
    scanout.Run(cancellation.Token);
}
catch (OperationCanceledException)
{
    // Shutdown requested: fall through to the restore below.
}

scanout.Dispose(); // restores the previous display state
Console.WriteLine("rosella: scanout stopped, previous display state restored");
return 0;
