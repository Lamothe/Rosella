using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Threading.Channels;

namespace Rosella.Compositor;

/// <summary>Drives one BRAVIA over Simple IP Control, independently of the render loop.</summary>
internal sealed class BraviaPower : IDisposable
{
    private const int Port = 20060;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMilliseconds(750);
    private readonly IPEndPoint _endpoint;
    private readonly CancellationTokenSource _stop = new();
    private readonly Channel<bool> _requests = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        SingleReader = true,
        FullMode = BoundedChannelFullMode.DropOldest,
    });
    private readonly Task _worker;

    internal BraviaPower(IPEndPoint endpoint)
    {
        _endpoint = endpoint;
        _worker = DriveAsync();
    }

    private static IPAddress? ConfiguredAddress()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        var homeConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        const string filename = "gnome-bravia-screensaver.config";
        var path = Path.Combine(config is not null && Path.IsPathFullyQualified(config) ? config : homeConfig, filename);
        if (!File.Exists(path))
        {
            path = Path.Combine(homeConfig, filename);
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            if (document.RootElement.ValueKind == JsonValueKind.Object &&
                document.RootElement.TryGetProperty("BraviaSettings", out var settings) && settings.ValueKind == JsonValueKind.Object &&
                settings.TryGetProperty("IPAddress", out var address) && address.ValueKind == JsonValueKind.String &&
                IPAddress.TryParse(address.GetString(), out var parsed))
            {
                return parsed;
            }

            Console.Error.WriteLine($"rosella: {path} has no valid BRAVIA IP address");
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            // A Sony display can also be used without Simple IP Control.
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"rosella: could not read BRAVIA settings: {exception.Message}");
        }
        return null;
    }

    internal static BraviaPower? FromConfiguration() => ConfiguredAddress() is { } address ? new(new IPEndPoint(address, Port)) : null;

    internal void SetBlanked(bool blank) => _requests.Writer.TryWrite(blank);

    private async Task DriveAsync()
    {
        // The TV may receive a standby command even when its reply is lost.
        // Send the wake in that case too, and serialize it behind the standby.
        var mayBeOff = false;
        bool? acknowledged = false;
        var blank = false;
        var failed = false;
        var token = _stop.Token;
        while (!token.IsCancellationRequested)
        {
            if (acknowledged == blank && !await _requests.Reader.WaitToReadAsync(token).ConfigureAwait(false))
            {
                return;
            }

            while (_requests.Reader.TryRead(out var requested))
            {
                blank = requested;
            }
            if (acknowledged == blank || (!blank && !mayBeOff))
            {
                continue;
            }

            mayBeOff = true;
            try
            {
                await SendPowerAsync(!blank, token).ConfigureAwait(false);
                acknowledged = blank;
                mayBeOff = blank;
                failed = false;
                Console.WriteLine($"rosella: BRAVIA {(blank ? "standby" : "wake")} acknowledged");
            }
            catch (Exception exception) when (!token.IsCancellationRequested && exception is IOException or SocketException or OperationCanceledException)
            {
                acknowledged = null;
                if (!failed)
                {
                    Console.Error.WriteLine($"rosella: BRAVIA {(blank ? "standby" : "wake")} failed: {exception.Message}; retrying");
                    failed = true;
                }

                // A newer request (especially a wake) interrupts the retry delay.
                using var retry = CancellationTokenSource.CreateLinkedTokenSource(token);
                retry.CancelAfter(TimeSpan.FromSeconds(1));
                try
                {
                    if (!await _requests.Reader.WaitToReadAsync(retry.Token).ConfigureAwait(false))
                    {
                        return;
                    }
                }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                {
                }
            }
        }
    }

    private async Task SendPowerAsync(bool on, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(CommandTimeout);
        using var client = new TcpClient(_endpoint.AddressFamily);
        await client.ConnectAsync(_endpoint.Address, _endpoint.Port, deadline.Token).ConfigureAwait(false);
        using var stream = client.GetStream();
        var command = on ? "*SCPOWR0000000000000001\n"u8.ToArray() : "*SCPOWR0000000000000000\n"u8.ToArray();
        await stream.WriteAsync(command, deadline.Token).ConfigureAwait(false);

        // BRAVIAs can announce the current power state immediately on connect.
        // Only the matching answer acknowledges this command.
        var frame = new byte[24];
        while (true)
        {
            await stream.ReadExactlyAsync(frame, deadline.Token).ConfigureAwait(false);
            if (frame.AsSpan().StartsWith("*SAPOWR"u8))
            {
                if (!frame.AsSpan().SequenceEqual("*SAPOWR0000000000000000\n"u8))
                {
                    throw new IOException("The BRAVIA rejected the power command");
                }
                return;
            }
        }
    }

    public void Dispose()
    {
        SetBlanked(false);
        _requests.Writer.TryComplete();
        _stop.CancelAfter(CommandTimeout * 2);
        try
        {
            _worker.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("rosella: timed out restoring BRAVIA power at logout");
        }
        finally
        {
            _stop.Dispose();
        }
    }
}
