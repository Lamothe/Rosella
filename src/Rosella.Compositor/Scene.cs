
namespace Rosella.Compositor;

/// <summary>
/// The compositor's scene: windows in mapping order over the desktop
/// background, shared between client threads and the scanout loop.
/// </summary>
public sealed class Scene
{
    private readonly object _gate = new();
    private readonly List<SurfaceHandler> _surfaces = new();
    private bool _dirty = true;

    /// <summary>
    /// Something on screen changed: the scanout loop composites on the next
    /// wake instead of sleeping. Everything visible funnels through here —
    /// commits, cursor moves, focus, shell state — so an idle desktop does
    /// no rendering work at all.
    /// </summary>
    internal void MarkDirty()
    {
        lock (_gate)
        {
            _dirty = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Waits until the scene is dirty or <paramref name="maxWait"/> passes
    /// (bound for clock ticks). Returns false when the token is cancelled.
    /// Consumes the dirty flag; a change arriving during the subsequent
    /// render re-arms it and the next frame picks it up.
    /// </summary>
    internal bool WaitForDirty(TimeSpan maxWait, CancellationToken token)
    {
        // Cancellation must end the wait promptly: the token itself pulses
        // the gate, so the loop below re-checks it on every wake.
        using var wake = token.Register(() =>
        {
            lock (_gate)
            {
                Monitor.PulseAll(_gate);
            }
        });

        lock (_gate)
        {
            while (!_dirty)
            {
                if (token.IsCancellationRequested)
                {
                    return false;
                }

                if (!Monitor.Wait(_gate, maxWait))
                {
                    break;
                }
            }

            _dirty = false;
            return !token.IsCancellationRequested;
        }
    }

    /// <summary>Registers a surface; it gets its position in the cascade.</summary>
    internal void Register(SurfaceHandler surface)
    {
        lock (_gate)
        {
            surface.Position = Cascade(_surfaces.Count);
            _surfaces.Add(surface);
            _dirty = true;
            Monitor.PulseAll(_gate);
        }
    }

    internal void Remove(SurfaceHandler surface)
    {
        lock (_gate)
        {
            // Gone first: a stale hit-test reference must not be re-focused.
            surface.Gone = true;
            _surfaces.Remove(surface);
            _dirty = true;
            Monitor.PulseAll(_gate);
        }

        // Destroy paths only: the surface id is dying or gone, so focus is
        // cleared without leave events (they would name a dead object).
        Seat?.OnSurfaceHidden(surface, canSendLeave: false);
    }

    /// <summary>The scanout's logical size (physical pixels / Scale); windows are placed in it.</summary>
    internal (int Width, int Height) OutputSize { get; set; } = (1920, 1080);

    /// <summary>
    /// Physical pixels per logical pixel on this output (ScalePolicy): all
    /// scene coordinates are logical, rendering multiplies by this.
    /// </summary>
    internal int Scale { get; set; } = 1;

    /// <summary>Where windows live: the output minus the taskbar band.</summary>
    internal Rect WorkArea => new(0, 0, OutputSize.Width, OutputSize.Height - (Shell is null ? 0 : Shell.BarHeight));

    /// <summary>The desktop shell, when one is wired in (taskbar and system menu).</summary>
    internal Shell? Shell { get; set; }

    /// <summary>
    /// The input seat, notified when surfaces hide or go away. Setting it
    /// wires the seat's dirty callback (cursor image changes re-composite).
    /// </summary>
    internal SeatState? Seat
    {
        get => _seat;
        set
        {
            _seat = value;
            if (value is not null)
            {
                value.Dirty = MarkDirty;
            }
        }
    }

    private SeatState? _seat;

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

            MarkDirty();
        }
    }

    private (int X, int Y) _cursor;

    /// <summary>Focus teardown for the seat (unmap = leave, destroy = silent).</summary>
    internal void OnSurfaceHidden(SurfaceHandler surface, bool canSendLeave)
    {
        Seat?.OnSurfaceHidden(surface, canSendLeave);
    }

