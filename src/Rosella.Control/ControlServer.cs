using System.Net.Sockets;
using System.Text;

namespace Rosella.Control;

/// <summary>
/// A tiny line-oriented control socket, the compositor's hands and eyes for
/// outside automation. One request per line, answers are
/// "OK &lt;payload-bytes&gt;\n" + raw payload bytes, or "ERR &lt;message&gt;\n".
/// Payloads are length-prefixed so binary frames need no escaping. The
/// socket is per-user (XDG_RUNTIME_DIR) and unauthenticated like the
/// Wayland socket it sits beside: it can drive the seat, so treat it with
/// the same trust as the session itself.
/// </summary>
public sealed class ControlServer : IDisposable
{
    private const int MaxRequestLine = 256;
    private const int MaxClients = 8;

    private readonly IControlTarget _target;
    private readonly Socket _listener;
    private readonly string _path;
    private readonly Thread _acceptThread;
    private int _activeClients;
    private volatile bool _running = true;

    public ControlServer(string socketPath, IControlTarget target)
    {
        _target = target;
        _path = socketPath;

        // A stale socket file with a live listener behind it means two
        // servers; refuse rather than silently hijack the first. The probe
        // is bounded: a wedged squatter must not freeze startup.
        if (File.Exists(socketPath))
        {
            if (ListenerPresent(socketPath))
            {
                throw new InvalidOperationException($"someone is already bound to {socketPath}; refusing to hijack it");
            }

            File.Delete(socketPath);
        }

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(4);

        // The socket can drive the seat (inject keystrokes): keep it as
        // private as the file system allows, never world-open.
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (Exception)
            {
                // Unusual file systems may refuse; the path is still
                // per-user under XDG_RUNTIME_DIR.
            }
        }

