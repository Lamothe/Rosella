using System.Runtime.InteropServices;

namespace Rosella.Input;

/// <summary>Constants and ioctl request encoding from the evdev UAPI.</summary>
public static class InputConstants
{
    // EV_*
    public const ushort EventSyn = 0x00;
    public const ushort EventKey = 0x01;
    public const ushort EventRelative = 0x02;
    public const ushort EventAbsolute = 0x03;

    // REL_*
    public const ushort RelativeX = 0x00;
    public const ushort RelativeY = 0x01;
    public const ushort RelativeHWheel = 0x06;
    public const ushort RelativeWheel = 0x08;

    // KEY_* (the subset Rosella binds or routes around)
    public const ushort KeyEsc = 1;
    public const ushort Key1 = 2;
    public const ushort Key2 = 3;
    public const ushort Key3 = 4;
    public const ushort Key4 = 5;
    public const ushort KeyEnter = 28;
    public const ushort KeyA = 30;
    public const ushort KeyLeftCtrl = 29;
    public const ushort KeyLeftShift = 42;
    public const ushort KeyRightShift = 54;
    public const ushort KeyLeftAlt = 56;
    public const ushort KeyCapsLock = 58;
    public const ushort KeyF4 = 62;
    public const ushort KeyNumLock = 69;
    public const ushort KeyRightCtrl = 97;
    public const ushort KeyRightAlt = 100;
    public const ushort KeyUp = 103;
    public const ushort KeyDown = 108;
    public const ushort KeyLeftMeta = 125;
    public const ushort KeyRightMeta = 126;

    // BTN_* (linux/input-event-codes.h)
    public const ushort ButtonLeft = 0x110;
    public const ushort ButtonRight = 0x111;
    public const ushort ButtonMiddle = 0x112;

    // Multitouch (protocol B) — bcm5974-class trackpads report finger
    // positions here instead of relative motion (include/uapi/linux/input-event-codes.h).
    public const ushort AbsMtSlot = 0x2f;
    public const ushort AbsMtTrackingId = 0x39;
    public const ushort AbsMtPositionX = 0x35;
    public const ushort AbsMtPositionY = 0x36;
    public const int TrackingIdUp = -1;

    // EV_KEY values
    public const int KeyReleased = 0;
    public const int KeyPressed = 1;
    public const int KeyRepeated = 2;

    private const uint IoctlWrite = 1;
    private const uint IoctlRead = 2;

    /// <summary>The IOC macro family: direction in the top bits, size next, type, then number.</summary>
    private static uint Ioctl(uint direction, uint type, uint number, uint size)
    {
        return (direction << 30) | (size << 16) | (type << 8) | number;
    }

    internal static uint Read(char type, uint number, uint size) => Ioctl(IoctlRead, type, number, size);

    internal static uint Write(char type, uint number, uint size) => Ioctl(IoctlWrite, type, number, size);

    // EVIOCG* request codes ('E' = 0x45), struct input_id = 8 bytes, int = 4.
    internal static readonly uint IoctlGetId = Read('E', 0x02, Size<InputId>());
    internal static readonly uint IoctlGetName = Read('E', 0x06, 256);
    internal static readonly uint IoctlGrab = Write('E', 0x90, 4);

    /// <summary>EVIOCGBIT(ev, len): capability bitmap for one event type.</summary>
    internal static uint IoctlGetBits(int eventType, uint length) => Read('E', 0x20 + (uint)eventType, length);

    private static uint Size<T>() => (uint)Marshal.SizeOf<T>();
}
