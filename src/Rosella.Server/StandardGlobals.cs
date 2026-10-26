namespace Rosella.Server;

/// <summary>
/// The globals Rosella advertises. Opinionated and hardcoded: these are the
/// interfaces the compositor implements, numbered in this order.
/// </summary>
public static class StandardGlobals
{
    public static IReadOnlyList<Global> Create()
    {
        return
        [
            new(1, "wl_compositor", 7),
            new(2, "wl_subcompositor", 1),
            new(3, "wl_shm", 3),
            new(4, "wl_seat", 11),
            new(5, "wl_output", 4),
            new(6, "wl_data_device_manager", 3),
        ];
    }
}
