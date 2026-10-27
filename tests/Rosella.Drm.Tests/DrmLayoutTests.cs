using System.Runtime.InteropServices;

namespace Rosella.Drm.Tests;

/// <summary>
/// Pins the native struct layouts to x86_64 kernel ABI values taken from
/// include/uapi/drm/drm.h and drm_mode.h. A failure here invalidates every
/// ioctl in Rosella.Drm.
/// </summary>
public class DrmLayoutTests
{
    [Theory]
    [InlineData(typeof(DrmVersion), 64)]
    [InlineData(typeof(DrmGetCap), 16)]
    [InlineData(typeof(DrmModeCardRes), 64)]
    [InlineData(typeof(DrmModeGetConnector), 80)]
    [InlineData(typeof(DrmModeModeInfo), 68)]
    [InlineData(typeof(DrmModeCrtc), 104)]
    [InlineData(typeof(DrmModeGetEncoder), 20)]
    [InlineData(typeof(DrmModeFbCmd), 28)]
    [InlineData(typeof(DrmModeCrtcPageFlip), 24)]
    [InlineData(typeof(DrmModeCreateDumb), 32)]
    [InlineData(typeof(DrmModeMapDumb), 16)]
    [InlineData(typeof(DrmModeDestroyDumb), 4)]
    [InlineData(typeof(DrmEvent), 8)]
    [InlineData(typeof(DrmEventVblank), 32)]
    public void StructSizes_MatchKernelAbi(Type type, int expectedSize)
    {
        Assert.Equal(expectedSize, Marshal.SizeOf(type));
    }

    [Theory]
    [InlineData(typeof(DrmVersion), "NameLen", 16)]
    [InlineData(typeof(DrmVersion), "Name", 24)]
    [InlineData(typeof(DrmVersion), "Desc", 56)]
    [InlineData(typeof(DrmModeCardRes), "CrtcIdPtr", 8)]
    [InlineData(typeof(DrmModeCardRes), "CountCrtcs", 36)]
    [InlineData(typeof(DrmModeCardRes), "MinHeight", 56)]
    [InlineData(typeof(DrmModeGetConnector), "ModesPtr", 8)]
    [InlineData(typeof(DrmModeGetConnector), "EncoderId", 44)]
    [InlineData(typeof(DrmModeGetConnector), "Connection", 60)]
    [InlineData(typeof(DrmModeGetConnector), "Pad", 76)]
    [InlineData(typeof(DrmModeModeInfo), "VScan", 22)]
    [InlineData(typeof(DrmModeModeInfo), "VRefresh", 24)]
    [InlineData(typeof(DrmModeModeInfo), "Type", 32)]
    [InlineData(typeof(DrmModeModeInfo), "Name", 36)]
    [InlineData(typeof(DrmModeCrtc), "CrtcId", 12)]
    [InlineData(typeof(DrmModeCrtc), "ModeValid", 32)]
    [InlineData(typeof(DrmModeCrtc), "Mode", 36)]
    [InlineData(typeof(DrmModeGetEncoder), "PossibleCrtcs", 12)]
    [InlineData(typeof(DrmModeFbCmd), "Handle", 24)]
    [InlineData(typeof(DrmModeCrtcPageFlip), "Flags", 8)]
    [InlineData(typeof(DrmModeCrtcPageFlip), "UserData", 16)]
    [InlineData(typeof(DrmModeCreateDumb), "Handle", 16)]
    [InlineData(typeof(DrmModeCreateDumb), "Pitch", 20)]
    [InlineData(typeof(DrmModeCreateDumb), "Size", 24)]
    [InlineData(typeof(DrmModeMapDumb), "Offset", 8)]
    [InlineData(typeof(DrmEventVblank), "UserData", 8)]
    [InlineData(typeof(DrmEventVblank), "Sequence", 24)]
    [InlineData(typeof(DrmEventVblank), "CrtcId", 28)]
    public void FieldOffsets_MatchKernelAbi(Type type, string field, int expectedOffset)
    {
        Assert.Equal(expectedOffset, Marshal.OffsetOf(type, field).ToInt32());
    }
}
