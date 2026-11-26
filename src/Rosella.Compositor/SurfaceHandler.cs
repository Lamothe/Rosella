using System.Net.Sockets;
using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>wl_surface error codes, as defined by the protocol.</summary>
internal static class SurfaceErrors
{
    internal const uint InvalidScale = 0;
    internal const uint InvalidTransform = 1;
    internal const uint InvalidOffset = 3;
    internal const uint DefunctRoleObject = 4;
    internal const uint NoBuffer = 5;
}

/// <summary>
/// A role (xdg-shell) on a surface: notified at commit time and destruction,
/// so role state machines can map, unmap, and raise role-specific errors.
/// </summary>
internal interface ISurfaceRole
{
    /// <summary>
    /// Called with the commit's outcome before it becomes visible; may raise
    /// protocol errors. <paramref name="hasContent"/> and the dimensions
    /// describe the content this commit will apply.
    /// </summary>
    void OnCommit(SurfaceHandler surface, bool contentChanged, bool hasContent, int contentWidth, int contentHeight);

    /// <summary>Called when the wl_surface is destroyed.</summary>
    void OnSurfaceDestroyed();

    /// <summary>True once the role object has been destroyed (or its session ended).</summary>
    bool IsDefunct { get; }
}

/// <summary>
/// Handles <c>wl_surface</c> and holds its state: double-buffered pending
/// state applied at commit, content copied out of the client's buffer at that
/// moment (then immediately released), and frame callbacks fired after the
/// next composited frame. All state is guarded by one lock in the order
/// surface state → session, matching the scene's surface list → session order.
/// </summary>
internal sealed record CachedSurfaceCommit(Action Apply, Action Cancel, long CopiedBytes = 0, int Depth = 1);

internal sealed class SurfaceHandler : IRequestHandler, IDisposable
{
    private Scene _scene;
    private readonly object _state = new();
    private readonly List<ServerObject> _pendingCallbacks = new();
    private readonly List<ServerObject> _activeCallbacks = new();
    private readonly List<ServerObject> _cachedCallbacks = new();
    private readonly List<ServerObject> _pendingReleaseCallbacks = new();
    private ClientSession? _session;
    private ServerObject? _self;
    private BufferHandler? _pendingBuffer;
    private bool _pendingAttach;
    private byte[]? _content;
    // The previous displayed copy can become the next commit's staging array.
    // Current content and queued synchronized commits never share this storage.
    private byte[]? _recycledContent;
    private byte[]? _patchPixels;
    private int _width;
    private int _height;
    private uint _format = ShmFormats.Xrgb8888;
    private int _bufferScale = 1;
    private ViewportState _viewport;
    internal uint ViewportId { get; set; }
    internal DrmSyncobjSurface? DrmSyncobj { get; set; }
    internal ViewportState PendingViewport { get; set; }
    internal volatile PointerConstraint? PointerConstraint;
    private RegionSnapshot? _opaqueRegion;
    private RegionSnapshot? _pendingOpaque;
    private bool _hasPendingOpaque;
    private RegionSnapshot? _inputRegion;
    private RegionSnapshot? _pendingInputRegion;
    private bool _hasPendingInputRegion;
    private int _pendingBufferScale = 1;
    private readonly List<(long X, long Y, long Right, long Bottom, bool Buffer)> _pendingDamage = new();
    private bool _damageOverflow;
    private ISurfaceRole? _role;
    private XdgRoleKind? _xdgRoleKind;

    public SurfaceHandler(Scene scene)
    {
        HomeScene = scene;
        _scene = scene;
    }

    internal Scene HomeScene { get; }

    internal Scene Scene
    {
        get => Volatile.Read(ref _scene);
        set => Volatile.Write(ref _scene, value);
    }

    internal void WithScene(Action<Scene> action)
    {
        while (true)
        {
            var scene = Scene;
            lock (scene.Gate)
            {
                if (scene != Scene)
                {
                    continue;
                }
                action(scene);
                return;
            }
        }
    }

    internal void OutputChanged()
    {
        try
        {
            if (!Gone && _session is { } session && _self is { Version: >= 6 } bound)
            {
                session.TrySendEvent(bound, "preferred_buffer_scale", WireArgument.Int32(Scene.Scale));
            }
            if (!Gone && XdgRole is { Toplevel: not null } role)
            {
                role.SendStateConfigure();
            }
        }
        catch (Exception exception) when (exception is SocketException or IOException or ObjectDisposedException)
        {
            // The client can close the moved window while notifications are sent.
        }
    }

    /// <summary>Where the surface is composited (logical pixels), assigned by the scene.</summary>
    internal (int X, int Y) Position
    {
        get
        {
            lock (_state)
            {
                var local = _position;
                if (Subsurface is not { Parent: { } parent } sub)
                {
                    return local;
                }

                var parentPosition = parent.Position;
                return (parentPosition.X + ScaleOffset(sub.Position.X, parent.Width, parent.ContentWidth),
                    parentPosition.Y + ScaleOffset(sub.Position.Y, parent.Height, parent.ContentHeight));
            }
        }

        set
        {
            lock (_state)
            {
                _position = value;
            }
        }
    }

