using Microsoft.Win32.SafeHandles;
using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// Handles <c>wl_shm_pool</c>: the pool's mapping and buffer creation.
/// Errors are raised on this object (wl_shm_pool has its own error enum since
/// version 3), falling back to the wl_shm object for older bindings.
/// </summary>
internal sealed class ShmPoolHandler : IRequestHandler, IDisposable
{
    private readonly ServerObject _shm;
    private readonly ShmPool _pool;

    public ShmPoolHandler(ServerObject shmObject, SafeFileHandle handle, long size)
    {
        _shm = shmObject;
        _pool = ShmPool.Map(handle, size);
    }

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        switch (description.Name)
        {
            case "create_buffer":
            {
                var buffer = CreateBuffer(target, request);
                BufferHandler handler;
                try
                {
                    handler = new BufferHandler(buffer);
                    var created = HandlerSupport.CreateChild(session, target, "wl_buffer", handler, request.Arguments[0].AsNewId());
                    handler.Bind(session, created);
                }
                catch
                {
                    buffer.Release(); // the buffer's pool reference must not leak
                    throw;
                }

                break;
            }

            case "destroy": // destructor
                session.DestroyObject(target);
                Dispose();
                break;

            case "resize":
                Resize(target, request);
                break;

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }

    public void Dispose()
    {
        _pool.Dispose();
    }

    private ShmBuffer CreateBuffer(ServerObject poolObject, DecodedMessage request)
    {
        try
        {
            return _pool.CreateBuffer(
                request.Arguments[1].AsInt32(),
                request.Arguments[2].AsInt32(),
                request.Arguments[3].AsInt32(),
                request.Arguments[4].AsInt32(),
                request.Arguments[5].AsUInt32());
        }
        catch (ShmValidationException exception)
        {
            throw new ProtocolErrorException(ErrorTarget(poolObject).Id, exception.ErrorCode, exception.Message);
        }
    }

    private void Resize(ServerObject poolObject, DecodedMessage request)
    {
        try
        {
            _pool.Resize(request.Arguments[0].AsInt32());
        }
        catch (ShmValidationException exception)
        {
            throw new ProtocolErrorException(ErrorTarget(poolObject).Id, exception.ErrorCode, exception.Message);
        }
    }

    /// <summary>wl_shm_pool carries the error enum from version 3.</summary>
    private ServerObject ErrorTarget(ServerObject poolObject)
    {
        return poolObject.Version >= 3 ? poolObject : _shm;
    }
}
