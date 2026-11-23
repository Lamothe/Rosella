namespace Rosella.Protocol.Tests;

public class FrameHeaderTests
{
    [Fact]
    public void RoundTrips_ObjectId_Opcode_AndSize()
    {
        var header = new FrameHeader(42, 7, 1024);
        Span<byte> buffer = stackalloc byte[FrameHeader.ByteSize];
        header.Write(buffer);

        Assert.Equal(header, FrameHeader.Read(buffer));
    }

    [Fact]
    public void Encodes_SizeInHighHalf_OpcodeInLowHalf()
    {
        // wl_display.get_registry: object 1, opcode 1, 12-byte message.
        Span<byte> buffer = stackalloc byte[FrameHeader.ByteSize];
        new FrameHeader(1, 1, 12).Write(buffer);

        Assert.Equal(1u, BitConverter.ToUInt32(buffer));
        Assert.Equal(0x000C_0001u, BitConverter.ToUInt32(buffer.Slice(4)));
    }

    [Fact]
    public void Read_RejectsShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => FrameHeader.Read(new byte[4]));
    }

    [Fact]
    public void Write_RejectsShortBuffer()
    {
        Assert.Throws<ArgumentException>(() => new FrameHeader(1, 1, 8).Write(new byte[4]));
    }
}
