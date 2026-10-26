using System.Net.Sockets;
using Rosella.Protocol;
using Rosella.Transport;

namespace Rosella.Server.Tests;

/// <summary>
/// A minimal Wayland client for exercising the server end to end. Requests
/// are built from the protocol description; tests that matter at the byte
/// level use SendRaw with hand-written frames instead.
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

    /// <summary>Sends hand-written wire bytes, bypassing the encoder.</summary>
    public void SendRaw(byte[] data)
    {
        _connection.Socket.Send(data);
    }

    /// <summary>Sends a request, looked up by interface and request name.</summary>
    public void SendRequest(uint objectId, string interfaceName, string requestName, params WireArgument[] arguments)
    {
        var interfaceDescription = ProtocolDescription.Core.FindInterface(interfaceName)
            ?? throw new InvalidOperationException($"unknown interface '{interfaceName}'");
        var request = interfaceDescription.Requests.FirstOrDefault(r => r.Name == requestName)
            ?? throw new InvalidOperationException($"'{interfaceName}' has no request '{requestName}'");
        _connection.Send(MessageEncoder.Encode(objectId, request.Opcode, request.WireSignature, arguments));
    }

    /// <summary>Tells the client harness which interface an object id carries.</summary>
    public void RegisterObject(uint id, string interfaceName)
    {
        _interfaces[id] = interfaceName;
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

    private IReadOnlyList<ArgumentType> ResolveEvent(uint objectId, ushort opcode)
    {
        if (!_interfaces.TryGetValue(objectId, out var interfaceName))
        {
            throw new InvalidOperationException($"event for unknown object {objectId}");
        }

        var interfaceDescription = ProtocolDescription.Core.FindInterface(interfaceName)
            ?? throw new InvalidOperationException($"unknown interface '{interfaceName}'");
        var description = interfaceDescription.FindEvent(opcode)
            ?? throw new InvalidOperationException($"'{interfaceName}' has no event {opcode}");
        return description.WireSignature;
    }
}
