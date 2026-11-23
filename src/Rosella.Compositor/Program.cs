using System.Runtime.InteropServices;
using Rosella.Compositor;
using Rosella.Control;
using Rosella.Drm;
using System.Net.Sockets;
using Rosella.Server;

LiveReload.ReportProcess();

if (args.Contains("--probe", StringComparer.Ordinal))
{
    Probe.Run();
    if (Scanout.DetectDesktops() is { } selected)
    {
        using var probeDevice = selected.Device;
        foreach (var output in selected.Outputs)
        {
            Console.WriteLine($"rosella: desktop {output.Connector} at {output.Mode}@{output.Mode.RefreshHz:F2} Hz via CRTC {output.CrtcId}");
        }
    }
    return 0;
}

// greetd greeter mode: Rosella draws the login screen itself, authenticates
// through greetd's socket and hands the VT to the chosen session. No display
// server and no control socket are started; greetd owns the seat.
if (args.Contains("--greeter", StringComparer.Ordinal))
{
    return Rosella.Compositor.Greeter.GreeterApp.Run();
}

// The display server picks the first free socket (wayland-0 may already be
// taken by another session's compositor in the same runtime directory).
var runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath();
if (args is ["--headless", var headlessDisplay, var renderNode])
{
    return HeadlessDisplay.Run(Path.Combine(runtimeDirectory, headlessDisplay), renderNode);
}

var displayName = args.Length > 0 ? args[0] : Path.GetFileName(DisplayServer.FindFreeSocketPath(runtimeDirectory));
var socketPath = Path.Combine(runtimeDirectory, displayName);

// Single instance per seat, checked before any hardware is touched: a
// sibling compositor's grab sweep would steal this session's devices the
// moment it releases them, and the keyboard would die while the desktop
// kept showing. Refuse to be either half of that pair.
if (ControlServer.ListenerPresent(ControlClient.DefaultSocketPath))
{
    Console.Error.WriteLine("rosella: refusing to start — another compositor is already running");
    return 1;
}

var detection = Scanout.DetectDesktops();
if (detection is not { } displays)
{
    Console.Error.WriteLine("rosella: no usable connected output found");
    return 1;
}

using var device = displays.Device;
using var sessionSwitch = new SessionSwitch();
if (sessionSwitch.VtState is null)
{
    Console.Error.WriteLine("rosella: refusing to start — no controlling VT (run from a console login, not ssh)");
    return 1;
}

using var cancellation = new CancellationTokenSource();
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

using var dmaBuf = Environment.GetEnvironmentVariable("ROSELLA_DMABUF") == "1"
    ? DmaBufReader.TryOpen(DmaBufReader.FindRenderNode(device.Path))
    : null;
using var flips = displays.Outputs.Length > 1 ? new DrmFlipQueue(device) : null;
using var primary = new DesktopSession(device, ResolutionPicker.Select(displays.Outputs[0]), socketPath, cancellation.Cancel, dmaBuf, flips);

DesktopSession? CreateDellDesktop(DrmOutput output)
{
    try
    {
        var secondaryPath = Path.Combine(runtimeDirectory, $"{displayName}-dell");
        return new DesktopSession(device, output, secondaryPath, cancellation.Cancel, dmaBuf, flips, primary.Seat, secondary: true);
    }
    catch (Exception exception) when (exception is DrmException or IOException or SocketException or InvalidOperationException)
    {
        Console.Error.WriteLine($"rosella: could not create the Dell desktop: {exception.Message}");
        return null;
    }
}

bool TryLightDellDesktop(DesktopSession desktop)
{
    try
    {
        desktop.Scanout.Initialize();
        return true;
    }
    catch (DrmException exception)
    {
        Console.Error.WriteLine($"rosella: could not light the Dell desktop at {desktop.Output.Mode.RefreshHz:F2} Hz: {exception.Message}");
        return false;
    }
}

// Both CRTC snapshots must be taken before either modeset changes them.
var secondaryCandidate = displays.Outputs.Length > 1 ? CreateDellDesktop(ResolutionPicker.Select(displays.Outputs[1])) : null;

InputThread? runningInput = null;
InputRouter? secondaryRouter = null;
InputThread ControlInput() => Volatile.Read(ref runningInput) ?? throw new InvalidOperationException("Rosella is starting or stopping");
DesktopSession ActiveDesktop() => ControlInput().Router == secondaryRouter && secondaryCandidate is { } second ? second : primary;

