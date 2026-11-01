using System.Net.Sockets;

namespace Rosella.Compositor.Greeter;

/// <summary>
/// The one connection to greetd's socket (GREETD_SOCK). The greeter lives for
/// one login attempt after another on this connection, so it is opened once
/// and closed only when the process ends.
/// </summary>
internal sealed class GreetdConnection : IDisposable
{
    private readonly Socket _socket;
    private readonly NetworkStream _stream;

    private GreetdConnection(Socket socket)
    {
        _socket = socket;
        _stream = new NetworkStream(socket, ownsSocket: false);
    }

    /// <summary>The stream framing reads and writes.</summary>
    internal Stream Stream => _stream;

    /// <summary>Connects to greetd's socket. Failures are the caller's to report.</summary>
    internal static GreetdConnection Connect(string path)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Connect(new UnixDomainSocketEndPoint(path));
            return new GreetdConnection(socket);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>Closing the socket also unblocks a driver thread waiting on a read.</summary>
    public void Dispose()
    {
        _stream.Dispose();
        _socket.Dispose();
    }
}