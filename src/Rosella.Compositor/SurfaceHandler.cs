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
internal sealed class SurfaceHandler : IRequestHandler, IDisposable
{
    private readonly Scene _scene;
    private readonly object _state = new();
    private readonly List<ServerObject> _pendingCallbacks = new();
    private readonly List<ServerObject> _activeCallbacks = new();
    private readonly List<ServerObject> _pendingReleaseCallbacks = new();
    private ClientSession? _session;
    private ServerObject? _self;
    private BufferHandler? _pendingBuffer;
    private bool _pendingAttach;
    private byte[]? _content;
    private int _width;
    private int _height;
    private uint _format = ShmFormats.Xrgb8888;
    private int _bufferScale = 1;
    private int _pendingBufferScale = 1;
    private ISurfaceRole? _role;

    public SurfaceHandler(Scene scene)
    {
        _scene = scene;
    }

    /// <summary>Where the surface is composited (logical pixels), assigned by the scene.</summary>
    internal (int X, int Y) Position
    {
        get
        {
            lock (_state)
            {
                return _position;
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

    /// <summary>The surface's logical width: the placed rect, else the content's own.</summary>
    internal int Width
    {
        get
        {
            lock (_state)
            {
                return _placeWidth > 0 ? _placeWidth : _width / _bufferScale;
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
                return _placeHeight > 0 ? _placeHeight : _height / _bufferScale;
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
                return _width / _bufferScale;
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
                return _height / _bufferScale;
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
            var width = _placeWidth > 0 ? _placeWidth : _width / _bufferScale;
            return width > 0 ? (int)(((long)(sceneX - _position.X) * (_width / _bufferScale)) / width) : 0;
        }
    }

    /// <summary>Maps a scene y to the surface's own coordinate space.</summary>
    internal int LocalY(int sceneY)
    {
        lock (_state)
        {
            var height = _placeHeight > 0 ? _placeHeight : _height / _bufferScale;
            return height > 0 ? (int)(((long)(sceneY - _position.Y) * (_height / _bufferScale)) / height) : 0;
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
            _content = null;
            _width = 0;
            _height = 0;
        }

        // The object still exists, so the seat may send leave events.
        _scene.OnSurfaceHidden(this, canSendLeave: true);

        // The window left the scene; the frame must reflect it.
        _scene.MarkDirty();
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
            return $"surface {Id}: mapped={IsMapped} cursor={IsCursorSurface} xdg={HasXdgRole} gone={Gone} " +
                   $"content={_width}x{_height} fmt={_format:X} pos={Position.X},{Position.Y} " +
                   $"place={_placeWidth}x{_placeHeight} visible={visible.X},{visible.Y} {visible.W}x{visible.H}";
        }
    }

    /// <summary>True once any xdg role object has been created for this surface.</summary>
    internal bool HasXdgRole { get; private set; }

    /// <summary>
    /// Assigns the surface's xdg role. A surface's role never changes, so the
    /// first assignment wins for the wl_surface's lifetime — and a surface
    /// named as the pointer cursor (role: cursor) cannot become a window.
    /// </summary>
    internal bool TryAssignRole(ISurfaceRole role)
    {
        if (HasXdgRole || IsCursorSurface)
        {
            return false;
        }

        HasXdgRole = true;
        _role = role;
        return true;
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
                Commit();
                break;

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

            case "damage":
            case "damage_buffer":
            case "set_opaque_region":
            case "set_input_region":
            case "offset":
                // Parsed and ignored: full repaints and inert regions until
                // the later milestones need them.
                break;

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
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
        lock (_state)
        {
            if (_content is null)
            {
                return;
            }

            Blitter.Blit(
                pixels,
                width,
                height,
                stride,
                _content,
                _width,
                _height,
                _width * 4,
                _format,
                Position.X * outputScale,
                Position.Y * outputScale,
                Width * outputScale,
                Height * outputScale);
        }
    }

    /// <summary>Blits the content at a physical position (the pointer cursor).</summary>
    internal void BlitAt(Span<byte> pixels, int width, int height, int stride, int x, int y, int destinationWidth, int destinationHeight)
    {
        lock (_state)
        {
            if (_content is null)
            {
                return;
            }

            Blitter.Blit(
                pixels,
                width,
                height,
                stride,
                _content,
                _width,
                _height,
                _width * 4,
                _format,
                x,
                y,
                destinationWidth,
                destinationHeight);
        }
    }

    /// <summary>Session end: leave the scene and drop state without sending.</summary>
    public void Dispose()
    {
        _scene.Remove(this);
        lock (_state)
        {
            if (_pendingBuffer is { } pending)
            {
                pending.ReleaseReference();
                _pendingBuffer = null;
            }

            _pendingAttach = false;
            _content = null;
            _pendingCallbacks.Clear();
            _activeCallbacks.Clear();
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

    private void Commit()
    {
        bool contentChanged;
        byte[]? newContent;
        var newWidth = 0;
        var newHeight = 0;
        uint newFormat;
        int newBufferScale;
        lock (_state)
        {
            // get_release requires a non-null buffer in this content update.
            if (_pendingReleaseCallbacks.Count > 0 && !(_pendingAttach && _pendingBuffer is not null))
            {
                throw new ProtocolErrorException(_self!.Id, SurfaceErrors.NoBuffer, "get_release without a committed buffer");
            }

            contentChanged = _pendingAttach;
            newContent = _content;
            newWidth = _width;
            newHeight = _height;
            newFormat = _format;

            // set_buffer_scale is double-buffered state: applied at every
            // commit, with or without a new buffer (spec: wl_surface.commit).
            newBufferScale = _pendingBufferScale;
            if (_pendingAttach)
            {
                _pendingAttach = false;
                if (_pendingBuffer is { } buffer)
                {
                    newContent = buffer.Buffer.ReadPixels();
                    newWidth = buffer.Buffer.Width;
                    newHeight = buffer.Buffer.Height;
                    newFormat = buffer.Buffer.Format;
                    _pendingBuffer = null;
                    buffer.ReleaseReference();
                    buffer.MarkReleased(_pendingReleaseCallbacks);
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

        // The role hook runs outside the state lock (lock order: scene ->
        // surface state) and BEFORE the content becomes visible, so a mapped
        // window is at its final position the moment its pixels appear.
        // Sizes are logical (buffer pixels / buffer scale), the coordinate
        // space the window policy and the protocol speak.
        _role?.OnCommit(
            this,
            contentChanged,
            newContent is not null,
            newWidth / newBufferScale,
            newHeight / newBufferScale);

        var unmapped = false;
        lock (_state)
        {
            if (contentChanged)
            {
                unmapped = _content is not null && newContent is null;
                _content = newContent;
                _width = newWidth;
                _height = newHeight;
                _format = newFormat;
            }

            // Applied on every commit, buffer or not — double-buffered
            // state is committed, not attached.
            _bufferScale = newBufferScale;

            _activeCallbacks.AddRange(_pendingCallbacks);
            _pendingCallbacks.Clear();
        }

        if (unmapped)
        {
            // A client-driven unmap: the surface id is still valid, so the
            // seat sends proper leave events.
            _scene.OnSurfaceHidden(this, canSendLeave: true);
        }

        // Every commit earns the next composited frame (frame callbacks
        // fire "when the next frame is displayed after the commit").
        _scene.MarkDirty();
    }

    private void Destroy(ClientSession session, ServerObject target)
    {
        if (_role is { IsDefunct: false })
        {
            // wayland.xml: the role object must be destroyed first.
            throw new ProtocolErrorException(_self!.Id, SurfaceErrors.DefunctRoleObject, "wl_surface destroyed before its role object");
        }

        _scene.Remove(this);
        lock (_state)
        {
            if (_pendingBuffer is { } pending)
            {
                pending.ReleaseReference();
                _pendingBuffer = null;
            }

            _pendingAttach = false;
            _content = null;

            // Callbacks are freed silently: done is a "good time to draw"
            // signal and must not fire for a surface that no longer exists.
            DestroyCallbacks(session, _pendingCallbacks);
            DestroyCallbacks(session, _activeCallbacks);
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
