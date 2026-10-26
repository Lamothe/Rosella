using System.Net.Sockets;
using Rosella.Protocol;
using Rosella.Transport;

namespace Rosella.Server;

/// <summary>
/// The Rosella display server: listens on a Wayland socket and runs a
/// <see cref="ClientSession"/> per client.
/// </summary>
public sealed class DisplayServer : IDisposable
{
    private readonly Socket _listener;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly CancellationToken _token;
    private readonly List<Task> _sessions = new();
    private readonly object _gate = new();
    private Task _acceptTask = Task.CompletedTask;
    private bool _disposed;

    public DisplayServer(string socketPath, IReadOnlyList<Global>? globals = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(socketPath);
        SocketPath = socketPath;
        Globals = globals ?? StandardGlobals.Create();

        if (File.Exists(socketPath))
        {
            File.Delete(socketPath);
        }

        _listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        _listener.Bind(new UnixDomainSocketEndPoint(socketPath));
        _listener.Listen(16);

        // Capture the token up front: it stays usable after the source is
        // disposed, unlike the Token property itself.
        _token = _cancellation.Token;
    }

    public string SocketPath { get; }

    public IReadOnlyList<Global> Globals { get; }

    /// <summary>Starts accepting clients in the background.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _acceptTask = Task.Run(AcceptLoop);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cancellation.Cancel();
        _listener.Dispose();

        try
        {
            _acceptTask.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Accept-loop failures are already handled inside the loop.
        }

        Task[] sessions;
        lock (_gate)
        {
            sessions = _sessions.ToArray();
        }

        try
        {
            Task.WaitAll(sessions, TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Session failures are already handled inside Run.
        }

        _cancellation.Dispose();

        if (File.Exists(SocketPath))
        {
            File.Delete(SocketPath);
        }
    }

    private void AcceptLoop()
    {
        while (!_token.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = _listener.Accept();
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                if (_token.IsCancellationRequested)
                {
                    break;
                }

                // Transient accept failures (e.g. running out of descriptors)
                // must not stop the server for good.
                Thread.Sleep(50);
                continue;
            }

            socket.ReceiveTimeout = 250;
            var session = Task.Run(() => RunSession(socket));
            lock (_gate)
            {
                _sessions.Add(session);
                _ = session.ContinueWith(completed =>
                {
                    lock (_gate)
                    {
                        _sessions.Remove(completed);
                    }
                });
            }
        }
    }

    private void RunSession(Socket socket)
    {
        using var connection = new WireConnection(socket);
        var session = new ClientSession(connection, ProtocolDescription.Core, Globals, _token);
        session.Run();
    }
}
