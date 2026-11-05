using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

/// <summary>
/// wl_surface.set_opaque_region: a client that declares its whole surface
/// opaque is believed. Chrome and GTK send ARGB8888 buffers with a
/// full-surface opaque region — every pixel covers — and the compositor
/// must take the copy path instead of blending 4.9M pixels a frame.
/// </summary>
public class OpaqueRegionTests
{
    /// <summary>A colour no blend of the test content can produce.</summary>
    private const uint OpaqueColour = 0x00FF0000;

    private const uint SecondColour = 0x0000FF00;

    /// <summary>
    /// Alpha 0 with a non-zero colour: invalid as premultiplied on purpose.
    /// A blend hides it (the desktop shows through); a copy shows it, which
    /// is exactly the distinction this file tests.
    /// </summary>
    private static byte[] TransparentContent(int width, int height, uint colour = OpaqueColour)
    {
        var content = new byte[width * height * 4];
        MemoryMarshal.Cast<byte, uint>(content.AsSpan()).Fill(colour);
        return content;
    }

    [Fact]
    public void FullOpaqueRegion_MakesAnArgbWindowOpaque()
    {
        using var harness = new Harness();
        var surface = harness.Surface;

        // Before the region: transparent pixels, so the desktop shows.
        Assert.False(surface.IsOpaqueContent);
        Assert.Equal(Background.ColourAt(64, 600), harness.PixelAt(64, 64));

        harness.SetOpaqueRegion(0, 0, 32, 32);

        Assert.True(surface.IsOpaqueContent);
        Assert.Equal(OpaqueColour, harness.PixelAt(64, 64));
    }

    [Fact]
    public void PartialOpaqueRegion_StaysBlended()
    {
        using var harness = new Harness();
        var surface = harness.Surface;

        // Half the surface is not the surface: the renderer must not claim
        // certainty it does not have.
        harness.SetOpaqueRegion(0, 0, 16, 32);

        Assert.False(surface.IsOpaqueContent);
        Assert.Equal(Background.ColourAt(64, 600), harness.PixelAt(64, 64));
    }

    [Fact]
    public void ClearedOpaqueRegion_ReturnsToBlending()
    {
        using var harness = new Harness();
        var surface = harness.Surface;
        harness.SetOpaqueRegion(0, 0, 32, 32);
        Assert.True(surface.IsOpaqueContent);

        harness.Client.SendRequest(8, "wl_surface", "set_opaque_region", WireArgument.Object(0));
        harness.Client.SendRequest(8, "wl_surface", "commit");
        harness.RoundTrip();

        Assert.False(surface.IsOpaqueContent);
        Assert.Equal(Background.ColourAt(64, 600), harness.PixelAt(64, 64));
    }

    [Fact]
    public void OpaqueRegionIsDoubleBuffered_AndCopiedFromTheRegion()
    {
        using var harness = new Harness();
        var surface = harness.Surface;

        // The request alone changes nothing: it is committed state.
        harness.CreateRegion(30);
        harness.Client.SendRequest(30, "wl_region", "add", WireArgument.Int32(0), WireArgument.Int32(0), WireArgument.Int32(32), WireArgument.Int32(32));
        harness.Client.SendRequest(8, "wl_surface", "set_opaque_region", WireArgument.Object(30));
        harness.RoundTrip();
        Assert.False(surface.IsOpaqueContent);

        // Destroying the region after the request must not matter: the
        // surface copied it (clients do exactly this).
        harness.Client.SendRequest(30, "wl_region", "destroy");
        harness.Client.SendRequest(8, "wl_surface", "commit");
        harness.RoundTrip();
        Assert.True(surface.IsOpaqueContent);

        // A region the surface was never given must not leak in.
        harness.CreateRegion(31);
        harness.Client.SendRequest(31, "wl_region", "add", WireArgument.Int32(0), WireArgument.Int32(0), WireArgument.Int32(1), WireArgument.Int32(1));
        harness.Client.SendRequest(8, "wl_surface", "commit");
        harness.RoundTrip();
        Assert.True(surface.IsOpaqueContent);
    }

    [Fact]
    public void OddBufferWidth_AtScaleTwo_NeedsTheLastPixelCovered()
    {
        // 33 buffer pixels at scale 2 is 16.5 surface units. A region of 16
        // stops short of the last column: believing it would copy that
        // column verbatim and suppress the desktop under it.
        using var harness = new Harness(33, 33, bufferScale: 2);
        harness.SetOpaqueRegion(0, 0, 16, 16);
        Assert.False(harness.Surface.IsOpaqueContent);

        harness.SetOpaqueRegion(0, 0, 17, 17);
        Assert.True(harness.Surface.IsOpaqueContent);
    }

    [Fact]
    public void OpaqueArgbWindow_LeavesNoDesktopUnderIt()
    {
        using var harness = new Harness();
        harness.SetOpaqueRegion(0, 0, 32, 32);

        // The desktop is painted for the whole frame except the window.
        var cost = harness.RenderCost();
        Assert.Equal((800L * 600) - (32 * 32), cost.BackgroundPixels);
    }

