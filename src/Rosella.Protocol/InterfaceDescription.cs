namespace Rosella.Protocol;

/// <summary>
/// One protocol interface: its name, maximum version, and the requests and
/// events it defines, indexed by opcode.
/// </summary>
public sealed class InterfaceDescription
{
    private readonly Dictionary<ushort, MessageDescription> _requestsByOpcode;
    private readonly Dictionary<ushort, MessageDescription> _eventsByOpcode;

    public InterfaceDescription(string name, uint version, IReadOnlyList<MessageDescription> requests, IReadOnlyList<MessageDescription> events)
    {
        Name = name;
        Version = version;
        Requests = requests;
        Events = events;
        _requestsByOpcode = requests.ToDictionary(r => r.Opcode);
        _eventsByOpcode = events.ToDictionary(e => e.Opcode);
    }

    public string Name { get; }

    /// <summary>The highest version of this interface the protocol XML defines.</summary>
    public uint Version { get; }

    /// <summary>Requests in opcode order (client to server).</summary>
    public IReadOnlyList<MessageDescription> Requests { get; }

    /// <summary>Events in opcode order (server to client).</summary>
    public IReadOnlyList<MessageDescription> Events { get; }

    /// <summary>Returns the request for <paramref name="opcode"/>, or null when it does not exist.</summary>
    public MessageDescription? FindRequest(ushort opcode)
    {
        return _requestsByOpcode.GetValueOrDefault(opcode);
    }

    /// <summary>Returns the event for <paramref name="opcode"/>, or null when it does not exist.</summary>
    public MessageDescription? FindEvent(ushort opcode)
    {
        return _eventsByOpcode.GetValueOrDefault(opcode);
    }
}
