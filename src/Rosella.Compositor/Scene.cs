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

    /// <summary>The scanout's size; windows are placed relative to it.</summary>
    internal (int Width, int Height) OutputSize { get; set; } = (1920, 1080);

    /// <summary>
    /// The window policy: every mapped window goes to the centre of the
    /// output with a small per-window cascade offset, raised to the top.
    /// Returns where the window's visible corner belongs.
    /// </summary>
    internal (int X, int Y) PlaceWindow(SurfaceHandler surface, int windowWidth, int windowHeight)
    {
        lock (_gate)
        {
            var (outputWidth, outputHeight) = OutputSize;
            var offset = (_placed++ % 8) * 48;
            var x = Math.Max(0, ((outputWidth - windowWidth) / 2) + offset);
            var y = Math.Max(0, ((outputHeight - windowHeight) / 2) + offset);

            // Raise: later in the list renders on top.
            _surfaces.Remove(surface);
            _surfaces.Add(surface);
            return (x, y);
        }
    }

    private int _placed;

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

    /// <summary>Cascade placement for surfaces with no role (raw test clients).</summary>
    private static (int X, int Y) Cascade(int index)
    {
        var offset = (index % 10) * 72;
        return (64 + offset, 64 + offset);
    }
}
