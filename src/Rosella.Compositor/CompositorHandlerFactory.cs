using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>Routes the globals Rosella implements to their real handlers.</summary>
internal sealed class CompositorHandlerFactory : IGlobalHandlerFactory
{
    private readonly Scene _scene;
    private readonly SeatState _seat;
    private readonly OutputInfo? _output;

    public CompositorHandlerFactory(Scene scene, SeatState seat, OutputInfo? output = null)
    {
        _scene = scene;
        _seat = seat;
        _output = output;
        scene.Seat = seat;
    }

    /// <summary>A factory with a private seat (tests without input).</summary>
    public CompositorHandlerFactory(Scene scene, OutputInfo? output = null)
        : this(scene, new SeatState(), output)
    {
    }

    public IRequestHandler? Create(string interfaceName)
    {
        return interfaceName switch
        {
            "wl_compositor" => new CompositorHandler(_scene),
            "wl_shm" => new ShmHandler(),
            "wl_seat" => new SeatHandler(_seat),
            "wl_output" => _output is null ? null : new OutputHandler(_output),
            "xdg_wm_base" => new XdgWmBaseHandler(_scene),
            _ => null,
        };
    }
}
