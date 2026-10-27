using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// Handles <c>wl_buffer</c>: lifetime and release. The buffer's content is
/// copied at commit, so release is sent as soon as that happens. The buffer's
/// pool reference is held until the object is destroyed and no surface holds
/// it pending, so destroying pool and buffer in any order is safe.
/// </summary>
internal sealed class BufferHandler : IRequestHandler
{
    private ClientSession? _session;
    private ServerObject? _self;
    private int _references = 1; // the wl_buffer object itself

    public BufferHandler(ShmBuffer buffer)
    {
        Buffer = buffer;
    }

    public ShmBuffer Buffer { get; }

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

        session.DestroyObject(target);
        ReleaseReference();
    }

    /// <summary>Called when a surface takes a pending reference to the buffer.</summary>
    public void Acquire()
    {
        _references++;
    }

    /// <summary>Drops one reference; the pool mapping goes once all are gone.</summary>
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
        session.SendEvent(self, "release");
        foreach (var callback in releaseCallbacks)
        {
            HandlerSupport.FireAndDestroy(session, callback, payload: 0);
        }
    }
}
