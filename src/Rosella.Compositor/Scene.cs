
using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>
/// A full-frame overlay drawn instead of the desktop scene: the greeter's
/// login screen. It has no clients underneath and paints every pixel itself
/// (background included), so the scene never composites the desktop or the
/// pointer while one is set.
/// </summary>
internal interface ISceneOverlay
{
    /// <summary>
    /// Paints the whole output. The buffer is physical pixels; the overlay
    /// scales its own logical layout by <paramref name="scale"/>.
    /// </summary>
    void Draw(Span<byte> pixels, int width, int height, int stride, int scale);
}

/// <summary>
/// The compositor's scene: windows in mapping order over the desktop
/// background, shared between client threads and the scanout loop.
/// </summary>
public sealed class Scene
{
    private readonly object _gate = new();
    private readonly List<SurfaceHandler> _surfaces = new();
    private readonly List<XdgPopupHandler> _popupGrabs = new();
    private bool _dirty = true;

    /// <summary>The pixels this frame has to repaint (guarded by <see cref="_gate"/>).</summary>
    private readonly DamageTracker _damage = new();

    /// <summary>Scratch for the parts of a damage region no opaque window covers; render-loop only.</summary>
    private readonly RectList _uncovered = new();

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
            _damage.MarkFull();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Something changed only inside <paramref name="rect"/>: the loop wakes
    /// and repaints that region instead of the whole output. Callers that
    /// cannot say what changed keep using the no-argument overload, which
    /// means "repaint everything" — over-painting is a cost, under-painting
    /// is a bug.
    /// </summary>
    internal void MarkDirty(in DamageRect rect)
    {
        lock (_gate)
        {
            _dirty = true;
            _damage.Add(rect);
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Takes this frame's damage, clipped to an output of the given size.
    /// Zero rects means nothing changed (the loop can skip the frame
    /// entirely); one whole-output rect means everything did.
    /// </summary>
    internal int TakeDamage(int width, int height, Span<DamageRect> destination)
    {
        ExpireMagnify();
        lock (_gate)
        {
            return _damage.Take(width, height, destination);
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

            // Joining, leaving and restacking change what is on screen in
            // ways the scene cannot name: damage the whole output. A wake
            // with no damage would otherwise paint nothing and leave the
            // departed window's pixels up (see MarkDirty).
            _damage.MarkFull();
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
            _popupGrabs.RemoveAll(popup => ReferenceEquals(popup.Surface, surface));
            _damage.MarkFull();
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

    /// <summary>
    /// The greeter's login screen, when this scene belongs to a greeter. A
    /// full-screen overlay owns every pixel, so a partial damage region still
    /// produces a complete frame (see <see cref="RenderRegion"/>).
    /// </summary>
    internal ISceneOverlay? Overlay { get; set; }

    /// <summary>The desktop shell, when one is wired in (taskbar and settings panel).</summary>
    internal Shell? Shell
    {
        get => _shell;
        set
        {
            _shell = value;
            if (value is not null)
            {
                value.Settings.Dirty = MarkDirty;
            }
        }
    }

    private Shell? _shell;

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
            // Read the pointer footprint before taking the gate: the seat and
            // surface locks are never taken underneath it.
            var footprint = CursorFootprint(Magnified);
            lock (_gate)
            {
                var previous = _cursor;
                if (previous == value)
                {
                    return;
                }

                _cursor = value;
                _dirty = true;

                // A moving pointer changes two small boxes and nothing else:
                // the frame has to repaint where it was and where it is, not
                // the whole desktop.
                _damage.Add(CursorDamage(previous, footprint));
                _damage.Add(CursorDamage(value, footprint));
                Monitor.PulseAll(_gate);
            }
        }
    }

    private (int X, int Y) _cursor;

    /// <summary>How much larger a shaken pointer is drawn.</summary>
    internal const int MagnifyFactor = 3;

    /// <summary>How long the enlarged pointer stays before shrinking back.</summary>
    internal static readonly TimeSpan MagnifyHold = TimeSpan.FromMilliseconds(1000);

    /// <summary>Milliseconds since boot, injectable so tests can age the pointer.</summary>
    internal Func<long> Now { get; set; } = () => Environment.TickCount64;

    /// <summary>When the enlargement ends (guarded by <see cref="_gate"/>); 0 when not magnified.</summary>
    private long _magnifyUntil;

    /// <summary>Whether the pointer is currently drawn enlarged (guarded by <see cref="_gate"/>).</summary>
    private bool Magnified
    {
        get
        {
            lock (_gate)
            {
                return _magnifyUntil != 0;
            }
        }
    }

    /// <summary>
    /// Enlarges the pointer for a moment — the shake-to-find gesture. The
    /// pointer grows around its hotspot and shrinks back after
    /// <see cref="MagnifyHold"/>; shaking again during the hold restarts the
    /// clock. The growth is a different image from the theme (crisp, not a
    /// resample) at <see cref="MagnifyFactor"/> the nominal size.
    /// </summary>
    internal void MagnifyCursor()
    {
        // Both footprints are read before the gate: the seat and surface
        // locks are never taken underneath it.
        var normal = CursorFootprint(magnified: false);
        var large = CursorFootprint(magnified: true);
        lock (_gate)
        {
            _magnifyUntil = Now() + (long)MagnifyHold.TotalMilliseconds;
            _dirty = true;
            _damage.Add(CursorDamage(_cursor, normal));
            _damage.Add(CursorDamage(_cursor, large));
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// How long until an enlarged pointer shrinks back, or null when it is
    /// not enlarged. The scanout loop sleeps no longer than this, so the
    /// shrink happens on time even on an idle desktop.
    /// </summary>
    internal TimeSpan? UntilPointerShrinks
    {
        get
        {
            lock (_gate)
            {
                if (_magnifyUntil == 0)
                {
                    return null;
                }

                var remaining = _magnifyUntil - Now();
                return remaining <= 0 ? TimeSpan.Zero : TimeSpan.FromMilliseconds(remaining);
            }
        }
    }

    /// <summary>
    /// Ends an enlargement whose hold has passed. Whole-output damage: the
    /// large image has to be painted over wherever it was, and one full
    /// repaint a second after a shake is cheaper than tracking the old box
    /// through concurrent pointer moves (over-painting is a cost, under-
    /// painting is a bug).
    /// </summary>
    private void ExpireMagnify()
    {
        lock (_gate)
        {
            if (_magnifyUntil == 0 || Now() < _magnifyUntil)
            {
                return;
            }

            _magnifyUntil = 0;
            _dirty = true;
            _damage.MarkFull();
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>
    /// Rosella's own pointer art, loaded from the Xcursor theme the client
    /// toolkits use. Null keeps the built-in arrow (no theme found, or the
    /// compositor started before any icons were installed).
    /// </summary>
    internal CursorTheme? Cursors { get; set; }

    /// <summary>
    /// The union of every pointer image's box relative to the pointer, in
    /// physical pixels: (X, Y) is the top-left offset, Width/Height the
    /// extent. The themed pointer and a client cursor surface have
    /// independent hotspots — a client's may be negative — so they cannot
    /// share one width/hotspot pair without under-covering one of them.
    /// </summary>
    private (int X, int Y, int Width, int Height) CursorFootprint(bool magnified)
    {
        var scale = Math.Max(1, Scale);
        var factor = magnified ? MagnifyFactor : 1;
        var own = OwnPointerFootprint(scale, factor);
        var ownBox = (-own.HotspotX, -own.HotspotY, own.Width, own.Height);
        var cursor = Seat?.CurrentCursor() ?? default;
        var clientBox = cursor.Surface is { } surface
            ? ClientCursorBox(surface.ContentWidth, surface.ContentHeight, cursor.HotspotX, cursor.HotspotY, scale, factor)
            : ((int X, int Y, int Width, int Height)?)null;
        return Union(ownBox, clientBox);
    }

    /// <summary>
    /// A client cursor's physical box relative to the pointer. The hotspot
    /// may be negative (the protocol allows it), and the enlargement scales
    /// the image about that hotspot, so the box keeps its own origin.
    /// </summary>
    internal static (int X, int Y, int Width, int Height) ClientCursorBox(
        int contentWidth,
        int contentHeight,
        int hotspotX,
        int hotspotY,
        int scale,
        int factor) =>
        (
            -hotspotX * scale * factor,
            -hotspotY * scale * factor,
            contentWidth * scale * factor,
            contentHeight * scale * factor
        );

    /// <summary>
    /// The union of two pointer-image boxes relative to the pointer. Each
    /// keeps its own origin: clamping or re-deriving one from the union
    /// would leave part of that image outside the damage rect.
    /// </summary>
    internal static (int X, int Y, int Width, int Height) Union(
        (int X, int Y, int Width, int Height) first,
        (int X, int Y, int Width, int Height)? second)
    {
        if (second is not { } other)
        {
            return first;
        }

        var left = Math.Min(first.X, other.X);
        var top = Math.Min(first.Y, other.Y);
        var right = Math.Max(first.X + first.Width, other.X + other.Width);
        var bottom = Math.Max(first.Y + first.Height, other.Y + other.Height);
        return (left, top, Math.Max(1, right - left), Math.Max(1, bottom - top));
    }

    /// <summary>
    /// The compositor's own pointer box in physical pixels: the themed bitmap
    /// is native physical pixels (as Chrome's set_cursor bitmaps are), and
    /// the arrow sprite is logical, so it scales up. Keeping this physical is
    /// what lets the damage rect use the drawn origin exactly — dividing a
    /// hotspot down and back multiplies rounding error into missing columns.
    /// </summary>
    private (int Width, int Height, int HotspotX, int HotspotY) OwnPointerFootprint(int scale, int factor)
    {
        if (Cursors?.Pointer(scale, factor) is { } bitmap)
        {
            return (bitmap.Width, bitmap.Height, bitmap.HotspotX, bitmap.HotspotY);
        }

        return (CursorSprite.Width * scale * factor, CursorSprite.Height * scale * factor, 0, 0);
    }

    /// <summary>The physical box any pointer image can touch at a position (one pixel of slack).</summary>
    private DamageRect CursorDamage((int X, int Y) position, (int X, int Y, int Width, int Height) footprint)
    {
        var scale = Math.Max(1, Scale);
        return new DamageRect(
            (position.X * scale) + footprint.X,
            (position.Y * scale) + footprint.Y,
            footprint.Width + 1,
            footprint.Height + 1);
    }

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
            _damage.MarkFull();
            _dirty = true;
            Monitor.PulseAll(_gate);
            return (WorkArea.X, WorkArea.Y);
        }
    }

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

    /// <summary>
    /// Draws the background, the windows, the shell chrome, and the cursor —
    /// all in logical pixels scaled to the physical scanout by Scale.
    /// </summary>
    public void Render(Span<byte> pixels, int width, int height, int stride, long frame)
    {
        RenderRegion(pixels, width, height, stride, frame, DamageRect.For(width, height));
    }

    /// <summary>
    /// Composites one damage region of a frame. The caller has seeded the
    /// rest of the buffer from the frame already on screen, so every pixel
    /// inside <paramref name="damage"/> comes out identical to what a full
    /// <see cref="Render"/> would put there, while the untouched pixels cost
    /// nothing. Returns what was written — the number damage tracking exists
    /// to shrink.
    /// </summary>
    internal RenderCost RenderRegion(Span<byte> pixels, int width, int height, int stride, long frame, in DamageRect damage)
    {
        var scale = Math.Max(1, Scale);
        var region = damage.Intersect(DamageRect.For(width, height));
        if (region.IsEmpty)
        {
            return default;
        }

        // The greeter's overlay paints the entire frame — background, card
        // and fields — so a partial damage region still yields a complete
        // picture and nothing below it (desktop, windows, cursor) belongs on
        // screen. The cost is a full repaint per keystroke, which is the
        // right trade for a login screen and keeps it independent of the
        // damage tracker's seeding.
        if (Overlay is { } overlay)
        {
            overlay.Draw(pixels, width, height, stride, scale);
            return new RenderCost((long)width * height, 0, 0);
        }

        // Opaque windows hide the desktop: only the rest of the region needs
        // the background painted under it. Repainting a whole-output
        // wallpaper behind a maximised window is most of the memcpy the
        // damage path exists to remove.
        _uncovered.Reset(region);
        lock (_gate)
        {
            foreach (var surface in _surfaces)
            {
                if (IsOpaqueWindow(surface))
                {
                    _uncovered.Subtract(WindowRect(surface, scale));
                    if (_uncovered.Overflowed)
                    {
                        break;
                    }
                }
            }
        }

        long backgroundPixels = 0;
        if (_uncovered.Overflowed)
        {
            backgroundPixels += Background.RenderRegion(pixels, width, height, stride, region);
        }
        else
        {
            for (var index = 0; index < _uncovered.Count; index++)
            {
                backgroundPixels += Background.RenderRegion(pixels, width, height, stride, _uncovered[index]);
            }
        }

        long surfacePixels = 0;
        var surfaceCount = 0;
        lock (_gate)
        {
            foreach (var surface in _surfaces)
            {
                // Cursor surfaces are drawn at the pointer, never as windows;
                // minimised windows are off screen until the taskbar asks
                // for them back.
                if (!surface.IsMapped || surface.IsCursorSurface || surface.Hidden)
                {
                    continue;
                }

                var rect = WindowRect(surface, scale).Intersect(region);

                // The desktop was painted on the assumption that opaque
                // windows hide it. A window that is translucent *now* — it
                // committed a different format, or lost its content, since
                // the pass above — must blend against the desktop, not
                // against the frame it is replacing.
                if (!IsOpaqueWindow(surface) && !_uncovered.Touches(rect))
                {
                    backgroundPixels += Background.RenderRegion(pixels, width, height, stride, rect);
                }

                surface.BlitInto(pixels, width, height, stride, scale, region);
                if (!rect.IsEmpty)
                {
                    surfacePixels += rect.Area;
                    surfaceCount++;
                }
            }

            // No chrome around windows: the toolkit owns its decorations
            // (and the taskbar's accent is the focus's visible consequence).
        }

        // Chrome lays out in logical pixels (matching the input thread's
        // hit-tests exactly) and draws scaled onto the physical scanout. It
        // is drawn whole whenever the region can show it — the band is
        // opaque and opaque-overdrawn pixels are pixels already correct in
        // the seeded buffer — and skipped when the region cannot, which is
        // what makes a pointer move over the desktop cheap.
        if (NeedsChrome(region, scale, width, height))
        {
            Shell?.Draw(pixels, width / scale, height / scale, stride, scale, Bar.ClockString(), TaskWindows(), Seat?.KeyboardFocus, Cursor);
        }

        // The cursor: the client's cursor surface when the focused client set
        // one (context shapes — text beam, hand — as the apps intend), hidden
        // when the focused client hid it, else Rosella's own pointer from the
        // cursor theme (or the built-in arrow when no theme loaded). Once a
        // client has named a cursor surface the image is that surface's —
        // never our pointer over the app's intent (content may still be in
        // flight).
        var (cursorX, cursorY) = Cursor;
        var cursor = Seat?.CurrentCursor() ?? default;
        var factor = Magnified ? MagnifyFactor : 1;

        // A client's cursor is a fixed small bitmap: warping it to three
        // times its size makes its antialiased rim three pixels of mush.
        // While the pointer is enlarged the theme's own large art is drawn
        // instead — for the shake the point is to be found, not to keep the
        // app's shape for one second. A client bitmap with more pixels than
        // the theme's own art is still stretched: it is the better source,
        // and replacing it with less detailed art would lose the pointer
        // rather than find it.
        var client = cursor.Surface is { } clientSurface
            ? (Width: clientSurface.ContentWidth * scale, Height: clientSurface.ContentHeight * scale)
            : default;
        var enlarged = factor > 1 ? Cursors?.Pointer(scale, factor) : null;
        if (cursor.Hidden)
        {
            // The focused client hid the pointer image (spec: NULL surface).
        }
        else if (enlarged is { } big && client.Width <= big.SourceWidth && client.Height <= big.SourceHeight)
        {
            DrawTheme(pixels, width, height, stride, cursorX, cursorY, scale, big, region);
        }
        else if (cursor.Surface is { } cursorSurface)
        {
            if (cursorSurface is { IsMapped: true, Gone: false })
            {
                // Enlarged, the client's bitmap is stretched about its
                // hotspot: nearest-neighbour, but the shape the app chose is
                // preserved. The box is the same one the damage footprint
                // uses, so the two cannot drift apart.
                var box = ClientCursorBox(
                    cursorSurface.ContentWidth,
                    cursorSurface.ContentHeight,
                    cursor.HotspotX,
                    cursor.HotspotY,
                    scale,
                    factor);
                cursorSurface.BlitAt(
                    pixels, width, height, stride,
                    (cursorX * scale) + box.X,
                    (cursorY * scale) + box.Y,
                    box.Width,
                    box.Height,
                    region);
            }
        }
        else if (Cursors?.Pointer(scale) is { } bitmap)
        {
            DrawTheme(pixels, width, height, stride, cursorX, cursorY, scale, bitmap, region);
        }
        else
        {
            // The arrow draws at the pointer with the buffer's dimensions;
            // handing it a viewport at the region's origin clips it to the
            // damage without it having to know about damage at all.
            var viewport = pixels.Slice((region.Y * stride) + (region.X * 4));
            CursorSprite.Draw(viewport, region.W, region.H, stride, (cursorX * scale) - region.X, (cursorY * scale) - region.Y, scale * factor);
        }

        return new RenderCost(backgroundPixels, surfacePixels, surfaceCount);
    }

    /// <summary>
    /// Draws one of the theme's pointer images at the pointer: native
    /// physical pixels, its own hotspot, clipped to the damage region.
    /// </summary>
    private static void DrawTheme(
        Span<byte> pixels,
        int width,
        int height,
        int stride,
        int cursorX,
        int cursorY,
        int scale,
        CursorBitmap bitmap,
        in DamageRect region) =>
        Blitter.Blit(
            pixels, width, height, stride,
            MemoryMarshal.AsBytes(bitmap.Pixels.AsSpan()),
            bitmap.SourceWidth, bitmap.SourceHeight, bitmap.SourceWidth * 4,
            ShmFormats.Argb8888,
            (cursorX * scale) - bitmap.HotspotX,
            (cursorY * scale) - bitmap.HotspotY,
            bitmap.Width, bitmap.Height,
            region);

    /// <summary>
    /// Whether a region can show chrome: the taskbar band, which every
    /// chrome draw repaints in full, or the settings panel, which floats
    /// above the work area whenever it is open. Anything else is a region the
    /// seeded buffer already shows correctly, so the chrome stays untouched —
    /// which is what makes a pointer move over the desktop cheap.
    /// </summary>
    private bool NeedsChrome(in DamageRect region, int scale, int width, int height)
    {
        if (Shell is not { } shell)
        {
            return false;
        }

        var bandTop = ((height / scale) - Shell.BarHeight) * scale;
        if (region.Y + region.H > bandTop)
        {
            return true;
        }

        if (!shell.Settings.IsOpen)
        {
            return false;
        }

        // The panel sits over the work area: a region that cannot reach it
        // does not need it redrawn (a video playing behind an open panel must
        // not pay for the panel every frame).
        if (shell.LastBarLayout is not { } layout)
        {
            return true;
        }

        var panel = shell.Settings.PanelRect(layout.Clock, width / scale, height / scale).Scaled(scale);
        return region.Intersects(new DamageRect(panel.X, panel.Y, panel.W, panel.H));
    }

    /// <summary>Whether the surface is a mapped opaque (xrgb) window: nothing shows through it.</summary>
    private static bool IsOpaqueWindow(SurfaceHandler surface) =>
        surface.IsMapped
        && !surface.Gone
        && !surface.IsCursorSurface
        && !surface.Hidden
        && surface.Format == ShmFormats.Xrgb8888
        && surface.ContentWidth > 0
        && surface.ContentHeight > 0;

    /// <summary>The physical rectangle a window's content is blitted into.</summary>
    private static DamageRect WindowRect(SurfaceHandler surface, int scale) =>
        new(surface.Position.X * scale, surface.Position.Y * scale, surface.Width * scale, surface.Height * scale);

    /// <summary>Fires frame callbacks for the frame just composited.</summary>
    public void CompleteFrame()
    {
        lock (_gate)
        {
            foreach (var surface in _surfaces)
            {
                // A minimised window gets no frame callbacks: that is the
                // throttle xdg-shell points clients at, and it keeps a hidden
                // video or spinner from redrawing for a screen it is not on.
                // Callbacks queued while hidden are not dropped — they fire
                // on the first frame after the restore, which is the frame
                // the client's new content is actually on screen (a burst of
                // "done" then is harmless: each was one-shot).
                if (surface.IsMapped && !surface.Hidden)
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

    /// <summary>Enables a popup grab, keeping the popup stack in creation order.</summary>
    internal bool TryGrabPopup(XdgPopupHandler popup)
    {
        lock (_gate)
        {
            if (popup.RootParentSurface?.XdgRole?.Toplevel is not { } rootToplevel
                || rootToplevel.Surface.Session != popup.Surface.Session)
            {
                return false;
            }

            if (popup.ParentIsPopup && (popup.Parent is not { } parent || !_popupGrabs.Contains(parent)))
            {
                return false;
            }

            _popupGrabs.Add(popup);
            return true;
        }
    }

    /// <summary>Snapshot of popup grabs in nesting order.</summary>
    internal XdgPopupHandler[] PopupGrabs
    {
        get
        {
            lock (_gate)
            {
                return [.. _popupGrabs];
            }
        }
    }

    /// <summary>The current topmost grabbed popup, if any.</summary>
    internal XdgPopupHandler? TopmostPopupGrab
    {
        get
        {
            lock (_gate)
            {
                return _popupGrabs.Count > 0 ? _popupGrabs[^1] : null;
            }
        }
    }

    /// <summary>Releases a popup grab and any nested grabs above it.</summary>
    internal void ReleasePopupGrab(XdgPopupHandler popup)
    {
        XdgPopupHandler[] descendants;
        lock (_gate)
        {
            var index = _popupGrabs.IndexOf(popup);
            if (index < 0)
            {
                return;
            }

            descendants = _popupGrabs.Skip(index + 1).Reverse().ToArray();
            _popupGrabs.RemoveRange(index, _popupGrabs.Count - index);
        }

        foreach (var descendant in descendants)
        {
            descendant.ParentDismissed();
        }
    }

    /// <summary>Dismisses the current grab stack in reverse nesting order.</summary>
    internal void DismissPopupGrabs()
    {
        while (TopmostPopupGrab is { } popup)
        {
            popup.DismissFromCompositor();
        }
    }

    /// <summary>The topmost mapped surface at the point, if any.</summary>
    internal SurfaceHandler? HitTest(int x, int y)
    {
        lock (_gate)
        {
            if (_popupGrabs.Count > 0)
            {
                var popup = _popupGrabs[^1];
                var popupSurface = popup.Surface;
                for (var i = _surfaces.Count - 1; i >= 0; i--)
                {
                    var surface = _surfaces[i];
                    if (surface.Session != popupSurface.Session || !surface.IsMapped || surface.Gone || surface.IsCursorSurface
                        || surface.Hidden || !Contains(surface.Visible, x, y))
                    {
                        continue;
                    }

                    return surface;
                }

                // An owner-events grab blocks unrelated clients from receiving
                // the press. The caller dismisses the grab on this null hit.
                return null;
            }

            for (var i = _surfaces.Count - 1; i >= 0; i--)
            {
                var surface = _surfaces[i];
                if (!surface.IsMapped || surface.IsCursorSurface || surface.Hidden)
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
                    tasks.Add(new BarTask(
                        Bar.LabelFor(toplevel.Title, toplevel.AppId), toplevel.AppId, surface, surface.Hidden));
                }
            }

            return tasks;
        }
    }

    /// <summary>
    /// Hides a window (xdg_toplevel.set_minimized): no drawing, no hits, and
    /// the keyboard goes to the topmost window still on screen. The surface
    /// stays mapped with its buffer, and its taskbar button stays put — that
    /// is the way back, since xdg-shell gives the client no "unminimize".
    /// </summary>
    internal void Minimize(SurfaceHandler surface)
    {
        lock (_gate)
        {
            if (surface.Hidden || !surface.IsMapped)
            {
                return;
            }

            surface.Hidden = true;
            _damage.MarkFull();
            _dirty = true;
            Monitor.PulseAll(_gate);
        }

        // Snapshot the focus decision and the held keys before the popup
        // teardown: dismissing a grabbed menu moves focus back to this
        // window with an empty key set, and the handover must still carry
        // what the user is holding. Hiding an unfocused window takes no
        // focus with it, so it must not re-target anyone else's keyboard.
        var hadFocus = Seat?.KeyboardFocusIsWithin(surface) == true;
        var held = Seat?.HeldKeys ?? [];

        // A menu or tooltip belongs to a place on screen the window no longer
        // occupies.
        DismissDescendantPopups(surface);

        // Clears the seat's focus and sends leave while the surface id is
        // still valid, exactly like an unmap.
        Seat?.OnSurfaceHidden(surface, canSendLeave: true);

        if (hadFocus && TopmostVisible() is { } next)
        {
            Seat?.SetKeyboardFocus(next, held);
        }
    }

    /// <summary>Brings a minimised window back; the caller raises and focuses it.</summary>
    internal void Restore(SurfaceHandler surface)
    {
        lock (_gate)
        {
            if (!surface.Hidden)
            {
                return;
            }

            surface.Hidden = false;
            _damage.MarkFull();
            _dirty = true;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>The topmost window that is on screen and can take the keyboard.</summary>
    internal SurfaceHandler? TopmostVisible()
    {
        lock (_gate)
        {
            for (var i = _surfaces.Count - 1; i >= 0; i--)
            {
                var candidate = _surfaces[i];
                if (candidate.IsMapped && !candidate.Gone && !candidate.IsCursorSurface && !candidate.Hidden
                    && candidate.XdgRole?.Toplevel is not null)
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    /// <summary>
    /// Dismisses every popup whose ancestor chain reaches the surface: a
    /// popup may never outlive its parent's place on screen.
    /// </summary>
    internal void DismissDescendantPopups(SurfaceHandler surface)
    {
        List<XdgSurfaceHandler> snapshot;
        lock (_gate)
        {
            snapshot = _surfaces
                .Where(candidate => candidate.XdgRole?.PopupParent is not null)
                .Select(candidate => candidate.XdgRole!)
                .OrderByDescending(role => PopupDepth(role))
                .ToList();
        }

        foreach (var role in snapshot)
        {
            var parent = role.PopupParent;
            while (parent is not null)
            {
                if (parent.Surface == surface)
                {
                    role.DismissPopup();
                    break;
                }

                parent = parent.PopupParent;
            }
        }
    }

    private static int PopupDepth(XdgSurfaceHandler role)
    {
        var depth = 0;
        while (role.PopupParent is { } parent)
        {
            depth++;
            role = parent;
        }

        return depth;
    }

    private static bool Contains((int X, int Y, int W, int H) rect, int x, int y) =>
        x >= rect.X && x < rect.X + rect.W && y >= rect.Y && y < rect.Y + rect.H;

    /// <summary>Raises the surface to the top (it renders last).</summary>
    internal void Raise(SurfaceHandler surface)
    {
        lock (_gate)
        {
            if (_surfaces.Remove(surface))
            {
                _surfaces.Add(surface);
                _damage.MarkFull();
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
