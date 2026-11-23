using System.Runtime.InteropServices;

namespace Rosella.GpuProbe;

/// <summary>
/// A dumb buffer on a card node, mmap'd for CPU writes — exactly the scanout
/// buffer Rosella's software compositor draws into. Creating one neither
/// needs nor takes DRM master.
/// </summary>
internal sealed unsafe class DumbBuffer : IDisposable
{
    private readonly int _fd;
    private readonly nint _map;
    private readonly nuint _mapLength;
    private bool _disposed;

    private DumbBuffer(int fd, uint handle, uint pitch, ulong size, nint map)
    {
        _fd = fd;
        Handle = handle;
        Pitch = pitch;
        Size = size;
        _map = map;
        _mapLength = checked((nuint)size);
    }

    internal uint Handle { get; }

    internal uint Pitch { get; }

    internal ulong Size { get; }

    internal nint Pixels => _map;

    internal static DumbBuffer Create(int fd, uint width, uint height)
    {
        var result = LibDrm.ModeCreateDumbBuffer(fd, width, height, 32, 0, out var handle, out var pitch, out var size);
        if (result != 0)
        {
            throw new ProbeException($"drmModeCreateDumbBuffer({width}x{height}) failed: {Describe(result)}");
        }

        result = LibDrm.ModeMapDumbBuffer(fd, handle, out var offset);
        if (result != 0)
        {
            LibDrm.ModeDestroyDumbBuffer(fd, handle);
            throw new ProbeException($"drmModeMapDumbBuffer failed: {Describe(result)}");
        }

        var map = Libc.Mmap(0, checked((nuint)size), Libc.ProtRead | Libc.ProtWrite, Libc.MapShared, fd, checked((long)offset));
        if (map == -1)
        {
            var errno = Marshal.GetLastPInvokeError();
            LibDrm.ModeDestroyDumbBuffer(fd, handle);
            throw new ProbeException($"mmap(dumb {size} bytes) failed: {Libc.ErrorName(errno)} ({errno})");
        }

        return new DumbBuffer(fd, handle, pitch, size, map);
    }

    /// <summary>libdrm returns 0 or a negated errno; both spellings end up readable here.</summary>
    internal static string Describe(int result)
    {
        if (result == 0)
        {
            return "OK";
        }

        var errno = result < 0 ? -result : result;
        return $"{Libc.ErrorName(errno)} ({errno})";
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Libc.Munmap(_map, _mapLength);
        LibDrm.ModeDestroyDumbBuffer(_fd, Handle);
    }
}