    private static int ScaleOffset(int value, int placed, int content) =>
        content > 0 ? (int)Math.Clamp((long)value * placed / content, int.MinValue, int.MaxValue) : value;

    private (int X, int Y) _position;

    /// <summary>
    /// Where the content renders (logical pixels): defaults to the content's
    /// own size at <see cref="Position"/>; the window policy sets a window
    /// rect to maximise a toplevel into the work area. Guarded by the same
    /// lock as the content it maps (session threads write, the scanout and
    /// input threads read).
    /// </summary>
    internal void Place(int x, int y, int width, int height)
    {
        lock (_state)
        {
            Position = (x, y);
            _placeWidth = width;
            _placeHeight = height;
        }
    }

    /// <summary>
    /// The visible window's rect on screen (logical pixels): the window
    /// geometry after placement — what a focus frame should hug. Defaults to
    /// the placed rect until the window policy says otherwise.
    /// </summary>
    internal void SetVisible(int x, int y, int width, int height)
    {
        lock (_state)
        {
            _visible = (x, y, width, height);
        }
    }

    private (int X, int Y, int W, int H)? _visible;

    internal (int X, int Y, int W, int H) Visible
    {
        get
        {
            lock (_state)
            {
                return _visible ?? (Position.X, Position.Y, Width, Height);
            }
        }
    }

    private int _placeWidth;
    private int _placeHeight;
    private long _unmapVersion;

    // Changes only when a mapped buffer is dropped. A newly created or
    // currently mapped surface never qualifies for launcher recovery.
    internal long UnmapVersion
    {
        get
        {
            lock (_state)
            {
                return _content is null ? _unmapVersion : 0;
            }
        }
    }

    /// <summary>The session that owns this surface, for input routing.</summary>
    internal ClientSession? Session => _session;


    /// <summary>The xdg role on this surface, if any.</summary>
    internal XdgSurfaceHandler? XdgRole => _role as XdgSurfaceHandler;

    /// <summary>Set once the surface is destroyed: focus must skip it.</summary>
    internal volatile bool Gone;

    /// <summary>
    /// True once the client names this surface as its pointer cursor: it is
    /// drawn at the pointer (not as a window) and never hit-tested.
    /// </summary>
    internal volatile bool IsCursorSurface;
    internal volatile bool IsDragIcon;

    internal bool IsMapped
    {
        get
        {
            lock (_state)
            {
                return _content is not null;
            }
        }
    }

