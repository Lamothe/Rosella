namespace Rosella.Protocol;

/// <summary>
/// Word-level primitives for the Wayland wire format. Wayland messages use the
/// host's native byte order because both peers live on the same machine.
/// </summary>
internal static class WireFormat
{
    public static uint ReadUInt32(ReadOnlySpan<byte> buffer, int offset)
    {
        if (offset < 0 || offset > buffer.Length - sizeof(uint))
        {
            throw new WireProtocolException("Message is truncated while reading a 32-bit word.");
        }

        return BitConverter.ToUInt32(buffer.Slice(offset));
    }

    public static int ReadInt32(ReadOnlySpan<byte> buffer, int offset)
    {
        if (offset < 0 || offset > buffer.Length - sizeof(int))
        {
            throw new WireProtocolException("Message is truncated while reading a 32-bit word.");
        }

        return BitConverter.ToInt32(buffer.Slice(offset));
    }

    public static void WriteUInt32(Span<byte> buffer, int offset, uint value)
    {
        if (offset < 0 || offset > buffer.Length - sizeof(uint))
        {
            throw new ArgumentException("Buffer is too small for a 32-bit word.", nameof(buffer));
        }

        BitConverter.TryWriteBytes(buffer.Slice(offset), value);
    }

    public static void WriteInt32(Span<byte> buffer, int offset, int value)
    {
        if (offset < 0 || offset > buffer.Length - sizeof(int))
        {
            throw new ArgumentException("Buffer is too small for a 32-bit word.", nameof(buffer));
        }

        BitConverter.TryWriteBytes(buffer.Slice(offset), value);
    }

    /// <summary>Rounds a byte length up to the next 32-bit word boundary.</summary>
    public static int Align4(int length)
    {
        return (length + 3) & ~3;
    }
}
