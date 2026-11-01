using System.Runtime.InteropServices;

namespace Rosella.GpuProbe;

/// <summary>libgbm: buffer allocation and dma-buf export on a render node.</summary>
internal static unsafe class Gbm
{
    private const string Lib = "libgbm.so.1";

    // gbm.h: GBM_BO_USE_*
    internal const uint BoUseScanout = 1 << 0;
    internal const uint BoUseCursor64x64 = 1 << 1;
    internal const uint BoUseRendering = 1 << 2;
    internal const uint BoUseWrite = 1 << 3;
    internal const uint BoUseLinear = 1 << 4;
    internal const uint BoUseProtected = 1 << 5;
    internal const uint BoUseFrontRendering = 1 << 6;

    // gbm.h: GBM_BO_TRANSFER_*
    internal const uint BoTransferRead = 1;
    internal const uint BoTransferWrite = 2;
    internal const uint BoTransferReadWrite = 3;

    // gbm.h: GBM_FORMAT_XRGB8888 = fourcc('X','R','2','4')
    internal const uint FormatXrgb8888 = 0x34325258;

    [DllImport(Lib, EntryPoint = "gbm_create_device")]
    internal static extern nint CreateDevice(int fd);

    [DllImport(Lib, EntryPoint = "gbm_device_destroy")]
    internal static extern void DeviceDestroy(nint gbm);

    [DllImport(Lib, EntryPoint = "gbm_device_get_backend_name")]
    internal static extern nint DeviceGetBackendName(nint gbm);

    [DllImport(Lib, EntryPoint = "gbm_device_get_fd")]
    internal static extern int DeviceGetFd(nint gbm);

    [DllImport(Lib, EntryPoint = "gbm_bo_create")]
    internal static extern nint BoCreate(nint gbm, uint width, uint height, uint format, uint flags);

    [DllImport(Lib, EntryPoint = "gbm_bo_destroy")]
    internal static extern void BoDestroy(nint bo);

    [DllImport(Lib, EntryPoint = "gbm_bo_get_width")]
    internal static extern uint BoGetWidth(nint bo);

    [DllImport(Lib, EntryPoint = "gbm_bo_get_height")]
    internal static extern uint BoGetHeight(nint bo);

    [DllImport(Lib, EntryPoint = "gbm_bo_get_stride")]
    internal static extern uint BoGetStride(nint bo);

    [DllImport(Lib, EntryPoint = "gbm_bo_get_offset")]
    internal static extern uint BoGetOffset(nint bo, int plane);

    [DllImport(Lib, EntryPoint = "gbm_bo_get_modifier")]
    internal static extern ulong BoGetModifier(nint bo);

    [DllImport(Lib, EntryPoint = "gbm_bo_get_fd")]
    internal static extern int BoGetFd(nint bo);

    [DllImport(Lib, EntryPoint = "gbm_bo_map")]
    internal static extern nint BoMap(nint bo, uint x, uint y, uint width, uint height, uint flags, out uint stride, out nint mapData);

    [DllImport(Lib, EntryPoint = "gbm_bo_unmap")]
    internal static extern void BoUnmap(nint bo, nint mapData);
}