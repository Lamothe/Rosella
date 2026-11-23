using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

internal interface IClientBuffer
{
    int Width { get; }
    int Height { get; }
    uint Format { get; }
    byte[]? ReadPixels(byte[]? reusable);
    void Release();
}

/// <summary>
/// Manages wl_buffer lifetime and sends release after the compositor takes its snapshot.
/// Backing storage survives destruction of the protocol object while an attachment is pending.
/// </summary>
internal sealed class BufferHandler : IRequestHandler, IDisposable
{
    private ClientSession? _session;
    private ServerObject? _self;
    private int _references = 1; // the wl_buffer object itself
    private bool _destroyed;

    public BufferHandler(IClientBuffer buffer)
    {
        Buffer = buffer;
    }

    public IClientBuffer Buffer { get; }

    public void Bind(ClientSession session, ServerObject self)
    {
        _session = session;
        _self = self;
    }

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        if (description.Name != "destroy")
        {
            throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }

        try
        {
            session.DestroyObject(target);
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        if (!_destroyed)
        {
            _destroyed = true;
            ReleaseReference();
        }
    }

    /// <summary>Called when a surface takes a pending reference to the buffer.</summary>
    public void Acquire()
    {
        _references++;
    }

    /// <summary>Drops one reference and releases the backing storage when all are gone.</summary>
    public void ReleaseReference()
    {
        _references--;
        if (_references <= 0)
        {
            Buffer.Release();
        }
    }

    /// <summary>Sends wl_buffer.release and completes any get_release callbacks (payload 0, per the protocol).</summary>
    public void MarkReleased(IReadOnlyList<ServerObject> releaseCallbacks)
    {
        var session = _session ?? throw new InvalidOperationException("buffer is not bound");
        var self = _self ?? throw new InvalidOperationException("buffer is not bound");
        session.TrySendEvent(self, "release");
        foreach (var callback in releaseCallbacks)
        {
            HandlerSupport.FireAndDestroy(session, callback, payload: 0);
        }
    }
}
