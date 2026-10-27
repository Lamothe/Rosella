using Microsoft.Win32.SafeHandles;

namespace Rosella.Drm;

/// <summary>
/// A GEM dumb buffer: allocated on the card, mmap'd for CPU rendering.
/// Disposal unmaps and destroys it.
/// </summary>
public sealed class DrmDumbBuffer : IDisposable
{
    private readonly DrmDevice _device;
    private readonly nint _map;
    private bool _disposed;

    internal DrmDumbBuffer(DrmDevice device, uint handle, uint pitch, ulong size, nint map)
    {
        _device = device;
        Handle = handle;
        Pitch = pitch;
        Size = size;
        _map = map;
    }

    public uint Handle { get; }

    /// <summary>Bytes per row, which may exceed width × bytes-per-pixel.</summary>
    public uint Pitch { get; }

    public ulong Size { get; }

    /// <summary>The mapped pixels, for CPU rendering.</summary>
    public unsafe Span<byte> Pixels
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return new((void*)_map, checked((int)Size));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        unsafe
        {
            if (_map != 0)
            {
                Libc.munmap(_map, checked((nuint)Size));
            }
        }

        _device.DestroyDumbHandle(Handle);
    }}

/// <summary>A page-flip completion event from the kernel.</summary>
public sealed record DrmFlipEvent(uint Type, ulong UserData, uint Sequence);
