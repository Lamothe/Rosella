namespace Rosella.Server;

/// <summary>
/// A protocol violation by the peer. The session reports it with a
/// <c>wl_display.error</c> event and then closes the connection.
/// </summary>
public sealed class ProtocolErrorException : Exception
{
    public ProtocolErrorException(uint objectId, uint code, string message)
        : base(message)
    {
        ObjectId = objectId;
        Code = code;
    }

    /// <summary>The object where the violation occurred.</summary>
    public uint ObjectId { get; }

    /// <summary>A <c>wl_display.error</c> code.</summary>
    public uint Code { get; }
}
