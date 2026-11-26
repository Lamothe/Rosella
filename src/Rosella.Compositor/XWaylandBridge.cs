using System.Diagnostics;

namespace Rosella.Compositor;

/// <summary>Restarts a failed desktop bridge without restarting the Wayland session.</summary>
internal sealed class XWaylandBridge : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _watch;
    private volatile XWaylandSession? _current;

    internal XWaylandBridge(string executable, Scene scene, SeatState seat, OutputInfo output, DmaBufReader? dmaBuf)
    {
        XWaylandSession? Start() => XWaylandSession.Start(executable, scene, seat, output, dmaBuf);
        _current = Start();
        _watch = Watch(Start, _stop.Token);
    }

    internal XWaylandSession? Current => _current;

    private async Task Watch(Func<XWaylandSession?> start, CancellationToken stop)
    {
        var previousExitWasEarly = false;
        try
        {
            while (_current is { } running)
            {
                var started = Stopwatch.GetTimestamp();
                await running.Exited.WaitAsync(stop);
                _current = null;
                running.Dispose();

                var earlyExit = Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(5);
                if (earlyExit && previousExitWasEarly)
                {
                    Console.Error.WriteLine("rosella: XWayland exited twice during startup");
                    return;
                }
                previousExitWasEarly = earlyExit;
                await Task.Delay(250, stop);
                Console.Error.WriteLine("rosella: XWayland exited unexpectedly; restarting");
                _current = start();
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        finally
        {
            _current?.Dispose();
            _current = null;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _watch.GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
