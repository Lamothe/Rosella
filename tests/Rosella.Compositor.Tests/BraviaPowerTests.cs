using System.Net;
using System.Net.Sockets;

namespace Rosella.Compositor.Tests;

public class BraviaPowerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WakeFollowsStandbyEvenWhenItsReplyIsAnError(bool accepted)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var power = new BraviaPower((IPEndPoint)listener.LocalEndpoint);

        power.SetBlanked(true);
        using (var standby = await listener.AcceptTcpClientAsync(deadline.Token))
        using (var stream = standby.GetStream())
        {
            await AssertCommand(stream, "*SCPOWR0000000000000000\n"u8.ToArray(), deadline.Token);
            power.SetBlanked(false);

            // The TV can send a notification first and split a response across writes.
            await stream.WriteAsync("*SNPOWR0000000000000001\n*SAPOWR"u8.ToArray(), deadline.Token);
            var result = accepted ? "0000000000000000\n"u8.ToArray() : "FFFFFFFFFFFFFFFF\n"u8.ToArray();
            await stream.WriteAsync(result, deadline.Token);
        }

        using var wake = await listener.AcceptTcpClientAsync(deadline.Token);
        using var wakeStream = wake.GetStream();
        await AssertCommand(wakeStream, "*SCPOWR0000000000000001\n"u8.ToArray(), deadline.Token);
        await wakeStream.WriteAsync("*SAPOWR0000000000000000\n"u8.ToArray(), deadline.Token);
    }

    [Fact]
    public async Task LogoutWakesTheDisplayAfterBlanking()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var address = (IPEndPoint)listener.LocalEndpoint;
        var standbyReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tv = Task.Run(async () =>
        {
            foreach (var request in new[] { "*SCPOWR0000000000000000\n"u8.ToArray(), "*SCPOWR0000000000000001\n"u8.ToArray() })
            {
                using var client = await listener.AcceptTcpClientAsync(deadline.Token);
                using var stream = client.GetStream();
                await AssertCommand(stream, request, deadline.Token);
                await stream.WriteAsync("*SAPOWR0000000000000000\n"u8.ToArray(), deadline.Token);
                standbyReceived.TrySetResult();
            }
        });

        using (var power = new BraviaPower(address))
        {
            power.SetBlanked(true);
            await standbyReceived.Task.WaitAsync(deadline.Token);
        }
        await tv;
    }

    private static async Task AssertCommand(NetworkStream stream, byte[] expected, CancellationToken token)
    {
        var data = new byte[expected.Length];
        await stream.ReadExactlyAsync(data, token);
        Assert.Equal(expected, data);
    }
}
