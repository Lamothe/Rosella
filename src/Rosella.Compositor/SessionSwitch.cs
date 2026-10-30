using System.Runtime.InteropServices;
using Rosella.Drm;

namespace Rosella.Compositor;

/// <summary>
/// The session's side of a VT switch. The kernel asks for release before
/// switching away (SIGUSR2) and announces our return (SIGUSR1) — the same
/// pair X uses; the .NET enum does not name them, but raw numbers work and
/// avoid the job-control signals a stray kill could fire. Both handlers
/// run on their own threads, so the handover work is safe to do there.
/// Order matters: hand the hardware over before allowing the switch, and
/// take it back before routing input again.
/// </summary>
internal sealed class SessionSwitch : IDisposable
{
    /// <summary>SIGUSR2 on Linux (the kernel's release request).</summary>
    private const int SignalRelease = 12;

    /// <summary>SIGUSR1 on Linux (the kernel's acquire announcement).</summary>
    private const int SignalAcquire = 10;

    private readonly object _gate = new();
    private readonly VirtualTerminal? _terminal;
    private PosixSignalRegistration? _sigRelease;
    private PosixSignalRegistration? _sigAcquire;
    private Scanout? _scanout;
    private InputThread? _input;
    private bool _released;
    private bool _disposed;

    internal SessionSwitch()
    {
        _terminal = VirtualTerminal.TryOpen();
        if (_terminal is null)
        {
            Console.Error.WriteLine("rosella: no controlling tty; VT switching disabled");
            return;
        }

        // Signals last: the handlers only fire once the terminal is in
        // VT_PROCESS mode, which TryOpen has just arranged.
        if (!OperatingSystem.IsWindows())
        {
            _sigRelease = PosixSignalRegistration.Create((PosixSignal)SignalRelease, context =>
            {
                context.Cancel = true;
                Release();
            });
            _sigAcquire = PosixSignalRegistration.Create((PosixSignal)SignalAcquire, context =>
            {
                context.Cancel = true;
                Acquire();
            });
        }

        _terminal.SetMode(VirtualTerminal.KdGraphics);
        Console.WriteLine($"rosella: VT {_terminal.Number} owned (Ctrl+Alt+Fn switches)");
    }

    /// <summary>
    /// Hands over the display and input when the kernel asks to switch
    /// away. Idempotent and attach-guarded: a stray signal before the
    /// hardware is wired (or a repeated one) must do no harm. The lock is
    /// held for the whole handover so a release and an acquire can never
    /// interleave — the kernel may signal from either thread at any time.
    /// </summary>
    private void Release()
    {
        lock (_gate)
        {
            if (_released)
            {
                return;
            }

            if (_scanout is not { } scanout || _input is not { } input)
            {
                // Nothing to hand over yet; the switch may still proceed.
                _terminal?.Release();
                return;
            }

            _released = true;
            input.Suspend();
            scanout.Release();

            // Only now may the switch complete — the kernel has been
            // waiting since the signal arrived.
            _terminal?.Release();
            Console.WriteLine("rosella: VT released (switched away)");
        }
    }

    /// <summary>
    /// Takes the display and input back once our VT is active again. The
    /// mirror of Release: harmless when no release ever happened.
    /// </summary>
    private void Acquire()
    {
        lock (_gate)
        {
            if (!_released)
            {
                return;
            }

            _released = false;
            _scanout?.Acquire();
            _input?.Resume();
            Console.WriteLine("rosella: VT acquired (switched back)");
        }
    }

    /// <summary>Ctrl+Alt+Fn: request a switch to VT <paramref name="vt"/>.</summary>
    internal void SwitchTo(int vt) => _terminal?.SwitchTo(vt);

    /// <summary>This session's VT and whether it owns the screen (control state).</summary>
    internal (int Number, bool Active)? VtState =>
        _terminal is { } terminal ? (terminal.Number, terminal.IsActive) : null;

    /// <summary>
    /// Wires the hardware the handover must coordinate. Both signals are
    /// attach-guarded, so registering the handlers first and the hardware
    /// later is safe either way.
    /// </summary>
    internal void Attach(Scanout scanout, InputThread input)
    {
        lock (_gate)
        {
            _scanout = scanout;
            _input = input;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _sigRelease?.Dispose();
        _sigAcquire?.Dispose();

        // Text console back on its own; VT_AUTO returns switching to the kernel.
        if (_terminal is { } terminal)
        {
            terminal.SetMode(VirtualTerminal.KdText);
            terminal.Dispose();
        }
    }
}
