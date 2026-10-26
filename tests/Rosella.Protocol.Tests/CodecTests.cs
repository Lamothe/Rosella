namespace Rosella.Protocol.Tests;

public class CodecTests
{
    // Golden byte arrays below are written for little-endian hosts, which is
    // where Rosella runs; the wire format is defined in native byte order.

    [Fact]
    public void Encodes_RegistryBind_GoldenBytes()
    {
        // wl_registry.bind(name=7, "wl_shm", version=1, id=42): "un" in XML, "usun" on the wire.
        var encoded = MessageEncoder.Encode(
            objectId: 2,
            opcode: 0,
            Signature.Parse("usun"),
            [WireArgument.UInt32(7), WireArgument.String("wl_shm"), WireArgument.UInt32(1), WireArgument.NewId(42)]);

        byte[] expected =
        [
            0x02, 0x00, 0x00, 0x00, // object id 2
            0x00, 0x00, 0x20, 0x00, // size 32 << 16 | opcode 0
            0x07, 0x00, 0x00, 0x00, // name 7
            0x07, 0x00, 0x00, 0x00, // string length 7: "wl_shm" plus NUL
            0x77, 0x6C, 0x5F, 0x73, // "wl_s"
            0x68, 0x6D, 0x00, 0x00, // "hm", NUL, padding
            0x01, 0x00, 0x00, 0x00, // version 1
            0x2A, 0x00, 0x00, 0x00, // new id 42
        ];

        Assert.Equal(expected, encoded.Bytes);
        Assert.Empty(encoded.FileDescriptors);
    }

    [Fact]
    public void Encodes_GetRegistry_GoldenBytes()
    {
        // wl_display.get_registry(new_id=2): object 1, opcode 1, 12-byte message.
        var encoded = MessageEncoder.Encode(1, 1, Signature.Parse("n"), [WireArgument.NewId(2)]);

        byte[] expected =
        [
            0x01, 0x00, 0x00, 0x00,
            0x01, 0x00, 0x0C, 0x00, // size 12 << 16 | opcode 1
            0x02, 0x00, 0x00, 0x00,
        ];

        Assert.Equal(expected, encoded.Bytes);
    }

    [Fact]
    public void Encodes_NullString_AsZeroLength()
    {
        var encoded = MessageEncoder.Encode(1, 7, Signature.Parse("?s"), [WireArgument.NullString()]);

        byte[] expected =
        [
            0x01, 0x00, 0x00, 0x00,
            0x07, 0x00, 0x0C, 0x00, // size 12 << 16 | opcode 7
            0x00, 0x00, 0x00, 0x00, // length 0: null string
        ];

        Assert.Equal(expected, encoded.Bytes);
    }

    [Fact]
    public void Encodes_Array_WithPaddedTail()
    {
        var encoded = MessageEncoder.Encode(3, 0, Signature.Parse("a"), [WireArgument.Array([0xAA, 0xBB, 0xCC])]);

        byte[] expected =
        [
            0x03, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x10, 0x00, // size 16 << 16 | opcode 0
            0x03, 0x00, 0x00, 0x00, // 3 payload bytes
            0xAA, 0xBB, 0xCC, 0x00, // payload plus padding
        ];

        Assert.Equal(expected, encoded.Bytes);
    }

    [Fact]
    public void RoundTrips_AllScalarKinds()
    {
        var message = RoundTrip(
            5,
            3,
            "iufon",
            WireArgument.Int32(-17),
            WireArgument.UInt32(4_000_000_000),
            WireArgument.FixedPoint(Fixed.FromDouble(-1.25)),
            WireArgument.Object(9),
            WireArgument.NewId(64));

        Assert.Equal(-17, message.Arguments[0].AsInt32());
        Assert.Equal(4_000_000_000u, message.Arguments[1].AsUInt32());
        Assert.Equal(-1.25, message.Arguments[2].AsFixed().ToDouble());
        Assert.Equal(9u, message.Arguments[3].AsObject());
        Assert.Equal(64u, message.Arguments[4].AsNewId());
    }

