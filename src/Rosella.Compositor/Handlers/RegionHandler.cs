using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// Handles <c>wl_region</c>. Regions are parsed but inert until input and
/// blending land; the requests exist so clients can run unmodified.
/// </summary>
internal sealed class RegionHandler : IRequestHandler
{
    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        switch (description.Name)
        {
            case "destroy": // destructor
                session.DestroyObject(target);
                break;

            case "add":
            case "subtract":
                break;

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }
}
