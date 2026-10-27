using System.Buffers.Binary;
using System.Net.Sockets;
using Rosella.Protocol;
using Rosella.Transport;

namespace Rosella.SmokeClient;

/// <summary>
/// A tiny, dependency-free Wayland client for smoke-testing Rosella: opens a
/// real xdg-shell window (the full configure → ack → map dance) and draws an
/// animated gradient with a moving bar into an shm buffer, paced by frame
/// callbacks. Runs until Ctrl+C or the compositor closes the window. Also
/// serves as a reference for the wire format from the client side.
/// </summary>
internal static class Program
{
    private const uint DisplayId = 1;
    private const uint RegistryId = 2;
    private const uint SyncCallbackId = 3;
    private const uint CompositorId = 4;
    private const uint ShmId = 5;
    private const uint PoolId = 6;
    private const uint BufferId = 7;
    private const uint SurfaceId = 8;
    private const uint WmBaseId = 10;
    private const uint XdgSurfaceId = 11;
    private const uint XdgToplevelId = 12;
    private const uint FirstFrameCallbackId = 13;

    private const int Width = 640;
    private const int Height = 360;
    private const int Stride = Width * 4;

    private static readonly Dictionary<uint, string> Interfaces = new()
    {
        [DisplayId] = "wl_display",
        [RegistryId] = "wl_registry",
        [SyncCallbackId] = "wl_callback",
        [CompositorId] = "wl_compositor",
        [ShmId] = "wl_shm",
        [PoolId] = "wl_shm_pool",
        [BufferId] = "wl_buffer",
        [SurfaceId] = "wl_surface",
        [WmBaseId] = "xdg_wm_base",
        [XdgSurfaceId] = "xdg_surface",
        [XdgToplevelId] = "xdg_toplevel",
    };

    // Frame-callback ids are recycled only after wl_display.delete_id, as the
    // protocol requires. In-flight callbacks track which ids are callbacks.
    private static readonly HashSet<uint> CallbackIds = [];
    private static readonly Queue<uint> FreeCallbackIds = new();
    private static uint _nextCallbackId = FirstFrameCallbackId;

