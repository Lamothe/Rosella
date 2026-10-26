using Rosella.Server;

// Rosella's entry point. For now this hosts the display server; the
// compositor and shell grow in here from milestone 3 onward.

var displayName = args.Length > 0 ? args[0] : "wayland-0";
var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath();
var socketPath = Path.Combine(runtimeDirectory, displayName);

using var server = new DisplayServer(socketPath);
server.Start();
Console.WriteLine($"rosella: display server listening on {socketPath}");
Console.WriteLine($"rosella: clients connect with WAYLAND_DISPLAY={displayName}");

var finished = new ManualResetEventSlim();
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    finished.Set();
};
finished.Wait();
