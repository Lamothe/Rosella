using System.Diagnostics;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

/// <summary>
/// Runs real, third-party Wayland clients (from their distro packages)
/// against the compositor. Skips silently when a client is not installed.
/// </summary>
public class RealClientTests
{
    [Fact]
    public void WaylandInfo_CompletesAgainstTheCompositor()
    {
        var waylandInfo = Find("wayland-info");
        if (waylandInfo is null)
        {
            return;
        }

        var scene = new Scene();
        var socketPath = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
        using var server = new DisplayServer(socketPath, handlerFactory: new CompositorHandlerFactory(scene));
        server.Start();

        var startInfo = new ProcessStartInfo(waylandInfo)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["XDG_RUNTIME_DIR"] = Path.GetDirectoryName(socketPath)!;
        startInfo.Environment["WAYLAND_DISPLAY"] = Path.GetFileName(socketPath);

        using var process = Process.Start(startInfo)!;
        Assert.True(process.WaitForExit(10000), "wayland-info did not finish");
        Assert.Equal(0, process.ExitCode);
    }

    private static string? Find(string name)
    {
        foreach (var directory in new[] { "/usr/bin", "/usr/local/bin", "/bin" })
        {
            var path = Path.Combine(directory, name);
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
