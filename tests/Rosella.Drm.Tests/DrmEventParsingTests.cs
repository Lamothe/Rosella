using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Rosella.Drm.Tests;

/// <summary>
/// Event parsing from crafted bytes, including hostile lengths, and the
/// version-string clamp (the kernel reports full lengths while copying less).
/// </summary>
public class DrmEventParsingTests
{
    private static byte[] EventBytes(uint type, uint length, ulong userData = 0, uint sequence = 0)
    {
        var buffer = new byte[32];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), type);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(4, 4), length);
        BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(8, 8), userData);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(24, 4), sequence);
        return buffer;
    }

    [Fact]
    public void ParseEvents_ReadsMultipleEventsPerBatch()
    {
        var batch = EventBytes(DrmConstants.EventFlipComplete, 32, userData: 7, sequence: 1)
            .Concat(EventBytes(DrmConstants.EventFlipComplete, 32, userData: 8, sequence: 2))
            .ToArray();

        var events = DrmDevice.ParseEvents(batch);
        Assert.Equal(2, events.Count);
        Assert.Equal(7ul, events[0].UserData);
        Assert.Equal(1u, events[0].Sequence);
        Assert.Equal(8ul, events[1].UserData);
        Assert.Equal(2u, events[1].Sequence);
    }

    [Fact]
    public void ParseEvents_SurvivesHostileLength()
    {
        var batch = EventBytes(DrmConstants.EventFlipComplete, 0xFFFFFFFF, userData: 7);
        var events = DrmDevice.ParseEvents(batch);
        Assert.Empty(events);
    }

    [Fact]
    public void ParseEvents_StopsOnSubHeaderLength()
    {
        var batch = EventBytes(DrmConstants.EventFlipComplete, 4);
        Assert.Empty(DrmDevice.ParseEvents(batch));
    }

    [Fact]
    public void ParseEvents_IgnoresTruncatedTail()
    {
        var batch = EventBytes(DrmConstants.EventFlipComplete, 32, userData: 7)
            .Concat(new byte[] { 0x02, 0x00, 0x00 })
            .ToArray();

        var events = DrmDevice.ParseEvents(batch);
        Assert.Single(events);
        Assert.Equal(7ul, events[0].UserData);
    }

    [Fact]
    public void ParseEvents_ReportsUnknownTypesWithoutDetails()
    {
        var batch = EventBytes(0x99, 32, userData: 7, sequence: 5);
        var events = DrmDevice.ParseEvents(batch);
        Assert.Single(events);
        Assert.Equal(0x99u, events[0].Type);
        Assert.Equal(0ul, events[0].UserData);
        Assert.Equal(0u, events[0].Sequence);
    }

    [Fact]
    public void ReadVersionString_ClampsLengthAndTrimsAtNul()
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            var text = Encoding.ASCII.GetBytes("nvidia-drm");
            Marshal.Copy(text, 0, buffer, text.Length);
            for (var i = text.Length; i < 256; i++)
            {
                Marshal.WriteByte(buffer, i, 0xEE); // garbage past the terminator
            }

            Marshal.WriteByte(buffer, text.Length, 0);

            Assert.Equal("nvidia-drm", DrmDevice.ReadVersionString(buffer, 9999));
            Assert.Equal("nvidia-drm", DrmDevice.ReadVersionString(buffer, 10));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
