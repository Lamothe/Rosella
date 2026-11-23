using Rosella.Protocol;

namespace Rosella.Server.Handlers;

/// <summary>
/// Handles <c>wl_registry</c>: advertises globals and binds them.
/// </summary>
public sealed class RegistryHandler : IRequestHandler
{
    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = target.Interface.FindRequest(request.Opcode)
            ?? throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, "unknown request");
        if (description.Name != "bind")
        {
            throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, "unknown request");
        }

        // Wire order of the untyped new_id expansion: name, interface, version, id.
        var name = request.Arguments[0].AsUInt32();
        var interfaceName = request.Arguments[1].AsString();
        var version = request.Arguments[2].AsUInt32();
        var id = request.Arguments[3].AsNewId();

        var global = session.Globals.FirstOrDefault(g => g.Name == name)
            ?? throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidObject, $"no global named {name}");
        if (global.Interface != interfaceName)
        {
            throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidObject, $"global {name} is '{global.Interface}', not '{interfaceName}'");
        }

        var boundInterface = session.Protocol.FindInterface(global.Interface)
            ?? throw new ProtocolErrorException(target.Id, ClientSession.ErrorImplementation, $"unknown interface '{global.Interface}'");
        if (version == 0 || version > global.Version || version > boundInterface.Version)
        {
            throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"global '{global.Interface}' does not support version {version}");
        }

        var handler = CreateHandler(session, global.Interface);
        var bound = session.CreateClientObject(id, boundInterface, version, handler, target);
        if (handler is IBindEvents bindEvents)
        {
            bindEvents.SendBindEvents(session, bound);
        }
        else
        {
            SendBoundEvents(session, bound);
        }
    }

    /// <summary>The compositor's handler for this global, or the generic stub.</summary>
    private static IRequestHandler CreateHandler(ClientSession session, string interfaceName)
    {
        return session.HandlerFactory?.Create(interfaceName) ?? StubHandler.Instance;
    }

    /// <summary>The events a client expects right after binding these globals.</summary>
    private static void SendBoundEvents(ClientSession session, ServerObject bound)
    {
        switch (bound.Interface.Name)
        {
            case "wl_shm":
                SendEventIfSupported(session, bound, "format", WireArgument.UInt32(0)); // argb8888
                SendEventIfSupported(session, bound, "format", WireArgument.UInt32(1)); // xrgb8888
                break;

            case "wl_seat":
                SendEventIfSupported(session, bound, "capabilities", WireArgument.UInt32(0));
                SendEventIfSupported(session, bound, "name", WireArgument.String("rosella"));
                break;

            case "wl_output":
                SendEventIfSupported(
                    session,
                    bound,
                    "geometry",
                    WireArgument.Int32(0),
                    WireArgument.Int32(0),
                    WireArgument.Int32(600),
                    WireArgument.Int32(340),
                    WireArgument.Int32(0),
                    WireArgument.String("rosella"),
                    WireArgument.String("headless-1"),
                    WireArgument.Int32(0));
                SendEventIfSupported(
                    session,
                    bound,
                    "mode",
                    WireArgument.UInt32(1), // current
                    WireArgument.Int32(1920),
                    WireArgument.Int32(1080),
                    WireArgument.Int32(60000));
                SendEventIfSupported(session, bound, "scale", WireArgument.Int32(1));
                SendEventIfSupported(session, bound, "done");
                SendEventIfSupported(session, bound, "name", WireArgument.String("rosella-1"));
                SendEventIfSupported(session, bound, "description", WireArgument.String("Rosella virtual output"));
                break;
        }
    }

    /// <summary>Sends an event unless it is newer than the bound object's version.</summary>
    private static void SendEventIfSupported(ClientSession session, ServerObject bound, string eventName, params WireArgument[] arguments)
    {
        var description = bound.Interface.Events.First(e => e.Name == eventName);
        if (description.Since <= bound.Version)
        {
            session.SendEvent(bound, eventName, arguments);
        }
    }
}
