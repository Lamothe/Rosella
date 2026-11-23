using System.Runtime.InteropServices;

namespace Rosella.GpuProbe;

/// <summary>
/// libdrm's mode-setting helpers. Everything here is non-master: dumb buffer
/// allocation, framebuffer creation and PRIME import. There is no page flip,
/// no modeset and no drmSetMaster call anywhere in this probe.
/// </summary>
internal static unsafe class LibDrm
{
    private const string Lib = "libdrm.so.2";

    internal const uint FormatXrgb8888 = 0x34325258;
    internal const uint ModeFbModifiers = 1 << 1;

    [DllImport(Lib, EntryPoint = "drmModeCreateDumbBuffer")]
    internal static extern int ModeCreateDumbBuffer(int fd, uint width, uint height, uint bpp, uint flags, out uint handle, out uint pitch, out ulong size);

    [DllImport(Lib, EntryPoint = "drmModeMapDumbBuffer")]
    internal static extern int ModeMapDumbBuffer(int fd, uint handle, out ulong offset);

    [DllImport(Lib, EntryPoint = "drmModeDestroyDumbBuffer")]
    internal static extern int ModeDestroyDumbBuffer(int fd, uint handle);

    [DllImport(Lib, EntryPoint = "drmModeAddFB2")]
    internal static extern int ModeAddFb2(int fd, uint width, uint height, uint pixelFormat, uint* handles, uint* pitches, uint* offsets, out uint bufferId, uint flags);

    [DllImport(Lib, EntryPoint = "drmModeAddFB2WithModifiers")]
    internal static extern int ModeAddFb2WithModifiers(int fd, uint width, uint height, uint pixelFormat, uint* handles, uint* pitches, uint* offsets, ulong* modifiers, out uint bufferId, uint flags);

    [DllImport(Lib, EntryPoint = "drmModeRmFB")]
    internal static extern int ModeRmFb(int fd, uint bufferId);

    [DllImport(Lib, EntryPoint = "drmPrimeFDToHandle", SetLastError = true)]
    internal static extern int PrimeFdToHandle(int fd, int primeFd, out uint handle);

    [DllImport(Lib, EntryPoint = "drmCloseBufferHandle", SetLastError = true)]
    internal static extern int CloseBufferHandle(int fd, uint handle);

    [DllImport(Lib, EntryPoint = "drmGetVersion")]
    internal static extern nint GetVersion(int fd);

    [DllImport(Lib, EntryPoint = "drmFreeVersion")]
    internal static extern void FreeVersion(nint version);
}