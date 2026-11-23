using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// Handles <c>wl_shm</c>: creates pools from client-passed descriptors.
/// Validation failures are reported as wl_shm errors on this object.
/// </summary>
internal sealed class ShmHandler : IRequestHandler
{
    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        switch (description.Name)
        {
            case "create_pool":
            {
                var poolHandler = CreatePoolHandler(target, request);
                try
                {
                    HandlerSupport.CreateChild(session, target, "wl_shm_pool", poolHandler, request.Arguments[0].AsNewId());
                }
                catch
                {
                    poolHandler.Dispose(); // the mapping must not leak on a rejected id
                    throw;
                }

                break;
            }

            case "release": // destructor, since 2
                session.DestroyObject(target);
                break;

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }

    private static ShmPoolHandler CreatePoolHandler(ServerObject shmObject, DecodedMessage request)
    {
        try
        {
            var descriptor = request.Arguments[1].AsFileDescriptor();
            var size = request.Arguments[2].AsInt32();
            return new ShmPoolHandler(shmObject, ShmNative.Duplicate(descriptor), size);
        }
        catch (ShmValidationException exception)
        {
            throw new ProtocolErrorException(shmObject.Id, exception.ErrorCode, exception.Message);
        }
    }
}
