using System.Net.Sockets;
using Microsoft.Win32.SafeHandles;
using Rosella.Protocol;
using Rosella.Transport;

namespace Rosella.Server.Tests;

public class WireConnectionTests
{
    [Fact]
    public void SendReceive_RoundTripsBytesAndDescriptors()
    {
        var directory = CreateTempDirectory();
        try
        {
            var pair = CreateConnectedPair(directory);
            using var server = pair.First;
            using var client = pair.Second;
            var payloadPath = Path.Combine(directory, "payload.txt");
            File.WriteAllText(payloadPath, "descriptor payload");

            using (var file = File.OpenHandle(payloadPath, FileMode.Open, FileAccess.Read))
            {
                server.Send([1, 2, 3, 4], [unchecked((int)file.DangerousGetHandle())]);
            }

            var buffer = new byte[16];
            var descriptors = new int[4];
            var received = client.Receive(buffer, descriptors, out var descriptorCount);

            Assert.Equal(4, received);
            Assert.Equal(1, descriptorCount);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer.AsSpan(0, received).ToArray());

            // The received descriptor is a fresh handle to the same file.
            using var receivedFile = new SafeFileHandle((IntPtr)descriptors[0], ownsHandle: true);
            using var stream = new FileStream(receivedFile, FileAccess.Read);
            using var reader = new StreamReader(stream);
            Assert.Equal("descriptor payload", reader.ReadToEnd());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Receive_ReturnsZeroOnOrderlyClose()
    {
        var directory = CreateTempDirectory();
        try
        {
            var pair = CreateConnectedPair(directory);
            using var server = pair.First;
            using var client = pair.Second;
            server.Dispose();

            var received = client.Receive(new byte[16], [], out _);
            Assert.Equal(0, received);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Send_PreservesDescriptorsOnPartialWrites()
    {
        var directory = CreateTempDirectory();
        try
        {
            var pair = CreateConnectedPair(directory);
            using var server = pair.First;
            using var client = pair.Second;
            server.Socket.SendBufferSize = 2048;
            server.Socket.Blocking = false;
            client.Socket.ReceiveTimeout = 10000;

            var payloadPath = Path.Combine(directory, "payload.bin");
            File.WriteAllBytes(payloadPath, new byte[16]);
            using var file = File.OpenHandle(payloadPath, FileMode.Open, FileAccess.Read);
            var descriptor = unchecked((int)file.DangerousGetHandle());

            // With no reader draining the 2 KB send buffer, the first sendmsg
            // must return a short count and further writes must follow — the
            // descriptors may ride on the first write only. A blocking socket
            // would instead stall inside one sendmsg and never split.
            var payload = new byte[256 * 1024];
            var sendTask = Task.Run(() => server.Send(payload, [descriptor, descriptor]));
            await Task.Delay(200);

            var buffer = new byte[4096];
            var slots = new int[8];
            var receivedDescriptors = new List<int>();
            var receivedTotal = 0;
            while (receivedTotal < payload.Length)
            {
                var received = client.Receive(buffer, slots, out var descriptorCount);
                Assert.True(received > 0, "receive timed out before the payload completed");
                receivedTotal += received;
                for (var i = 0; i < descriptorCount; i++)
                {
                    receivedDescriptors.Add(slots[i]);
                }
            }

            await sendTask;
            Assert.Equal(2, receivedDescriptors.Count);
            foreach (var receivedDescriptor in receivedDescriptors)
            {
                using var handle = new SafeFileHandle((IntPtr)receivedDescriptor, ownsHandle: true);
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Receive_MoreDescriptorsThanSpan_Throws()
    {
        var directory = CreateTempDirectory();
        try
        {
            var pair = CreateConnectedPair(directory);
            using var server = pair.First;
            using var client = pair.Second;

            var payloadPath = Path.Combine(directory, "payload.bin");
            File.WriteAllBytes(payloadPath, new byte[16]);
            using var file = File.OpenHandle(payloadPath, FileMode.Open, FileAccess.Read);
            var descriptor = unchecked((int)file.DangerousGetHandle());

            server.Send([1, 2], [descriptor, descriptor, descriptor, descriptor, descriptor, descriptor, descriptor, descriptor]);

            // The delivered descriptors are closed before throwing, so nothing leaks.
            Assert.Throws<WireProtocolException>(
                () => client.Receive(new byte[16], new int[4], out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Receive_TruncatedControlData_Throws()
    {
        var directory = CreateTempDirectory();
        try
        {
            var pair = CreateConnectedPair(directory);
            using var server = pair.First;
            using var client = pair.Second;

            var payloadPath = Path.Combine(directory, "payload.bin");
            File.WriteAllBytes(payloadPath, new byte[16]);
            using var file = File.OpenHandle(payloadPath, FileMode.Open, FileAccess.Read);
            var descriptor = unchecked((int)file.DangerousGetHandle());

            // More descriptors than the control buffer can carry: the kernel
            // truncates the control message and flags MSG_CTRUNC.
            var descriptors = Enumerable.Repeat(descriptor, 70).ToArray();
            server.Send([1], descriptors);

            Assert.Throws<WireProtocolException>(
                () => client.Receive(new byte[16], new int[64], out _));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Send_RejectsMoreThanKernelFdLimit()
    {
        var directory = CreateTempDirectory();
        try
        {
            var pair = CreateConnectedPair(directory);
            using var server = pair.First;
            var descriptors = new int[WireConnection.MaxDescriptorsPerSend + 1];
            Assert.Throws<ArgumentException>(() => server.Send([1], descriptors));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    internal static string CreateTempDirectory()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rosella-transport-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    internal static (WireConnection First, WireConnection Second) CreateConnectedPair(string directory)
    {
        var socketPath = Path.Combine(directory, "wire-test");
        using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        listener.Listen(1);

        var clientSocket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        clientSocket.Connect(new UnixDomainSocketEndPoint(socketPath));
        var serverSocket = listener.Accept();
        return (new WireConnection(serverSocket), new WireConnection(clientSocket));
    }
}
