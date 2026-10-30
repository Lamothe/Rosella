using System.Runtime.InteropServices;

namespace Rosella.Drm;

/// <summary>
/// The session's virtual terminal. The compositor grabs input devices
/// exclusively, which kills the kernel's own Ctrl+Alt+Fn handling — so
/// switching VTs becomes the display server's job, exactly as it is for
/// X or any other session compositor. The dance: own the VT in VT_PROCESS
/// mode, let the kernel ask for release before a switch (SIGUSR2) and
/// announce our return (SIGUSR1), and park or resume around the switch so
/// the other VT's console or compositor gets a working display and input.
/// </summary>
public sealed class VirtualTerminal : IDisposable
{
    private const int OpenReadWrite = 0x2;
    private const int ONoTty = 0x100; // O_NOCTTY: never steal a controlling tty

    // /usr/include/linux/vt.h
    private const uint IoctlSetMode = 0x5602;
    private const uint IoctlGetState = 0x5603;
    private const uint IoctlRelDisp = 0x5605;
    private const uint IoctlActivate = 0x5606;
    private const uint IoctlWaitActive = 0x5607;

    public const int ModeAuto = 0x00;
    public const int ModeProcess = 0x01;

    // /usr/include/linux/kd.h
    private const uint IoctlSetKdMode = 0x4B3A;
    public const int KdText = 0x00;
    public const int KdGraphics = 0x01;

    private readonly int _fd;
    private bool _disposed;

    /// <summary>Our own VT number (parsed from the tty device).</summary>
    public int Number { get; }

    private VirtualTerminal(int fd, int number)
    {
        _fd = fd;
        Number = number;
    }

    /// <summary>
    /// Opens the controlling terminal and claims it in VT_PROCESS mode.
    /// Null when there is no controlling tty (e.g. a plain SSH run) — the
    /// session then simply never switches VTs.
    /// </summary>
    public static unsafe VirtualTerminal? TryOpen()
    {
        var fd = Libc.open("/dev/tty", OpenReadWrite | ONoTty);
        if (fd < 0)
        {
            return null;
        }

        var state = new VtStat();
        if (Libc.ioctl(fd, IoctlGetState, (nint)(&state)) < 0)
        {
            Libc.close(fd);
            return null;
        }

        // Our own VT: the terminal's number, not whatever is active now.
        int number = state.VActive;
        var name = Marshal.PtrToStringUTF8(Libc.ttyname(fd)) ?? string.Empty;
        if (name.StartsWith("/dev/tty", StringComparison.Ordinal)
            && int.TryParse(name.AsSpan("/dev/tty".Length), out var parsed))
        {
            number = parsed;
        }

        var mode = new VtMode
        {
            Mode = ModeProcess,
            Waitv = 0,

            // The kernel sends these before and after a switch: the classic
            // X pair — SIGUSR2 asks for release, SIGUSR1 announces our
            // return. Safe on Linux: the .NET runtime's own signal is
            // SIGRTMIN, and job-control signals (TSTP/CONT) must be avoided
            // or a stray kill -TSTP would run the handover for nothing.
            RelSig = SignalRelease,
            AcqSig = SignalAcquire,
            FrSig = 0,
        };
        if (Libc.ioctl(fd, IoctlSetMode, (nint)(&mode)) < 0)
        {
            Libc.close(fd);
            return null;
        }

        return new VirtualTerminal(fd, number);
    }

    /// <summary>The currently active VT number (whoever is on screen).</summary>
    public unsafe int Active
    {
        get
        {
            var state = new VtStat();
            return Libc.ioctl(_fd, IoctlGetState, (nint)(&state)) < 0 ? Number : state.VActive;
        }
    }

    /// <summary>Whether our VT owns the screen right now.</summary>
    public bool IsActive => Active == Number;

    /// <summary>Switches to another VT (returns once the request is made).</summary>
    public void SwitchTo(int vt) => Libc.ioctl(_fd, IoctlActivate, (nint)vt);

    /// <summary>Allows the pending switch away to proceed (call once parked).</summary>
    public void Release() => Libc.ioctl(_fd, IoctlRelDisp, (nint)1);

    /// <summary>Text mode (console owns the screen) or graphics mode (we do).</summary>
    public void SetMode(int mode) => Libc.ioctl(_fd, IoctlSetKdMode, (nint)mode);

    /// <summary>Back to the kernel's automatic VT handling on the way out.</summary>
    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var mode = new VtMode { Mode = ModeAuto };
        Libc.ioctl(_fd, IoctlSetMode, (nint)(&mode));
        Libc.close(_fd);
    }

    /// <summary>The signal numbers vt_mode wants (SIGUSR2/1 on Linux, as X uses).</summary>
    internal const short SignalRelease = 12;

    internal const short SignalAcquire = 10;

    [StructLayout(LayoutKind.Sequential)]
    private struct VtMode
    {
        internal byte Mode;
        internal byte Waitv;
        internal short RelSig;
        internal short AcqSig;
        internal short FrSig;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct VtStat
    {
        internal ushort VActive;
        internal ushort VSignal;
        internal ushort VState;
    }
}
