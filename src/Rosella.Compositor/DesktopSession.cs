using Rosella.Drm;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>One output's display socket, windows, dock and seat state.</summary>
internal sealed class DesktopSession : IDisposable
{
    private readonly DisplayServer _server;
    private readonly OutputInfo _outputInfo;
    private readonly SceneWindowRegistry _windows;
    private XWaylandBridge? _xWayland;
    internal Scene Scene { get; }
    internal SeatState Seat { get; }
    internal Shell Shell { get; }
    internal ScreenPower Power { get; } = new();
    internal Scanout Scanout { get; }
    internal string Display { get; }
    internal DrmOutput Output { get; }

    internal DesktopSession(DrmDevice device, DrmOutput output, string socketPath, Action quit,
        DmaBufReader? dmaBuf, DrmFlipQueue? flips, SeatState? sharedSeat = null, bool secondary = false)
    {
        Output = output;
        Seat = sharedSeat ?? new SeatState();
        Display = Path.GetFileName(socketPath);
        var scale = ScalePolicy.For(output.Mode.HDisplay, output.Mode.VDisplay, output.Connector.MmWidth, output.Connector.MmHeight);
        Scene = new Scene
        {
            Scale = scale,
            OutputSize = (output.Mode.HDisplay / scale, output.Mode.VDisplay / scale),
        };

        var taskbarPath = TaskbarGeneration.PublishedPath(AppContext.BaseDirectory);
        PinnedLauncher[] ResolvePinned() => PinnedApps.Resolve(Bar.IconSize * scale, Display, secondary);
        _windows = new SceneWindowRegistry(Scene, Seat, Display);
        Shell = new Shell(new SystemActions(quit), taskbarPath: taskbarPath)
        {
            SocketName = Display,
            Windows = _windows,
            Pinned = ResolvePinned(),
            Dashboard = new Dashboard(weather: new DashboardWeather(DashboardWeather.ConfigurationPath()),
                updates: new DashboardUpdates(enabled: true)),
            ResolvePinned = ResolvePinned,
            SavePinRemoval = PinnedConfiguration.Remove,
            ResolvePin = id => PinnedApps.ResolvePin(id, Bar.IconSize * scale, Display, secondary),
            SavePinAddition = PinnedConfiguration.Add,
            SavePinMove = PinnedConfiguration.Move,
        };
        Shell.ConfigureDashboardWidgets(DashboardWidgetModules.PublishedPath(AppContext.BaseDirectory));
        Scene.Shell = Shell;
        if (output.Connector.Modes.Select(mode => (mode.HDisplay, mode.VDisplay)).Distinct().Skip(1).Any())
        {
            Shell.Settings.Resolution = new ResolutionPicker(output);
        }

        var cursor = CursorSettings.Resolve();
        foreach (var name in new[] { cursor.Theme, cursor.Fallback, "default", "Adwaita" })
        {
            if (name is not null && CursorTheme.Open(name, cursor.Size) is { } theme && theme.Pointer(scale) is not null)
            {
                Scene.Cursors = theme;
                Console.Error.WriteLine($"rosella: cursor theme {name} at {cursor.Size}");
                break;
            }
        }

        if (Scene.Cursors is null)
        {
            Console.Error.WriteLine($"rosella: no cursor theme found ('{cursor.Theme}'); using the built-in arrow");
        }

        _outputInfo = new OutputInfo(output.Mode.HDisplay, output.Mode.VDisplay, output.Connector.MmWidth, output.Connector.MmHeight,
            (int)Math.Round(output.Mode.RefreshHz * 1000), scale, output.Connector.TypeName, output.Mode.Name,
            $"{output.Connector.TypeName}-{output.Connector.TypeId}");
        var factory = new CompositorHandlerFactory(Scene, Seat, _outputInfo, dmaBuf);
        _server = new DisplayServer(socketPath, factory.Globals, factory);
        try
        {
            Scanout = new Scanout(device, output, Scene, Power, flips);
        }
        catch
        {
            _server.Dispose();
            Scene.Seat = null;
            throw;
        }
    }

    internal void Start(string? xWaylandHelper, DmaBufReader? xWaylandDmaBuf)
    {
        _server.Start();
        if (xWaylandHelper is not null)
        {
            _xWayland = new XWaylandBridge(xWaylandHelper, Scene, Seat, _outputInfo, xWaylandDmaBuf);
            _windows.XWayland = _xWayland;
        }
        Console.WriteLine($"rosella: {Display} driving {Output.Connector} at {Output.Mode}@{Output.Mode.RefreshHz:F2} Hz via CRTC {Output.CrtcId}, scale {Scene.Scale}");
    }

    internal void AttachInput(InputRouter router)
    {
        Scene.ToplevelMapped = surface =>
        {
            if (surface.XdgRole?.Toplevel is { AppId.Length: > 0 } toplevel)
            {
                Console.WriteLine($"rosella: {Display} mapped {toplevel.AppId}");
            }
            router.FocusNewWindow(surface);
        };
        router.PlaceCursor(Scene.OutputSize.Width / 2, Scene.OutputSize.Height / 2);
    }

    public void Dispose()
    {
        Scanout.CancelPendingCapture();
        Shell.Settings.Close();
        _xWayland?.Dispose();
        _server.Dispose();
        Scene.Seat = null;
        Scanout.Dispose();
        Shell.Dashboard.Widgets.Unload();
    }
}
