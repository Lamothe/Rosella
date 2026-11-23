using Rosella.Protocol;
using Rosella.Transport;

namespace Rosella.Server.Tests;

public class ClientSessionTests
{
    [Fact]
    public void ClientSession_ExposesPeerPid()
    {
        var directory = WireConnectionTests.CreateTempDirectory();
        try
        {
            var pair = WireConnectionTests.CreateConnectedPair(directory);
            using var first = pair.First;
            using var second = pair.Second;
            var session = new ClientSession(first, ProtocolDescription.Combined, StandardGlobals.Create(), CancellationToken.None);

            Assert.Equal(Environment.ProcessId, session.PeerPid);
            using var process = session.OpenPeerProcess();
            if (process is not null)
            {
                var info = File.ReadLines($"/proc/self/fdinfo/{process.DangerousGetHandle()}");
                Assert.Contains(info, line => line == $"Pid:\t{Environment.ProcessId}");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