    [Fact]
    public void RoundTrips_UnicodeStrings()
    {
        var message = RoundTrip(1, 9, "s?s", WireArgument.String("héllo wörld"), WireArgument.NullString());

        Assert.Equal("héllo wörld", message.Arguments[0].AsString());
        Assert.Null(message.Arguments[1].AsNullableString());
    }

    [Fact]
    public void RoundTrips_ArrayContent()
    {
        var payload = Enumerable.Range(0, 11).Select(i => (byte)i).ToArray();
        var message = RoundTrip(1, 4, "a", WireArgument.Array(payload));

        Assert.Equal(payload, message.Arguments[0].AsArray().ToArray());
    }

    [Fact]
    public void RoundTrips_FileDescriptors_InSignatureOrder()
    {
        var message = RoundTrip(1, 2, "uhh", WireArgument.UInt32(1), WireArgument.FileDescriptor(3), WireArgument.FileDescriptor(9));

        Assert.Equal(3, message.Arguments[1].AsFileDescriptor());
        Assert.Equal(9, message.Arguments[2].AsFileDescriptor());
    }

    [Fact]
    public void EncodedFileDescriptors_AreCarriedOutOfBand()
    {
        var encoded = MessageEncoder.Encode(1, 2, Signature.Parse("h"), [WireArgument.FileDescriptor(12)]);

        Assert.Equal(12, Assert.Single(encoded.FileDescriptors));
        Assert.Equal(FrameHeader.ByteSize, encoded.Bytes.Length);
    }

    [Fact]
    public void TryDecode_NeedsMoreData_ForPartialMessage()
    {
        var encoded = MessageEncoder.Encode(1, 1, Signature.Parse("n"), [WireArgument.NewId(2)]);

        Assert.False(MessageDecoder.TryDecode(encoded.Bytes.AsSpan(0, 6), Signature.Parse("n"), [], out _, out _, out _));
        Assert.False(MessageDecoder.TryDecode(encoded.Bytes.AsSpan(0, 8), Signature.Parse("n"), [], out _, out _, out _));
    }

    [Fact]
    public void TryDecode_DecodesFirstOfTwoMessages()
    {
        var first = MessageEncoder.Encode(1, 1, Signature.Parse("n"), [WireArgument.NewId(2)]);
        var second = MessageEncoder.Encode(1, 0, Signature.Parse(""), []);
        var buffer = first.Bytes.Concat(second.Bytes).ToArray();

        Assert.True(MessageDecoder.TryDecode(buffer, Signature.Parse("n"), [], out var message, out var consumed, out _));
        Assert.Equal(first.Bytes.Length, consumed);
        Assert.Equal(2u, message.Arguments[0].AsNewId());
    }

    [Fact]
    public void TryDecode_RejectsInvalidMessageSizes()
    {
        // Size 6: too small and not a multiple of four.
        var tooSmall = RawMessage(1, 0, 6);
        Assert.Throws<WireProtocolException>(() => MessageDecoder.TryDecode(tooSmall, Signature.Parse(""), [], out _, out _, out _));

        // Size 9: multiple-of-four rule.
        var misaligned = RawMessage(1, 0, 9);
        Assert.Throws<WireProtocolException>(() => MessageDecoder.TryDecode(misaligned, Signature.Parse(""), [], out _, out _, out _));
    }

    [Fact]
    public void TryDecode_RejectsSizeMismatchWithSignature()
    {
        // Declared 16 bytes but the signature only accounts for 12.
        var message = RawMessage(1, 0, 16, 5u);
        Assert.Throws<WireProtocolException>(() => MessageDecoder.TryDecode(message, Signature.Parse("i"), [], out _, out _, out _));

        // Declared 8 bytes but the signature needs 12.
        var truncated = RawMessage(1, 0, 8);
        Assert.Throws<WireProtocolException>(() => MessageDecoder.TryDecode(truncated, Signature.Parse("i"), [], out _, out _, out _));
    }

