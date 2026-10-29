namespace Rosella.Compositor;

/// <summary>
/// The taskbar's view of the session: the windows an app launcher can
/// claim, and the two things a click may do to them — raise and focus, or
/// launch the app. Production is <see cref="SceneWindowRegistry"/>; tests
/// fake it.
/// </summary>
internal interface IWindowRegistry
{
    /// <summary>The topmost mapped window in z-order belonging to the app, if any.</summary>
    SurfaceHandler? MostRecentMatching(IReadOnlyList<string> matchKeys);

    /// <summary>Raises the window and hands it the keyboard.</summary>
    void RaiseAndFocus(SurfaceHandler surface, IReadOnlyList<int> heldKeys);

    /// <summary>Launches the app's argv through the shared spawn helper.</summary>
    void Launch(IReadOnlyList<string> argv);
}

/// <summary>The real registry: the scene's mapped toplevels and the seat's keyboard.</summary>
internal sealed class SceneWindowRegistry : IWindowRegistry
{
    private readonly Scene _scene;
    private readonly SeatState _seat;
    private readonly string? _socketName;

    internal SceneWindowRegistry(Scene scene, SeatState seat, string? socketName)
    {
        _scene = scene;
        _seat = seat;
        _socketName = socketName;
    }

    public SurfaceHandler? MostRecentMatching(IReadOnlyList<string> matchKeys)
    {
        var surfaces = _scene.Surfaces;
        for (var i = surfaces.Count - 1; i >= 0; i--)
        {
            var surface = surfaces[i];
            if (!surface.IsMapped || surface.Gone || surface.IsCursorSurface)
            {
                continue;
            }

            if (surface.XdgRole?.Toplevel is { } toplevel && PinnedMatch.Matches(toplevel.AppId, matchKeys))
            {
                return surface;
            }
        }

        return null;
    }

    public void RaiseAndFocus(SurfaceHandler surface, IReadOnlyList<int> heldKeys)
    {
        _scene.Raise(surface);
        _seat.SetKeyboardFocus(surface, heldKeys);
    }

    public void Launch(IReadOnlyList<string> argv) => AppLaunch.Spawn(argv, _socketName);
}
