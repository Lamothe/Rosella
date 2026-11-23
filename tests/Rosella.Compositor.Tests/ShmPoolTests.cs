namespace Rosella.Compositor.Tests;

public class ShmPoolTests
{
    private const int Width = 8;
    private const int Height = 8;
    private const int Stride = Width * 4;

    private static (ShmPool Pool, string Path) CreatePool(long size, byte[]? content = null)
    {
        var path = Path.GetTempFileName();
        var bytes = content ?? new byte[size];
        File.WriteAllBytes(path, bytes);
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        return (ShmPool.Map(handle, size), path);
    }

    [Fact]
    public void CreateBuffer_ReadsPixels()
    {
        var content = new byte[Stride * Height];
        for (var i = 0; i < content.Length; i += 4)
        {
            content[i] = 0x44;
        }

        var (pool, path) = CreatePool(content.Length, content);
        try
        {
            var buffer = pool.CreateBuffer(0, Width, Height, Stride, ShmFormats.Xrgb8888);
            var pixels = buffer.ReadPixels();
            Assert.Equal(content, pixels);
        }
        finally
        {
            pool.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void CreateBuffer_HonoursOffset()
    {
        var content = new byte[(Stride * Height * 2) + 16];
        content[(Stride * Height) + 4] = 0x55;
        var (pool, path) = CreatePool(content.Length, content);
        try
        {
            var buffer = pool.CreateBuffer(Stride * Height, Width, Height, Stride, ShmFormats.Xrgb8888);
            var pixels = buffer.ReadPixels();
            Assert.Equal(0x55, pixels[4]);
        }
        finally
        {
            pool.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void CreateBuffer_RejectsUnknownFormat()
    {
        var (pool, path) = CreatePool(Stride * Height);
        try
        {
            var exception = Assert.Throws<ShmValidationException>(() => pool.CreateBuffer(0, Width, Height, Stride, 2));
            Assert.Equal(ShmErrors.InvalidFormat, exception.ErrorCode);
        }
        finally
        {
            pool.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void CreateBuffer_RejectsBadStride()
    {
        var (pool, path) = CreatePool(Stride * Height);
        try
        {
            var exception = Assert.Throws<ShmValidationException>(() => pool.CreateBuffer(0, Width, Height, Stride - 4, ShmFormats.Xrgb8888));
            Assert.Equal(ShmErrors.InvalidStride, exception.ErrorCode);
        }
        finally
        {
            pool.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void CreateBuffer_RejectsBufferBeyondPool()
    {
        var (pool, path) = CreatePool(Stride * Height);
        try
        {
            var exception = Assert.Throws<ShmValidationException>(() => pool.CreateBuffer(16, Width, Height, Stride, ShmFormats.Xrgb8888));
            Assert.Equal(ShmErrors.InvalidStride, exception.ErrorCode);
        }
        finally
        {
            pool.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void Map_RejectsNonPositiveSize()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[64]);
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var exception = Assert.Throws<ShmValidationException>(() => ShmPool.Map(handle, 0));
        Assert.Equal(ShmErrors.InvalidStride, exception.ErrorCode);
        File.Delete(path);
    }

    [Fact]
    public void Map_RejectsSizeBeyondTheDescriptor()
    {
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[64]);
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        var exception = Assert.Throws<ShmValidationException>(() => ShmPool.Map(handle, 4096));
        Assert.Equal(ShmErrors.InvalidStride, exception.ErrorCode);
        File.Delete(path);
    }

    [Fact]
    public void Resize_ShrinkIsRejected()
    {
        var (pool, path) = CreatePool(Stride * Height);
        try
        {
            Assert.Throws<ShmValidationException>(() => pool.Resize(16));
            Assert.Equal(Stride * Height, pool.Size);
        }
        finally
        {
            pool.Dispose();
            File.Delete(path);
        }
    }

    [Fact]
    public void Resize_GrowsThePool()
    {
        var content = new byte[Stride * Height];
        var (pool, path) = CreatePool(content.Length, content);
        try
        {
            File.WriteAllBytes(path, new byte[Stride * Height * 2]);
            pool.Resize(Stride * Height * 2);
            Assert.Equal(Stride * Height * 2, pool.Size);
            var buffer = pool.CreateBuffer(0, Width, Height * 2, Stride, ShmFormats.Xrgb8888);
            Assert.Equal(Stride * Height * 2, buffer.ReadPixels().Length);
        }
        finally
        {
            pool.Dispose();
            File.Delete(path);
        }
    }

}
