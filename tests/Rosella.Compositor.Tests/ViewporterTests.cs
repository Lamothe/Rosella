using System.Runtime.InteropServices;
using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

public class ViewporterTests
{
    [Fact]
    public void CropAndScaleApplyOnCommitAndResetAfterViewportDestruction()
    {
        var scene = new Scene();
        var factory = new CompositorHandlerFactory(scene, new SeatState(), includeXwaylandProtocols: true);
        using var server = new DisplayServer(Path.Combine(Path.GetTempPath(), $"rosella-viewport-{Guid.NewGuid():N}"),
            factory.Globals, factory);
        server.Start();
        using var client = new TestClient(server.SocketPath);
        CompositorSessionTests.Handshake(client);

        uint[] colours = [0xFF020000, 0xFF030000, 0xFF040000, 0xFF050000,
            0xFF120000, 0xFF130000, 0xFF140000, 0xFF150000];
        var path = CompositorSessionTests.SetupShmBuffer(client, MemoryMarshal.AsBytes(colours.AsSpan()).ToArray(), 4, 2);
        try
        {
            client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(8));
            client.RegisterObject(8, "wl_surface");
            client.SendRequest(8, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            client.SendRequest(8, "wl_surface", "commit");
            client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(10), WireArgument.String(ViewporterHandler.Interface),
                WireArgument.UInt32(1), WireArgument.NewId(9));
            client.SendRequest(9, ViewporterHandler.Interface, "get_viewport", WireArgument.NewId(10), WireArgument.Object(8));
            client.SendRequest(10, "wp_viewport", "set_source", Fx(0.5), Fx(1), Fx(2), Fx(1));
            client.SendRequest(10, "wp_viewport", "set_destination", WireArgument.Int32(4), WireArgument.Int32(1));
            Sync(client, 11);
            var surface = Assert.Single(scene.Surfaces);
            Assert.Equal((4, 2), (surface.ContentWidth, surface.ContentHeight));

            client.SendRequest(8, "wl_surface", "commit");
            Sync(client, 12);
            Assert.Equal((4, 1), (surface.ContentWidth, surface.ContentHeight));
            var pixels = new byte[16];
            surface.BlitAt(pixels, 4, 1, 16, 0, 0, 4, 1);
            Assert.Equal(new[] { colours[4], colours[5], colours[5], colours[6] }, MemoryMarshal.Cast<byte, uint>(pixels).ToArray());

            client.SendRequest(10, "wp_viewport", "destroy");
            Sync(client, 13);
            Assert.Equal(1, surface.ContentHeight);
            client.SendRequest(8, "wl_surface", "commit");
            Sync(client, 14);
            Assert.Equal(2, surface.ContentHeight);
            surface.BlitAt(pixels, 4, 1, 16, 0, 0, 4, 2);
            Assert.Equal(colours[..4], MemoryMarshal.Cast<byte, uint>(pixels).ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static WireArgument Fx(double value) => WireArgument.FixedPoint(Fixed.FromDouble(value));

    private static void Sync(TestClient client, uint callback)
    {
        client.SendRequest(1, "wl_display", "sync", WireArgument.NewId(callback));
        client.RegisterObject(callback, "wl_callback");
        client.ReadUntil(callback, "done");
    }
}
