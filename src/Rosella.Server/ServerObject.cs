using Rosella.Protocol;

namespace Rosella.Server;

/// <summary>An object living in a client session's object map.</summary>
public sealed class ServerObject
{
    public ServerObject(uint id, InterfaceDescription interfaceDescription, uint version, IRequestHandler handler)
    {
        Id = id;
        Interface = interfaceDescription;
        Version = version;
        Handler = handler;
    }

    /// <summary>The object's id on this connection.</summary>
    public uint Id { get; }

    public InterfaceDescription Interface { get; }

    /// <summary>The version this object was created at.</summary>
    public uint Version { get; }

    public IRequestHandler Handler { get; }
}
