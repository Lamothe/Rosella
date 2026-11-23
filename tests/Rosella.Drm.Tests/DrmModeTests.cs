using System.Runtime.InteropServices;
using System.Text;

namespace Rosella.Drm.Tests;

public class DrmModeTests
{
    [Fact]
    public void NativeConversion_RoundTrips()
    {
        var mode = new DrmMode(148500, 1920, 2008, 2052, 2200, 0, 1080, 1084, 1089, 1125, 0, 60, 0, DrmConstants.ModeTypePreferred, "1920x1080");

        var roundTripped = DrmMode.FromNative(mode.ToNative());
        Assert.Equal(mode, roundTripped);
    }

    [Fact]
    public void FromNative_StopsNameAtNul()
    {
        var native = new DrmModeModeInfo();
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref native, 1));
        Encoding.ASCII.GetBytes("1280x720").AsSpan().CopyTo(bytes.Slice(36, DrmConstants.DisplayModeNameLength));

        Assert.Equal("1280x720", DrmMode.FromNative(native).Name);
    }

    [Fact]
    public void ToNative_TruncatesOverlongNames()
    {
        var mode = new DrmMode(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, new string('x', 64));
        var native = mode.ToNative();

        // The name field holds 32 bytes; no terminator is required.
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref native, 1));
        var name = bytes.Slice(36, DrmConstants.DisplayModeNameLength);
        Assert.All(name.ToArray(), value => Assert.Equal((byte)'x', value));
    }
}
