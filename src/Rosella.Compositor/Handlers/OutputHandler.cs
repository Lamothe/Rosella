using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>The driven output's identity, as clients need to hear it.</summary>
internal sealed record OutputInfo(
    int Width,
    int Height,
    uint MmWidth,
    uint MmHeight,
    int RefreshMilliHz,
    int Scale,
    string Make,
    string Model,
    string Name);

/// <summary>
/// Handles <c>wl_output</c>: tells clients the real geometry, mode and
/// scale of the driven output, so toolkits size and render correctly
/// (HiDPI clients render at the output scale and set buffer_scale to match).
/// </summary>
internal sealed class OutputHandler : IRequestHandler, IBindEvents
{
    private readonly OutputInfo _info;

    internal OutputHandler(OutputInfo info)
    {
        _info = info;
    }

    public void SendBindEvents(ClientSession session, ServerObject bound)
    {
        // physical size in mm, subpixel unknown (0), transform normal (0).
        SendIfSupported(session, bound, "geometry",
            WireArgument.Int32(0),
            WireArgument.Int32(0),
            WireArgument.Int32((int)_info.MmWidth),
            WireArgument.Int32((int)_info.MmHeight),
            WireArgument.Int32(0),
            WireArgument.String(_info.Make),
            WireArgument.String(_info.Model),
            WireArgument.Int32(0));

        // mode size is physical pixels; refresh is milli-Hz.
        SendIfSupported(session, bound, "mode",
            WireArgument.UInt32(1), // current
            WireArgument.Int32(_info.Width),
            WireArgument.Int32(_info.Height),
            WireArgument.Int32(_info.RefreshMilliHz));

        SendIfSupported(session, bound, "scale", WireArgument.Int32(_info.Scale));

        // wl_output.name: the connector's identity ("HDMI-A-2"), the same
        // name DRM tools show; description is the human-readable form. Both
        // precede the closing done (wayland.xml: done is "sent after all
        // other properties"; name/description are "followed by a done").
        SendIfSupported(session, bound, "name", WireArgument.String(_info.Name));
        SendIfSupported(session, bound, "description", WireArgument.String($"{_info.Make} {_info.Model}"));
        SendIfSupported(session, bound, "done");
    }

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        if (description.Name != "release")
        {
            throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }

        session.DestroyObject(target);
    }

    /// <summary>Sends an event unless it is newer than the bound object's version.</summary>
    private static void SendIfSupported(ClientSession session, ServerObject bound, string eventName, params WireArgument[] arguments)
    {
        var eventDescription = bound.Interface.Events.First(e => e.Name == eventName);
        if (eventDescription.Since <= bound.Version)
        {
            session.SendEvent(bound, eventName, arguments);
        }
    }
}