    [Fact]
    public void TryDecode_RejectsNullStringForNonNullable()
    {
        var message = RawMessage(1, 0, 12, 0u);
        Assert.Throws<WireProtocolException>(() => MessageDecoder.TryDecode(message, Signature.Parse("s"), [], out _, out _, out _));
    }

    [Fact]
    public void TryDecode_RejectsInteriorNul()
    {
        var message = RawMessage(1, 0, 16, 4u, 0x0062_0061u); // "a\0b\0"
        Assert.Throws<WireProtocolException>(() => MessageDecoder.TryDecode(message, Signature.Parse("s"), [], out _, out _, out _));
    }

    [Fact]
    public void TryDecode_RejectsUnterminatedString()
    {
        var message = RawMessage(1, 0, 16, 4u, 0x6463_6261u); // "abcd", no NUL
        Assert.Throws<WireProtocolException>(() => MessageDecoder.TryDecode(message, Signature.Parse("s"), [], out _, out _, out _));
    }

    [Fact]
    public void TryDecode_RejectsNewIdZero()
    {
        var message = RawMessage(1, 0, 12, 0u);
        Assert.Throws<WireProtocolException>(() => MessageDecoder.TryDecode(message, Signature.Parse("n"), [], out _, out _, out _));
    }

    [Fact]
    public void TryDecode_RejectsMissingFileDescriptor()
    {
        var message = RawMessage(1, 0, 8);
        Assert.Throws<WireProtocolException>(() => MessageDecoder.TryDecode(message, Signature.Parse("h"), [], out _, out _, out _));
    }

    [Fact]
    public void Encode_RejectsNullStringForNonNullable()
    {
        Assert.Throws<ArgumentException>(
            () => MessageEncoder.Encode(1, 0, Signature.Parse("s"), [WireArgument.NullString()]));
    }

    [Fact]
    public void Encode_RejectsInteriorNul()
    {
        Assert.Throws<ArgumentException>(
            () => MessageEncoder.Encode(1, 0, Signature.Parse("s"), [WireArgument.String("a\0b")]));
    }

    [Fact]
    public void Encode_RejectsKindMismatch()
    {
        Assert.Throws<ArgumentException>(
            () => MessageEncoder.Encode(1, 0, Signature.Parse("u"), [WireArgument.String("x")]));
    }

    [Fact]
    public void Encode_RejectsArgumentCountMismatch()
    {
        Assert.Throws<ArgumentException>(() => MessageEncoder.Encode(1, 0, Signature.Parse("uu"), [WireArgument.UInt32(1)]));
    }

    [Fact]
    public void Encode_RejectsOversizeMessage()
    {
        var huge = WireArgument.Array(new byte[70_000]);
        Assert.Throws<ArgumentException>(() => MessageEncoder.Encode(1, 0, Signature.Parse("a"), [huge]));
    }

    [Fact]
    public void Decodes_Bind_GoldenBytes()
    {
        byte[] wire =
        [
            0x02, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x20, 0x00,
            0x07, 0x00, 0x00, 0x00,
            0x07, 0x00, 0x00, 0x00,
            0x77, 0x6C, 0x5F, 0x73,
            0x68, 0x6D, 0x00, 0x00,
            0x01, 0x00, 0x00, 0x00,
            0x2A, 0x00, 0x00, 0x00,
        ];

        Assert.True(MessageDecoder.TryDecode(wire, Signature.Parse("usun"), [], out var message, out var consumed, out _));
        Assert.Equal(32, consumed);
        Assert.Equal(2u, message.ObjectId);
        Assert.Equal(0, message.Opcode);
        Assert.Equal(7u, message.Arguments[0].AsUInt32());
        Assert.Equal("wl_shm", message.Arguments[1].AsString());
        Assert.Equal(1u, message.Arguments[2].AsUInt32());
        Assert.Equal(42u, message.Arguments[3].AsNewId());
    }

