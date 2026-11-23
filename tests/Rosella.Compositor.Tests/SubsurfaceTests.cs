using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

public class SubsurfaceTests
{
    [Fact]
    public void SynchronizedChild_PositionStackingAndVisibilityFollowParentCommit()
    {
        var scene = new Scene();
        using var server = new DisplayServer(Path.Combine(Path.GetTempPath(), $"rosella-sub-{Guid.NewGuid():N}"), handlerFactory: new CompositorHandlerFactory(scene));
        server.Start();
        using var client = new TestClient(server.SocketPath);
        CompositorSessionTests.Handshake(client);
        var (_, path) = CompositorSessionTests.SetupShm(client);
        try
        {
            client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(2), WireArgument.String("wl_subcompositor"), WireArgument.UInt32(1), WireArgument.NewId(10));
            client.RegisterObject(10, "wl_subcompositor");
            foreach (var id in new uint[] { 11, 12 })
            {
                client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(id));
                client.RegisterObject(id, "wl_surface");
            }
            client.SendRequest(10, "wl_subcompositor", "get_subsurface", WireArgument.NewId(13), WireArgument.Object(12), WireArgument.Object(11));
            client.RegisterObject(13, "wl_subsurface");
            client.SendRequest(13, "wl_subsurface", "set_position", WireArgument.Int32(10), WireArgument.Int32(8));
            client.SendRequest(12, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            client.SendRequest(12, "wl_surface", "commit");
            client.SendRequest(11, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            Sync(client, 14);
            var parent = scene.Surfaces.Single(s => s.Id == 11);
            var child = scene.Surfaces.Single(s => s.Id == 12);
            Assert.False(child.IsMapped);
            Assert.Null(scene.HitTest(parent.Position.X + 11, parent.Position.Y + 9));
            client.SendRequest(11, "wl_surface", "commit");
            Sync(client, 15);
            Assert.True(child.IsMapped);
            Assert.Equal((parent.Position.X + 10, parent.Position.Y + 8), child.Position);
            Assert.Same(child, scene.HitTest(child.Position.X, child.Position.Y));
            client.SendRequest(13, "wl_subsurface", "place_below", WireArgument.Object(11));
            Sync(client, 16);
            Assert.Same(child, scene.HitTest(child.Position.X, child.Position.Y));
            client.SendRequest(11, "wl_surface", "commit");
            Sync(client, 17);
            Assert.Same(parent, scene.HitTest(child.Position.X, child.Position.Y));
            client.SendRequest(13, "wl_subsurface", "set_desync");
            client.SendRequest(12, "wl_surface", "attach", WireArgument.Object(0), WireArgument.Int32(0), WireArgument.Int32(0));
            client.SendRequest(12, "wl_surface", "commit");
            Sync(client, 18);
            Assert.False(child.IsMapped);
            client.SendRequest(13, "wl_subsurface", "destroy");
            Sync(client, 19);
            Assert.Null(child.Subsurface!.Parent);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void HiddenAncestorClearsDescendantFocusAndScalingMapsInput()
    {
        var scene = new Scene();
        var seat = new SeatState();
        using var server = new DisplayServer(Path.Combine(Path.GetTempPath(), $"rosella-sub-{Guid.NewGuid():N}"), handlerFactory: new CompositorHandlerFactory(scene, seat));
        server.Start();
        using var client = new TestClient(server.SocketPath);
        CompositorSessionTests.Handshake(client);
        var (_, path) = CompositorSessionTests.SetupShm(client);
        try
        {
            SetupNested(client);
            foreach (var id in new uint[] { 11, 12, 13 })
            {
                client.SendRequest(id, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
                client.SendRequest(id, "wl_surface", "commit");
            }
            // Nested synchronized state needs another intermediate and root commit.
            client.SendRequest(12, "wl_surface", "commit");
            client.SendRequest(11, "wl_surface", "commit");
            Sync(client, 30);
            var parent = scene.Surfaces.Single(s => s.Id == 11);
            var child = scene.Surfaces.Single(s => s.Id == 12);
            var nested = scene.Surfaces.Single(s => s.Id == 13);
            parent.Place(20, 30, parent.ContentWidth * 2, parent.ContentHeight * 3);
            scene.SetSubsurfacePosition(child.Subsurface!, (4, 5));
            client.SendRequest(11, "wl_surface", "commit");
            Sync(client, 31);
            Assert.Equal((28, 45), child.Position);
            Assert.Equal(child.ContentWidth * 2, child.Width);
            Assert.Equal(child.ContentHeight * 3, child.Height);
            Assert.Equal(3, child.LocalX(child.Position.X + 6));
            Assert.Equal(2, child.LocalY(child.Position.Y + 6));
            seat.SetPointerFocus(nested, 0, 0);
            scene.Minimize(parent);
            Assert.Null(seat.PointerFocus);
            // Simulate a hit-test result retained across minimisation.
            seat.SetPointerFocus(nested, 0, 0);
            seat.SetKeyboardFocus(nested, []);
            Assert.Null(seat.PointerFocus);
            Assert.Null(seat.KeyboardFocus);
            scene.Restore(parent);
            seat.SetPointerFocus(nested, 0, 0);
            client.SendRequest(11, "wl_surface", "attach", WireArgument.Object(0), WireArgument.Int32(0), WireArgument.Int32(0));
            client.SendRequest(11, "wl_surface", "commit");
            Sync(client, 32);
            Assert.Null(seat.PointerFocus);
            seat.SetPointerFocus(nested, 0, 0);
            seat.SetKeyboardFocus(nested, []);
            Assert.Null(seat.PointerFocus);
            Assert.Null(seat.KeyboardFocus);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DeepSubsurfacePositionRemainsPractical()
    {
        var scene = new Scene();
        var current = new SurfaceHandler(scene) { Position = (10, 20) };
        for (var i = 0; i < 100; i++)
        {
            var child = new SurfaceHandler(scene);
            var role = new SubsurfaceHandler(child, current) { Position = (1, 2) };
            child.AssignSubsurface(role);
            current = child;
        }
        Assert.Equal((110, 220), current.Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubsurfaceDepthLimitRejectsTopDownAndBottomUpTrees(bool bottomUp)
    {
        var scene = new Scene();
        using var server = new DisplayServer(Path.Combine(Path.GetTempPath(), $"rosella-sub-{Guid.NewGuid():N}"), handlerFactory: new CompositorHandlerFactory(scene));
        server.Start();
        using var client = new TestClient(server.SocketPath);
        CompositorSessionTests.Handshake(client);
        var (_, path) = CompositorSessionTests.SetupShm(client);
        try
        {
            client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(2), WireArgument.String("wl_subcompositor"), WireArgument.UInt32(1), WireArgument.NewId(10));
            client.RegisterObject(10, "wl_subcompositor");
            const uint firstSurface = 100;
            const uint firstRole = 1000;
            for (var i = 0; i <= Scene.MaxSubsurfaceDepth + 1; i++)
            {
                client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(firstSurface + (uint)i));
                client.RegisterObject(firstSurface + (uint)i, "wl_surface");
            }
            for (var i = 0; i < Scene.MaxSubsurfaceDepth; i++)
            {
                var edge = bottomUp ? Scene.MaxSubsurfaceDepth - i : i + 1;
                client.SendRequest(10, "wl_subcompositor", "get_subsurface", WireArgument.NewId(firstRole + (uint)i),
                    WireArgument.Object(firstSurface + (uint)edge), WireArgument.Object(firstSurface + (uint)edge - 1));
                client.RegisterObject(firstRole + (uint)i, "wl_subsurface");
            }
            // Exercise nested cached commit application at the maximum depth.
            for (var i = Scene.MaxSubsurfaceDepth; i >= 0; i--)
            {
                client.SendRequest(firstSurface + (uint)i, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
                client.SendRequest(firstSurface + (uint)i, "wl_surface", "commit");
            }
            Sync(client, 20);
            scene.Render(new byte[64 * 64 * 4], 64, 64, 64 * 4, 0);
            _ = scene.HitTest(0, 0);
            var deepest = scene.Surfaces.Single(surface => surface.Id == firstSurface + Scene.MaxSubsurfaceDepth);
            _ = deepest.Position;
            _ = deepest.Width;
            _ = deepest.Height;
            _ = deepest.Subsurface!.EffectiveSync;
            scene.SetSubsurfaceSync(scene.Surfaces.Single(surface => surface.Id == firstSurface + 1).Subsurface!, false);
            var child = bottomUp ? firstSurface : firstSurface + (uint)Scene.MaxSubsurfaceDepth + 1;
            var parent = bottomUp ? firstSurface + (uint)Scene.MaxSubsurfaceDepth + 1 : firstSurface + (uint)Scene.MaxSubsurfaceDepth;
            client.SendRequest(10, "wl_subcompositor", "get_subsurface", WireArgument.NewId(firstRole + (uint)Scene.MaxSubsurfaceDepth), WireArgument.Object(child), WireArgument.Object(parent));
            var error = client.ReadUntil(1, "error");
            Assert.Equal(1u, error.Arguments[0].AsObject());
            Assert.Equal(ClientSession.ErrorNoMemory, error.Arguments[1].AsUInt32());
            CompositorSessionTests.WaitFor(() => scene.Surfaces.Count, count => count == 0);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CachedCommitDepthIsBoundedEvenAfterReattachment()
    {
        var scene = new Scene();
        var parent = new SurfaceHandler(scene);
        var child = new SurfaceHandler(scene);
        var role = new SubsurfaceHandler(child, parent);
        child.AssignSubsurface(role);
        parent.PendingChildren.Add(child);
        var cancelled = 0;
        child.CachedCommits.Add(new CachedSurfaceCommit(() => { }, () => cancelled++, Depth: Scene.MaxSubsurfaceDepth));
        var grandparent = new SurfaceHandler(scene);
        parent.AssignSubsurface(new SubsurfaceHandler(parent, grandparent));
        var error = Assert.Throws<ProtocolErrorException>(() => scene.ApplySurfaceCommit(parent, new CachedSurfaceCommit(() => { }, () => cancelled++)));
        Assert.Equal(ClientSession.ErrorNoMemory, error.Code);
        Assert.Equal(2, cancelled);
    }

    [Fact]
    public void SynchronizedCommitCacheIsBoundedAndReleasesAccounting()
    {
        var scene = new Scene();
        var parent = new SurfaceHandler(scene);
        var child = new SurfaceHandler(scene);
        var role = new SubsurfaceHandler(child, parent);
        child.AssignSubsurface(role);
        var cancelled = 0;
        for (var i = 0; i < Scene.MaxCachedCommitCount; i++)
        {
            scene.ApplySurfaceCommit(child, new CachedSurfaceCommit(() => { }, () => cancelled++));
        }
        Assert.Throws<ProtocolErrorException>(() => scene.ApplySurfaceCommit(child, new CachedSurfaceCommit(() => { }, () => cancelled++)));
        Assert.Equal(1, cancelled);
        scene.SetSubsurfaceSync(role, false);
        scene.SetSubsurfaceSync(role, true);
        scene.ApplySurfaceCommit(child, new CachedSurfaceCommit(() => { }, () => cancelled++, Scene.MaxCachedCommitBytes));
        Assert.Throws<ProtocolErrorException>(() => scene.ApplySurfaceCommit(child, new CachedSurfaceCommit(() => { }, () => cancelled++, 1)));
        scene.RemoveSubsurface(role);
        Assert.Equal(3, cancelled);
    }

    [Fact]
    public void CacheBudgetIsIsolatedBetweenClients()
    {
        var scene = new Scene();
        using var server = new DisplayServer(Path.Combine(Path.GetTempPath(), $"rosella-sub-{Guid.NewGuid():N}"), handlerFactory: new CompositorHandlerFactory(scene));
        server.Start();
        using var first = new TestClient(server.SocketPath);
        using var second = new TestClient(server.SocketPath);
        CompositorSessionTests.Handshake(first);
        CompositorSessionTests.Handshake(second);
        SetupNested(first);
        SetupNested(second);
        Sync(first, 30);
        Sync(second, 30);
        var children = scene.Surfaces.Where(s => s.Id == 12).ToArray();
        Assert.Equal(2, children.Length);
        Assert.NotSame(children[0].Session, children[1].Session);
        for (var i = 0; i < Scene.MaxCachedCommitCount; i++)
        {
            scene.ApplySurfaceCommit(children[0], new CachedSurfaceCommit(() => { }, () => { }));
        }
        scene.ApplySurfaceCommit(children[1], new CachedSurfaceCommit(() => { }, () => { }, Scene.MaxCachedCommitBytes));
        Assert.Throws<ProtocolErrorException>(() => scene.ApplySurfaceCommit(children[0], new CachedSurfaceCommit(() => { }, () => { })));
        Assert.Throws<ProtocolErrorException>(() => scene.ApplySurfaceCommit(children[1], new CachedSurfaceCommit(() => { }, () => { }, 1)));
        scene.RemoveSubsurface(children[0].Subsurface!);
        scene.RemoveSubsurface(children[1].Subsurface!);
    }

    [Fact]
    public void CacheLimitDisconnectsClientAndSessionTeardownReclaimsBudget()
    {
        var scene = new Scene();
        using var server = new DisplayServer(Path.Combine(Path.GetTempPath(), $"rosella-sub-{Guid.NewGuid():N}"), handlerFactory: new CompositorHandlerFactory(scene));
        server.Start();
        using var client = new TestClient(server.SocketPath);
        CompositorSessionTests.Handshake(client);
        var (_, path) = CompositorSessionTests.SetupShm(client);
        try
        {
            SetupNested(client);
            for (var i = 0; i <= Scene.MaxCachedCommitCount; i++)
            {
                client.SendRequest(12, "wl_surface", "commit");
            }
            var error = client.ReadUntil(1, "error");
            Assert.Equal(1u, error.Arguments[0].AsObject());
            Assert.Equal(ClientSession.ErrorNoMemory, error.Arguments[1].AsUInt32());
            CompositorSessionTests.WaitFor(() => scene.Surfaces.Count, count => count == 0);
            var parent = new SurfaceHandler(scene);
            var child = new SurfaceHandler(scene);
            var role = new SubsurfaceHandler(child, parent);
            child.AssignSubsurface(role);
            for (var i = 0; i < Scene.MaxCachedCommitCount; i++)
            {
                scene.ApplySurfaceCommit(child, new CachedSurfaceCommit(() => { }, () => { }));
            }
            scene.RemoveSubsurface(role);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void CursorDirtyNotificationDoesNotHoldSeatGate()
    {
        var seat = new SeatState();
        var cursor = new SurfaceHandler(new Scene());
        var type = typeof(SeatState);
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var gate = type.GetField("_gate", flags)!.GetValue(seat)!;
        type.GetField("_cursorSurface", flags)!.SetValue(seat, cursor);
        var notified = false;
        seat.Dirty = () => { Assert.False(Monitor.IsEntered(gate)); notified = true; };
        seat.OnSurfaceHidden(cursor, true);
        Assert.True(notified);
    }

    [Theory]
    [InlineData(false, 0u)]
    [InlineData(true, 1u)]
    public void InvalidRoleOrParent_ProducesProtocolError(bool cycle, uint expectedCode)
    {
        var scene = new Scene();
        using var server = new DisplayServer(Path.Combine(Path.GetTempPath(), $"rosella-sub-{Guid.NewGuid():N}"), handlerFactory: new CompositorHandlerFactory(scene));
        server.Start();
        using var client = new TestClient(server.SocketPath);
        CompositorSessionTests.Handshake(client);
        client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(2), WireArgument.String("wl_subcompositor"), WireArgument.UInt32(1), WireArgument.NewId(10));
        client.RegisterObject(10, "wl_subcompositor");
        foreach (var id in new uint[] { 11, 12 })
        {
            client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(id));
            client.RegisterObject(id, "wl_surface");
        }
        client.SendRequest(10, "wl_subcompositor", "get_subsurface", WireArgument.NewId(13), WireArgument.Object(12), WireArgument.Object(11));
        client.RegisterObject(13, "wl_subsurface");
        client.SendRequest(10, "wl_subcompositor", "get_subsurface", WireArgument.NewId(14), WireArgument.Object(cycle ? 11u : 12u), WireArgument.Object(cycle ? 12u : 11u));
        var error = client.ReadUntil(1, "error");
        Assert.Equal(10u, error.Arguments[0].AsObject());
        Assert.Equal(expectedCode, error.Arguments[1].AsUInt32());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NestedSynchronizedCommit_WaitsForIntermediateCommit(bool childDesynchronised)
    {
        var scene = new Scene();
        using var server = new DisplayServer(Path.Combine(Path.GetTempPath(), $"rosella-sub-{Guid.NewGuid():N}"), handlerFactory: new CompositorHandlerFactory(scene));
        server.Start();
        using var client = new TestClient(server.SocketPath);
        CompositorSessionTests.Handshake(client);
        var (_, path) = CompositorSessionTests.SetupShm(client);
        try
        {
            SetupNested(client);
            if (childDesynchronised) { client.SendRequest(15, "wl_subsurface", "set_desync"); }
            client.SendRequest(13, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            client.SendRequest(13, "wl_surface", "commit");
            Sync(client, 16);
            var child = scene.Surfaces.Single(s => s.Id == 13);
            Assert.False(child.IsMapped);
            client.SendRequest(11, "wl_surface", "commit");
            Sync(client, 17);
            Assert.False(child.IsMapped);
            client.SendRequest(12, "wl_surface", "commit");
            client.SendRequest(11, "wl_surface", "commit");
            Sync(client, 18);
            Assert.True(child.IsMapped);
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DesynchronisingAncestor_OnlyFlushesUnlinkedChildCommits(bool linked)
    {
        var scene = new Scene();
        using var server = new DisplayServer(Path.Combine(Path.GetTempPath(), $"rosella-sub-{Guid.NewGuid():N}"), handlerFactory: new CompositorHandlerFactory(scene));
        server.Start();
        using var client = new TestClient(server.SocketPath);
        CompositorSessionTests.Handshake(client);
        var (_, path) = CompositorSessionTests.SetupShm(client);
        try
        {
            SetupNested(client);
            client.SendRequest(15, "wl_subsurface", "set_desync");
            client.SendRequest(13, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            client.SendRequest(13, "wl_surface", "commit");
            if (linked) { client.SendRequest(12, "wl_surface", "commit"); }
            client.SendRequest(14, "wl_subsurface", "set_desync");
            Sync(client, 16);
            Assert.True(scene.Surfaces.Single(s => s.Id == 13).IsMapped);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DestroyingAncestorWithCachedChildCommit_DoesNotResurrectItAndFreesCallback()
    {
        var scene = new Scene();
        using var server = new DisplayServer(Path.Combine(Path.GetTempPath(), $"rosella-sub-{Guid.NewGuid():N}"), handlerFactory: new CompositorHandlerFactory(scene));
        server.Start();
        using var client = new TestClient(server.SocketPath);
        CompositorSessionTests.Handshake(client);
        var (_, path) = CompositorSessionTests.SetupShm(client);
        try
        {
            SetupNested(client);
            client.SendRequest(13, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            client.SendRequest(13, "wl_surface", "frame", WireArgument.NewId(16));
            client.RegisterObject(16, "wl_callback");
            client.SendRequest(13, "wl_surface", "commit");
            client.SendRequest(12, "wl_surface", "commit");
            client.SendRequest(14, "wl_subsurface", "destroy");
            client.SendRequest(15, "wl_subsurface", "destroy");
            Sync(client, 17);
            Assert.False(scene.Surfaces.Single(s => s.Id == 13).IsMapped);
            // Reusing the callback id proves that destruction freed it.
            Sync(client, 16);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void DestroyingChildCapturedByParentCommit_DoesNotRestoreContentOrCallback()
    {
        var scene = new Scene();
        using var server = new DisplayServer(Path.Combine(Path.GetTempPath(), $"rosella-sub-{Guid.NewGuid():N}"), handlerFactory: new CompositorHandlerFactory(scene));
        server.Start();
        using var client = new TestClient(server.SocketPath);
        CompositorSessionTests.Handshake(client);
        var (_, path) = CompositorSessionTests.SetupShm(client);
        try
        {
            SetupNested(client);
            client.SendRequest(13, "wl_surface", "attach", WireArgument.Object(7), WireArgument.Int32(0), WireArgument.Int32(0));
            client.SendRequest(13, "wl_surface", "frame", WireArgument.NewId(16));
            client.RegisterObject(16, "wl_callback");
            client.SendRequest(13, "wl_surface", "commit");
            client.SendRequest(12, "wl_surface", "commit");
            client.SendRequest(15, "wl_subsurface", "destroy");
            Sync(client, 17);
            Sync(client, 16);
            client.SendRequest(11, "wl_surface", "commit");
            Sync(client, 18);
            Assert.False(scene.Surfaces.Single(s => s.Id == 13).IsMapped);
        }
        finally { File.Delete(path); }
    }

    private static void SetupNested(TestClient client)
    {
        client.SendRequest(2, "wl_registry", "bind", WireArgument.UInt32(2), WireArgument.String("wl_subcompositor"), WireArgument.UInt32(1), WireArgument.NewId(10));
        client.RegisterObject(10, "wl_subcompositor");
        foreach (var id in new uint[] { 11, 12, 13 })
        {
            client.SendRequest(4, "wl_compositor", "create_surface", WireArgument.NewId(id));
            client.RegisterObject(id, "wl_surface");
        }
        client.SendRequest(10, "wl_subcompositor", "get_subsurface", WireArgument.NewId(14), WireArgument.Object(12), WireArgument.Object(11));
        client.RegisterObject(14, "wl_subsurface");
        client.SendRequest(10, "wl_subcompositor", "get_subsurface", WireArgument.NewId(15), WireArgument.Object(13), WireArgument.Object(12));
        client.RegisterObject(15, "wl_subsurface");
    }

    private static void Sync(TestClient client, uint id)
    {
        client.SendRequest(1, "wl_display", "sync", WireArgument.NewId(id));
        client.RegisterObject(id, "wl_callback");
        client.ReadUntil(id, "done");
    }
}
