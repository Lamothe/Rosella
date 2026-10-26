using Rosella.Protocol;

namespace Rosella.Server.Tests;

public class DisplayServerTests
{
    [Fact]
    public void GetRegistry_AdvertisesStandardGlobals()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            using var server = StartServer(directory);
            using var client = new TestClient(server.SocketPath);
            var (registryId, globals) = GetRegistry(client);

            Assert.Equal(2u, registryId);
            Assert.Equal(
                StandardGlobals.Create().Select(g => (g.Name, g.Interface, g.Version)),
                globals.Select(g => (g.Arguments[0].AsUInt32(), g.Arguments[1].AsString(), g.Arguments[2].AsUInt32())));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Sync_ReturnsDoneThenDeleteId()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            using var server = StartServer(directory);
            using var client = new TestClient(server.SocketPath);

            client.SendRequest(1, "wl_display", "sync", WireArgument.NewId(3));
            client.RegisterObject(3, "wl_callback");

            var done = client.ReadEvent();
            Assert.Equal(3u, done.ObjectId);
            Assert.Equal(0, done.Opcode);
            Assert.Equal(1u, done.Arguments[0].AsUInt32()); // serial starts at 1

            var deleteId = client.ReadEvent();
            Assert.Equal(1u, deleteId.ObjectId);
            Assert.Equal(1, deleteId.Opcode);
            Assert.Equal(3u, deleteId.Arguments[0].AsUInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Bind_WlShm_SendsFormats_AndReleaseFreesId()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            using var server = StartServer(directory);
            using var client = new TestClient(server.SocketPath);
            var (registryId, _) = GetRegistry(client);

            client.SendRequest(
                registryId,
                "wl_registry",
                "bind",
                WireArgument.UInt32(3),
                WireArgument.String("wl_shm"),
                WireArgument.UInt32(3),
                WireArgument.NewId(4));
            client.RegisterObject(4, "wl_shm");

            var format0 = client.ReadEvent();
            Assert.Equal(4u, format0.ObjectId);
            Assert.Equal(0u, format0.Arguments[0].AsUInt32()); // argb8888
            var format1 = client.ReadEvent();
            Assert.Equal(1u, format1.Arguments[0].AsUInt32()); // xrgb8888

            client.SendRequest(4, "wl_shm", "release");

            var deleteId = client.ReadEvent();
            Assert.Equal(1u, deleteId.ObjectId);
            Assert.Equal(4u, deleteId.Arguments[0].AsUInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Bind_WlSeat_SendsCapabilitiesAndName()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            using var server = StartServer(directory);
            using var client = new TestClient(server.SocketPath);
            var (registryId, _) = GetRegistry(client);

            client.SendRequest(
                registryId,
                "wl_registry",
                "bind",
                WireArgument.UInt32(4),
                WireArgument.String("wl_seat"),
                WireArgument.UInt32(11),
                WireArgument.NewId(6));
            client.RegisterObject(6, "wl_seat");

            var capabilities = client.ReadEvent();
            Assert.Equal(6u, capabilities.ObjectId);
            Assert.Equal(0u, capabilities.Arguments[0].AsUInt32()); // no input devices yet

            var name = client.ReadEvent();
            Assert.Equal("rosella", name.Arguments[0].AsString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Bind_InvalidVersion_ReportsError()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            using var server = StartServer(directory);
            using var client = new TestClient(server.SocketPath);
            var (registryId, _) = GetRegistry(client);

            client.SendRequest(
                registryId,
                "wl_registry",
                "bind",
                WireArgument.UInt32(4),
                WireArgument.String("wl_seat"),
                WireArgument.UInt32(999),
                WireArgument.NewId(6));

            var error = client.ReadEvent();
            Assert.Equal(1u, error.ObjectId);
            Assert.Equal(registryId, error.Arguments[0].AsObject());
            Assert.Equal(ClientSession.ErrorInvalidMethod, error.Arguments[1].AsUInt32());
            Assert.NotEqual(string.Empty, error.Arguments[2].AsString());

            // The connection closes after a fatal error.
            Assert.Throws<InvalidOperationException>(() => client.ReadEvent());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnknownObject_ReportsInvalidObject()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            using var server = StartServer(directory);
            using var client = new TestClient(server.SocketPath);

            // Hand-written frame: object 99, opcode 0, 8-byte message.
            client.SendRaw([0x63, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00]);

            var error = client.ReadEvent();
            Assert.Equal(1u, error.ObjectId);
            Assert.Equal(1u, error.Arguments[0].AsObject()); // unknown ids are reported on the display
            Assert.Equal(ClientSession.ErrorInvalidObject, error.Arguments[1].AsUInt32());
            Assert.Equal("invalid object 99", error.Arguments[2].AsString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void UnknownOpcode_ReportsInvalidMethod()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            using var server = StartServer(directory);
            using var client = new TestClient(server.SocketPath);

            // Hand-written frame: wl_display (object 1), opcode 7, 8-byte message.
            client.SendRaw([0x01, 0x00, 0x00, 0x00, 0x07, 0x00, 0x08, 0x00]);

            var error = client.ReadEvent();
            Assert.Equal(1u, error.ObjectId);
            Assert.Equal(1u, error.Arguments[0].AsObject()); // the error names wl_display
            Assert.Equal(ClientSession.ErrorInvalidMethod, error.Arguments[1].AsUInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void CreatePool_PassesFileDescriptor_EndToEnd()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            using var server = StartServer(directory);
            using var client = new TestClient(server.SocketPath);
            var (registryId, _) = GetRegistry(client);

            client.SendRequest(
                registryId,
                "wl_registry",
                "bind",
                WireArgument.UInt32(3),
                WireArgument.String("wl_shm"),
                WireArgument.UInt32(3),
                WireArgument.NewId(4));
            client.RegisterObject(4, "wl_shm");
            client.ReadEvent(); // format
            client.ReadEvent(); // format

            var poolPath = Path.Combine(directory, "pool.bin");
            File.WriteAllBytes(poolPath, new byte[4096]);
            using (var pool = File.OpenHandle(poolPath, FileMode.Open, FileAccess.Read))
            {
                client.SendRequest(
                    4,
                    "wl_shm",
                    "create_pool",
                    WireArgument.NewId(5),
                    WireArgument.FileDescriptor(unchecked((int)pool.DangerousGetHandle())),
                    WireArgument.Int32(4096));
            }

            client.RegisterObject(5, "wl_shm_pool");
            client.SendRequest(5, "wl_shm_pool", "destroy");

            var deleteId = client.ReadEvent();
            Assert.Equal(1u, deleteId.ObjectId);
            Assert.Equal(5u, deleteId.Arguments[0].AsUInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RequestToObjectIdZero_ReportsError()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            using var server = StartServer(directory);
            using var client = new TestClient(server.SocketPath);

            // Hand-written frame targeting reserved object id 0.
            client.SendRaw([0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x08, 0x00]);

            var error = client.ReadEvent();
            Assert.Equal(1u, error.Arguments[0].AsObject());
            Assert.Equal(ClientSession.ErrorInvalidObject, error.Arguments[1].AsUInt32());
            Assert.Equal("invalid object 0", error.Arguments[2].AsString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DuplicateNewId_ReportsError()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            using var server = StartServer(directory);
            using var client = new TestClient(server.SocketPath);
            GetRegistry(client);

            // Object 2 is already the registry.
            client.SendRequest(1, "wl_display", "get_registry", WireArgument.NewId(2));

            var error = client.ReadEvent();
            Assert.Equal(1u, error.Arguments[0].AsObject());
            Assert.Equal(ClientSession.ErrorInvalidObject, error.Arguments[1].AsUInt32());
            Assert.Equal("invalid new id 2 (already in use)", error.Arguments[2].AsString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void ServerRangeNewId_ReportsError()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            using var server = StartServer(directory);
            using var client = new TestClient(server.SocketPath);

            // 0xFF000000 is the server's id range; clients must not allocate it.
            client.SendRequest(1, "wl_display", "sync", WireArgument.NewId(0xFF000000));

            var error = client.ReadEvent();
            Assert.Equal(1u, error.Arguments[0].AsObject());
            Assert.Equal(ClientSession.ErrorInvalidObject, error.Arguments[1].AsUInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Bind_LowVersion_GatesEventsBySince()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            using var server = StartServer(directory);
            using var client = new TestClient(server.SocketPath);
            var (registryId, _) = GetRegistry(client);

            client.SendRequest(
                registryId,
                "wl_registry",
                "bind",
                WireArgument.UInt32(5),
                WireArgument.String("wl_output"),
                WireArgument.UInt32(1),
                WireArgument.NewId(8));
            client.RegisterObject(8, "wl_output");

            var geometry = client.ReadEvent();
            Assert.Equal(8u, geometry.ObjectId);
            Assert.Equal(0, geometry.Opcode);
            var mode = client.ReadEvent();
            Assert.Equal(1, mode.Opcode);

            // done/scale (since 2) and name/description (since 4) must not be
            // sent at version 1: the callback's done must arrive next.
            client.SendRequest(1, "wl_display", "sync", WireArgument.NewId(3));
            client.RegisterObject(3, "wl_callback");
            Assert.Equal(3u, client.ReadEvent().ObjectId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void RequestBeyondBoundVersion_ReportsInvalidMethod()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            using var server = StartServer(directory);
            using var client = new TestClient(server.SocketPath);
            var (registryId, _) = GetRegistry(client);

            client.SendRequest(
                registryId,
                "wl_registry",
                "bind",
                WireArgument.UInt32(4),
                WireArgument.String("wl_seat"),
                WireArgument.UInt32(1),
                WireArgument.NewId(6));
            client.RegisterObject(6, "wl_seat");

            var capabilities = client.ReadEvent();
            Assert.Equal(6u, capabilities.ObjectId);
            Assert.Equal(0, capabilities.Opcode); // capabilities (since 1); name (since 2) is gated

            client.SendRequest(6, "wl_seat", "release"); // release is since version 5

            var error = client.ReadEvent();
            Assert.Equal(1u, error.ObjectId);
            Assert.Equal(6u, error.Arguments[0].AsObject()); // reported on the seat object
            Assert.Equal(ClientSession.ErrorInvalidMethod, error.Arguments[1].AsUInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Bind_DuplicateNewId_ReportsOnRegistry()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            using var server = StartServer(directory);
            using var client = new TestClient(server.SocketPath);
            var (registryId, _) = GetRegistry(client);

            // Object 2 is the registry itself; the violation is attributed to
            // the requesting registry, not the display.
            client.SendRequest(
                registryId,
                "wl_registry",
                "bind",
                WireArgument.UInt32(3),
                WireArgument.String("wl_shm"),
                WireArgument.UInt32(3),
                WireArgument.NewId(2));

            var error = client.ReadEvent();
            Assert.Equal(registryId, error.Arguments[0].AsObject());
            Assert.Equal(ClientSession.ErrorInvalidObject, error.Arguments[1].AsUInt32());
            Assert.Equal("invalid new id 2 (already in use)", error.Arguments[2].AsString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Bind_ServerRangeNewId_ReportsOnRegistry()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            using var server = StartServer(directory);
            using var client = new TestClient(server.SocketPath);
            var (registryId, _) = GetRegistry(client);

            client.SendRequest(
                registryId,
                "wl_registry",
                "bind",
                WireArgument.UInt32(3),
                WireArgument.String("wl_shm"),
                WireArgument.UInt32(3),
                WireArgument.NewId(0xFF000001));

            var error = client.ReadEvent();
            Assert.Equal(registryId, error.Arguments[0].AsObject());
            Assert.Equal(ClientSession.ErrorInvalidObject, error.Arguments[1].AsUInt32());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void TwoClients_AreIndependent()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            using var server = StartServer(directory);
            using var first = new TestClient(server.SocketPath);
            using var second = new TestClient(server.SocketPath);

            first.SendRequest(1, "wl_display", "sync", WireArgument.NewId(3));
            second.SendRequest(1, "wl_display", "sync", WireArgument.NewId(3));
            first.RegisterObject(3, "wl_callback");
            second.RegisterObject(3, "wl_callback");

            Assert.Equal(3u, first.ReadEvent().ObjectId);
            Assert.Equal(3u, second.ReadEvent().ObjectId);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DisplayServer StartServer(string directory)
    {
        var server = new DisplayServer(Path.Combine(directory, "wayland-0"));
        server.Start();
        return server;
    }

    /// <summary>Performs get_registry with a hand-written golden frame and consumes the global events.</summary>
    private static (uint RegistryId, IReadOnlyList<DecodedMessage> Globals) GetRegistry(TestClient client)
    {
        // wl_display.get_registry: object 1, opcode 1, 12 bytes, new_id 2.
        client.SendRaw([0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x0C, 0x00, 0x02, 0x00, 0x00, 0x00]);
        client.RegisterObject(2, "wl_registry");

        var globals = new List<DecodedMessage>();
        foreach (var expected in StandardGlobals.Create())
        {
            var message = client.ReadEvent();
            Assert.Equal(2u, message.ObjectId);
            Assert.Equal(0, message.Opcode);
            Assert.Equal(expected.Name, message.Arguments[0].AsUInt32());
            globals.Add(message);
        }

        return (2, globals);
    }
}
