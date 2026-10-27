using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;
using Rosella.Protocol;
using Rosella.Server.Handlers;
using Rosella.Transport;

namespace Rosella.Server;

/// <summary>
/// One connected client: its object map, request dispatch, and event
/// marshalling. Client-allocated object ids are below
/// <see cref="ServerIdStart"/>; the server allocates from there upward.
/// </summary>
public sealed class ClientSession
{
    public const uint ErrorInvalidObject = 0;
    public const uint ErrorInvalidMethod = 1;
    public const uint ErrorNoMemory = 2;
    public const uint ErrorImplementation = 3;

    private const uint ServerIdStart = 0xFF000000;
    private const uint DisplayId = 1;

    private readonly WireConnection _connection;
    private readonly CancellationToken _token;
    private readonly MessageStream _stream = new();
    private readonly Dictionary<uint, ServerObject> _objects = new();
    private readonly object _gate = new();
    private readonly ServerObject _display;
    private uint _serial;

    public ClientSession(
        WireConnection connection,
        ProtocolDescription protocol,
        IReadOnlyList<Global> globals,
        CancellationToken token,
        IGlobalHandlerFactory? handlerFactory = null)
    {
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
        Protocol = protocol ?? throw new ArgumentNullException(nameof(protocol));
        Globals = globals ?? throw new ArgumentNullException(nameof(globals));
        _token = token;
        HandlerFactory = handlerFactory;

        var displayInterface = protocol.FindInterface("wl_display")
            ?? throw new InvalidOperationException("the protocol has no wl_display interface");
        _display = new ServerObject(DisplayId, displayInterface, 1, new DisplayHandler());
        _objects.Add(DisplayId, _display);
    }

    public ProtocolDescription Protocol { get; }

    public IReadOnlyList<Global> Globals { get; }

    /// <summary>The compositor's handler factory, or null for all-stub behaviour.</summary>
    public IGlobalHandlerFactory? HandlerFactory { get; }

    /// <summary>Returns the next event serial, as used by wl_callback.done.</summary>
    public uint NextSerial()
    {
        lock (_gate)
        {
            return ++_serial;
        }
    }

    /// <summary>Looks up an object by id.</summary>
    public bool TryGetObject(uint id, out ServerObject target)
    {
        lock (_gate)
        {
            return _objects.TryGetValue(id, out target!);
        }
    }

    /// <summary>Runs the request loop until the client disconnects or is cancelled.</summary>
    public void Run()
    {
        var buffer = new byte[4096];
        var descriptors = new int[WireConnection.MaxDescriptorsPerReceive];

        try
        {
            while (!_token.IsCancellationRequested)
            {
                var received = _connection.Receive(buffer, descriptors, out var descriptorCount);
                if (received == 0)
                {
                    break;
                }

                if (received < 0)
                {
                    continue;
                }

                _stream.Push(buffer.AsSpan(0, received), descriptors.AsSpan(0, descriptorCount));
                while (_stream.TryRead(Resolve, out var message))
                {
                    Dispatch(message);
                }
            }
        }
        catch (ProtocolErrorException exception)
        {
            TrySendError(exception.ObjectId, exception.Code, exception.Message);
        }
        catch (WireProtocolException exception)
        {
            // Malformed requests are the client's fault: wayland.xml defines
            // invalid_method as "method doesn't exist ... or malformed request".
            TrySendError(DisplayId, ErrorInvalidMethod, exception.Message);
        }
        catch (SocketException)
        {
            // The peer vanished; nothing to report.
        }
        catch (Exception exception)
        {
            // Never let one client take the session down ungracefully.
            TrySendError(DisplayId, ErrorImplementation, exception.Message);
        }
        finally
        {
            DisposeHandlers();
            _stream.Dispose();
        }
    }

    /// <summary>
    /// Releases handler-owned resources when the session ends (mappings, for
    /// one). Object ids are gone with the connection, so nothing is reported.
    /// </summary>
    private void DisposeHandlers()
    {
        ServerObject[] objects;
        lock (_gate)
        {
            objects = _objects.Values.ToArray();
        }

        foreach (var target in objects)
        {
            (target.Handler as IDisposable)?.Dispose();
        }
    }

