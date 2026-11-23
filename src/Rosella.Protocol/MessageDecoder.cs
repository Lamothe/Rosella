using System.Collections.Immutable;
using System.Text;

namespace Rosella.Protocol;

/// <summary>One decoded message: the target object, the opcode and its arguments.</summary>
public readonly record struct DecodedMessage(uint ObjectId, ushort Opcode, ImmutableArray<WireArgument> Arguments);

/// <summary>
/// Decodes messages from the Wayland wire format.
/// </summary>
public static class MessageDecoder
{
    /// <summary>
    /// Decodes a single message from the front of <paramref name="buffer"/>.
    /// Returns <see langword="false"/> when the buffer does not yet contain a
    /// complete message; throws <see cref="WireProtocolException"/> when the
    /// data is malformed.
    /// </summary>
    /// <remarks>
    /// File descriptors arrive out-of-band and are consumed in signature order;
    /// a message that needs more descriptors than supplied is a protocol error.
    /// </remarks>
    public static bool TryDecode(
        ReadOnlySpan<byte> buffer,
        IReadOnlyList<ArgumentType> signature,
        ReadOnlySpan<int> fileDescriptors,
        out DecodedMessage message,
        out int bytesConsumed,
        out int descriptorsConsumed)
    {
        ArgumentNullException.ThrowIfNull(signature);
        message = default;
        bytesConsumed = 0;
        descriptorsConsumed = 0;

        if (buffer.Length < FrameHeader.ByteSize)
        {
            return false;
        }

        var header = FrameHeader.Read(buffer);
        if (header.MessageSize < FrameHeader.ByteSize || header.MessageSize % 4 != 0)
        {
            throw new WireProtocolException(
                $"Message size {header.MessageSize} must be a multiple of four and at least {FrameHeader.ByteSize} bytes.");
        }

        if (buffer.Length < header.MessageSize)
        {
            return false;
        }

        var body = buffer.Slice(0, header.MessageSize);
        var arguments = ImmutableArray.CreateBuilder<WireArgument>(signature.Count);
        var offset = FrameHeader.ByteSize;
        var descriptorIndex = 0;

        for (var i = 0; i < signature.Count; i++)
        {
            var type = signature[i];
            switch (type.Kind)
            {
                case ArgumentKind.Int32:
                    arguments.Add(WireArgument.Int32(WireFormat.ReadInt32(body, offset)));
                    offset += sizeof(int);
                    break;
                case ArgumentKind.UInt32:
                    arguments.Add(WireArgument.UInt32(WireFormat.ReadUInt32(body, offset)));
                    offset += sizeof(uint);
                    break;
                case ArgumentKind.Fixed:
                    arguments.Add(WireArgument.FixedPoint(Fixed.FromRaw(WireFormat.ReadInt32(body, offset))));
                    offset += sizeof(int);
                    break;
                case ArgumentKind.Object:
                    arguments.Add(ReadObject(body, type, i, ref offset));
                    break;
                case ArgumentKind.NewId:
                    arguments.Add(WireArgument.NewId(ReadNewId(body, offset, i)));
                    offset += sizeof(uint);
                    break;
                case ArgumentKind.String:
                    arguments.Add(ReadString(body, type, i, ref offset));
                    break;
                case ArgumentKind.Array:
                    arguments.Add(ReadArray(body, i, ref offset));
                    break;
                case ArgumentKind.Fd:
                    arguments.Add(ReadFileDescriptor(fileDescriptors, i, ref descriptorIndex));
                    break;
            }
        }

        if (offset != header.MessageSize)
        {
            throw new WireProtocolException(
                $"Message of {header.MessageSize} bytes does not match its signature ({offset} bytes accounted for).");
        }

        message = new DecodedMessage(header.ObjectId, header.Opcode, arguments.MoveToImmutable());
        bytesConsumed = header.MessageSize;
        descriptorsConsumed = descriptorIndex;
        return true;
    }

    private static uint ReadNewId(ReadOnlySpan<byte> body, int offset, int index)
    {
        var id = WireFormat.ReadUInt32(body, offset);
        if (id == 0)
        {
            throw new WireProtocolException($"Argument {index} is a new_id of 0, which is reserved.");
        }

        return id;
    }

    private static WireArgument ReadObject(ReadOnlySpan<byte> body, ArgumentType type, int index, ref int offset)
    {
        var id = WireFormat.ReadUInt32(body, offset);
        offset += sizeof(uint);
        if (id == 0 && !type.IsNullable)
        {
            throw new WireProtocolException($"Argument {index} is the null object but the signature does not allow it.");
        }

        return WireArgument.Object(id);
    }

    private static WireArgument ReadString(ReadOnlySpan<byte> body, ArgumentType type, int index, ref int offset)
    {
        var length = WireFormat.ReadUInt32(body, offset);
        offset += sizeof(uint);

        if (length == 0)
        {
            if (!type.IsNullable)
            {
                throw new WireProtocolException($"Argument {index} is a null string but the signature does not allow it.");
            }

            return WireArgument.NullString();
        }

        if (length > (uint)(body.Length - offset))
        {
            throw new WireProtocolException($"Argument {index} string of {length} bytes exceeds the message bounds.");
        }

        var text = body.Slice(offset, (int)length);
        if (text[^1] != 0)
        {
            throw new WireProtocolException($"Argument {index} string is not NUL-terminated.");
        }

        if (text[..^1].IndexOf((byte)0) >= 0)
        {
            throw new WireProtocolException($"Argument {index} string contains an interior NUL byte.");
        }

        offset += WireFormat.Align4((int)length);
        try
        {
            return WireArgument.String(WireText.Utf8.GetString(text[..^1]));
        }
        catch (DecoderFallbackException exception)
        {
            throw new WireProtocolException($"Argument {index} string is not valid UTF-8.", exception);
        }
    }

    private static WireArgument ReadArray(ReadOnlySpan<byte> body, int index, ref int offset)
    {
        var length = WireFormat.ReadUInt32(body, offset);
        offset += sizeof(uint);

        if (length > (uint)(body.Length - offset))
        {
            throw new WireProtocolException($"Argument {index} array of {length} bytes exceeds the message bounds.");
        }

        var data = body.Slice(offset, (int)length).ToArray();
        offset += WireFormat.Align4((int)length);
        return WireArgument.Array(data);
    }

    private static WireArgument ReadFileDescriptor(ReadOnlySpan<int> fileDescriptors, int index, ref int descriptorIndex)
    {
        if (descriptorIndex >= fileDescriptors.Length)
        {
            throw new WireProtocolException($"Argument {index} needs a file descriptor but none was supplied.");
        }

        var descriptor = fileDescriptors[descriptorIndex];
        descriptorIndex++;
        return WireArgument.FileDescriptor(descriptor);
    }
}
