using System.Collections.Immutable;
using System.Text;

namespace Rosella.Protocol;

/// <summary>
/// The encoded form of one message: its wire bytes plus the file descriptors
/// that must be sent alongside them via SCM_RIGHTS.
/// </summary>
public readonly record struct EncodedMessage(byte[] Bytes, ImmutableArray<int> FileDescriptors);

/// <summary>
/// Encodes protocol messages into the Wayland wire format.
/// </summary>
public static class MessageEncoder
{
    /// <summary>
    /// Encodes one message: <paramref name="objectId"/> identifies the target
    /// object, <paramref name="opcode"/> selects the request or event within its
    /// interface, and <paramref name="arguments"/> must match
    /// <paramref name="signature"/> exactly.
    /// </summary>
    public static EncodedMessage Encode(
        uint objectId,
        ushort opcode,
        IReadOnlyList<ArgumentType> signature,
        IReadOnlyList<WireArgument> arguments)
    {
        ArgumentNullException.ThrowIfNull(signature);
        ArgumentNullException.ThrowIfNull(arguments);

        if (signature.Count != arguments.Count)
        {
            throw new ArgumentException(
                $"Signature has {signature.Count} argument(s) but {arguments.Count} value(s) were supplied.",
                nameof(arguments));
        }

        long size = FrameHeader.ByteSize;
        var descriptors = new List<int>();

        for (var i = 0; i < signature.Count; i++)
        {
            var type = signature[i];
            var argument = arguments[i];
            if (argument.Kind != type.Kind)
            {
                throw new ArgumentException(
                    $"Argument {i} is {argument.Kind} but the signature requires {type.Kind}.",
                    nameof(arguments));
            }

            size += type.Kind switch
            {
                ArgumentKind.Int32 or ArgumentKind.UInt32 or ArgumentKind.Fixed
                    or ArgumentKind.NewId => sizeof(uint),
                ArgumentKind.Object => ObjectFootprint(argument, type, i),
                ArgumentKind.String => StringFootprint(argument, type, i),
                ArgumentKind.Array => ArrayFootprint(argument, i),
                ArgumentKind.Fd => FileDescriptorFootprint(argument, descriptors),
                _ => throw new InvalidOperationException($"Unhandled argument kind {type.Kind}."),
            };
        }

        if (size > FrameHeader.MaxMessageSize)
        {
            throw new ArgumentException(
                $"Message is {size} bytes, which exceeds the maximum wire size of {FrameHeader.MaxMessageSize}.",
                nameof(arguments));
        }

        var buffer = new byte[(int)size];
        new FrameHeader(objectId, opcode, (ushort)size).Write(buffer);

        var offset = FrameHeader.ByteSize;
        for (var i = 0; i < signature.Count; i++)
        {
            var argument = arguments[i];
            switch (signature[i].Kind)
            {
                case ArgumentKind.Int32:
                    WireFormat.WriteInt32(buffer, offset, argument.AsInt32());
                    offset += sizeof(int);
                    break;
                case ArgumentKind.UInt32:
                    WireFormat.WriteUInt32(buffer, offset, argument.AsUInt32());
                    offset += sizeof(uint);
                    break;
                case ArgumentKind.Fixed:
                    WireFormat.WriteInt32(buffer, offset, argument.AsFixed().RawValue);
                    offset += sizeof(int);
                    break;
                case ArgumentKind.Object:
                    WireFormat.WriteUInt32(buffer, offset, argument.AsObject());
                    offset += sizeof(uint);
                    break;
                case ArgumentKind.NewId:
                    WireFormat.WriteUInt32(buffer, offset, argument.AsNewId());
                    offset += sizeof(uint);
                    break;
                case ArgumentKind.String:
                    offset = WriteString(buffer, offset, argument);
                    break;
                case ArgumentKind.Array:
                    offset = WriteArray(buffer, offset, argument);
                    break;
                case ArgumentKind.Fd:
                    break;
            }
        }

        return new EncodedMessage(buffer, descriptors.ToImmutableArray());
    }

    private static int WriteString(Span<byte> buffer, int offset, WireArgument argument)
    {
        var text = argument.AsNullableString();
        if (text is null)
        {
            WireFormat.WriteUInt32(buffer, offset, 0);
            return offset + sizeof(uint);
        }

        var byteCount = WireText.Utf8.GetByteCount(text);
        WireFormat.WriteUInt32(buffer, offset, (uint)(byteCount + 1));
        offset += sizeof(uint);
        WireText.Utf8.GetBytes(text, buffer.Slice(offset));
        buffer[offset + byteCount] = 0;
        return offset + WireFormat.Align4(byteCount + 1);
    }

    private static int WriteArray(Span<byte> buffer, int offset, WireArgument argument)
    {
        var data = argument.AsArray();
        WireFormat.WriteUInt32(buffer, offset, (uint)data.Length);
        offset += sizeof(uint);
        data.Span.CopyTo(buffer.Slice(offset));
        return offset + WireFormat.Align4(data.Length);
    }

    private static int ObjectFootprint(WireArgument argument, ArgumentType type, int index)
    {
        if (argument.AsObject() == 0 && !type.IsNullable)
        {
            throw new ArgumentException(
                $"Argument {index} is the null object but the signature does not allow it.",
                "arguments");
        }

        return sizeof(uint);
    }

    private static int StringFootprint(WireArgument argument, ArgumentType type, int index)
    {
        var text = argument.AsNullableString();
        if (text is null)
        {
            if (!type.IsNullable)
            {
                throw new ArgumentException(
                    $"Argument {index} is null but the signature does not allow a null string.",
                    "arguments");
            }

            return sizeof(uint);
        }

        if (text.AsSpan().IndexOf('\0') >= 0)
        {
            throw new ArgumentException($"Argument {index} contains an interior NUL character.", "arguments");
        }

        int byteCount;
        try
        {
            byteCount = WireText.Utf8.GetByteCount(text);
        }
        catch (EncoderFallbackException exception)
        {
            throw new ArgumentException($"Argument {index} contains invalid UTF-16.", "arguments", exception);
        }

        if (byteCount >= FrameHeader.MaxMessageSize)
        {
            throw new ArgumentException($"Argument {index} string is too large for a message.", "arguments");
        }

        return sizeof(uint) + WireFormat.Align4(byteCount + 1);
    }

    private static int ArrayFootprint(WireArgument argument, int index)
    {
        var length = argument.AsArray().Length;
        if (length >= FrameHeader.MaxMessageSize)
        {
            throw new ArgumentException($"Argument {index} array is too large for a message.", "arguments");
        }

        return sizeof(uint) + WireFormat.Align4(length);
    }

    private static int FileDescriptorFootprint(WireArgument argument, List<int> descriptors)
    {
        descriptors.Add(argument.AsFileDescriptor());
        return 0;
    }
}
