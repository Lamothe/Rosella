namespace Rosella.Protocol;

/// <summary>
/// Raised when a peer sends malformed wire data or violates the protocol wire format.
/// </summary>
public sealed class WireProtocolException : Exception
{
    public WireProtocolException(string message)
        : base(message)
    {
    }

    public WireProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
