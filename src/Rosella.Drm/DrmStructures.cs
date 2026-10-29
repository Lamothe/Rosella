using System.Runtime.InteropServices;

namespace Rosella.Drm;

// Native structures from the kernel's DRM UAPI (include/uapi/drm/drm.h and
// drm_mode.h), laid out exactly as x86_64 C lays them out. Layouts are pinned
// by golden-value tests in Rosella.Drm.Tests — if those fail, the kernel ABI
// has been violated and every ioctl here is suspect.

[StructLayout(LayoutKind.Sequential)]
internal struct DrmVersion
{
    internal int VersionMajor;
    internal int VersionMinor;
    internal int VersionPatchlevel;
    internal ulong NameLen;
    internal nint Name;
    internal ulong DateLen;
    internal nint Date;
    internal ulong DescLen;
    internal nint Desc;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DrmGetCap
{
    internal ulong Capability;
    internal ulong Value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DrmModeCardRes
{
    internal ulong FbIdPtr;
    internal ulong CrtcIdPtr;
    internal ulong ConnectorIdPtr;
    internal ulong EncoderIdPtr;
    internal uint CountFbs;
    internal uint CountCrtcs;
    internal uint CountConnectors;
    internal uint CountEncoders;
    internal uint MinWidth;
    internal uint MaxWidth;
    internal uint MinHeight;
    internal uint MaxHeight;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DrmModeGetConnector
{
    internal ulong EncodersPtr;
    internal ulong ModesPtr;
    internal ulong PropsPtr;
    internal ulong PropValuesPtr;
    internal uint CountModes;
    internal uint CountProps;
    internal uint CountEncoders;
    internal uint EncoderId;
    internal uint ConnectorId;
    internal uint ConnectorType;
    internal uint ConnectorTypeId;
    internal uint Connection;
    internal uint MmWidth;
    internal uint MmHeight;
    internal uint Subpixel;
    internal uint Pad;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DrmModeModeInfo
{
    internal uint Clock;
    internal ushort HDisplay;
    internal ushort HSyncStart;
    internal ushort HSyncEnd;
    internal ushort HTotal;
    internal ushort HSkew;
    internal ushort VDisplay;
    internal ushort VSyncStart;
    internal ushort VSyncEnd;
    internal ushort VTotal;
    internal ushort VScan;
    internal uint VRefresh;
    internal uint Flags;
    internal uint Type;
    internal fixed byte Name[DrmConstants.DisplayModeNameLength];
}

[StructLayout(LayoutKind.Sequential)]
internal struct DrmModeCrtc
{
    internal ulong SetConnectorsPtr;
    internal uint CountConnectors;
    internal uint CrtcId;
    internal uint FbId;
    internal uint X;
    internal uint Y;
    internal uint GammaSize;
    internal uint ModeValid;
    internal DrmModeModeInfo Mode;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DrmModeGetEncoder
{
    internal uint EncoderId;
    internal uint EncoderType;
    internal uint CrtcId;
    internal uint PossibleCrtcs;
    internal uint PossibleClones;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DrmModeFbCmd
{
    internal uint FbId;
    internal uint Width;
    internal uint Height;
    internal uint Pitch;
    internal uint Bpp;
    internal uint Depth;
    internal uint Handle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DrmModeCrtcPageFlip
{
    internal uint CrtcId;
    internal uint FbId;
    internal uint Flags;
    internal uint Reserved;
    internal ulong UserData;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DrmModeCreateDumb
{
    internal uint Height;
    internal uint Width;
    internal uint Bpp;
    internal uint Flags;
    internal uint Handle;
    internal uint Pitch;
    internal ulong Size;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DrmModeMapDumb
{
    internal uint Handle;
    internal uint Pad;
    internal ulong Offset;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DrmModeDestroyDumb
{
    internal uint Handle;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DrmEvent
{
    internal uint Type;
    internal uint Length;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DrmEventVblank
{
    internal DrmEvent Base;
    internal ulong UserData;
    internal uint TvSec;
    internal uint TvUsec;
    internal uint Sequence;
    internal uint CrtcId;
}

// Modeset base-object properties (drm_mode.h:602-638) — the DPMS path. The
// property id is *not* stable across drivers, so the connector's properties
// are enumerated and the one named "DPMS" is used.

[StructLayout(LayoutKind.Sequential)]
internal struct DrmModeConnectorSetProperty
{
    internal ulong Value;
    internal uint PropId;
    internal uint ConnectorId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DrmModeObjGetProperties
{
    internal ulong PropsPtr;
    internal ulong PropValuesPtr;
    internal uint CountProps;
    internal uint ObjId;
    internal uint ObjType;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DrmModeGetProperty
{
    internal ulong ValuesPtr;
    internal ulong EnumBlobPtr;
    internal uint PropId;
    internal uint Flags;
    internal fixed byte Name[DrmConstants.PropNameLength];
    internal uint CountValues;
    internal uint CountEnumBlobs;
}
