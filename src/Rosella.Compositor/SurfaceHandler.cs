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
    internal const uint NoBuffer = 5;
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

    public SurfaceHandler(Scene scene)
    {
        _scene = scene;
    }

    /// <summary>Where the surface is composited, assigned by the scene.</summary>
    internal (int X, int Y) Position { get; set; }

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

    internal int Width => _width;

    internal int Height => _height;

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

    public void Bind(ClientSession session, ServerObject self)
    {
        _session = session;
        _self = self;
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
                if (request.Arguments[0].AsInt32() <= 0)
                {
                    throw new ProtocolErrorException(target.Id, SurfaceErrors.InvalidScale, "buffer scale must be positive");
                }

                break;

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

    /// <summary>Draws the surface's content at its position, if mapped.</summary>
    internal void BlitInto(Span<byte> pixels, int width, int height, int stride)
    {
        lock (_state)
        {
            if (_content is not null)
            {
                Blitter.Copy(pixels, width, height, stride, _content, _width, _height, Position.X, Position.Y);
            }
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
        lock (_state)
        {
            // get_release requires a non-null buffer in this content update.
            if (_pendingReleaseCallbacks.Count > 0 && !(_pendingAttach && _pendingBuffer is not null))
            {
                throw new ProtocolErrorException(_self!.Id, SurfaceErrors.NoBuffer, "get_release without a committed buffer");
            }

            if (_pendingAttach)
            {
                _pendingAttach = false;
                if (_pendingBuffer is { } buffer)
                {
                    _content = buffer.Buffer.ReadPixels();
                    _width = buffer.Buffer.Width;
                    _height = buffer.Buffer.Height;
                    _pendingBuffer = null;
                    buffer.ReleaseReference();
                    buffer.MarkReleased(_pendingReleaseCallbacks);
                    _pendingReleaseCallbacks.Clear();
                }
                else
                {
                    _content = null;
                }
            }

            _activeCallbacks.AddRange(_pendingCallbacks);
            _pendingCallbacks.Clear();
        }
    }

    private void Destroy(ClientSession session, ServerObject target)
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

            // Callbacks are freed silently: done is a "good time to draw"
            // signal and must not fire for a surface that no longer exists.
            DestroyCallbacks(session, _pendingCallbacks);
            DestroyCallbacks(session, _activeCallbacks);
            DestroyCallbacks(session, _pendingReleaseCallbacks);
        }

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
