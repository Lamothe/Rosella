using Rosella.Protocol;

namespace Rosella.Server.Handlers;

/// <summary>
/// Handles <c>wl_display</c>: <c>sync</c> and <c>get_registry</c>.
/// </summary>
public sealed class DisplayHandler : IRequestHandler
{
    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = target.Interface.FindRequest(request.Opcode)
            ?? throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, "unknown request");

        switch (description.Name)
        {
            case "sync":
            {
                var callbackInterface = session.Protocol.FindInterface("wl_callback")
                    ?? throw new ProtocolErrorException(target.Id, ClientSession.ErrorImplementation, "wl_callback is missing");
                var callback = session.CreateClientObject(request.Arguments[0].AsNewId(), callbackInterface, 1, StubHandler.Instance, target);
                session.SendEvent(callback, "done", WireArgument.UInt32(session.NextSerial()));
                session.DestroyObject(callback);
                break;
            }

            case "get_registry":
            {
                var registryInterface = session.Protocol.FindInterface("wl_registry")
                    ?? throw new ProtocolErrorException(target.Id, ClientSession.ErrorImplementation, "wl_registry is missing");
                var registry = session.CreateClientObject(request.Arguments[0].AsNewId(), registryInterface, 1, new RegistryHandler(), target);

                // A new registry is immediately told about every global.
                foreach (var global in session.Globals)
                {
                    session.SendEvent(
                        registry,
                        "global",
                        WireArgument.UInt32(global.Name),
                        WireArgument.String(global.Interface),
                        WireArgument.UInt32(global.Version));
                }

                break;
            }

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, "unknown request");
        }
    }
}