    /// <summary>
    /// Sends an event on <paramref name="target"/> by event name. Thread-safe:
    /// handlers and the render loop both send events.
    /// </summary>
    public void SendEvent(ServerObject target, string eventName, params WireArgument[] arguments)
    {
        var description = target.Interface.Events.FirstOrDefault(e => e.Name == eventName)
            ?? throw new InvalidOperationException($"'{target.Interface.Name}' has no event '{eventName}'");
        if (description.Since > target.Version)
        {
            throw new InvalidOperationException(
                $"'{target.Interface.Name}.{eventName}' requires version {description.Since} but object {target.Id} is version {target.Version}");
        }

        lock (_gate)
        {
            _connection.Send(MessageEncoder.Encode(target.Id, description.Opcode, description.WireSignature, arguments));
        }
    }

    /// <summary>Reports a protocol violation with wl_display.error.</summary>
    public void SendError(uint objectId, uint code, string message)
    {
        SendEvent(_display, "error", WireArgument.Object(objectId), WireArgument.UInt32(code), WireArgument.String(message));
    }

    /// <summary>
    /// Adds a client-allocated object to the session's map. Violations are
    /// reported on <paramref name="source"/>, matching libwayland's
    /// convention of posting errors on the requesting object.
    /// </summary>
    public ServerObject CreateClientObject(uint id, InterfaceDescription interfaceDescription, uint version, IRequestHandler handler, ServerObject source)
    {
        if (id == 0 || id >= ServerIdStart)
        {
            throw new ProtocolErrorException(source.Id, ErrorInvalidObject, $"invalid new id {id}");
        }

        ServerObject target;
        lock (_gate)
        {
            if (_objects.ContainsKey(id))
            {
                throw new ProtocolErrorException(source.Id, ErrorInvalidObject, $"invalid new id {id} (already in use)");
            }

            target = new ServerObject(id, interfaceDescription, version, handler);
            _objects.Add(id, target);
        }

        return target;
    }

    /// <summary>Destroys an object and, for client-allocated ids, frees the id.</summary>
    public void DestroyObject(ServerObject target)
    {
        if (Retire(target) && target.Id < ServerIdStart)
        {
            SendDeleteId(target.Id);
        }
    }

    /// <summary>Removes an object from the map without reporting. True when it was present.</summary>
    public bool Retire(ServerObject target)
    {
        lock (_gate)
        {
            return _objects.Remove(target.Id);
        }
    }

    /// <summary>Frees a client-allocated id (wl_display.delete_id).</summary>
    public void SendDeleteId(uint id)
    {
        SendEvent(_display, "delete_id", WireArgument.UInt32(id));
    }

    private IReadOnlyList<ArgumentType> Resolve(uint objectId, ushort opcode)
    {
        ServerObject? target;
        lock (_gate)
        {
            _objects.TryGetValue(objectId, out target);
        }

        if (target is null)
        {
            // libwayland clients cannot resolve an event naming an id they
            // never created, so violations by unknown objects are reported on
            // the display object with the offending id in the message.
            throw new ProtocolErrorException(DisplayId, ErrorInvalidObject, $"invalid object {objectId}");
        }

        var request = target.Interface.FindRequest(opcode)
            ?? throw new ProtocolErrorException(target.Id, ErrorInvalidMethod, $"invalid method {opcode} on '{target.Interface.Name}'");
        return request.WireSignature;
    }

    private void Dispatch(DecodedMessage message)
    {
        ServerObject target;
        lock (_gate)
        {
            target = _objects[message.ObjectId];
        }

        try
        {
            target.Handler.HandleRequest(this, target, message);
        }
        finally
        {
            CloseMessageDescriptors(message);
        }
    }

    /// <summary>
    /// Closes the message's file descriptors after dispatch. Handlers that
    /// need to keep one must duplicate it first.
    /// </summary>
    private static void CloseMessageDescriptors(DecodedMessage message)
    {
        foreach (var argument in message.Arguments)
        {
            if (argument.Kind == ArgumentKind.Fd)
            {
                using var handle = new SafeFileHandle((IntPtr)argument.AsFileDescriptor(), ownsHandle: true);
            }
        }
    }

    private void TrySendError(uint objectId, uint code, string message)
    {
        try
        {
            SendError(objectId, code, message);
        }
        catch (Exception)
        {
            // Best effort: the connection may already be gone, and error
            // reporting must never fault the session.
        }
    }
}
