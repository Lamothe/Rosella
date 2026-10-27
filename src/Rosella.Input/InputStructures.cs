using System.Buffers.Binary;

namespace Rosella.Input;

/// <summary>
/// <c>struct input_event</c>: a timeval, then type/code/value. On x86_64 the
/// kernel's timeval is two 64-bit longs, so the event is 24 bytes.
/// </summary>
public readonly record struct InputEvent(long Sec, long Usec, ushort Type, ushort Code, int Value)
{
    /// <summary>Size on the wire (x86_64): timeval(16) + type(2) + code(2) + value(4).</summary>
    public const int Size = 24;

    /// <summary>Milliseconds on the kernel's clock, as clients want it. Wraps like the spec says.</summary>
    public uint TimeMs => unchecked((uint)((Sec * 1000) + (Usec / 1000)));

    /// <summary>Decodes one event from raw bytes.</summary>
    public static InputEvent Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Size)
        {
            throw new ArgumentException($"an input event is {Size} bytes, got {bytes.Length}", nameof(bytes));
        }

        var sec = BinaryPrimitives.ReadInt64LittleEndian(bytes);
        var usec = BinaryPrimitives.ReadInt64LittleEndian(bytes[8..]);
        var type = BinaryPrimitives.ReadUInt16LittleEndian(bytes[16..]);
        var code = BinaryPrimitives.ReadUInt16LittleEndian(bytes[18..]);
        var value = BinaryPrimitives.ReadInt32LittleEndian(bytes[20..]);
        return new InputEvent(sec, usec, type, code, value);
    }

    /// <summary>Encodes one event to raw bytes (test seam for golden bytes).</summary>
    public byte[] ToBytes()
    {
        var bytes = new byte[Size];
        var span = bytes.AsSpan();
        BinaryPrimitives.WriteInt64LittleEndian(span, Sec);
        BinaryPrimitives.WriteInt64LittleEndian(span[8..], Usec);
        BinaryPrimitives.WriteUInt16LittleEndian(span[16..], Type);
        BinaryPrimitives.WriteUInt16LittleEndian(span[18..], Code);
        BinaryPrimitives.WriteInt32LittleEndian(span[20..], Value);
        return bytes;
    }
}

/// <summary><c>struct input_id</c>: bus/vendor/product/version, all u16.</summary>
public readonly record struct InputId(ushort Bustype, ushort Vendor, ushort Product, ushort Version);