// Claim the seat's control endpoint before the first modeset or input grab.
ControlServer? control = null;
try
{
    control = new ControlServer(ControlClient.DefaultSocketPath,
        new ControlAdapter(ActiveDesktop, ControlInput, () => sessionSwitch.VtState));
    Console.WriteLine($"rosella: control socket at {control.SocketPath}");
}
catch (ControlSocketInUseException exception)
{
    Console.Error.WriteLine($"rosella: refusing to start — {exception.Message}");
    secondaryCandidate?.Dispose();
    return 1;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"rosella: control socket unavailable: {exception.Message}");
}
using var controlLifetime = control;

try
{
    try
    {
        primary.Scanout.Initialize();
    }
    catch (DrmException) when (primary.Output.Mode != displays.Outputs[0].Mode)
    {
        ResolutionPicker.Discard(primary.Output);
        Console.Error.WriteLine("rosella: display rejected the saved resolution; the next login will use the default");
        throw;
    }
    if (secondaryCandidate is { } other && !TryLightDellDesktop(other))
    {
        var output = other.Output;
        if (output.Mode != displays.Outputs[1].Mode)
        {
            ResolutionPicker.Discard(output);
        }
        var fallback = DrmSelection.SelectMode(output.Connector.Modes);
        other.Dispose();
        secondaryCandidate = null;
        if (fallback != output.Mode)
        {
            secondaryCandidate = CreateDellDesktop(output with { Mode = fallback });
            if (secondaryCandidate is { } retry && !TryLightDellDesktop(retry))
            {
                retry.Dispose();
                secondaryCandidate = null;
            }
        }
    }
}
catch
{
    secondaryCandidate?.Dispose();
    throw;
}

using var secondary = secondaryCandidate;
secondaryRouter = secondary is { } dell
    ? new InputRouter(dell.Scene, dell.Seat, cancellation.Cancel, dell.Shell, dell.Power, sessionSwitch.SwitchTo)
    : null;
using var input = new InputThread(primary.Scene, primary.Seat, cancellation.Cancel, primary.Shell,
    primary.Power, sessionSwitch.SwitchTo,
    secondary: secondary is not null && secondaryRouter is not null ? (secondaryRouter, secondary.Seat) : null);
primary.AttachInput(input.PrimaryRouter);
if (secondary is not null && secondaryRouter is not null)
{
    primary.Scene.OtherDesktop = secondary.Scene;
    secondary.Scene.OtherDesktop = primary.Scene;
    secondary.AttachInput(secondaryRouter);
}
input.Discover();

void Repaint()
{
    primary.Scene.MarkDirty();
    secondary?.Scene.MarkDirty();
}

LiveReload.Repaint = Repaint;
primary.Start();
secondary?.Start();
using var portalSession = PortalSession.Start(primary.Display);
using var notifications = new DesktopNotifications(primary.Shell.Notifications);
var startupApps = StartupApps.ForMachine();
primary.Shell.StartupApps = startupApps;
if (secondary is not null)
{
    secondary.Shell.StartupApps = startupApps;
}

var wallpaper = new WallpaperService(
    Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CACHE_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache"),
        "rosella"),
    primary.Output.Mode.HDisplay,
    primary.Output.Mode.VDisplay,
    Repaint,
    secondarySize: secondary is { } second ? (second.Output.Mode.HDisplay, second.Output.Mode.VDisplay) : null);
wallpaper.Start();

using var sigwinch = OperatingSystem.IsWindows() ? null : PosixSignalRegistration.Create(PosixSignal.SIGWINCH, context =>
{
    context.Cancel = true;
    if (Volatile.Read(ref runningInput) is { } current)
    {
        (current.Router == secondaryRouter ? secondary : primary)?.Scanout.RequestDump();
    }
});

Scanout[] scanouts = secondary is null ? [primary.Scanout] : [primary.Scanout, secondary.Scanout];
var exitCode = 0;

void Run(Scanout scanout)
{
    try
    {
        scanout.Run(cancellation.Token);
    }
    catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
    {
    }
    catch (Exception exception)
    {
        Console.Error.WriteLine($"rosella: scanout failed: {exception.Message}");
        Interlocked.Exchange(ref exitCode, 1);
    }
    finally
    {
        cancellation.Cancel();
    }
}

try
{
    sessionSwitch.Attach(device, scanouts, input);
    startupApps.Launch(primary.Shell.Pinned, primary.Shell.Windows!);
    input.Start();
    Volatile.Write(ref runningInput, input);
    if (secondary is not null)
    {
        var renderSecond = Task.Factory.StartNew(() => Run(secondary.Scanout),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Run(primary.Scanout);
        renderSecond.GetAwaiter().GetResult();
    }
    else
    {
        Run(primary.Scanout);
    }
}
finally
{
    Volatile.Write(ref runningInput, null);
    sessionSwitch.Detach();
    foreach (var scanout in scanouts)
    {
        scanout.CancelPendingCapture();
    }
}
Console.WriteLine("rosella: scanout stopped");
return exitCode;
