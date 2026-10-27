using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

/// <summary>
/// Regression tests for review findings: lifecycle edge cases, protocol error
/// rules and callback ordering.
/// </summary>
public class SurfaceLifecycleTests
{
    private const uint ContentColour = CompositorSessionTests.ContentColour;
    private const int Width = CompositorSessionTests.Width;
    private const int Height = CompositorSessionTests.Height;
    private const int Stride = Width * 4;

    [Fact]
    public void Commit_WithoutAttach_PreservesContent()
    {
        using var harness = new Harness();
        var surface = harness.CreateMappedSurface();

        harness.Client.SendRequest(8, "wl_surface", "commit");
        harness.RoundTrip();

        Assert.True(surface.IsMapped);
        Assert.Equal(harness.Content, surface.Content);
    }

    [Fact]
    public void BufferDestroyedBeforeCommit_KeepsPixels()
    {
        using var harness = new Harness();
        harness.Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(8));
        harness.Client.RegisterObject(8, "wl_surface");
        harness.Client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
        harness.Client.SendRequest(7, "wl_buffer", "destroy");
        harness.Client.SendRequest(8, "wl_surface", "commit");
        harness.RoundTrip();

        var surface = harness.Scene.Surfaces.Single();
        Assert.True(surface.IsMapped);
        Assert.Equal(harness.Content, surface.Content);
    }

    [Fact]
    public void PoolDestroyedBeforeCommit_KeepsPixels()
    {
        using var harness = new Harness();
        harness.Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(8));
        harness.Client.RegisterObject(8, "wl_surface");
        harness.Client.SendRequest(6, "wl_shm_pool", "destroy");
        harness.Client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
        harness.Client.SendRequest(8, "wl_surface", "commit");
        harness.RoundTrip();

        var surface = harness.Scene.Surfaces.Single();
        Assert.True(surface.IsMapped);
        Assert.Equal(harness.Content, surface.Content);
    }

    [Fact]
    public void GetReleaseWithoutBuffer_ReportsNoBuffer()
    {
        using var harness = new Harness();
        harness.Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(8));
        harness.Client.RegisterObject(8, "wl_surface");
        harness.Client.SendRequest(8, "wl_surface", "get_release", WireArgument.NewId(9));
        harness.Client.RegisterObject(9, "wl_callback");
        harness.Client.SendRequest(8, "wl_surface", "commit");

        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(8u, error.Arguments[0].AsObject());
        Assert.Equal(SurfaceErrors.NoBuffer, error.Arguments[1].AsUInt32());
    }

    [Fact]
    public void Attach_NonZeroOffsetOnV5_ReportsInvalidOffset()
    {
        using var harness = new Harness();
        harness.Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(8));
        harness.Client.RegisterObject(8, "wl_surface");
        harness.Client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(5), WireArgument.Int32(0));

        var error = harness.Client.ReadUntil(1, "error");
        Assert.Equal(8u, error.Arguments[0].AsObject());
        Assert.Equal(SurfaceErrors.InvalidOffset, error.Arguments[1].AsUInt32());
    }

    [Fact]
    public void CreatePool_BeyondDescriptorSize_ReportsStrideError()
    {
        using var harness = new Harness();
        var path = Path.GetTempFileName();
        File.WriteAllBytes(path, new byte[64]);
        try
        {
            var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            harness.Client.SendRequest(5, "wl_shm", "create_pool", WireArgument.NewId(6), WireArgument.FileDescriptor(unchecked((int)handle.DangerousGetHandle())), WireArgument.Int32(4096));
            handle.Dispose();

            var error = harness.Client.ReadUntil(1, "error");
            Assert.Equal(5u, error.Arguments[0].AsObject());
            Assert.Equal(ShmErrors.InvalidStride, error.Arguments[1].AsUInt32());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FrameCallback_DonePrecedesDeleteId()
    {
        using var harness = new Harness();
        var surface = harness.CreateMappedSurface();
        harness.Client.SendRequest(8, "wl_surface", "frame", WireArgument.NewId(9));
        harness.Client.RegisterObject(9, "wl_callback");
        harness.Client.SendRequest(8, "wl_surface", "commit");

        harness.RenderFrame();
        while (true)
        {
            var message = harness.Client.ReadEvent();
            if (message.ObjectId == 9 && message.Arguments.Length == 1 && IsEvent(harness.Client, message, "done"))
            {
                break;
            }

            if (message.ObjectId == 1 && IsEvent(harness.Client, message, "delete_id") && message.Arguments[0].AsUInt32() == 9)
            {
                Assert.Fail("delete_id for the callback arrived before done");
            }
        }

        harness.Client.ReadUntil(1, "delete_id");
        Assert.NotNull(surface.Content);
    }

    [Fact]
    public void SurfaceDestroy_FreesCallbacksSilently()
    {
        using var harness = new Harness();
        harness.CreateMappedSurface();
        harness.Client.SendRequest(8, "wl_surface", "frame", WireArgument.NewId(9));
        harness.Client.RegisterObject(9, "wl_callback");
        harness.Client.SendRequest(8, "wl_surface", "commit");
        harness.Client.SendRequest(8, "wl_surface", "destroy");

        while (true)
        {
            var message = harness.Client.ReadEvent();
            if (message.ObjectId == 9 && IsEvent(harness.Client, message, "done"))
            {
                Assert.Fail("done must not fire for a destroyed surface's callbacks");
            }

            if (message.ObjectId == 1 && IsEvent(harness.Client, message, "delete_id") && message.Arguments[0].AsUInt32() == 9)
            {
                break;
            }
        }

        Assert.Empty(harness.Scene.Surfaces);
    }

    private static bool IsEvent(TestClient client, DecodedMessage message, string name)
    {
        return client.EventName(message) == name;
    }

    /// <summary>A live compositor session with pool (6), buffer (7) set up.</summary>
    private sealed class Harness : IDisposable
    {
        private readonly DisplayServer _server;

        public Harness()
        {
            Scene = new Scene();
            var socketPath = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
            _server = new DisplayServer(socketPath, handlerFactory: new CompositorHandlerFactory(Scene));
            _server.Start();
            Client = new TestClient(_server.SocketPath);
            CompositorSessionTests.Handshake(Client);
            (Content, PoolFile) = CompositorSessionTests.SetupShm(Client);
        }

        public Scene Scene { get; }

        public TestClient Client { get; }

        public byte[] Content { get; }

        public string PoolFile { get; }

        public SurfaceHandler CreateMappedSurface()
        {
            Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(8));
            Client.RegisterObject(8, "wl_surface");
            Client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            Client.SendRequest(8, "wl_surface", "commit");
            return CompositorSessionTests.WaitFor(() => Scene.Surfaces.FirstOrDefault(), value => value is { IsMapped: true })!;
        }

        public void RenderFrame()
        {
            var pixels = new byte[800 * 600 * 4];
            Scene.Render(pixels, 800, 600, 800 * 4, frame: 1);
            Scene.CompleteFrame();
        }

        /// <summary>A sync round trip, so the server has processed prior requests.</summary>
        public void RoundTrip()
        {
            Client.SendRequest(1, "wl_display", "sync", WireArgument.NewId(100));
            Client.RegisterObject(100, "wl_callback");
            Client.ReadUntil(100, "done");
        }

        public void Dispose()
        {
            Client.Dispose();
            _server.Dispose();
            File.Delete(PoolFile);
        }
    }
}
