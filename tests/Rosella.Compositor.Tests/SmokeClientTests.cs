using System.Diagnostics;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

public class SmokeClientTests
{
    [Fact]
    public void SmokeClient_MapsASurfaceOnTheCompositor()
    {
        var smoke = FindSmokeClient();
        if (smoke is null)
        {
            return;
        }

        var scene = new Scene();
        var socketPath = Path.Combine(Path.GetTempPath(), $"rosella-test-{Guid.NewGuid():N}");
        using var server = new DisplayServer(socketPath, handlerFactory: new CompositorHandlerFactory(scene));
        server.Start();

        var startInfo = new ProcessStartInfo(smoke)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.Environment["XDG_RUNTIME_DIR"] = Path.GetDirectoryName(socketPath)!;
        startInfo.Environment["WAYLAND_DISPLAY"] = Path.GetFileName(socketPath);

        using var process = Process.Start(startInfo)!;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < deadline)
            {
                var surface = scene.Surfaces.FirstOrDefault();
                if (surface is { IsMapped: true })
                {
                    Assert.Equal(640, surface.Width);
                    Assert.Equal(360, surface.Height);
                    Assert.NotNull(surface.Content);
                    return;
                }

                if (process.HasExited)
                {
                    break;
                }

                Thread.Sleep(20);
            }

            Assert.Fail($"smoke client did not map a surface (exit: {process.HasExited}, {process.ExitCode})");
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>Locates the built smoke client relative to this test assembly.</summary>
    private static string? FindSmokeClient()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Rosella.slnx")))
        {
            directory = directory.Parent;
        }

        if (directory is null)
        {
            return null;
        }

        var path = Path.Combine(directory.FullName, "tools", "Rosella.SmokeClient", "bin", "Debug", "net10.0", "Rosella.SmokeClient");
        return File.Exists(path) ? path : null;
    }
}
