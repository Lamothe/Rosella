using System.Diagnostics;
using Rosella.Server;

namespace Rosella.Compositor.Tests;

/// <summary>
/// Opt-in real Firefox regression. This starts no display server on the seat:
/// Firefox connects only to an in-process Rosella server and is rendered into memory.
/// Set ROSELLA_TEST_FIREFOX to an executable path to run it.
/// </summary>
public class FirefoxWaylandTests
{
    [Fact]
    public async Task Firefox_RendersPageIntoTheScene()
    {
        var firefox = Environment.GetEnvironmentVariable("ROSELLA_TEST_FIREFOX");
        if (string.IsNullOrWhiteSpace(firefox))
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"rosella-firefox-{Guid.NewGuid():N}");
        var profile = Path.Combine(directory, "profile");
        Directory.CreateDirectory(profile);
        await File.WriteAllTextAsync(Path.Combine(profile, "user.js"), """
            user_pref("browser.shell.checkDefaultBrowser", false);
            user_pref("browser.aboutwelcome.enabled", false);
            user_pref("browser.startup.homepage_override.mstone", "ignore");
            """);
        var scene = new Scene { OutputSize = (1280, 800) };
        var output = new OutputInfo(1280, 800, 300, 190, 60000, 1, "TEST", "1280x800", "TEST-1");
        using var server = new DisplayServer(Path.Combine(directory, "wayland-0"), handlerFactory: new CompositorHandlerFactory(scene, output));
        server.Start();
        using var frames = new CancellationTokenSource();
        var centreBgr = 0;
        var thread = new Thread(() =>
        {
            var pixels = new byte[1280 * 800 * 4];
            var offset = ((400 * 1280) + 640) * 4;
            while (!frames.IsCancellationRequested)
            {
                scene.Render(pixels, 1280, 800, 1280 * 4, 0);
                Volatile.Write(ref centreBgr, pixels[offset] | (pixels[offset + 1] << 8) | (pixels[offset + 2] << 16));
                scene.CompleteFrame();
                Thread.Sleep(16);
            }
        }) { IsBackground = true };
        thread.Start();

        var html = "<html><body style='margin:0;background:rgb(18,211,73)'></body></html>";
        var page = Path.Combine(directory, "page.html");
        await File.WriteAllTextAsync(page, html);
        // Exercise the same environment setup as a shell-launched application.
        var start = AppLaunch.CreateStartInfo([firefox, "--no-remote", "--profile", profile, new Uri(page).AbsoluteUri], "wayland-0");
        start.RedirectStandardError = true;
        start.RedirectStandardOutput = true;
        start.Environment["XDG_RUNTIME_DIR"] = directory;
        start.Environment["WAYLAND_DISPLAY"] = "wayland-0";
        start.Environment["MOZ_ENABLE_WAYLAND"] = "1";
        start.Environment["MOZ_CRASHREPORTER_DISABLE"] = "1";
        // Fedora’s wrapper otherwise inspects or modifies the real profile.
        start.Environment["HOME"] = directory;
        start.Environment["XDG_CONFIG_HOME"] = Path.Combine(directory, "config");
        start.Environment["XDG_CACHE_HOME"] = Path.Combine(directory, "cache");
        start.Environment["GDK_BACKEND"] = "wayland";
        start.Environment.Remove("DISPLAY");
        using var process = Process.Start(start)!;
        // Drain asynchronously: diagnostic output must not block the client.
        var stderr = process.StandardError.ReadToEndAsync();
        var stdout = process.StandardOutput.ReadToEndAsync();
        try
        {
            var deadline = Stopwatch.StartNew();
            var rendered = false;
            var sample = 0;
            while (deadline.Elapsed < TimeSpan.FromSeconds(30) && !process.HasExited)
            {
                sample = Volatile.Read(ref centreBgr);
                if (sample == (73 | (211 << 8) | (18 << 16)))
                {
                    rendered = true;
                    break;
                }
                await Task.Delay(100);
            }
            if (!rendered)
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); }
                await process.WaitForExitAsync();
                var errors = await stderr;
                var outputText = await stdout;
                Assert.Fail($"Firefox did not render the test page; centre BGR {sample & 255},{(sample >> 8) & 255},{(sample >> 16) & 255}. " +
                    $"Exit code {process.ExitCode}. stderr: {errors} stdout: {outputText}");
            }
        }
        finally
        {
            frames.Cancel();
            thread.Join(5000);
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync();
            server.Dispose();
            Directory.Delete(directory, recursive: true);
        }
    }
}