        _acceptThread = new Thread(AcceptLoop)
        {
            IsBackground = true,
            Name = "rosella-control",
        };
        _acceptThread.Start();
    }

    public string SocketPath => _path;

    private void AcceptLoop()
    {
        while (_running)
        {
            Socket client;
            try
            {
                client = _listener.Accept();
            }
            catch (Exception)
            {
                // Listener closed during dispose, or accept failed: the
                // loop's job is over either way.
                return;
            }

            // Each client gets its own thread: a wedged client must not
            // block the next one. The cap bounds parked threads from
            // half-sent lines (the socket is same-user trusted, but the
            // compositor must still survive sloppy scripts).
            if (Interlocked.Increment(ref _activeClients) > MaxClients)
            {
                Interlocked.Decrement(ref _activeClients);
                try
                {
                    client.Close();
                }
                catch (Exception)
                {
                    // Best effort.
                }

                continue;
            }

            var thread = new Thread(() =>
            {
                try
                {
                    Serve(client);
                }
                finally
                {
                    Interlocked.Decrement(ref _activeClients);
                }
            })
            {
                IsBackground = true,
                Name = "rosella-control-client",
            };
            thread.Start();
        }
    }

    private void Serve(Socket client)
    {
        using (client)
        {
            try
            {
                var line = new byte[MaxRequestLine];
                var length = 0;
                var buffer = new byte[256];

                while (_running)
                {
                    // Read one request line, byte by byte: requests are
                    // tiny and this makes the framing unambiguous with no
                    // buffering edge cases.
                    length = 0;
                    while (true)
                    {
                        var n = client.Receive(buffer, 0, 1, SocketFlags.None);
                        if (n <= 0)
                        {
                            return; // Client hung up.
                        }

                        if (buffer[0] == (byte)'\n')
                        {
                            break;
                        }

                        if (length >= MaxRequestLine - 1)
                        {
                            SendError(client, "request line too long");
                            return;
                        }

                        line[length++] = buffer[0];
                    }

                    var request = Encoding.UTF8.GetString(line, 0, length).Trim();
                    if (request.Length == 0)
                    {
                        continue;
                    }

                    Handle(client, request);
                }
            }
            catch (Exception)
            {
                // Any socket-level failure ends this client silently: the
                // compositor must never be at the mercy of a control client.
            }
        }
    }

    private void Handle(Socket client, string request)
    {
        var parts = request.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        try
        {
            switch (parts[0])
            {
                case "ping":
                    SendPayload(client, "pong"u8.ToArray());
                    break;

                case "frame":
                    SendPayload(client, _target.CaptureFrame());
                    break;

                case "table":
                    SendPayload(client, Encoding.UTF8.GetBytes(_target.SurfaceTable()));
                    break;

                case "state":
                    SendPayload(client, Encoding.UTF8.GetBytes(_target.State()));
                    break;

                case "move":
                    if (parts.Length != 3 || !TryInt(parts[1], out var dx) || !TryInt(parts[2], out var dy))
                    {
                        SendError(client, "usage: move <dx> <dy>");
                        break;
                    }

                    _target.RelativeMotion(dx, dy);
                    SendPayload(client, Array.Empty<byte>());
                    break;

                case "warp":
                    if (parts.Length != 3 || !TryInt(parts[1], out var wx) || !TryInt(parts[2], out var wy))
                    {
                        SendError(client, "usage: warp <x> <y>");
                        break;
                    }

                    _target.WarpCursor(wx, wy);
                    SendPayload(client, Array.Empty<byte>());
                    break;

                case "button":
                    if (parts.Length != 3 || !TryInt(parts[1], out var bcode) || !TryValue(parts[2], out var bvalue) || bcode is < 0 or > ushort.MaxValue)
                    {
                        SendError(client, "usage: button <code 0-65535> <0|1>");
                        break;
                    }

                    _target.Button((ushort)bcode, bvalue);
                    SendPayload(client, Array.Empty<byte>());
                    break;

                case "key":
                    if (parts.Length != 3 || !TryInt(parts[1], out var kcode) || !TryValue(parts[2], out var kvalue) || kcode is < 0 or > ushort.MaxValue)
                    {
                        SendError(client, "usage: key <code 0-65535> <0|1>");
                        break;
                    }

                    _target.Key((ushort)kcode, kvalue);
                    SendPayload(client, Array.Empty<byte>());
                    break;

                case "blank":
                case "unblank":
                {
                    var blank = parts[0] == "blank";
                    if (parts.Length != 1)
                    {
                        SendError(client, $"usage: {parts[0]}");
                        break;
                    }

                    SendPayload(client, Encoding.UTF8.GetBytes(_target.SetBlanked(blank) + "\n"));
                    break;
                }

                default:
                    SendError(client, $"unknown command: {parts[0]}");
                    break;
            }
        }
        catch (Exception ex)
        {
            // A failing target must not kill the connection: report it.
            SendError(client, ex.Message);
        }
    }

    private static bool TryInt(string s, out int value) =>
        int.TryParse(s, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out value);

    private static bool TryValue(string s, out int value)
    {
        if (s is "0" or "1")
        {
            value = s == "1" ? 1 : 0;
            return true;
        }

        value = 0;
        return false;
    }

    private static void SendAll(Socket client, byte[] data)
    {
        // Socket.Send may write fewer bytes than asked (signals interrupt),
        // and a short send desynchronises the framing permanently.
        var sent = 0;
        while (sent < data.Length)
        {
            var n = client.Send(data, sent, data.Length - sent, SocketFlags.None);
            if (n <= 0)
            {
                throw new IOException("control connection closed mid-send");
            }

            sent += n;
        }
    }

    private static void SendPayload(Socket client, byte[] payload)
    {
        var header = Encoding.ASCII.GetBytes($"OK {payload.Length}\n");
        SendAll(client, header);
        if (payload.Length > 0)
        {
            SendAll(client, payload);
        }
    }

    private static void SendError(Socket client, string message)
    {
        var oneLine = message.Replace('\n', ' ').Replace('\r', ' ');
        SendAll(client, Encoding.UTF8.GetBytes($"ERR {oneLine}\n"));
    }

    public void Dispose()
    {
        _running = false;
        try
        {
            _listener.Close();
        }
        catch (Exception)
        {
            // Already closed.
        }

        try
        {
            _acceptThread.Join(TimeSpan.FromSeconds(1));
        }
        catch (Exception)
        {
            // Best effort.
        }

        try
        {
            if (File.Exists(_path))
            {
                // Never delete a socket someone else is now serving: a
                // slow shutdown racing a replacement would unlink the
                // *new* server's live socket. If anything answers here,
                // the file belongs to them.
                if (!ListenerPresent(_path))
                {
                    File.Delete(_path);
                }
            }
        }
        catch (Exception)
        {
            // Best effort.
        }
    }

    /// <summary>
    /// True when a listener answers on the path (or is wedged holding it);
    /// false only when the connection is refused — i.e. nothing is there.
    /// </summary>
    private static bool ListenerPresent(string path)
    {
        using var probe = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        var probeTask = probe.ConnectAsync(new UnixDomainSocketEndPoint(path));
        try
        {
            if (!probeTask.Wait(TimeSpan.FromSeconds(1)))
            {
                return true; // Timed out: wedged but present. Not ours.
            }
        }
        catch (AggregateException)
        {
            return false; // Refused: nothing is listening. Stale file.
        }

        return true; // Connected: a live listener.
    }
}
