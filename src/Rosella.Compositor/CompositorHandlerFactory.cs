using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>Routes the globals Rosella implements to their real handlers.</summary>
internal sealed class CompositorHandlerFactory : IGlobalHandlerFactory
{
    private readonly Scene _scene;

    public CompositorHandlerFactory(Scene scene)
    {
        _scene = scene;
    }

    public IRequestHandler? Create(string interfaceName)
    {
        return interfaceName switch
        {
            "wl_compositor" => new CompositorHandler(_scene),
            "wl_shm" => new ShmHandler(),
            "xdg_wm_base" => new XdgWmBaseHandler(_scene),
            _ => null,
        };
    }
}
