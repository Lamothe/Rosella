namespace Rosella.Protocol;

/// <summary>
/// The 8-byte message header: the target object id, then one word holding the
/// total message size (header included) in its high 16 bits and the opcode in
/// its low 16 bits.
/// </summary>
public readonly record struct FrameHeader(uint ObjectId, ushort Opcode, ushort MessageSize)
{
    /// <summary>Size of the header on the wire, in bytes.</summary>
    public const int ByteSize = 8;

    /// <summary>Largest possible message size; the size field is 16 bits wide.</summary>
    public const int MaxMessageSize = ushort.MaxValue;

    /// <summary>Reads a header from the start of <paramref name="buffer"/>.</summary>
    public static FrameHeader Read(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < ByteSize)
        {
            throw new ArgumentException($"A frame header needs {ByteSize} bytes, got {buffer.Length}.", nameof(buffer));
        }

        var objectId = WireFormat.ReadUInt32(buffer, 0);
        var sizeAndOpcode = WireFormat.ReadUInt32(buffer, 4);
        return new FrameHeader(objectId, (ushort)(sizeAndOpcode & 0xFFFF), (ushort)(sizeAndOpcode >> 16));
    }

    /// <summary>Writes this header at the start of <paramref name="buffer"/>.</summary>
    public void Write(Span<byte> buffer)
    {
        if (buffer.Length < ByteSize)
        {
            throw new ArgumentException($"A frame header needs {ByteSize} bytes, got {buffer.Length}.", nameof(buffer));
        }

        WireFormat.WriteUInt32(buffer, 0, ObjectId);
        WireFormat.WriteUInt32(buffer, 4, ((uint)MessageSize << 16) | Opcode);
    }
}
