
namespace Rosella.Compositor;

/// <summary>
/// The compositor's scene: windows in mapping order over the desktop
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

    /// <summary>The desktop shell, when one is wired in (taskbar and Start menu).</summary>
    internal Shell? Shell { get; set; }

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
    /// output's work area (below the bar, when there is a shell) with a
    /// small per-window cascade offset, raised to the top. Returns where the
    /// window's visible corner belongs.
    /// </summary>
    internal (int X, int Y) PlaceWindow(SurfaceHandler surface, int windowWidth, int windowHeight)
    {
        lock (_gate)
        {
            var (outputWidth, outputHeight) = OutputSize;
            const int topMargin = 8;
            var workBottom = Shell is null ? outputHeight : outputHeight - Shell.BarHeight;
            var offset = (_placed++ % 8) * 48;

            // Centred with a cascade, clamped to stay inside the work area:
            // no window may land under the taskbar or off the right edge.
            var maxX = Math.Max(0, outputWidth - windowWidth);
            var maxY = Math.Max(topMargin, workBottom - windowHeight);
            var x = Math.Clamp(((outputWidth - windowWidth) / 2) + offset, 0, maxX);
            var y = Math.Clamp(topMargin + ((workBottom - topMargin - windowHeight) / 2) + offset, topMargin, maxY);

            // Raise: later in the list renders on top.
            _surfaces.Remove(surface);
            _surfaces.Add(surface);
            return (x, y);
        }
    }

    private int _placed;

    /// <summary>Draws the background, the windows, the shell chrome, and the cursor.</summary>
    public void Render(Span<byte> pixels, int width, int height, int stride, long frame)
    {
        Background.Render(pixels, width, height, stride);
        lock (_gate)
        {
            foreach (var surface in _surfaces)
            {
                if (surface.IsMapped)
                {
                    surface.BlitInto(pixels, width, height, stride);
                }
            }

            // Windows 11 style: the focused window wears an accent border,
            // so a click has a visible consequence.
            if (Seat?.KeyboardFocus is { IsMapped: true } focus)
            {
                Chrome.FrameRect(pixels, width, height, stride,
                    new Rect(focus.Position.X - 1, focus.Position.Y - 1, focus.Width + 2, focus.Height + 2),
                    0xFF7B86E8);
            }
        }

        Shell?.Draw(pixels, width, height, stride, Bar.ClockString(), TaskWindows(), Seat?.KeyboardFocus);
        CursorSprite.Draw(pixels, width, height, stride, Cursor.X, Cursor.Y);
    }

    /// <summary>Fires frame callbacks for the frame just composited.</summary>
    public void CompleteFrame()
    {
        lock (_gate)
        {
            foreach (var surface in _surfaces)
            {
                if (surface.IsMapped)
                {
                    surface.CompleteFrame();
                }
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

    /// <summary>The taskbar's windows: every mapped toplevel, oldest first.</summary>
    internal IReadOnlyList<BarTask> TaskWindows()
    {
        lock (_gate)
        {
            var tasks = new List<BarTask>();
            foreach (var surface in _surfaces)
            {
                if (!surface.IsMapped || surface.Gone)
                {
                    continue;
                }

                if (surface.XdgRole?.Toplevel is { } toplevel)
                {
                    tasks.Add(new BarTask(Bar.LabelFor(toplevel.Title, toplevel.AppId), surface));
                }
            }

            return tasks;
        }
    }

    /// <summary>
    /// Dismisses every popup whose ancestor chain reaches the surface: a
    /// popup may never outlive its parent's place on screen.
    /// </summary>
    internal void DismissDescendantPopups(SurfaceHandler surface)
    {
        List<SurfaceHandler> snapshot;
        lock (_gate)
        {
            snapshot = _surfaces.ToList();
        }

        foreach (var candidate in snapshot)
        {
            var role = candidate.XdgRole;
            while (role is not null)
            {
                if (role.PopupParent?.Surface == surface)
                {
                    role.DismissPopup();
                    break;
                }

                role = role.PopupParent;
            }
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