    /// <summary>Whether this surface and all its subsurface ancestors can receive input.</summary>
    internal bool IsInputVisible
    {
        get
        {
            for (SurfaceHandler? current = this; current is not null; current = current.Subsurface?.Parent)
            {
                if (current.Gone || !current.IsMapped || current.Hidden || current.IsCursorSurface
                    || current.Subsurface is { IsDefunct: true })
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>
    /// Off screen by policy: a minimised window, or a popup whose window is
    /// minimised. The surface keeps its buffer — restoring must show the
    /// window again without a round trip — but the scene neither draws nor
    /// hit-tests it, and its frame callbacks stop. Only the policy sets and
    /// clears this; an unmap clears it because the next map is a fresh
    /// appearance.
    /// </summary>
    internal bool Hidden
    {
        get
        {
            lock (_state)
            {
                return _hidden;
            }
        }

        set
        {
            lock (_state)
            {
                _hidden = value;
            }
        }
    }

    private bool _hidden;

    /// <summary>
    /// The surface's whole area is declared opaque, in surface-local
    /// coordinates: either it is an XRGB buffer, or the client set an opaque
    /// region that covers it (wl_surface.set_opaque_region). Such a window
    /// hides the desktop behind it and copies without blending; anything
    /// less certain keeps the blending path.
    /// </summary>
    internal bool IsOpaqueContent
    {
        get
        {
            lock (_state)
            {
                if (_content is null)
                {
                    return false;
                }

                if (_format == ShmFormats.Xrgb8888)
                {
                    return true;
                }

                return _opaqueRegion is { } region && (_viewport == default
                    ? region.Covers(_width, _height, _bufferScale)
                    : region.Covers(ContentWidth, ContentHeight, 1));
            }
        }
    }

    /// <summary>The surface's logical width: the placed rect, else the content's own.</summary>
    internal int Width
    {
        get
        {
            lock (_state)
            {
                var width = _placeWidth > 0 ? _placeWidth : _viewport.Width(_width, _bufferScale);
                return Subsurface?.Parent is { } parent ? ScaleOffset(width, parent.Width, parent.ContentWidth) : width;
            }
        }
    }

    /// <summary>The surface's logical height: the placed rect, else the content's own.</summary>
    internal int Height
    {
        get
        {
            lock (_state)
            {
                var height = _placeHeight > 0 ? _placeHeight : _viewport.Height(_height, _bufferScale);
                return Subsurface?.Parent is { } parent ? ScaleOffset(height, parent.Height, parent.ContentHeight) : height;
            }
        }
    }

    /// <summary>The content's own logical width (buffer pixels / buffer scale).</summary>
    internal int ContentWidth
    {
        get
        {
            lock (_state)
            {
                return _viewport.Width(_width, _bufferScale);
            }
        }
    }

    /// <summary>The content's own logical height (buffer pixels / buffer scale).</summary>
    internal int ContentHeight
    {
        get
        {
            lock (_state)
            {
                return _viewport.Height(_height, _bufferScale);
            }
        }
    }

    /// <summary>How many buffer pixels make one logical pixel.</summary>
    internal int BufferScale
    {
        get
        {
            lock (_state)
            {
                return _bufferScale;
            }
        }
    }

    /// <summary>The committed buffer's pixel format (argb blends, xrgb copies).</summary>
    internal uint Format => _format;

    /// <summary>Maps a scene x to the surface's own coordinate space.</summary>
    internal int LocalX(int sceneX)
    {
        lock (_state)
        {
            var width = Width;
            return width > 0 ? (int)(((long)(sceneX - Position.X) * ContentWidth) / width) : 0;
        }
    }

    /// <summary>Maps a scene y to the surface's own coordinate space.</summary>
    internal int LocalY(int sceneY)
    {
        lock (_state)
        {
            var height = Height;
            return height > 0 ? (int)(((long)(sceneY - Position.Y) * ContentHeight) / height) : 0;
        }
    }

    /// <summary>Test seam: the committed content, if any.</summary>
    internal byte[]? Content
    {
        get
        {
            lock (_state)
            {
                return _content;
            }
        }
    }

    /// <summary>Drops the content without a commit (role/surface teardown).</summary>
    internal void UnmapContent()
    {
        lock (_state)
        {
            UnmapContentLocked();
        }

        // The object still exists, so the seat may send leave events.
        Scene.OnSurfaceHidden(this, canSendLeave: true);

        // The window left the scene; the frame must reflect it.
        Scene.MarkDirty();
    }

    private void UnmapContentLocked()
    {
        if (_content is not null)
        {
            _unmapVersion++;
        }

        _content = null;
        _recycledContent = null;
        _patchPixels = null;
        _width = 0;
        _height = 0;

        // A window that leaves the screen cannot stay hidden: the next
        // map is a fresh appearance and must be visible.
        _hidden = false;
    }

    public void Bind(ClientSession session, ServerObject self)
    {
        _session = session;
        _self = self;
    }

    /// <summary>The wl_surface id, for role errors raised elsewhere.</summary>
    internal uint Id => _self!.Id;

    /// <summary>One-line runtime state dump (diagnostics: ground truth for render reports).</summary>
    internal string Describe()
    {
        lock (_state)
        {
            var visible = _visible ?? (Position.X, Position.Y, Width, Height);
            return $"surface {Id}: mapped={IsMapped} hidden={_hidden} cursor={IsCursorSurface} xdg={HasXdgRole} gone={Gone} " +
                   $"content={_width}x{_height} fmt={_format:X} pos={Position.X},{Position.Y} " +
                   $"place={_placeWidth}x{_placeHeight} visible={visible.X},{visible.Y} {visible.W}x{visible.H}";
        }
    }

    /// <summary>True once any xdg role object has been created for this surface.</summary>
    internal bool HasXdgRole => _role is not null && _role is not SubsurfaceHandler;

    internal SubsurfaceHandler? Subsurface => _role as SubsurfaceHandler;
    internal SurfaceHandler MainSurface
    {
        get
        {
            var surface = this;
            while (surface.Subsurface?.Parent is { } parent) { surface = parent; }
            return surface;
        }
    }
    internal bool HasAnyRole => _role is not null || IsCursorSurface;
    internal readonly List<SurfaceHandler> Children = [];
    internal readonly List<SurfaceHandler> PendingChildren = [];
    internal readonly List<CachedSurfaceCommit> CachedCommits = [];

    /// <summary>
    /// Associates an xdg_surface object. Clients can recreate that object
    /// after destroying it, while the underlying surface keeps its role.
    /// </summary>
    internal bool TryAssignXdgSurface(XdgSurfaceHandler surface)
    {
        if (IsCursorSurface || _role is not (null or XdgSurfaceHandler { IsDefunct: true }))
        {
            return false;
        }

        _role = surface;
        return true;
    }

    internal bool TryAssignXdgRole(XdgRoleKind kind)
    {
        if (_xdgRoleKind is { } current && current != kind)
        {
            return false;
        }

        _xdgRoleKind = kind;
        return true;
    }

    internal void AssignSubsurface(SubsurfaceHandler role)
    {
        _role = role;
    }

    internal void DiscardCachedCommits()
    {
        foreach (var commit in CachedCommits) { commit.Cancel(); }
        CachedCommits.Clear();
        lock (_state)
        {
            if (_session is { } session && !Gone)
            {
                DestroyCallbacks(session, _cachedCallbacks);
            }
            _cachedCallbacks.Clear();
        }
    }

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        switch (description.Name)
        {
            case "destroy":
                Destroy(session, target);
                break;

            case "attach":
                Attach(session, target, request);
                break;

            case "frame":
                lock (_state)
                {
                    _pendingCallbacks.Add(HandlerSupport.CreateCallback(session, target, request.Arguments[0].AsNewId()));
                }

                break;

            case "get_release":
                lock (_state)
                {
                    _pendingReleaseCallbacks.Add(HandlerSupport.CreateCallback(session, target, request.Arguments[0].AsNewId()));
                }

                break;

            case "commit":
            {
                var wasUnmappedToplevel = XdgRole?.Toplevel is not null && !IsMapped;
                var commit = CreateCommit();
                while (true)
                {
                    var scene = Scene;
                    lock (scene.Gate)
                    {
                        if (scene != Scene)
                        {
                            continue;
                        }
                        scene.ApplySurfaceCommit(this, commit);
                        break;
                    }
                }
                if (wasUnmappedToplevel && IsMapped)
                {
                    Scene.ToplevelMapped?.Invoke(this);
                }
                if (IsDragIcon && Scene.Seat?.Drag.Icon == this)
                {
                    Scene.MarkDirty();
                }
                else if (IsCursorSurface)
                {
                    (Scene.Seat?.PointerFocus?.Scene ?? Scene).MarkCursorDirty(this);
                }
                if (PointerConstraint is not null)
                {
                    Scene.Seat?.RefreshPointerConstraint();
                }

                break;
            }

            case "set_buffer_scale":
            {
                var scale = request.Arguments[0].AsInt32();
                if (scale <= 0)
                {
                    throw new ProtocolErrorException(target.Id, SurfaceErrors.InvalidScale, "buffer scale must be positive");
                }

                // Double-buffered: applied at the next commit.
                _pendingBufferScale = scale;
                break;
            }

            case "set_buffer_transform":
                if (request.Arguments[0].AsInt32() is < 0 or > 7)
                {
                    throw new ProtocolErrorException(target.Id, SurfaceErrors.InvalidTransform, "invalid buffer transform");
                }

                break;

            case "set_opaque_region":
            case "set_input_region":
            {
                // Region objects can be changed or destroyed before the commit.
                var regionId = request.Arguments[0].AsObject();
                var snapshot = regionId != 0
                    && session.TryGetObject(regionId, out var regionObject)
                    && regionObject.Handler is RegionHandler region
                        ? region.Snapshot()
                        : (RegionSnapshot?)null;

                lock (_state)
                {
                    if (description.Name == "set_input_region")
                    {
                        _pendingInputRegion = snapshot;
                        _hasPendingInputRegion = true;
                    }
                    else
                    {
                        _pendingOpaque = snapshot;
                        _hasPendingOpaque = true;
                    }
                }

                break;
            }

            case "damage":
            case "damage_buffer":
            {
                var x = request.Arguments[0].AsInt32();
                var y = request.Arguments[1].AsInt32();
                var width = request.Arguments[2].AsInt32();
                var height = request.Arguments[3].AsInt32();
                if (width > 0 && height > 0)
                {
                    lock (_state)
                    {
                        if (_pendingDamage.Count < DamageTracker.Capacity)
                        {
                            _pendingDamage.Add((x, y, (long)x + width, (long)y + height, description.Name == "damage_buffer"));
                        }
                        else
                        {
                            _damageOverflow = true;
                        }
                    }
                }
                break;
            }
            case "offset":
                // Surface offsets are not yet implemented.
                break;

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }

    internal bool AcceptsInputAt(int x, int y)
    {
        lock (_state)
        {
            var position = Position;
            var placedX = (long)x - position.X;
            var placedY = (long)y - position.Y;
            return placedX >= 0 && placedX < Width && placedY >= 0 && placedY < Height
                && (_inputRegion?.Contains(LocalX(x), LocalY(y)) ?? true);
        }
    }

    internal bool AcceptsLocalInput(int x, int y)
    {
        lock (_state)
        {
            return x >= 0 && x < ContentWidth && y >= 0 && y < ContentHeight &&
                (_inputRegion?.Contains(x, y) ?? true);
        }
    }

    /// <summary>Fires the frame callbacks queued for the frame just composited.</summary>
    internal void CompleteFrame()
    {
        lock (_state)
        {
            foreach (var callback in _activeCallbacks)
            {
                try
                {
                    // Frame callbacks carry the current time in milliseconds.
                    HandlerSupport.FireAndDestroy(_session!, callback, unchecked((uint)Environment.TickCount64));
                }
                catch (Exception exception) when (exception is ObjectDisposedException or SocketException)
                {
                    // The client went away mid-frame; its session teardown
                    // frees whatever is left.
                }
            }

            _activeCallbacks.Clear();
        }
    }

    /// <summary>
    /// Draws the surface's content into its placed rect, if mapped. The
    /// scene is logical pixels; the scanout is <paramref name="outputScale"/>
    /// times that, and the buffer may be at its own buffer scale — the blit
    /// maps buffer pixels onto the scaled destination rect, blending ARGB
    /// and copying XRGB.
    /// </summary>
    internal void BlitInto(Span<byte> pixels, int width, int height, int stride, int outputScale)
    {
        BlitInto(pixels, width, height, stride, outputScale, DamageRect.For(width, height));
    }

    /// <summary>
    /// The damage path: the same blit, clipped to one physical region, so a
    /// repaint of part of the screen only pays for that part.
    /// </summary>
    internal void BlitInto(Span<byte> pixels, int width, int height, int stride, int outputScale, in DamageRect clip)
    {
        lock (_state)
        {
            if (_content is null)
            {
                return;
            }

            // A surface that declared its whole opaque region is opaque: the
            // client promised every pixel covers, so the alpha channel is not
            // consulted and the copy takes the no-blend path. Chrome sends
            // ARGB8888 buffers with a full-surface opaque region, which is
            // most of a video frame's cost.
            BlitContent(
                pixels,
                width,
                height,
                stride,
                IsOpaqueContent ? ShmFormats.Xrgb8888 : _format,
                Position.X * outputScale,
                Position.Y * outputScale,
                Width * outputScale,
                Height * outputScale,
                clip);
        }
    }

    /// <summary>Paints a region only if this surface can completely replace what is below it.</summary>
    internal bool TryBlitOpaqueCover(Span<byte> pixels, int width, int height, int stride, int scale, in DamageRect region)
    {
        lock (_state)
        {
            if (_content is null || Gone || _hidden || IsCursorSurface || !IsOpaqueContent)
            {
                return false;
            }

            var position = Position;
            var bounds = new DamageRect(position.X * scale, position.Y * scale, Width * scale, Height * scale);
            if (!bounds.Contains(region))
            {
                return false;
            }

            BlitContent(pixels, width, height, stride,
                ShmFormats.Xrgb8888, bounds.X, bounds.Y, bounds.W, bounds.H, region);
            return true;
        }
    }

    /// <summary>Blits the content at a physical position (the pointer cursor).</summary>
    internal void BlitAt(Span<byte> pixels, int width, int height, int stride, int x, int y, int destinationWidth, int destinationHeight)
    {
        BlitAt(pixels, width, height, stride, x, y, destinationWidth, destinationHeight, DamageRect.For(width, height));
    }

    /// <summary>Blits the content at a physical position, clipped to a damage region.</summary>
    internal void BlitAt(Span<byte> pixels, int width, int height, int stride, int x, int y, int destinationWidth, int destinationHeight, in DamageRect clip)
    {
        lock (_state)
        {
            if (_content is null)
            {
                return;
            }

            BlitContent(
                pixels,
                width,
                height,
                stride,
                _format,
                x,
                y,
                destinationWidth,
                destinationHeight,
                clip);
        }
    }

    private void BlitContent(Span<byte> pixels, int width, int height, int stride, uint format,
        int x, int y, int destinationWidth, int destinationHeight, in DamageRect clip)
    {
        ViewportSource? crop = null;
        if (_viewport.Source is { } source)
        {
            crop = new ViewportSource(source.X * _bufferScale, source.Y * _bufferScale,
                source.Width * _bufferScale, source.Height * _bufferScale);
        }
        Blitter.Blit(pixels, width, height, stride, _content, _width, _height, _width * 4, format,
            x, y, destinationWidth, destinationHeight, clip, crop);
    }

    /// <summary>Session end: leave the scene and drop state without sending.</summary>
    public void Dispose()
    {
        Scene.Remove(this);
        lock (_state)
        {
            if (_pendingBuffer is { } pending)
            {
                pending.ReleaseReference();
                _pendingBuffer = null;
            }

            _pendingAttach = false;
            _content = null;
            _recycledContent = null;
            _patchPixels = null;
            _pendingCallbacks.Clear();
            _activeCallbacks.Clear();
            _cachedCallbacks.Clear();
            _pendingReleaseCallbacks.Clear();
        }
    }

    private void Attach(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var x = request.Arguments[1].AsInt32();
        var y = request.Arguments[2].AsInt32();
        if ((x != 0 || y != 0) && target.Version >= 5)
        {
            throw new ProtocolErrorException(target.Id, SurfaceErrors.InvalidOffset, "buffer offset must be zero");
        }

        var bufferId = request.Arguments[0].AsObject();
        var buffer = bufferId == 0 ? null : ResolveBuffer(session, target, bufferId);
        lock (_state)
        {
            if (_pendingBuffer is { } previous)
            {
                previous.ReleaseReference();
            }

            buffer?.Acquire();
            _pendingBuffer = buffer;
            _pendingAttach = true;
        }
    }

    private static BufferHandler ResolveBuffer(ClientSession session, ServerObject target, uint bufferId)
    {
        if (!session.TryGetObject(bufferId, out var bufferObject) || bufferObject.Handler is not BufferHandler buffer)
        {
            throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidObject, $"invalid buffer {bufferId}");
        }

        return buffer;
    }

    private readonly record struct PixelPatch(DamageRect Region, byte[] Pixels)
    {
        internal void Apply(byte[] target, int stride)
        {
            var rowBytes = Region.W * 4;
            for (var row = 0; row < Region.H; row++)
            {
                var offset = (Region.Y + row) * stride + Region.X * 4;
                Pixels.AsSpan(row * rowBytes, rowBytes).CopyTo(target.AsSpan(offset, rowBytes));
            }
        }
    }

    private CachedSurfaceCommit CreateCommit()
    {
        bool contentChanged;
        byte[]? newContent;
        PixelPatch? patch = null;
        (long X, long Y, long Right, long Bottom, bool Buffer)[] damage;
        bool damageOverflow;
        var newWidth = 0;
        var newHeight = 0;
        uint newFormat;
        int newBufferScale;
        var viewport = PendingViewport;
        var viewportId = ViewportId;
        BufferHandler? attachedBuffer = null;
        byte[]? dmaBufDestination = null;
        ServerObject[] releaseCallbacks = [];
        DrmTimelinePoint? acquire;
        DrmTimelinePoint? release;
        lock (_state)
        {
            // get_release requires a non-null buffer in this content update.
            if (_pendingReleaseCallbacks.Count > 0 && !(_pendingAttach && _pendingBuffer is not null))
            {
                throw new ProtocolErrorException(_self!.Id, SurfaceErrors.NoBuffer, "get_release without a committed buffer");
            }

            (acquire, release) = DrmSyncobj?.Take(_pendingAttach ? _pendingBuffer?.Buffer : null) ?? default;
            damage = _pendingDamage.ToArray();
            _pendingDamage.Clear();
            damageOverflow = _damageOverflow;
            _damageOverflow = false;

            contentChanged = _pendingAttach;
            // A state-only cached commit must not retain an old pixel copy.
            // Its current content is resolved when the commit is applied.
            newContent = null;
            newWidth = 0;
            newHeight = 0;
            newFormat = _format;

            // set_buffer_scale is double-buffered state: applied at every
            // commit, with or without a new buffer (spec: wl_surface.commit).
            newBufferScale = _pendingBufferScale;
            if (_pendingAttach)
            {
                _pendingAttach = false;
                if (_pendingBuffer is { } buffer)
                {
                    attachedBuffer = buffer;
                    newWidth = buffer.Buffer.Width;
                    newHeight = buffer.Buffer.Height;
                    newFormat = buffer.Buffer.Format;
                    if (buffer.Buffer is ShmBuffer shared && _content is not null && XdgRole?.Toplevel is not null && !IsCursorSurface
                        && viewport == default && _viewport == default && !damageOverflow && damage.Length == 1
                        && newWidth == _width && newHeight == _height && newFormat == _format && newBufferScale == _bufferScale
                        && SmallBufferDamage(damage[0], newWidth, newHeight, newBufferScale) is { } region)
                    {
                        // Toplevel commits apply immediately in order. Stage only
                        // the changed pixels, then patch the committed image under
                        // its state lock. Synchronized surfaces keep full copies.
                        newContent = _content;
                        _patchPixels = shared.ReadRegion(region, _patchPixels);
                        patch = new PixelPatch(region, _patchPixels);
                    }
                    else if (buffer.Buffer is ShmBuffer)
                    {
                        newContent = buffer.Buffer.ReadPixels(_recycledContent);
                        _recycledContent = null;
                    }
                    else
                    {
                        dmaBufDestination = _recycledContent;
                        _recycledContent = null;
                    }
                    _pendingBuffer = null;
                    releaseCallbacks = _pendingReleaseCallbacks.ToArray();
                    _pendingReleaseCallbacks.Clear();
                }
                else
                {
                    newContent = null;
                    newWidth = 0;
                    newHeight = 0;
                }
            }
        }

        if (attachedBuffer is not null)
        {
            try
            {
                if (attachedBuffer.Buffer is not ShmBuffer)
                {
                    // A producer fence or readback may block. The renderer
                    // can keep showing the previous snapshot while we wait.
                    if (acquire is null || acquire.Wait())
                    {
                        newContent = attachedBuffer.Buffer.ReadPixels(dmaBufDestination);
                    }
                    contentChanged = newContent is not null;
                }
            }
            finally
            {
                acquire?.Dispose();
                if (release is not null)
                {
                    release.Signal();
                    release.Dispose();
                }
                attachedBuffer.ReleaseReference();
                attachedBuffer.MarkReleased(releaseCallbacks);
            }
        }

        RegionSnapshot? opaque;
        bool hasOpaque;
        RegionSnapshot? inputRegion;
        bool hasInputRegion;
        List<ServerObject> callbacks;
        lock (_state)
        {
            opaque = _pendingOpaque;
            hasOpaque = _hasPendingOpaque;
            _hasPendingOpaque = false;
            inputRegion = _pendingInputRegion;
            hasInputRegion = _hasPendingInputRegion;
            _hasPendingInputRegion = false;
            callbacks = [.. _pendingCallbacks];
            _pendingCallbacks.Clear();
            _cachedCallbacks.AddRange(callbacks);
        }

        void Cancel()
        {
            lock (_state)
            {
                foreach (var callback in callbacks)
                {
                    if (_cachedCallbacks.Remove(callback) && _session is { } session && !Gone)
                    {
                        HandlerSupport.DestroySilently(session, callback);
                    }
                }
            }
        }
        var constraint = PointerConstraint;
        var constraintRegion = constraint?.PendingRegion;
        return new CachedSurfaceCommit(
            () =>
            {
                ApplyCommit(contentChanged, newContent, patch, newWidth, newHeight, newFormat, newBufferScale, viewport, viewportId,
                    hasOpaque, opaque, hasInputRegion, inputRegion, callbacks, damage, damageOverflow);
                constraint?.ApplyRegion(constraintRegion);
            }, Cancel,
            contentChanged ? (patch?.Pixels.LongLength ?? newContent?.LongLength ?? 0) : 0);
    }

    private static DamageRect? SmallBufferDamage(
        (long X, long Y, long Right, long Bottom, bool Buffer) damage, int width, int height, int scale)
    {
        var factor = damage.Buffer ? 1 : scale;
        var x = (int)Math.Clamp(damage.X * factor, 0, width);
        var y = (int)Math.Clamp(damage.Y * factor, 0, height);
        var right = (int)Math.Clamp(damage.Right * factor, 0, width);
        var bottom = (int)Math.Clamp(damage.Bottom * factor, 0, height);
        var rect = new DamageRect(x, y, right - x, bottom - y);
        return !rect.IsEmpty && rect.Area <= (long)width * height / 4 ? rect : null;
    }

    private void ApplyCommit(bool contentChanged, byte[]? newContent, PixelPatch? patch, int newWidth, int newHeight, uint newFormat,
        int newBufferScale, ViewportState viewport, uint viewportId, bool hasOpaque, RegionSnapshot? opaque, bool hasInputRegion, RegionSnapshot? inputRegion,
        List<ServerObject> callbacks,
        (long X, long Y, long Right, long Bottom, bool Buffer)[] damage, bool damageOverflow)
    {
        var oldMapped = IsMapped;
        var oldOpaque = IsOpaqueContent;
        var oldPosition = Position;
        var oldWidth = Width;
        var oldHeight = Height;
        var oldContentWidth = ContentWidth;
        var oldContentHeight = ContentHeight;
        var oldBufferScale = BufferScale;
        var viewportChanged = _viewport != viewport;
        if (!contentChanged)
        {
            lock (_state)
            {
                newContent = _content;
                newWidth = _width;
                newHeight = _height;
                newFormat = _format;
            }
        }

        viewport.Validate(viewportId, newWidth, newHeight, newBufferScale);
        // The role hook runs outside the state lock (lock order: scene ->
        // surface state) and BEFORE the content becomes visible, so a mapped
        // window is at its final position the moment its pixels appear.
        // Sizes are logical (buffer pixels / buffer scale), the coordinate
        // space the window policy and the protocol speak.
        _role?.OnCommit(
            this,
            contentChanged,
            newContent is not null,
            viewport.Width(newWidth, newBufferScale),
            viewport.Height(newHeight, newBufferScale));

        var unmapped = false;
        lock (_state)
        {
            if (contentChanged)
            {
                unmapped = _content is not null && newContent is null;
                if (patch is { } update)
                {
                    update.Apply(newContent!, newWidth * 4);
                }
                else
                {
                    _recycledContent = newContent is not null && _content?.Length == newContent.Length ? _content : null;
                    if (newWidth != _width || newHeight != _height)
                    {
                        _patchPixels = null;
                    }
                }

                _content = newContent;
                if (unmapped)
                {
                    _unmapVersion++;
                }

                _width = newWidth;
                _height = newHeight;
                _format = newFormat;
            }

            // Applied on every commit, buffer or not — double-buffered
            // state is committed, not attached.
            _bufferScale = newBufferScale;
            _viewport = viewport;
            if (hasOpaque)
            {
                _opaqueRegion = opaque;
            }

            if (hasInputRegion)
            {
                _inputRegion = inputRegion;
            }

            foreach (var callback in callbacks) { _cachedCallbacks.Remove(callback); }
            _activeCallbacks.AddRange(callbacks);
        }

        if (unmapped)
        {
            // A client-driven unmap: the surface id is still valid, so the
            // seat sends proper leave events.
            Scene.OnSurfaceHidden(this, canSendLeave: true);
        }

        if (unmapped || viewportChanged || oldMapped != IsMapped || oldPosition != Position || oldWidth != Width || oldHeight != Height
            || oldContentWidth != ContentWidth || oldContentHeight != ContentHeight || oldBufferScale != BufferScale
            || (!IsCursorSurface && oldOpaque != IsOpaqueContent))
        {
            // Geometry changes may need to erase pixels at the old location.
            Scene.MarkDirty();
        }
        else if (!IsCursorSurface && !Hidden && IsMapped)
        {
            if (_viewport != default && (contentChanged || damage.Length > 0 || damageOverflow))
            {
                var position = Position;
                var scale = Scene.Scale;
                Scene.MarkDirty(new DamageRect(position.X * scale, position.Y * scale, Width * scale, Height * scale));
            }
            else if (damageOverflow || (contentChanged && damage.Length == 0))
            {
                MarkContentDamage(0, 0, _width, _height, buffer: true);
            }
            else
            {
                foreach (var rect in damage)
                {
                    MarkContentDamage(rect.X, rect.Y, rect.Right, rect.Bottom, rect.Buffer);
                }
            }

            // The surface origin may be off-screen, including on maximised
            // windows with shadows. Pick a pixel from its visible intersection.
            if (callbacks.Count > 0)
            {
                var scale = Math.Max(1, Scene.Scale);
                var position = Position;
                var left = Math.Max(0L, (long)position.X * scale);
                var top = Math.Max(0L, (long)position.Y * scale);
                var right = Math.Min((long)int.MaxValue, ((long)position.X + Width) * scale);
                var bottom = Math.Min((long)int.MaxValue, ((long)position.Y + Height) * scale);
                if (left < right && top < bottom)
                {
                    Scene.MarkDirty(new DamageRect((int)left, (int)top, 1, 1));
                }
            }
        }
    }

    /// <summary>Maps a client damage rectangle conservatively onto physical scanout pixels.</summary>
    private void MarkContentDamage(long x, long y, long right, long bottom, bool buffer)
    {
        lock (_state)
        {
            if (_width <= 0 || _height <= 0 || Width <= 0 || Height <= 0)
            {
                return;
            }

            var factor = buffer ? 1L : _bufferScale;
            x = Math.Clamp(x * factor, 0, _width);
            y = Math.Clamp(y * factor, 0, _height);
            right = Math.Clamp(right * factor, 0, _width);
            bottom = Math.Clamp(bottom * factor, 0, _height);
            if (right <= x || bottom <= y)
            {
                return;
            }

            var scale = Math.Max(1, Scene.Scale);
            var position = Position;
            // TakeDamage clips against the actual physical mode, including
            // remainder pixels lost when its logical size was rounded down.
            var left = Math.Clamp((long)position.X * scale + (long)Math.Floor((decimal)x * Width * scale / _width), 0, int.MaxValue);
            var top = Math.Clamp((long)position.Y * scale + (long)Math.Floor((decimal)y * Height * scale / _height), 0, int.MaxValue);
            var endX = Math.Clamp((long)position.X * scale + (long)Math.Ceiling((decimal)right * Width * scale / _width), 0, int.MaxValue);
            var endY = Math.Clamp((long)position.Y * scale + (long)Math.Ceiling((decimal)bottom * Height * scale / _height), 0, int.MaxValue);
            if (endX > left && endY > top && endX <= int.MaxValue && endY <= int.MaxValue)
            {
                Scene.MarkDirty(new DamageRect((int)left, (int)top, (int)(endX - left), (int)(endY - top)));
            }
        }
    }

    private void Destroy(ClientSession session, ServerObject target)
    {
        if (_role is { IsDefunct: false })
        {
            // wayland.xml: the role object must be destroyed first.
            throw new ProtocolErrorException(_self!.Id, SurfaceErrors.DefunctRoleObject, "wl_surface destroyed before its role object");
        }

        Scene.Remove(this);
        lock (_state)
        {
            if (_pendingBuffer is { } pending)
            {
                pending.ReleaseReference();
                _pendingBuffer = null;
            }

            _pendingAttach = false;
            _content = null;
            _recycledContent = null;
            _patchPixels = null;

            // Callbacks are freed silently: done is a "good time to draw"
            // signal and must not fire for a surface that no longer exists.
            DestroyCallbacks(session, _pendingCallbacks);
            DestroyCallbacks(session, _activeCallbacks);
            DestroyCallbacks(session, _cachedCallbacks);
            DestroyCallbacks(session, _pendingReleaseCallbacks);
        }

        _role?.OnSurfaceDestroyed();
        session.DestroyObject(target);
    }

    private static void DestroyCallbacks(ClientSession session, List<ServerObject> callbacks)
    {
        foreach (var callback in callbacks)
        {
            HandlerSupport.DestroySilently(session, callback);
        }

        callbacks.Clear();
    }
}
