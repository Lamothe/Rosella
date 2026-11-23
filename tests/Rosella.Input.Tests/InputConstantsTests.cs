namespace Rosella.Input.Tests;

/// <summary>
/// Golden pins against include/uapi/linux/input-event-codes.h (kernel UAPI).
/// Tests that use these constants are self-consistent even when a value is
/// wrong, so the values themselves must be pinned to the kernel's — the same
/// discipline as the DRM ioctl golden tests. 0x30/0x31 were ABS_MT_TOUCH_*
/// while the trackpad reports positions at 0x35/0x36; nothing caught it.
/// </summary>
public sealed class InputConstantsTests
{
    [Fact]
    public void EventTypesMatchKernelUapi()
    {
        Assert.Equal(0x0000, InputConstants.EventSyn);
        Assert.Equal(0x0001, InputConstants.EventKey);
        Assert.Equal(0x0002, InputConstants.EventRelative);
        Assert.Equal(0x0003, InputConstants.EventAbsolute);
    }

    [Fact]
    public void RelativeCodesMatchKernelUapi()
    {
        Assert.Equal(0x00, InputConstants.RelativeX);
        Assert.Equal(0x01, InputConstants.RelativeY);
        Assert.Equal(0x06, InputConstants.RelativeHWheel);
        Assert.Equal(0x08, InputConstants.RelativeWheel);
    }

    [Fact]
    public void KeyCodesMatchKernelUapi()
    {
        Assert.Equal(1, InputConstants.KeyEsc);
        Assert.Equal(28, InputConstants.KeyEnter);
        Assert.Equal(30, InputConstants.KeyA);
        Assert.Equal(29, InputConstants.KeyLeftCtrl);
        Assert.Equal(42, InputConstants.KeyLeftShift);
        Assert.Equal(54, InputConstants.KeyRightShift);
        Assert.Equal(56, InputConstants.KeyLeftAlt);
        Assert.Equal(58, InputConstants.KeyCapsLock);
        Assert.Equal(62, InputConstants.KeyF4);
        Assert.Equal(69, InputConstants.KeyNumLock);
        Assert.Equal(97, InputConstants.KeyRightCtrl);
        Assert.Equal(100, InputConstants.KeyRightAlt);
        Assert.Equal(125, InputConstants.KeyLeftMeta);
        Assert.Equal(126, InputConstants.KeyRightMeta);
    }

    [Fact]
    public void ButtonCodesMatchKernelUapi()
    {
        Assert.Equal(0x110, InputConstants.ButtonLeft);
        Assert.Equal(0x111, InputConstants.ButtonRight);
        Assert.Equal(0x112, InputConstants.ButtonMiddle);
    }

    [Fact]
    public void MultitouchCodesMatchKernelUapi()
    {
        Assert.Equal(0x2f, InputConstants.AbsMtSlot);
        Assert.Equal(0x39, InputConstants.AbsMtTrackingId);
        Assert.Equal(0x35, InputConstants.AbsMtPositionX);
        Assert.Equal(0x36, InputConstants.AbsMtPositionY);
        Assert.Equal(-1, InputConstants.TrackingIdUp);
    }
}
