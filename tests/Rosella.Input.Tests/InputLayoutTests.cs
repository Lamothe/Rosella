using System.Runtime.InteropServices;

namespace Rosella.Input.Tests;

/// <summary>
/// Pins the native struct layouts and ioctl request codes to x86_64 kernel
/// ABI values from include/uapi/linux/input.h. A failure here invalidates
/// every evdev operation in Rosella.Input.
/// </summary>
public class InputLayoutTests
{
    [Fact]
    public void InputEvent_Is24Bytes()
    {
        Assert.Equal(InputEvent.Size, 24);
        Assert.Equal(24, Marshal.SizeOf<InputEvent>());
    }

    [Fact]
    public void InputId_Is8Bytes()
    {
        Assert.Equal(8, Marshal.SizeOf<InputId>());
    }

    [Fact]
    public void RequestCodes_MatchKernelAbi()
    {
        // _IOR('E', 0x02, struct input_id) / _IOW('E', 0x90, int).
        Assert.Equal(0x80084502u, InputConstants.IoctlGetId);
        Assert.Equal(0x40044590u, InputConstants.IoctlGrab);

        // EVIOCGNAME(256) = _IOC(_IOC_READ, 'E', 0x06, 256).
        Assert.Equal(0x81004506u, InputConstants.IoctlGetName);

        // EVIOCGBIT(ev, len) = _IOC(_IOC_READ, 'E', 0x20 + ev, len).
        Assert.Equal(0x80084520u, InputConstants.IoctlGetBits(0, 8));
        Assert.Equal(0x80204521u, InputConstants.IoctlGetBits(1, 32));
    }

    [Fact]
    public void EvdevEventCodes_MatchKernelAbi()
    {
        Assert.Equal(0, InputConstants.EventSyn);
        Assert.Equal(1, InputConstants.EventKey);
        Assert.Equal(2, InputConstants.EventRelative);
        Assert.Equal(0x110, InputConstants.ButtonLeft);
        Assert.Equal(1, InputConstants.KeyEsc);
        Assert.Equal(62, InputConstants.KeyF4);
        Assert.Equal(125, InputConstants.KeyLeftMeta);
    }
}
