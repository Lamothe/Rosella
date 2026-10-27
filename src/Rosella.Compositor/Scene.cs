namespace Rosella.Compositor;

/// <summary>
/// The compositor's scene: surfaces in mapping order over the test-pattern
/// background, shared between client threads and the scanout loop.
/// </summary>
public sealed class Scene
{
    private readonly object _gate = new();
    private readonly List<SurfaceHandler> _surfaces = new();

    /// <summary>Registers a surface; it gets its position in the cascade.</summary>
    internal void Register(SurfaceHandler surface)
    {
        lock (_gate)
        {
            surface.Position = Cascade(_surfaces.Count);
            _surfaces.Add(surface);
        }
    }

    internal void Remove(SurfaceHandler surface)
    {
        lock (_gate)
        {
            _surfaces.Remove(surface);
        }
    }

    /// <summary>Draws the background and every mapped surface.</summary>
    public void Render(Span<byte> pixels, int width, int height, int stride, long frame)
    {
        TestPattern.Render(pixels, width, height, stride, frame);
        lock (_gate)
        {
            foreach (var surface in _surfaces)
            {
                surface.BlitInto(pixels, width, height, stride);
            }
        }
    }

    /// <summary>Fires frame callbacks for the frame just composited.</summary>
    public void CompleteFrame()
    {
        lock (_gate)
        {
            foreach (var surface in _surfaces)
            {
                surface.CompleteFrame();
            }
        }
    }

    /// <summary>Test seam: the registered surfaces.</summary>
    internal IReadOnlyList<SurfaceHandler> Surfaces
    {
        get
        {
            lock (_gate)
            {
                return _surfaces.ToArray();
            }
        }
    }

    /// <summary>Cascade placement until the shell (M5) positions windows properly.</summary>
    private static (int X, int Y) Cascade(int index)
    {
        var offset = (index % 10) * 72;
        return (64 + offset, 64 + offset);
    }
}
