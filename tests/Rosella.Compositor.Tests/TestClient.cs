using System.Net.Sockets;
using Rosella.Protocol;
using Rosella.Server;
using Rosella.Transport;

namespace Rosella.Compositor.Tests;

/// <summary>
/// A minimal Wayland client for exercising the compositor end to end over a
/// real socket, including descriptor passing.
/// </summary>
internal sealed class TestClient : IDisposable
{
    private readonly WireConnection _connection;
    private readonly MessageStream _stream = new();
    private readonly Dictionary<uint, string> _interfaces = new() { [1] = "wl_display" };
    private readonly byte[] _receiveBuffer = new byte[4096];
    private readonly int[] _receiveDescriptors = new int[WireConnection.MaxDescriptorsPerReceive];

    public TestClient(string socketPath)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(socketPath));
        socket.ReceiveTimeout = 5000;
        _connection = new WireConnection(socket);
    }

    /// <summary>Sends a request, looked up by interface and request name.</summary>
    public void SendRequest(uint objectId, string interfaceName, string requestName, params WireArgument[] arguments)
    {
        var interfaceDescription = ProtocolDescription.Combined.FindInterface(interfaceName)
            ?? throw new InvalidOperationException($"unknown interface '{interfaceName}'");
        var request = interfaceDescription.Requests.FirstOrDefault(r => r.Name == requestName)
            ?? throw new InvalidOperationException($"'{interfaceName}' has no request '{requestName}'");
        _connection.Send(MessageEncoder.Encode(objectId, request.Opcode, request.WireSignature, arguments));
    }

    /// <summary>Tells the harness which interface an object id carries.</summary>
    public void RegisterObject(uint id, string interfaceName)
    {
        _interfaces[id] = interfaceName;
    }

    /// <summary>Reads events until the named event on the object arrives.</summary>
    public DecodedMessage ReadUntil(uint objectId, string eventName)
    {
        while (true)
        {
            var message = ReadEvent();
            if (message.ObjectId == objectId && EventName(message) == eventName)
            {
                return message;
            }
        }
    }

    /// <summary>Blocks until the next event arrives.</summary>
    public DecodedMessage ReadEvent()
    {
        while (true)
        {
            if (_stream.TryRead(ResolveEvent, out var message))
            {
                return message;
            }

            var received = _connection.Receive(_receiveBuffer, _receiveDescriptors, out var descriptorCount);
            if (received == 0)
            {
                throw new InvalidOperationException("connection closed while waiting for an event");
            }

            if (received < 0)
            {
                throw new TimeoutException("timed out waiting for an event");
            }

            _stream.Push(_receiveBuffer.AsSpan(0, received), _receiveDescriptors.AsSpan(0, descriptorCount));
        }
    }

    public void Dispose()
    {
        _stream.Dispose();
        _connection.Dispose();
    }

    internal string EventName(DecodedMessage message)
    {
        var interfaceDescription = ProtocolDescription.Combined.FindInterface(_interfaces[message.ObjectId])
            ?? throw new InvalidOperationException("unknown interface");
        return interfaceDescription.FindEvent(message.Opcode)?.Name
            ?? throw new InvalidOperationException($"unknown event {message.Opcode}");
    }

    private IReadOnlyList<ArgumentType> ResolveEvent(uint objectId, ushort opcode)
    {
        if (!_interfaces.TryGetValue(objectId, out var interfaceName))
        {
            throw new InvalidOperationException($"event for unknown object {objectId}");
        }

        var interfaceDescription = ProtocolDescription.Combined.FindInterface(interfaceName)
            ?? throw new InvalidOperationException($"unknown interface '{interfaceName}'");
        var description = interfaceDescription.FindEvent(opcode)
            ?? throw new InvalidOperationException($"'{interfaceName}' has no event {opcode}");
        return description.WireSignature;
    }
}
