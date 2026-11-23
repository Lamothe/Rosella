using Rosella.Server;

namespace Rosella.Server.Tests;

public class SocketNamingTests
{
    [Fact]
    public void FindFreeSocketPath_PrefersTheFirstFreeName()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            Assert.Equal(Path.Combine(directory, "wayland-0"), DisplayServer.FindFreeSocketPath(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FindFreeSocketPath_SkipsLiveSockets_ButReclaimsStaleOnes()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            // wayland-0: live listener; wayland-1: stale socket file.
            using var live = new DisplayServer(Path.Combine(directory, "wayland-0"));
            File.WriteAllBytes(Path.Combine(directory, "wayland-1"), []);

            Assert.Equal(Path.Combine(directory, "wayland-1"), DisplayServer.FindFreeSocketPath(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void DisplayServer_RefusesALiveSocket()
    {
        var socketPath = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
        using var live = new DisplayServer(socketPath);
        Assert.Throws<InvalidOperationException>(() => new DisplayServer(socketPath));
    }
}
