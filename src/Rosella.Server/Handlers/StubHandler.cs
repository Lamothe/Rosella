using Rosella.Protocol;

namespace Rosella.Server.Handlers;

/// <summary>
/// Generic handler for interfaces Rosella creates but does not yet implement:
/// destructor requests destroy the object, typed new_id arguments create
/// tracked child objects, and everything else is parsed and ignored.
/// </summary>
public sealed class StubHandler : IRequestHandler
{
    public static StubHandler Instance { get; } = new();

    private StubHandler()
    {
    }

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = target.Interface.FindRequest(request.Opcode)
            ?? throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, "unknown request");

        if (description.Since > target.Version)
        {
            throw new ProtocolErrorException(
                target.Id,
                ClientSession.ErrorInvalidMethod,
                $"'{target.Interface.Name}.{description.Name}' requires version {description.Since} but the object is version {target.Version}");
        }

        if (description.IsDestructor)
        {
            session.DestroyObject(target);
            return;
        }

        var wireIndex = 0;
        for (var i = 0; i < description.Arguments.Count; i++)
        {
            var argument = description.Arguments[i];
            if (argument.Kind == ArgumentKind.NewId && argument.Interface is null)
            {
                // Untyped new_id: interface string, version, then id. The
                // interface is chosen at runtime, so a stub cannot honour it.
                wireIndex += 3;
                continue;
            }

            if (argument.Kind == ArgumentKind.NewId && argument.Interface is { } interfaceName)
            {
                var childInterface = session.Protocol.FindInterface(interfaceName)
                    ?? throw new ProtocolErrorException(target.Id, ClientSession.ErrorImplementation, $"unknown interface '{interfaceName}'");
                var version = Math.Min(target.Version, childInterface.Version);
                session.CreateClientObject(request.Arguments[wireIndex].AsNewId(), childInterface, version, Instance, target);
            }

            wireIndex++;
        }
    }
}
