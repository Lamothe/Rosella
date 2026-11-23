using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>Routes the globals Rosella implements to their real handlers.</summary>
internal sealed class CompositorHandlerFactory : IGlobalHandlerFactory
{
    private readonly Scene _scene;
    private readonly SeatState _seat;
    private readonly OutputInfo? _output;
    private readonly DmaBufReader? _dmaBuf;

    public CompositorHandlerFactory(Scene scene, SeatState seat, OutputInfo? output = null, DmaBufReader? dmaBuf = null)
    {
        _scene = scene;
        _seat = seat;
        _output = output;
        _dmaBuf = dmaBuf;
        scene.Seat = seat;
    }

    /// <summary>A factory with a private seat (tests without input).</summary>
    public CompositorHandlerFactory(Scene scene, OutputInfo? output = null)
        : this(scene, new SeatState(), output)
    {
    }

    public IReadOnlyList<Global> Globals => _dmaBuf is null
        ? [.. StandardGlobals.Create(), new(9, IdleInhibitHandler.Interface, 1)]
        : [.. StandardGlobals.Create(), new(8, DmaBufHandler.Interface, 4), new(9, IdleInhibitHandler.Interface, 1)];

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
            _ => null,
        };
    }
}
