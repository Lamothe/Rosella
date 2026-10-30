using System.Runtime.InteropServices;

namespace Rosella.Drm;

/// <summary>Constants and ioctl request encoding from the DRM UAPI.</summary>
public static class DrmConstants
{
    /// <summary>Size of the mode name field (DRM_DISPLAY_MODE_LEN).</summary>
    public const int DisplayModeNameLength = 32;

    // DRM_CAP_*
    public const ulong CapDumbBuffer = 0x1;

    // DRM_EVENT_*
    public const uint EventVblank = 0x01;
    public const uint EventFlipComplete = 0x02;

    // DRM_MODE_PAGE_FLIP_*
    public const uint PageFlipEvent = 0x01;

    // DRM_MODE_TYPE_PREFERRED
    public const uint ModeTypePreferred = 1 << 3;

    // DRM_MODE_OBJECT_* (drm_mode.h: object types for the modeset base object
    // property ioctls; the kernel compares the full magic value, not a small int).
    public const uint ObjectConnector = 0xc0c0c0c0;

    // DRM_MODE_DPMS_* (drm_mode.h:129-132)
    public const uint DpmsOn = 0;
    public const uint DpmsStandby = 1;
    public const uint DpmsSuspend = 2;
    public const uint DpmsOff = 3;

    /// <summary>DRM_PROP_NAME_LEN: the name field of drm_mode_get_property.</summary>
    public const int PropNameLength = 32;

    // enum drm_connector_status
    public const uint ConnectorConnected = 1;
    public const uint ConnectorDisconnected = 2;
    public const uint ConnectorUnknown = 3;

    // DRM_MODE_CONNECTOR_* (the subset Rosella can name)
    public const uint ConnectorTypeDviD = 3;
    public const uint ConnectorTypeLvds = 7;
    public const uint ConnectorTypeDisplayPort = 10;
    public const uint ConnectorTypeHdmiA = 11;
    public const uint ConnectorTypeHdmiB = 12;
    public const uint ConnectorTypeEdp = 14;

    private const uint IoctlWrite = 1;
    private const uint IoctlRead = 2;

    /// <summary>The IOC macro family: direction in the top bits, size next, type, then number.</summary>
    private static uint Ioctl(uint direction, uint type, uint number, uint size)
    {
        return (direction << 30) | (size << 16) | (type << 8) | number;
    }

    internal static uint ReadWrite(char type, uint number, uint size) => Ioctl(IoctlRead | IoctlWrite, type, number, size);

    private static uint Size<T>() => (uint)Marshal.SizeOf<T>();

    // DRM_IOCTL_* request codes ('d' = 0x64 = DRM_IOCTL_BASE).
    internal static readonly uint IoctlVersion = ReadWrite('d', 0x00, Size<DrmVersion>());
    internal static readonly uint IoctlGetCap = ReadWrite('d', 0x0c, Size<DrmGetCap>());
    internal static readonly uint IoctlModeGetResources = ReadWrite('d', 0xA0, Size<DrmModeCardRes>());
    internal static readonly uint IoctlModeGetCrtc = ReadWrite('d', 0xA1, Size<DrmModeCrtc>());
    internal static readonly uint IoctlModeSetCrtc = ReadWrite('d', 0xA2, Size<DrmModeCrtc>());
    internal static readonly uint IoctlModeGetEncoder = ReadWrite('d', 0xA6, Size<DrmModeGetEncoder>());
    internal static readonly uint IoctlModeGetConnector = ReadWrite('d', 0xA7, Size<DrmModeGetConnector>());
    internal static readonly uint IoctlModeAddFb = ReadWrite('d', 0xAE, Size<DrmModeFbCmd>());
    internal static readonly uint IoctlModeRemoveFb = ReadWrite('d', 0xAF, Size<uint>());
    internal static readonly uint IoctlModePageFlip = ReadWrite('d', 0xB0, Size<DrmModeCrtcPageFlip>());
    internal static readonly uint IoctlModeCreateDumb = ReadWrite('d', 0xB2, Size<DrmModeCreateDumb>());
    internal static readonly uint IoctlModeMapDumb = ReadWrite('d', 0xB3, Size<DrmModeMapDumb>());
    internal static readonly uint IoctlModeDestroyDumb = ReadWrite('d', 0xB4, Size<DrmModeDestroyDumb>());

    // Modeset base-object properties: how DPMS (and every other connector
    // property) is read and written. drm.h:1201-1202, 1247.
    internal static readonly uint IoctlModeGetProperty = ReadWrite('d', 0xAA, Size<DrmModeGetProperty>());
    internal static readonly uint IoctlModeSetProperty = ReadWrite('d', 0xAB, Size<DrmModeConnectorSetProperty>());
    internal static readonly uint IoctlModeObjGetProperties = ReadWrite('d', 0xB9, Size<DrmModeObjGetProperties>());

    // DRM_IO (no payload): master handover across VT switches.
    internal static readonly uint IoctlSetMaster = Ioctl(0, 'd', 0x1E, 0);
    internal static readonly uint IoctlDropMaster = Ioctl(0, 'd', 0x1F, 0);
}

/// <summary>Thrown when the kernel rejects a DRM operation.</summary>
public sealed class DrmException : Exception
{
    public DrmException(string operation, int error)
        : base($"{operation} failed with errno {error}")
    {
        Operation = operation;
        Error = error;
    }

    public string Operation { get; }

    public int Error { get; }
}