    [Fact]
    public void RoundTrips_EmptyString()
    {
        var message = RoundTrip(1, 5, "s", WireArgument.String(string.Empty));
        Assert.Equal(string.Empty, message.Arguments[0].AsString());
    }

    [Fact]
    public void RoundTrips_NullableObject()
    {
        var message = RoundTrip(1, 6, "?o", WireArgument.Object(0));
        Assert.Equal(0u, message.Arguments[0].AsObject());
    }

    [Fact]
    public void Encode_RejectsNullObjectForNonNullable()
    {
        Assert.Throws<ArgumentException>(
            () => MessageEncoder.Encode(1, 0, Signature.Parse("o"), [WireArgument.Object(0)]));
    }

    [Fact]
    public void TryDecode_RejectsNullObjectForNonNullable()
    {
        var message = RawMessage(1, 0, 12, 0u);
        Assert.Throws<WireProtocolException>(() => MessageDecoder.TryDecode(message, Signature.Parse("o"), [], out _, out _, out _));
    }

    [Fact]
    public void TryDecode_RejectsStringExceedingBounds()
    {
        var message = RawMessage(1, 0, 12, 0xFFFFFFFFu);
        Assert.Throws<WireProtocolException>(() => MessageDecoder.TryDecode(message, Signature.Parse("s"), [], out _, out _, out _));
    }

    [Fact]
    public void TryDecode_RejectsArrayExceedingBounds()
    {
        var message = RawMessage(1, 0, 12, 0xFFFFFFFFu);
        Assert.Throws<WireProtocolException>(() => MessageDecoder.TryDecode(message, Signature.Parse("a"), [], out _, out _, out _));
    }

    [Fact]
    public void TryDecode_RejectsInvalidUtf8()
    {
        // Length 3: bytes C3 28 00 — an invalid UTF-8 sequence plus the terminator.
        var message = RawMessage(1, 0, 16, 3u, 0x0000_28C3u);
        Assert.Throws<WireProtocolException>(() => MessageDecoder.TryDecode(message, Signature.Parse("s"), [], out _, out _, out _));
    }

    [Fact]
    public void Encode_RejectsInvalidUtf16()
    {
        Assert.Throws<ArgumentException>(
            () => MessageEncoder.Encode(1, 0, Signature.Parse("s"), [WireArgument.String("\uD800")]));
    }

    private static DecodedMessage RoundTrip(uint objectId, ushort opcode, string signature, params WireArgument[] arguments)
    {
        var types = Signature.Parse(signature);
        var encoded = MessageEncoder.Encode(objectId, opcode, types, arguments);

        Assert.True(MessageDecoder.TryDecode(
            encoded.Bytes,
            types,
            encoded.FileDescriptors.AsSpan(),
            out var message,
            out var consumed,
            out var descriptorsConsumed));
        Assert.Equal(encoded.Bytes.Length, consumed);
        Assert.Equal(encoded.FileDescriptors.Length, descriptorsConsumed);
        Assert.Equal(objectId, message.ObjectId);
        Assert.Equal(opcode, message.Opcode);
        return message;
    }

    /// <summary>Builds a message with an explicit size field, bypassing the encoder, for malformed-input tests.</summary>
    private static byte[] RawMessage(uint objectId, ushort opcode, ushort declaredSize, params uint[] words)
    {
        // The buffer is grown to the declared size so decoding reaches the
        // argument checks instead of reporting "need more data".
        var buffer = new byte[Math.Max(FrameHeader.ByteSize + (4 * words.Length), declaredSize)];
        new FrameHeader(objectId, opcode, declaredSize).Write(buffer);
        for (var i = 0; i < words.Length; i++)
        {
            Assert.True(BitConverter.TryWriteBytes(buffer.AsSpan(FrameHeader.ByteSize + (4 * i)), words[i]));
        }

        return buffer;
    }
}
