using System.Net.Sockets;
using System.Text;

namespace Rosella.Control;

/// <summary>
/// The speaking side of the control protocol. Small enough to live here so
/// the driver tool and the tests share one implementation (with at least
/// one raw-socket test keeping the wire format honest).
/// </summary>
public sealed class ControlClient : IDisposable
{
    private const int MaxPayload = 256 * 1024 * 1024;

    private readonly Socket _socket;

    public ControlClient(string socketPath)
    {
        _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _socket.Connect(new UnixDomainSocketEndPoint(socketPath));
    }

    public static string DefaultSocketPath =>
        Path.Combine(
            Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR") ?? "/tmp",
            "rosella-control.sock");

    /// <summary>Sends one command and reads one answer. Throws on protocol violations.</summary>
    public (bool Ok, byte[] Payload) Send(string command)
    {
        var request = Encoding.UTF8.GetBytes(command + "\n");
        var sent = 0;
        while (sent < request.Length)
        {
            var n = _socket.Send(request, sent, request.Length - sent, SocketFlags.None);
            if (n <= 0)
            {
                throw new IOException("control server closed the connection mid-request");
            }

            sent += n;
        }

        // The answer header is exactly one line; the payload follows it.
        var header = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var n = _socket.Receive(one, 0, 1, SocketFlags.None);
            if (n <= 0)
            {
                throw new IOException("control server closed the connection mid-answer");
            }

            if (one[0] == (byte)'\n')
            {
                break;
            }

            header.Add(one[0]);
            if (header.Count > 128)
            {
                throw new IOException("control server sent an oversized answer header");
            }
        }

        var headerText = Encoding.UTF8.GetString(header.ToArray());

        if (headerText.StartsWith("ERR ", StringComparison.Ordinal))
        {
            return (false, Encoding.UTF8.GetBytes(headerText[4..]));
        }

        if (!headerText.StartsWith("OK ", StringComparison.Ordinal) ||
            !int.TryParse(headerText[3..], out var length) ||
            length < 0 ||
            length > MaxPayload)
        {
            throw new IOException($"malformed control answer: {headerText}");
        }

        var payload = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = _socket.Receive(payload, read, length - read, SocketFlags.None);
            if (n <= 0)
            {
                throw new IOException("control server closed the connection mid-payload");
            }

            read += n;
        }

        return (true, payload);
    }

    public void Dispose() => _socket.Dispose();
}