    [Fact]
    public void TranslucentSurfaceOverAnOpaqueWindow_KeepsTheWindowUnderneath()
    {
        // A menu over Chrome: the menu's rect lies entirely over the opaque
        // window, so the desktop must NOT be repainted under it — the window
        // below has to show through the menu's transparent pixels.
        using var harness = new Harness(width: 200, height: 200);
        harness.SetOpaqueRegion(0, 0, 200, 200);

        var top = harness.AddSurface(41, 42, 43, 32, 32, SecondColour);
        harness.RoundTrip();

        var (x, y) = (top.Position.X + 16, top.Position.Y + 16);
        Assert.Equal(OpaqueColour, harness.PixelAt(x, y));
    }

    private sealed class Harness : IDisposable
    {
        private readonly DisplayServer _server;
        private readonly List<string> _poolFiles = [];

        public Harness(int width = 32, int height = 32, int bufferScale = 1)
        {
            Scene = new Scene();
            var socketPath = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
            _server = new DisplayServer(socketPath, handlerFactory: new CompositorHandlerFactory(Scene));
            _server.Start();
            Client = new TestClient(_server.SocketPath);
            CompositorSessionTests.Handshake(Client);
            _poolFiles.Add(CompositorSessionTests.SetupShmBuffer(Client, TransparentContent(width, height), width, height));

            Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(8));
            Client.RegisterObject(8, "wl_surface");
            if (bufferScale != 1)
            {
                Client.SendRequest(8, "wl_surface", "set_buffer_scale", WireArgument.Int32(bufferScale));
            }

            Client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            Client.SendRequest(8, "wl_surface", "commit");
            Surface = CompositorSessionTests.WaitFor(
                () => Scene.Surfaces.FirstOrDefault(),
                value => value is { IsMapped: true })!;
        }

        public Scene Scene { get; }

        public TestClient Client { get; }

        public SurfaceHandler Surface { get; }

        public void CreateRegion(uint id)
        {
            Client.SendRequest(4, "wl_compositor", "create_region", WireArgument.NewId(id));
            Client.RegisterObject(id, "wl_region");
        }

        public void SetOpaqueRegion(int x, int y, int width, int height)
        {
            CreateRegion(9);
            Client.SendRequest(9, "wl_region", "add", WireArgument.Int32(x), WireArgument.Int32(y), WireArgument.Int32(width), WireArgument.Int32(height));
            Client.SendRequest(8, "wl_surface", "set_opaque_region", WireArgument.Object(9));
            Client.SendRequest(9, "wl_region", "destroy");
            Client.SendRequest(8, "wl_surface", "commit");
            RoundTrip();
        }

        /// <summary>Maps another surface with its own pool, at the scene's next slot.</summary>
        public SurfaceHandler AddSurface(uint surfaceId, uint poolId, uint bufferId, int width, int height, uint colour)
        {
            var content = TransparentContent(width, height, colour);
            var path = Path.GetTempFileName();
            File.WriteAllBytes(path, content);
            _poolFiles.Add(path);
            var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
            Client.SendRequest(5, "wl_shm", "create_pool", WireArgument.NewId(poolId), WireArgument.FileDescriptor(unchecked((int)handle.DangerousGetHandle())), WireArgument.Int32(content.Length));
            Client.RegisterObject(poolId, "wl_shm_pool");
            Client.SendRequest(poolId, "wl_shm_pool", "create_buffer", WireArgument.NewId(bufferId), WireArgument.Int32(0), WireArgument.Int32(width), WireArgument.Int32(height), WireArgument.Int32(width * 4), WireArgument.UInt32(0));
            Client.RegisterObject(bufferId, "wl_buffer");

            Client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(surfaceId));
            Client.RegisterObject(surfaceId, "wl_surface");
            Client.SendRequest(surfaceId, "wl_surface", "attach", WireArgument.Object(bufferId), WireArgument.Int32(0), WireArgument.Int32(0));
            Client.SendRequest(surfaceId, "wl_surface", "commit");
            return CompositorSessionTests.WaitFor(
                () => Scene.Surfaces.FirstOrDefault(surface => surface.Id == surfaceId),
                value => value is { IsMapped: true })!;
        }

        /// <summary>One composited frame's pixel at an output coordinate.</summary>
        public uint PixelAt(int x, int y)
        {
            var pixels = new byte[800 * 600 * 4];
            Scene.Render(pixels, 800, 600, 800 * 4, frame: 1);
            return BinaryPrimitives.ReadUInt32LittleEndian(pixels.AsSpan((y * 800 * 4) + (x * 4), 4));
        }

        public RenderCost RenderCost()
        {
            var pixels = new byte[800 * 600 * 4];
            return Scene.RenderRegion(pixels, 800, 600, 800 * 4, frame: 1, DamageRect.For(800, 600));
        }

        public void RoundTrip()
        {
            Client.SendRequest(1, "wl_display", "sync", WireArgument.NewId(90));
            Client.RegisterObject(90, "wl_callback");
            Client.ReadUntil(90, "done");
        }

        public void Dispose()
        {
            Client.Dispose();
            _server.Dispose();
            foreach (var path in _poolFiles)
            {
                File.Delete(path);
            }
        }
    }
}