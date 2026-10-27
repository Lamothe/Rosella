using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rosella.Compositor;

/// <summary>Pixel formats Rosella accepts (the two it advertises on wl_shm).</summary>
internal static class ShmFormats
{
    internal const uint Argb8888 = 0;
    internal const uint Xrgb8888 = 1;
}

/// <summary>
/// A client's wl_shm_pool: its descriptor, mapped read-only. Copies pixels
/// out at commit time so rendering never races client writes. The mapping is
/// torn down when the pool object is destroyed and its buffers are gone.
/// </summary>
internal sealed class ShmPool : IDisposable
{
    private readonly SafeFileHandle _handle;
    private nint _map;
    private long _size;
    private int _bufferReferences;
    private bool _destroyed;

    private ShmPool(SafeFileHandle handle, nint map, long size)
    {
        _handle = handle;
        _map = map;
        _size = size;
    }

    public long Size => _size;

    /// <summary>Maps a pool from an owned descriptor. Throws on unusable descriptors or sizes.</summary>
    public static ShmPool Map(SafeFileHandle handle, long size)
    {
        if (size <= 0)
        {
            handle.Dispose();
            throw new ShmValidationException(ShmErrors.InvalidStride, $"pool size {size} must be positive");
        }

        var length = BackingLength(handle);
        if (length < size)
        {
            handle.Dispose();
            throw new ShmValidationException(ShmErrors.InvalidStride, $"pool size {size} exceeds the descriptor's {length} bytes");
        }

        var map = ShmNative.mmap(0, checked((nuint)size), ShmNative.ProtRead, ShmNative.MapShared, unchecked((int)handle.DangerousGetHandle()), 0);
        if (map == (nint)ShmNative.MapFailedValue)
        {
            handle.Dispose();
            throw new ShmValidationException(ShmErrors.InvalidFd, $"mmap of pool failed with errno {Marshal.GetLastPInvokeError()}");
        }

        return new ShmPool(handle, map, size);
    }

    /// <summary>Grows the pool's mapping; shrinking is rejected, per the protocol.</summary>
    public void Resize(long size)
    {
        ObjectDisposedException.ThrowIf(_destroyed && _map == 0, this);
        if (size < _size)
        {
            throw new ShmValidationException(ShmErrors.InvalidStride, $"pool cannot shrink from {_size} to {size}");
        }

        if (size == _size)
        {
            return;
        }

        var length = BackingLength(_handle);
        if (length < size)
        {
            throw new ShmValidationException(ShmErrors.InvalidStride, $"pool size {size} exceeds the descriptor's {length} bytes");
        }

        var map = ShmNative.mmap(0, checked((nuint)size), ShmNative.ProtRead, ShmNative.MapShared, unchecked((int)_handle.DangerousGetHandle()), 0);
        if (map == (nint)ShmNative.MapFailedValue)
        {
            throw new ShmValidationException(ShmErrors.InvalidFd, $"mmap of grown pool failed with errno {Marshal.GetLastPInvokeError()}");
        }

        ShmNative.munmap(_map, checked((nuint)_size));
        _map = map;
        _size = size;
    }

    public ShmBuffer CreateBuffer(int offset, int width, int height, int stride, uint format)
    {
        ObjectDisposedException.ThrowIf(_destroyed && _map == 0, this);
        if (format is not (ShmFormats.Argb8888 or ShmFormats.Xrgb8888))
        {
            throw new ShmValidationException(ShmErrors.InvalidFormat, $"unsupported format {format}");
        }

        if (width <= 0 || height <= 0)
        {
            throw new ShmValidationException(ShmErrors.InvalidStride, $"buffer size {width}x{height} must be positive");
        }

        if ((long)width * 4 > stride)
        {
            throw new ShmValidationException(ShmErrors.InvalidStride, $"stride {stride} is smaller than a {width}-pixel row");
        }

        if (offset < 0 || (long)offset + ((long)(height - 1) * stride) + ((long)width * 4) > _size)
        {
            throw new ShmValidationException(ShmErrors.InvalidStride, $"buffer at offset {offset} does not fit in the {_size}-byte pool");
        }

        _bufferReferences++;
        return new ShmBuffer(this, offset, width, height, stride, format);
    }

    /// <summary>
    /// Destroys the pool object; the mapping is released once its buffers are
    /// gone (a buffer can outlive its pool object while attached to a surface).
    /// </summary>
    public void Dispose()
    {
        _destroyed = true;
        ReleaseIfUnused();
    }

    /// <summary>A window onto the pool mapping.</summary>
    internal ReadOnlySpan<byte> Bytes(long offset, int length)
    {
        unsafe
        {
            return new ReadOnlySpan<byte>((void*)(_map + (nint)offset), length);
        }
    }

    internal void ReleaseBuffer()
    {
        _bufferReferences--;
        ReleaseIfUnused();
    }

    /// <summary>The descriptor's size, so a lying size cannot SIGBUS the compositor later.</summary>
    private static long BackingLength(SafeFileHandle handle)
    {
        var length = ShmNative.lseek(unchecked((int)handle.DangerousGetHandle()), 0, ShmNative.SeekEnd);
        if (length < 0)
        {
            throw new ShmValidationException(ShmErrors.InvalidFd, $"cannot determine descriptor size (errno {Marshal.GetLastPInvokeError()})");
        }

        return length;
    }

    private void ReleaseIfUnused()
    {
        if (!_destroyed || _bufferReferences > 0 || _map == 0)
        {
            return;
        }

        ShmNative.munmap(_map, checked((nuint)_size));
        _map = 0;
        _handle.Dispose();
    }
}

/// <summary>
/// A wl_buffer backed by a slice of a pool. <see cref="ReadPixels"/> copies
/// the content out in packed XRGB rows. Holds a pool reference until released.
/// </summary>
internal sealed class ShmBuffer
{
    private readonly ShmPool _pool;
    private bool _released;

    internal ShmBuffer(ShmPool pool, int offset, int width, int height, int stride, uint format)
    {
        _pool = pool;
        Offset = offset;
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
    }

    internal int Offset { get; }

    public int Width { get; }

    public int Height { get; }

    internal int Stride { get; }

    internal uint Format { get; }

    /// <summary>Copies the buffer's pixels into packed rows of width × 4 bytes.</summary>
    public byte[] ReadPixels()
    {
        var pixels = new byte[checked(Width * 4 * Height)];
        for (var row = 0; row < Height; row++)
        {
            _pool.Bytes(Offset + ((long)row * Stride), Width * 4).CopyTo(pixels.AsSpan(row * Width * 4));
        }

        return pixels;
    }

    /// <summary>Drops this buffer's pool reference, once.</summary>
    public void Release()
    {
        if (_released)
        {
            return;
        }

        _released = true;
        _pool.ReleaseBuffer();
    }
}
