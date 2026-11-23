using System.Runtime.InteropServices;
using Rosella.Server;

namespace Rosella.Compositor;

/// <summary>A private Wayland display for GPU client diagnostics without a VT or DRM master.</summary>
internal static class HeadlessDisplay
{
    public static int Run(string socketPath, string renderNode)
    {
        const int width = 1920;
        const int height = 1080;
        using var reader = DmaBufReader.TryOpen(renderNode);
        if (reader is null)
        {
            return 1;
        }

        var scene = new Scene { OutputSize = (width, height) };
        var output = new OutputInfo(width, height, 600, 340, 60000, 1, "Virtual", "1920x1080", "HEADLESS-1");
        var factory = new CompositorHandlerFactory(scene, new SeatState(), output, reader);
        using var stop = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
        {
            context.Cancel = true;
            stop.Cancel();
        });
        using var server = new DisplayServer(socketPath, factory.Globals, factory);
        server.Start();
        Console.WriteLine($"rosella: headless display listening on {socketPath}");
        var pixels = new byte[width * height * 4];
        long frame = 0;
        while (!stop.IsCancellationRequested)
        {
            scene.Render(pixels, width, height, width * 4, frame++);
            scene.CompleteFrame();
            stop.Token.WaitHandle.WaitOne(16);
        }
        return 0;
    }
}