    public static int Main(string[] args)
    {
        var socketPath = ResolveSocket(args);
        Console.WriteLine($"rosella-smoke: connecting to {socketPath}");
        if (!File.Exists(socketPath))
        {
            Console.Error.WriteLine($"rosella-smoke: no socket at {socketPath}");
            return 1;
        }

        try
        {
            Run(socketPath);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"rosella-smoke: {exception.Message}");
            return 1;
        }
    }

    private static void Run(string socketPath)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(socketPath));
        socket.ReceiveTimeout = 30000;
        using var connection = new WireConnection(socket);
        using var stream = new MessageStream();

        var globals = Handshake(connection, stream);

        // The pool: a temp file handed to the compositor as a descriptor.
        var poolPath = Path.GetTempFileName();
        File.WriteAllBytes(poolPath, new byte[Stride * Height]);
        using var poolHandle = File.OpenHandle(poolPath, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);

        Request(connection, ShmId, "wl_shm", "create_pool", WireArgument.NewId(PoolId), WireArgument.FileDescriptor(unchecked((int)poolHandle.DangerousGetHandle())), WireArgument.Int32(Stride * Height));
        Request(connection, PoolId, "wl_shm_pool", "create_buffer", WireArgument.NewId(BufferId), WireArgument.Int32(0), WireArgument.Int32(Width), WireArgument.Int32(Height), WireArgument.Int32(Stride), WireArgument.UInt32(1));

        // The xdg-shell window: surface + xdg_surface + toplevel, then the
        // initial buffer-less commit that triggers the configure dance.
        Request(connection, CompositorId, "wl_compositor", "create_surface", WireArgument.NewId(SurfaceId));
        Request(connection, WmBaseId, "xdg_wm_base", "get_xdg_surface", WireArgument.NewId(XdgSurfaceId), WireArgument.Object(SurfaceId));
        Request(connection, XdgSurfaceId, "xdg_surface", "get_toplevel", WireArgument.NewId(XdgToplevelId));
        Request(connection, XdgToplevelId, "xdg_toplevel", "set_title", WireArgument.String("Rosella Smoke Test"));
        Request(connection, XdgToplevelId, "xdg_toplevel", "set_app_id", WireArgument.String("rosella-smoke"));
        Request(connection, SurfaceId, "wl_surface", "commit");
        Console.WriteLine("rosella-smoke: initial commit — waiting for configure");

        var frame = 0;
        var mapped = false;
        var buffer = new byte[Stride * Height];
        var receiveBuffer = new byte[4096];
        var receiveDescriptors = new int[WireConnection.MaxDescriptorsPerReceive];
        while (true)
        {
            if (mapped)
            {
                Draw(buffer, frame);
                RandomAccess.Write(poolHandle, new ReadOnlyMemory<byte>[] { buffer }, 0);
            }

            foreach (var message in ReadEvents(connection, stream, receiveBuffer, receiveDescriptors))
            {
                var eventName = EventName(message);
                if (message.ObjectId == DisplayId && eventName == "delete_id")
                {
                    // Ids may only be reused once delete_id arrives.
                    var freed = message.Arguments[0].AsUInt32();
                    if (CallbackIds.Remove(freed))
                    {
                        FreeCallbackIds.Enqueue(freed);
                    }

                    continue;
                }

                if (message.ObjectId == XdgToplevelId && eventName == "close")
                {
                    Console.WriteLine("rosella-smoke: compositor closed the window");
                    return;
                }

                if (message.ObjectId == XdgSurfaceId && eventName == "configure")
                {
                    var serial = message.Arguments[0].AsUInt32();
                    Request(connection, XdgSurfaceId, "xdg_surface", "ack_configure", WireArgument.UInt32(serial));
                    if (!mapped)
                    {
                        mapped = true;
                        Request(connection, SurfaceId, "wl_surface", "attach", WireArgument.Object(BufferId), WireArgument.Int32(0), WireArgument.Int32(0));
                        Request(connection, SurfaceId, "wl_surface", "damage", WireArgument.Int32(0), WireArgument.Int32(0), WireArgument.Int32(Width), WireArgument.Int32(Height));
                        Request(connection, SurfaceId, "wl_surface", "frame", WireArgument.NewId(AllocateCallback()));
                        Request(connection, SurfaceId, "wl_surface", "commit");
                        Console.WriteLine("rosella-smoke: window mapped — look for it on the output");
                    }

                    continue;
                }

                if (CallbackIds.Contains(message.ObjectId) && eventName == "done")
                {
                    frame++;
                    if (frame % 60 == 0)
                    {
                        Console.WriteLine($"rosella-smoke: {frame} frames");
                    }

                    Request(connection, SurfaceId, "wl_surface", "attach", WireArgument.Object(BufferId), WireArgument.Int32(0), WireArgument.Int32(0));
                    Request(connection, SurfaceId, "wl_surface", "damage", WireArgument.Int32(0), WireArgument.Int32(0), WireArgument.Int32(Width), WireArgument.Int32(Height));
                    Request(connection, SurfaceId, "wl_surface", "frame", WireArgument.NewId(AllocateCallback()));
                    Request(connection, SurfaceId, "wl_surface", "commit");
                }
            }
        }
    }

    /// <summary>Connects, learns the globals, and binds the ones we speak.</summary>
    private static Dictionary<uint, (string Interface, uint Version)> Handshake(WireConnection connection, MessageStream stream)
    {
        Request(connection, DisplayId, "wl_display", "get_registry", WireArgument.NewId(RegistryId));
        Request(connection, DisplayId, "wl_display", "sync", WireArgument.NewId(SyncCallbackId));

        var globals = new Dictionary<uint, (string, uint)>();
        var buffer = new byte[4096];
        var descriptors = new int[WireConnection.MaxDescriptorsPerReceive];
        while (true)
        {
            var synced = false;
            foreach (var message in ReadEvents(connection, stream, buffer, descriptors))
            {
                if (message.ObjectId == RegistryId && EventName(message) == "global")
                {
                    globals[message.Arguments[0].AsUInt32()] = (message.Arguments[1].AsString(), message.Arguments[2].AsUInt32());
                }

                if (message.ObjectId == SyncCallbackId)
                {
                    synced = true;
                }
            }

            if (synced)
            {
                break;
            }
        }

        Bind(connection, globals, "wl_compositor", 4, CompositorId);
        Bind(connection, globals, "wl_shm", 5, ShmId);
        Bind(connection, globals, "xdg_wm_base", 6, WmBaseId);
        return globals;
    }

    private static void Bind(WireConnection connection, Dictionary<uint, (string Interface, uint Version)> globals, string interfaceName, uint maxVersion, uint id)
    {
        foreach (var (name, (found, version)) in globals)
        {
            if (found == interfaceName)
            {
                Request(connection, RegistryId, "wl_registry", "bind", WireArgument.UInt32(name), WireArgument.String(interfaceName), WireArgument.UInt32(Math.Min(version, maxVersion)), WireArgument.NewId(id));
                return;
            }
        }

        throw new InvalidOperationException($"compositor does not advertise {interfaceName}");
    }

    /// <summary>Reads one batch of events, decoding them against the interface map.</summary>
    private static List<DecodedMessage> ReadEvents(WireConnection connection, MessageStream stream, byte[] buffer, int[] descriptors)
    {
        while (true)
        {
            var events = new List<DecodedMessage>();
            while (stream.TryRead(ResolveEvent, out var message))
            {
                events.Add(message);
            }

            if (events.Count > 0)
            {
                return events;
            }

            var received = connection.Receive(buffer, descriptors, out var descriptorCount);
            if (received == 0)
            {
                throw new InvalidOperationException("compositor closed the connection");
            }

            if (received < 0)
            {
                throw new TimeoutException("timed out waiting for events");
            }

            stream.Push(buffer.AsSpan(0, received), descriptors.AsSpan(0, descriptorCount));
        }
    }

    private static void Request(WireConnection connection, uint objectId, string interfaceName, string requestName, params WireArgument[] arguments)
    {
        var interfaceDescription = ProtocolDescription.Combined.FindInterface(interfaceName)
            ?? throw new InvalidOperationException($"unknown interface '{interfaceName}'");
        var request = interfaceDescription.Requests.First(r => r.Name == requestName);
        connection.Send(MessageEncoder.Encode(objectId, request.Opcode, request.WireSignature, arguments));
    }

    private static uint AllocateCallback()
    {
        var id = FreeCallbackIds.Count > 0 ? FreeCallbackIds.Dequeue() : _nextCallbackId++;
        CallbackIds.Add(id);
        return id;
    }

    private static string InterfaceOf(uint objectId)
    {
        return CallbackIds.Contains(objectId) ? "wl_callback" : Interfaces[objectId];
    }

    private static string EventName(DecodedMessage message)
    {
        var interfaceDescription = ProtocolDescription.Combined.FindInterface(InterfaceOf(message.ObjectId))
            ?? throw new InvalidOperationException("unknown interface");
        return interfaceDescription.FindEvent(message.Opcode)?.Name
            ?? throw new InvalidOperationException($"unknown event {message.Opcode}");
    }

    private static IReadOnlyList<ArgumentType> ResolveEvent(uint objectId, ushort opcode)
    {
        var interfaceDescription = ProtocolDescription.Combined.FindInterface(InterfaceOf(objectId))
            ?? throw new InvalidOperationException($"event for unknown object {objectId}");
        var description = interfaceDescription.FindEvent(opcode)
            ?? throw new InvalidOperationException($"unknown event {opcode} on object {objectId}");
        return description.WireSignature;
    }

    /// <summary>Animated frame: diagonal gradient with a moving vertical bar.</summary>
    private static void Draw(byte[] buffer, int frame)
    {
        var pixels = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(buffer.AsSpan());
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var red = (byte)((x * 255) / Width);
                var green = (byte)((y * 255) / Height);
                var blue = (byte)((frame * 4) & 0xFF);
                pixels[(y * Width) + x] = ((uint)red << 16) | ((uint)green << 8) | blue;
            }
        }

        var barX = (frame * 6) % Width;
        for (var y = 0; y < Height; y++)
        {
            for (var x = barX; x < Math.Min(Width, barX + 24); x++)
            {
                pixels[(y * Width) + x] = 0xFFFFFFFF;
            }
        }
    }

    private static string ResolveSocket(string[] args)
    {
        if (args.Length > 0)
        {
            return args[0].StartsWith('/') ? args[0] : Path.Combine(RuntimeDirectory(), args[0]);
        }

        return Path.Combine(RuntimeDirectory(), Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") ?? "wayland-0");
    }

    private static string RuntimeDirectory()
    {
        return Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? Path.GetTempPath();
    }
}
