using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

public class IdleInhibitTests
{
    [Fact]
    public void InhibitorDependsOnSurfaceVisibilityAndLifetime()
    {
        var scene = new Scene();
        var factory = new CompositorHandlerFactory(scene);
        using var server = new DisplayServer(Path.Combine(Path.GetTempPath(), $"rosella-idle-{Guid.NewGuid():N}"),
            factory.Globals, factory);
        server.Start();
        using var client = new TestClient(server.SocketPath);
        CompositorSessionTests.Handshake(client);
        var (_, path) = CompositorSessionTests.SetupShm(client);
        try
        {
            client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(9),
                WireArgument.String(IdleInhibitHandler.Interface), WireArgument.UInt32(1), WireArgument.NewId(10));
            client.RegisterObject(10, IdleInhibitHandler.Interface);
            CreateSurface(client, 8);
            client.SendRequest(10, IdleInhibitHandler.Interface, "create_inhibitor", WireArgument.NewId(11), WireArgument.Object(8));
            client.RegisterObject(11, "zwp_idle_inhibitor_v1");
            Sync(client, 20);
            Assert.False(scene.IdleInhibited());

            Commit(client, 8, 7);
            Sync(client, 21);
            Assert.True(scene.IdleInhibited());
            var video = scene.Surfaces.Single(s => s.Id == 8);

            CreateSurface(client, 12);
            client.SendRequest(4, "wl_compositor", "create_region", WireArgument.NewId(13));
            client.RegisterObject(13, "wl_region");
            client.SendRequest(13, "wl_region", "add", WireArgument.Int32(0), WireArgument.Int32(0),
                WireArgument.Int32(CompositorSessionTests.Width), WireArgument.Int32(CompositorSessionTests.Height));
            client.SendRequest(12, "wl_surface", "set_opaque_region", WireArgument.Object(13));
            Commit(client, 12, 7);
            Sync(client, 22);
            var cover = scene.Surfaces.Single(s => s.Id == 12);
            cover.Place(video.Position.X, video.Position.Y, video.Width, video.Height);
            Assert.False(scene.IdleInhibited());

            Commit(client, 12, 0);
            Sync(client, 23);
            Assert.True(scene.IdleInhibited());
            client.SendRequest(11, "zwp_idle_inhibitor_v1", "destroy");
            Sync(client, 24);
            Assert.False(scene.IdleInhibited());

            client.SendRequest(10, IdleInhibitHandler.Interface, "create_inhibitor", WireArgument.NewId(14), WireArgument.Object(8));
            Sync(client, 25);
            Assert.True(scene.IdleInhibited());
            client.SendRequest(8, "wl_surface", "destroy");
            Sync(client, 26);
            Assert.False(scene.IdleInhibited());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ScreenPowerRestartsIdleCountdownAfterInhibitionEnds()
    {
        var timeout = (long)ScreenPower.IdleBeforeBlank.TotalMilliseconds;
        long now = 0;
        var power = new ScreenPower(() => now);
        Assert.Equal(ScreenPowerAction.None, power.Tick(inhibited: true));
        now = timeout * 3;
        Assert.Equal(ScreenPowerAction.None, power.Tick(inhibited: true));
        Assert.Null(power.UntilIdle);
        Assert.Equal(ScreenPowerAction.None, power.Tick(inhibited: false));
        Assert.Equal(ScreenPower.IdleBeforeBlank, power.UntilIdle);
        now += timeout;
        Assert.Equal(ScreenPowerAction.Blank, power.Tick());
    }

    private static void CreateSurface(TestClient client, uint id)
    {
        client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(id));
        client.RegisterObject(id, "wl_surface");
    }

    private static void Commit(TestClient client, uint surface, uint buffer)
    {
        client.SendRequest(surface, "wl_surface", "attach",
            WireArgument.Object(buffer), WireArgument.Int32(0), WireArgument.Int32(0));
        client.SendRequest(surface, "wl_surface", "commit");
    }

    private static void Sync(TestClient client, uint callback)
    {
        client.SendRequest(1, "wl_display", "sync", WireArgument.NewId(callback));
        client.RegisterObject(callback, "wl_callback");
        client.ReadUntil(callback, "done");
    }
}
