using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>
/// The "it is loading" marker: a spinning busy pointer from the moment a click actually spawns a process
/// until that app's first window appears (or the launch is given up on). A
/// click that just focuses an existing window never spins — the window is
/// that click's answer.
/// <para>
/// A pending launch is resolved by whichever happens first: a matching
/// window mapped in the scene (the normal end), the spawned process dying
/// with no window (it crashed or daemonised out from under us), or the
/// timeout (some apps hand off to a launcher whose child outlives the
/// exec'd pid without ever exposing one to us, so liveness alone is not
/// the truth).
/// </para>
/// <para>
/// The clock, the process-liveness check and the live app ids are injected
/// so rotation and expiry are testable; the scene wires the live versions
/// and the dirty callback. Like the clock face and the pointer shrink, the
/// turn is a thing that changes on its own: the scanout loop's idle sleep
/// is bounded by <see cref="UntilNextTick"/>.
/// </para>
/// </summary>
internal sealed class LaunchSpinner
{
    /// <summary>One rotation step per this interval, matching the scanout idle floor.</summary>
    internal const int TickMilliseconds = 50;

    private const int StepsPerTurn = 24;

    /// <summary>How long a launch may spin before we stop claiming to load.</summary>
    internal const int MaximumLiveMilliseconds = 30_000;

    /// <summary>Current time in milliseconds, like the scene's clock.</summary>
    internal Func<long> Now { get; set; } = () => Environment.TickCount64;

    /// <summary>True when the pid still has a live process (the test seam).</summary>
    internal Func<int, bool> IsProcessAlive { get; set; } = ProcessAlive;

    /// <summary>The app ids with a window on screen right now (the scene's).</summary>
    internal Func<IReadOnlyList<string>>? LiveAppIds { get; set; }

    /// <summary>Set by the scene: the spin changes the picture, so it must dirty it.</summary>
    internal Action? Dirty { get; set; }

    /// <summary>True when something is spinning and the loop must keep ticking.</summary>
    internal bool IsActive
    {
        get
        {
            lock (_gate)
            {
                return _pending.Count > 0;
            }
        }
    }

    /// <summary>
    /// How long until the next rotation step, or null when nothing spins.
    /// Bounded by <see cref="TickMilliseconds"/> so a slow clock step
    /// cannot make the spin stutter.
    /// </summary>
    internal TimeSpan? UntilNextTick()
    {
        if (!IsActive)
        {
            return null;
        }

        var intoTick = (int)(Now() % TickMilliseconds);
        return TimeSpan.FromMilliseconds(TickMilliseconds - intoTick);
    }

    /// <summary>
    /// Starts the spin for a launcher whose click spawned a process. A null
    /// pid means nothing started, so nothing spins.
    /// </summary>
    internal void Start(PinnedLauncher launcher, int? pid)
    {
        if (pid is null)
        {
            return;
        }

        lock (_gate)
        {
            // Re-clicking a launcher that is still loading re-arms the spin:
            // the newest exec is the newest truth about what is loading.
            _pending.RemoveAll(entry => ReferenceEquals(entry.Launcher, launcher));
            _pending.Add(new Pending(launcher, pid.Value, Now()));
        }

        Dirty?.Invoke();
    }

    /// <summary>
    /// Advances the spin against the truth: drops entries whose app window
    /// has appeared, whose process died, or which outlived the timeout, and
    /// dirties the scene when the picture changed (a crossed step boundary
    /// or a removal).
    /// </summary>
    internal void Advance()
    {
        lock (_gate)
        {
            if (_pending.Count == 0)
            {
                return;
            }

            var changed = false;
            var live = LiveAppIds?.Invoke() ?? [];
            for (var i = _pending.Count - 1; i >= 0; i--)
            {
                if (IsResolved(_pending[i], live))
                {
                    _pending.RemoveAt(i);
                    changed = true;
                }
            }

            // Steady rotation changes the picture too: the arc moved.
            var step = StepLocked();
            if (_pending.Count > 0 && step != _lastStep)
            {
                _lastStep = step;
                changed = true;
            }

            if (changed)
            {
                Dirty?.Invoke();
            }
        }
    }

