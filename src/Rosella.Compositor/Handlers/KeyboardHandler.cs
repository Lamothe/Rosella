using Rosella.Input;
using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>
/// Handles <c>wl_keyboard</c>: hands the client the one keymap (US QWERTY,
/// generated with xkbcommon) and repeat parameters, then relays the seat's
/// key/modifier events. keymap is always the first event on the object.
/// </summary>
internal sealed class KeyboardHandler : IRequestHandler, IDisposable
{
    private static readonly Lazy<string> KeymapText = new(XkbKeymap.GenerateUsKeymap);

    private readonly SeatState _seat;
    private ClientSession? _session;
    private ServerObject? _self;

    internal KeyboardHandler(SeatState seat)
    {
        _seat = seat;
    }

    /// <summary>Sends keymap + repeat_info, then registers with the seat.</summary>
    internal void Bind(ClientSession session, ServerObject self)
    {
        // One memfd per keyboard: SCM_RIGHTS receivers share the file
        // description (and its read offset) with our fd, so a shared keymap
        // file would let clients interleave and truncate each other's reads.
        var text = KeymapText.Value;
        var fd = XkbKeymap.CreateKeymapFile(text);
        try
        {
            session.SendEvent(self, "keymap", WireArgument.UInt32(1), WireArgument.FileDescriptor(fd), WireArgument.UInt32((uint)XkbKeymap.KeymapFileSize(text)));
        }
        finally
        {
            // The send duplicated the descriptor; our copy is done.
            XkbKeymap.Close(fd);
        }

        if (self.Version >= 4)
        {
            session.SendEvent(self, "repeat_info", WireArgument.Int32(25), WireArgument.Int32(600));
        }

        // Registration last: before it, the seat cannot send key/enter to
        // this keyboard ahead of keymap/repeat_info.
        _session = session;
        _self = self;
        _seat.RegisterKeyboard(session, self);
    }

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var description = HandlerSupport.Require(target, request);
        switch (description.Name)
        {
            case "release":
                _seat.UnregisterKeyboard(session, target);
                _session = null;
                _self = null;
                session.DestroyObject(target);
                break;

            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{description.Name}'");
        }
    }

    public void Dispose()
    {
        if (_session is not null && _self is not null)
        {
            _seat.UnregisterKeyboard(_session, _self);
            _session = null;
            _self = null;
        }
    }
}