    /// <summary>
    /// The window policy: every mapped toplevel opens maximised to the work
    /// area, always (opinionated — there is no floating). Popups place
    /// themselves via their positioner corner. Whatever the path, a freshly
    /// mapped surface is raised to the top.
    /// </summary>
    internal (int X, int Y) PlaceWindow(SurfaceHandler surface, int windowWidth, int windowHeight)
    {
        lock (_gate)
        {
            _surfaces.Remove(surface);
            _surfaces.Add(surface);
            _dirty = true;
            Monitor.PulseAll(_gate);
            return (WorkArea.X, WorkArea.Y);
        }
    }

    /// <summary>
    /// Draws the background, the windows, the shell chrome, and the cursor —
    /// all in logical pixels scaled to the physical scanout by Scale.
    /// </summary>
    /// <summary>One line per surface (diagnostics: the runtime truth behind a render report).</summary>
    internal string[] DescribeSurfaces()
    {
        SurfaceHandler[] snapshot;
        lock (_gate)
        {
            snapshot = _surfaces.ToArray();
        }

        return snapshot.Select(s => s.Describe()).ToArray();
    }

    public void Render(Span<byte> pixels, int width, int height, int stride, long frame)
    {
        Background.Render(pixels, width, height, stride);
        var scale = Math.Max(1, Scale);
        lock (_gate)
        {
            foreach (var surface in _surfaces)
            {
                // Cursor surfaces are drawn at the pointer, never as windows.
                if (surface.IsMapped && !surface.IsCursorSurface)
                {
                    surface.BlitInto(pixels, width, height, stride, scale);
                }
            }

            // Windows 11 style: the focused window wears an accent border
            // hugging its visible rect, so a click has a visible consequence.
            if (Seat?.KeyboardFocus is { IsMapped: true } focus)
            {
                var visible = focus.Visible;
                Chrome.FrameRect(pixels, width, height, stride,
                    new Rect(visible.X, visible.Y, visible.W, visible.H),
                    0xFF7B86E8, scale);
            }
        }

        // Chrome lays out in logical pixels (matching the input thread's
        // hit-tests exactly) and draws scaled onto the physical scanout.
        Shell?.Draw(pixels, width / scale, height / scale, stride, scale, Bar.ClockString(), TaskWindows(), Seat?.KeyboardFocus, Cursor);

        // The cursor: the client's cursor surface when the focused client set
        // one (context shapes — text beam, hand — as the apps intend), hidden
        // when the focused client hid it, else our arrow. Once a client has
        // named a cursor surface the image is that surface's — never our
        // arrow over the app's intent (content may still be in flight).
        var (cursorX, cursorY) = Cursor;
        var cursor = Seat?.CurrentCursor() ?? default;
        if (cursor.Hidden)
        {
            // The focused client hid the pointer image (spec: NULL surface).
        }
        else if (cursor.Surface is { } cursorSurface)
        {
            if (cursorSurface is { IsMapped: true, Gone: false })
            {
                var (hotspotX, hotspotY) = (cursor.HotspotX, cursor.HotspotY);
                cursorSurface.BlitAt(
                    pixels, width, height, stride,
                    (cursorX - hotspotX) * scale, (cursorY - hotspotY) * scale,
                    cursorSurface.ContentWidth * scale, cursorSurface.ContentHeight * scale);
            }
        }
        else
        {
            CursorSprite.Draw(pixels, width, height, stride, cursorX * scale, cursorY * scale, scale);
        }
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
                if (!surface.IsMapped || surface.IsCursorSurface)
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
                if (!surface.IsMapped || surface.Gone || surface.IsCursorSurface)
                {
                    continue;
                }

                if (surface.XdgRole?.Toplevel is { } toplevel)
                {
                    tasks.Add(new BarTask(Bar.LabelFor(toplevel.Title, toplevel.AppId), toplevel.AppId, surface));
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
                _dirty = true;
                Monitor.PulseAll(_gate);
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
