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
            // Gone first: a stale hit-test reference must not be re-focused.
            surface.Gone = true;
            _surfaces.Remove(surface);
        }

        // Destroy paths only: the surface id is dying or gone, so focus is
        // cleared without leave events (they would name a dead object).
        Seat?.OnSurfaceHidden(surface, canSendLeave: false);
    }

    /// <summary>The scanout's size; windows are placed relative to it.</summary>
    internal (int Width, int Height) OutputSize { get; set; } = (1920, 1080);

    /// <summary>The input seat, notified when surfaces hide or go away.</summary>
    internal SeatState? Seat { get; set; }

    /// <summary>Where the cursor arrow's hotspot sits (guarded by the scene gate).</summary>
    internal (int X, int Y) Cursor
    {
        get
        {
            lock (_gate)
            {
                return _cursor;
            }
        }

        set
        {
            lock (_gate)
            {
                _cursor = value;
            }
        }
    }

    private (int X, int Y) _cursor;

    /// <summary>Focus teardown for the seat (unmap = leave, destroy = silent).</summary>
    internal void OnSurfaceHidden(SurfaceHandler surface, bool canSendLeave)
    {
        Seat?.OnSurfaceHidden(surface, canSendLeave);
    }

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

            CursorSprite.Draw(pixels, width, height, stride, Cursor.X, Cursor.Y);
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

    /// <summary>The topmost mapped surface at the point, if any.</summary>
    internal SurfaceHandler? HitTest(int x, int y)
    {
        lock (_gate)
        {
            for (var i = _surfaces.Count - 1; i >= 0; i--)
            {
                var surface = _surfaces[i];
                if (!surface.IsMapped)
                {
                    continue;
                }

                if (x >= surface.Position.X && x < surface.Position.X + surface.Width
                    && y >= surface.Position.Y && y < surface.Position.Y + surface.Height)
                {
                    return surface;
                }
            }

            return null;
        }
    }

    /// <summary>Raises the surface to the top (it renders last).</summary>
    internal void Raise(SurfaceHandler surface)
    {
        lock (_gate)
        {
            if (_surfaces.Remove(surface))
            {
                _surfaces.Add(surface);
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
