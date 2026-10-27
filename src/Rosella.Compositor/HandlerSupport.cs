using Rosella.Protocol;
using Rosella.Server;
using Rosella.Server.Handlers;

namespace Rosella.Compositor;

/// <summary>Shared request plumbing for the real interface handlers.</summary>
internal static class HandlerSupport
{
    /// <summary>Resolves the request and enforces its since-version gate.</summary>
    internal static MessageDescription Require(ServerObject target, DecodedMessage request)
    {
        var description = target.Interface.FindRequest(request.Opcode)
            ?? throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, "unknown request");
        if (description.Since > target.Version)
        {
            throw new ProtocolErrorException(
                target.Id,
                ClientSession.ErrorInvalidMethod,
                $"'{target.Interface.Name}.{description.Name}' requires version {description.Since} but object {target.Id} is version {target.Version}");
        }

        return description;
    }

    /// <summary>Creates a typed child object at the parent's version.</summary>
    internal static ServerObject CreateChild(ClientSession session, ServerObject parent, string interfaceName, IRequestHandler handler, uint newId)
    {
        var childInterface = session.Protocol.FindInterface(interfaceName)
            ?? throw new ProtocolErrorException(parent.Id, ClientSession.ErrorImplementation, $"unknown interface '{interfaceName}'");
        return session.CreateClientObject(newId, childInterface, Math.Min(parent.Version, childInterface.Version), handler, parent);
    }

    /// <summary>Creates a wl_callback object (it has no requests).</summary>
    internal static ServerObject CreateCallback(ClientSession session, ServerObject parent, uint newId)
    {
        return CreateChild(session, parent, "wl_callback", StubHandler.Instance, newId);
    }

    /// <summary>
    /// Retires the id, sends wl_callback.done, then frees the id. The id is
    /// retired first so a fast client cannot re-create it in the window
    /// between done and delete_id.
    /// </summary>
    internal static void FireAndDestroy(ClientSession session, ServerObject callback, uint payload)
    {
        if (!session.Retire(callback))
        {
            return;
        }

        session.SendEvent(callback, "done", WireArgument.UInt32(payload));
        session.SendDeleteId(callback.Id);
    }

    /// <summary>Frees a callback's id without a done event (wl_surface.destroy).</summary>
    internal static void DestroySilently(ClientSession session, ServerObject callback)
    {
        if (!session.Retire(callback))
        {
            return;
        }

        session.SendDeleteId(callback.Id);
    }
}
