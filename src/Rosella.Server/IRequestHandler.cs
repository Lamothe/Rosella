using Rosella.Protocol;

namespace Rosella.Server;

/// <summary>Handles requests arriving at a server-side object.</summary>
public interface IRequestHandler
{
    void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request);
}
