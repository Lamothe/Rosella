using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

/// <summary>
/// End-to-end tests over a real socket: a client hands the compositor an
/// shm descriptor, and the committed pixels must reach the scene and the
/// rendered frame. Descriptor passing included.
/// </summary>
public class CompositorSessionTests
{
    internal const int Width = 32;
    internal const int Height = 32;
    private const int Stride = Width * 4;
    internal const uint ContentColour = 0x7F123456;

    private static string TempSocket()
    {
        return Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
    }

    private static byte[] Pattern()
    {
        var content = new byte[Stride * Height];
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(content.AsSpan());
        pixels.Fill(ContentColour);
        return content;
    }

    internal static T WaitFor<T>(Func<T> probe, Func<T, bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            var value = probe();
            if (done(value))
            {
                return value;
            }

            Thread.Sleep(10);
        }

        throw new TimeoutException("condition was not reached");
    }

    [Fact]
    public void ShmSurface_CommitsPixels_FiresFrameCallbacks_AndUnmaps()
    {
        var scene = new Scene();
        using var server = new DisplayServer(TempSocket(), handlerFactory: new CompositorHandlerFactory(scene));
        server.Start();
        using var client = new TestClient(server.SocketPath);

        Handshake(client);
        var (content, path) = SetupShm(client);
        try
        {
            client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(8));
            client.RegisterObject(8, "wl_surface");
            client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            client.SendRequest(8, "wl_surface", "damage", WireArgument.Int32(0), WireArgument.Int32(0), WireArgument.Int32(Width), WireArgument.Int32(Height));
            client.SendRequest(8, "wl_surface", "frame", WireArgument.NewId(9));
            client.RegisterObject(9, "wl_callback");
            client.SendRequest(8, "wl_surface", "commit");

            var surface = WaitFor(() => scene.Surfaces.FirstOrDefault(), value => value is { IsMapped: true });
            Assert.Equal(Width, surface!.Width);
            Assert.Equal(Height, surface.Height);
            Assert.Equal((64, 64), surface.Position);
            Assert.Equal(content, surface.Content);

            // The buffer is copied at commit, so it is released immediately.
            client.ReadUntil(7, "release");

            // One composited frame completes the queued frame callback.
            var pixels = new byte[800 * 600 * 4];
            scene.Render(pixels, 800, 600, 800 * 4, frame: 1);
            Assert.Equal(ContentColour, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pixels.AsSpan((64 * 800 * 4) + (64 * 4), 4)));
            scene.CompleteFrame();
            client.ReadUntil(9, "done");

            // attach(null) unmaps and the background returns.
            client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(0), WireArgument.Int32(0), WireArgument.Int32(0));
            client.SendRequest(8, "wl_surface", "commit");
            WaitFor(() => scene.Surfaces.FirstOrDefault(), value => value is { IsMapped: false });
            scene.Render(pixels, 800, 600, 800 * 4, frame: 1);
            Assert.Equal(0xFFFFFFFFu, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(pixels.AsSpan((64 * 800 * 4) + (64 * 4), 4)));

            // Destroying the surface clears it from the scene.
            client.SendRequest(8, "wl_surface", "destroy");
            WaitFor(() => scene.Surfaces.Count, count => count == 0);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void CreateBuffer_TooLargeForPool_ReportsShmError()
    {
        var scene = new Scene();
        using var server = new DisplayServer(TempSocket(), handlerFactory: new CompositorHandlerFactory(scene));
        server.Start();
        using var client = new TestClient(server.SocketPath);

        Handshake(client);
        var (_, path) = SetupShm(client, poolSize: 64);
        try
        {
            client.SendRequest(6, "wl_shm_pool", "create_buffer", WireArgument.NewId(7), WireArgument.Int32(0), WireArgument.Int32(Width), WireArgument.Int32(Height), WireArgument.Int32(Stride), WireArgument.UInt32(0));
            var error = client.ReadUntil(1, "error");
            Assert.Equal(6u, error.Arguments[0].AsObject()); // raised on wl_shm_pool
            Assert.Equal(ShmErrors.InvalidStride, error.Arguments[1].AsUInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Commit_CopiesAndReleases_BeforeFrameCallbacks()
    {
        var scene = new Scene();
        using var server = new DisplayServer(TempSocket(), handlerFactory: new CompositorHandlerFactory(scene));
        server.Start();
        using var client = new TestClient(server.SocketPath);

        Handshake(client);
        var (_, path) = SetupShm(client);
        try
        {
            client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(8));
            client.RegisterObject(8, "wl_surface");
            client.SendRequest(8, "wl_surface", "get_release", WireArgument.NewId(9));
            client.RegisterObject(9, "wl_callback");
            client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            client.SendRequest(8, "wl_surface", "commit");

            // get_release callbacks complete when the buffer is released at commit.
            client.ReadUntil(9, "done");
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Registry handshake and binds for wl_compositor (4) and wl_shm (5).</summary>
    internal static void Handshake(TestClient client)
    {
        client.SendRequest(1, "wl_display", "get_registry", WireArgument.NewId(2));
        client.RegisterObject(2, "wl_registry");
        client.SendRequest(1, "wl_display", "sync", WireArgument.NewId(3));
        client.RegisterObject(3, "wl_callback");
        client.ReadUntil(3, "done");

        client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(1), WireArgument.String("wl_compositor"), WireArgument.UInt32(7), WireArgument.NewId(4));
        client.RegisterObject(4, "wl_compositor");
        client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(3), WireArgument.String("wl_shm"), WireArgument.UInt32(3), WireArgument.NewId(5));
        client.RegisterObject(5, "wl_shm");
    }

    /// <summary>Creates pool (6) and buffer (7) from a temp file handed over as a descriptor.</summary>
    internal static (byte[] Content, string Path) SetupShm(TestClient client, int poolSize = Stride * Height)
    {
        var content = Pattern();
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, content.AsSpan(0, Math.Min(content.Length, poolSize)).ToArray());
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        client.SendRequest(5, "wl_shm", "create_pool", WireArgument.NewId(6), WireArgument.FileDescriptor(unchecked((int)handle.DangerousGetHandle())), WireArgument.Int32(poolSize));
        client.RegisterObject(6, "wl_shm_pool");

        if (poolSize >= Stride * Height)
        {
            client.SendRequest(6, "wl_shm_pool", "create_buffer", WireArgument.NewId(7), WireArgument.Int32(0), WireArgument.Int32(Width), WireArgument.Int32(Height), WireArgument.Int32(Stride), WireArgument.UInt32(0));
            client.RegisterObject(7, "wl_buffer");
        }

        return (content, path);
    }
}
