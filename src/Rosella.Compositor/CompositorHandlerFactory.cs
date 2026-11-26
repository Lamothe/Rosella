using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>Routes the globals Rosella implements to their real handlers.</summary>
internal sealed class CompositorHandlerFactory : IGlobalHandlerFactory
{
    private readonly Scene _scene;
    private readonly SeatState _seat;
    private readonly OutputInfo? _output;
    private readonly DmaBufReader? _dmaBuf;
    private readonly bool _includeXwaylandProtocols;
    private bool SupportsExplicitSync => _includeXwaylandProtocols && _dmaBuf?.TimelineDevice is not null;

    public CompositorHandlerFactory(Scene scene, SeatState seat, OutputInfo? output = null, DmaBufReader? dmaBuf = null,
        bool includeXwaylandProtocols = false)
    {
        _scene = scene;
        _seat = seat;
        _output = output;
        _dmaBuf = dmaBuf;
        _includeXwaylandProtocols = includeXwaylandProtocols;
        scene.Seat = seat;
    }

    /// <summary>A factory with a private seat (tests without input).</summary>
    public CompositorHandlerFactory(Scene scene, OutputInfo? output = null)
        : this(scene, new SeatState(), output)
    {
    }

    public IReadOnlyList<Global> Globals
    {
        get
        {
            List<Global> globals = [.. StandardGlobals.Create()];
            if (_dmaBuf is not null)
            {
                globals.Add(new(8, DmaBufHandler.Interface, 4));
            }
            globals.Add(new(9, IdleInhibitHandler.Interface, 1));
            if (_includeXwaylandProtocols)
            {
                globals.Add(new(10, ViewporterHandler.Interface, 1));
            }
            globals.Add(new(11, RelativePointerHandler.Interface, 1));
            globals.Add(new(12, PointerConstraintsHandler.Interface, 1));
            if (SupportsExplicitSync)
            {
                globals.Add(new(13, DrmSyncobjHandler.Interface, 1));
            }
            return globals;
        }
    }

    public IRequestHandler? Create(string interfaceName)
    {
        return interfaceName switch
        {
            "wl_compositor" => new CompositorHandler(_scene),
            "wl_subcompositor" => new SubcompositorHandler(),
            "wl_shm" => new ShmHandler(),
            "wl_seat" => new SeatHandler(_seat),
            "wl_data_device_manager" => new ClipboardManager(_seat),
            "wl_output" => _output is null ? null : new OutputHandler(_output),
            "xdg_wm_base" => new XdgWmBaseHandler(),
            DmaBufHandler.Interface when _dmaBuf is not null => new DmaBufHandler(_dmaBuf),
            IdleInhibitHandler.Interface => new IdleInhibitHandler(),
            ViewporterHandler.Interface when _includeXwaylandProtocols => new ViewporterHandler(),
            RelativePointerHandler.Interface => new RelativePointerHandler(_seat),
            PointerConstraintsHandler.Interface => new PointerConstraintsHandler(_seat),
            DrmSyncobjHandler.Interface when SupportsExplicitSync => new DrmSyncobjHandler(_dmaBuf!.TimelineDevice!),
            _ => null,
        };
    }
}