    /// <summary>True while this launcher has a launch waiting on its window.</summary>
    internal bool IsSpinning(PinnedLauncher launcher)
    {
        lock (_gate)
        {
            return _pending.Any(entry => ReferenceEquals(entry.Launcher, launcher));
        }
    }

    /// <summary>The current rotation step.</summary>
    internal int Step
    {
        get
        {
            lock (_gate)
            {
                return StepLocked();
            }
        }
    }

    internal const int CursorRadius = 12;

    /// <summary>Draws a translucent ring and a rotating, antialiased arc in the damaged viewport.</summary>
    internal void DrawCursor(Span<byte> pixels, int width, int height, int stride, int x, int y, int scale)
    {
        var radius = 9.6 * scale;
        var trace = 8.2 * scale;
        var thickness = 0.9 * scale;
        var start = ((Step * Math.Tau) / StepsPerTurn) - (Math.PI / 2);
        const double Sweep = Math.PI * 0.65;
        var end = start + Sweep;
        var startX = Math.Cos(start) * trace;
        var startY = Math.Sin(start) * trace;
        var endX = Math.Cos(end) * trace;
        var endY = Math.Sin(end) * trace;
        var bounds = CursorRadius * scale;
        var target = MemoryMarshal.Cast<byte, uint>(pixels);

        for (var row = Math.Max(0, y - bounds); row < Math.Min(height, y + bounds + 1); row++)
        {
            for (var column = Math.Max(0, x - bounds); column < Math.Min(width, x + bounds + 1); column++)
            {
                var dx = column - x + 0.5;
                var dy = row - y + 0.5;
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                ref var destination = ref target[(row * (stride / 4)) + column];
                var disc = Coverage(radius - distance);
                var rim = Coverage(radius - (0.65 * scale) - distance);
                var centre = Coverage(radius - (1.55 * scale) - distance);
                BlendShade(ref destination, 54, 127 * (disc - rim));
                BlendShade(ref destination, 238, 120 * (rim - centre));
                BlendShade(ref destination, 228, 14 * centre);

                var angle = (Math.Atan2(dy, dx) - start + (2 * Math.Tau)) % Math.Tau;
                var arcDistance = angle <= Sweep
                    ? Math.Abs(distance - trace)
                    : Math.Sqrt(Math.Min(
                        ((dx - startX) * (dx - startX)) + ((dy - startY) * (dy - startY)),
                        ((dx - endX) * (dx - endX)) + ((dy - endY) * (dy - endY))));
                BlendShade(ref destination, 248, 235 * Coverage(thickness - arcDistance));
            }
        }
    }

    private static double Coverage(double signedDistance) => Math.Clamp(signedDistance + 0.5, 0, 1);

    private static void BlendShade(ref uint destination, uint shade, double opacity)
    {
        var alpha = (uint)Math.Round(opacity);
        var value = shade * alpha / 255;
        Blitter.Blend(ref destination, (alpha << 24) | (value << 16) | (value << 8) | value);
    }

    private readonly object _gate = new();
    private readonly List<Pending> _pending = [];
    private int _lastStep = -1;

    private sealed record Pending(PinnedLauncher Launcher, int Pid, long StartedAt);

    private bool IsResolved(Pending entry, IReadOnlyList<string> liveAppIds)
    {
        foreach (var appId in liveAppIds)
        {
            if (PinnedMatch.Matches(appId, entry.Launcher.MatchKeys))
            {
                return true;
            }
        }

        if (!IsProcessAlive(entry.Pid))
        {
            return true;
        }

        return Now() - entry.StartedAt >= MaximumLiveMilliseconds;
    }

    private int StepLocked()
    {
        var steps = unchecked(Now() / TickMilliseconds) % StepsPerTurn;
        return (int)(steps < 0 ? steps + StepsPerTurn : steps);
    }

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
