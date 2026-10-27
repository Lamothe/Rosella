namespace Rosella.Drm.Tests;

/// <summary>
/// Pins the ioctl request codes to values computed from the kernel headers:
/// _IOWR('d', number, struct) / _IOW('d', number, struct) on x86_64.
/// </summary>
public class DrmIoctlTests
{
    [Fact]
    public void RequestCodes_MatchKernelAbi()
    {
        Assert.Equal(0xC0406400u, DrmConstants.IoctlVersion);
        Assert.Equal(0xC010640Cu, DrmConstants.IoctlGetCap);
        Assert.Equal(0xC04064A0u, DrmConstants.IoctlModeGetResources);
        Assert.Equal(0xC06864A1u, DrmConstants.IoctlModeGetCrtc);
        Assert.Equal(0xC06864A2u, DrmConstants.IoctlModeSetCrtc);
        Assert.Equal(0xC01464A6u, DrmConstants.IoctlModeGetEncoder);
        Assert.Equal(0xC05064A7u, DrmConstants.IoctlModeGetConnector);
        Assert.Equal(0xC01C64AEu, DrmConstants.IoctlModeAddFb);
        Assert.Equal(0xC00464AFu, DrmConstants.IoctlModeRemoveFb);
        Assert.Equal(0xC01864B0u, DrmConstants.IoctlModePageFlip);
        Assert.Equal(0xC02064B2u, DrmConstants.IoctlModeCreateDumb);
        Assert.Equal(0xC01064B3u, DrmConstants.IoctlModeMapDumb);
        Assert.Equal(0xC00464B4u, DrmConstants.IoctlModeDestroyDumb);
    }
}
