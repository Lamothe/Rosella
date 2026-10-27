using System.Runtime.InteropServices;
using System.Text;

namespace Rosella.Input;

/// <summary>
/// One open <c>/dev/input/event*</c> node: identity, capabilities, exclusive
/// grab, and blocking event reads. Shutdown is via poll() on a wake eventfd,
/// never by closing the device fd under a blocked reader (a recycled fd
/// number would be read as input); the reader closes the device when it
/// exits, or Dispose does when no reader ever started.
/// </summary>
public sealed class EvdevDevice : IDisposable
{
    private readonly int _fd;
    private readonly int _wakeFd;
    private volatile bool _disposed;
    private bool _grabbed;
    private bool _readerStarted;
    private bool _closed;

    private EvdevDevice(int fd, int wakeFd, string name, InputId id, ulong eventBits)
    {
        _fd = fd;
        _wakeFd = wakeFd;
        Name = name;
        Id = id;
        EventBits = eventBits;
    }

    public string Name { get; }

    public InputId Id { get; }

    /// <summary>Bitmap of supported EV_* types (bit N = type N).</summary>
    public ulong EventBits { get; }

    /// <summary>Opens a device node, or returns null if it cannot be opened.</summary>
    public static EvdevDevice? TryOpen(string path)
    {
        var fd = Open(path, Libc.OpenReadOnly);
        if (fd < 0)
        {
            return null;
        }

        var wakeFd = Libc.eventfd(0, Libc.EfdNonblock);
        if (wakeFd < 0)
        {
            Libc.close(fd);
            return null;
        }

        try
        {
            var id = GetId(fd);
            var name = GetName(fd);
            var eventBits = GetBits(fd, 0, 8);
            return new EvdevDevice(fd, wakeFd, name, id, eventBits);
        }
        catch
        {
            Libc.close(wakeFd);
            Libc.close(fd);
            return null;
        }
    }

    /// <summary>True if the device reports the given EV_* type.</summary>
    public bool HasEventType(int eventType) => (EventBits & (1UL << eventType)) != 0;

    /// <summary>True if the device reports the given key code.</summary>
    public bool HasKey(int code) => HasEventType(InputConstants.EventKey) && HasBit(GetBits(_fd, InputConstants.EventKey, 32), code);

    /// <summary>True if the device reports the given REL_* axis.</summary>
    public bool HasRelative(int code) => HasEventType(InputConstants.EventRelative) && HasBit(GetBits(_fd, InputConstants.EventRelative, 8), code);

    /// <summary>Takes the device exclusively (EVIOCGRAB 1): no other client sees it.</summary>
    public void Grab() => SetGrab(true);

    /// <summary>Releases the exclusive grab (EVIOCGRAB 0).</summary>
    public void Ungrab() => SetGrab(grabbed: false);

    /// <summary>
    /// Blocks until the next event. Throws <see cref="ObjectDisposedException"/>
    /// once the device is disposed (the wake fd fires), and IOException when
    /// the device is gone. Single reader at a time.
    /// </summary>
    public unsafe InputEvent ReadEvent()
    {
        _readerStarted = true;
        var fds = stackalloc PollFd[2];
        var bytes = stackalloc byte[InputEvent.Size];
        while (true)
        {
            if (_disposed)
            {
                CloseAfterRead();
                throw new ObjectDisposedException(nameof(EvdevDevice));
            }

            fds[0] = new PollFd { Fd = _fd, Events = Libc.PollIn };
            fds[1] = new PollFd { Fd = _wakeFd, Events = Libc.PollIn };
            var ready = Libc.poll(fds, 2, -1);
            if (ready < 0 && Marshal.GetLastPInvokeError() == 4 /* EINTR */)
            {
                continue;
            }

            if (_disposed || fds[1].Revents != 0)
            {
                CloseAfterRead();
                throw new ObjectDisposedException(nameof(EvdevDevice));
            }

            var read = Libc.read(_fd, (nint)bytes, InputEvent.Size);
            if (read == InputEvent.Size)
            {
                return InputEvent.Parse(new ReadOnlySpan<byte>(bytes, InputEvent.Size));
            }

            if (read < 0 && Marshal.GetLastPInvokeError() == 4 /* EINTR */)
            {
                continue;
            }

            CloseAfterRead();
            throw new IOException($"evdev read on '{Name}' failed ({read}, errno {Marshal.GetLastPInvokeError()})");
        }
    }

    public unsafe void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_grabbed)
        {
            try
            {
                SetGrab(grabbed: false);
            }
            catch (IOException)
            {
                // The device may already be gone; nothing else to do.
            }
        }

        // Wake a blocked reader; it closes the fds when it exits. Without a
        // reader, close here.
        var one = stackalloc byte[8];
        one[0] = 1;
        _ = Libc.write(_wakeFd, (nint)one, 8);
        if (!_readerStarted)
        {
            CloseAfterRead();
        }
    }

    /// <summary>Closes both fds exactly once (reader exit or dispose-without-reader).</summary>
    private void CloseAfterRead()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Libc.close(_fd);
        Libc.close(_wakeFd);
    }

    private unsafe void SetGrab(bool grabbed)
    {
        var value = stackalloc int[1];
        *value = grabbed ? 1 : 0;
        if (Libc.ioctl(_fd, InputConstants.IoctlGrab, (nint)value) < 0)
        {
            throw new IOException($"EVIOCGRAB failed on '{Name}' with errno {Marshal.GetLastPInvokeError()}");
        }

        _grabbed = grabbed;
    }

    private static unsafe InputId GetId(int fd)
    {
        var id = stackalloc ushort[4];
        if (Libc.ioctl(fd, InputConstants.IoctlGetId, (nint)id) < 0)
        {
            throw new IOException($"EVIOCGID failed with errno {Marshal.GetLastPInvokeError()}");
        }

        return new InputId(id[0], id[1], id[2], id[3]);
    }

    private static unsafe string GetName(int fd)
    {
        var buffer = stackalloc byte[256];
        if (Libc.ioctl(fd, InputConstants.IoctlGetName, (nint)buffer) < 0)
        {
            return "";
        }

        var length = 0;
        while (length < 256 && buffer[length] != 0)
        {
            length++;
        }

        return Encoding.UTF8.GetString(buffer, length);
    }

    private static unsafe ulong GetBits(int fd, int eventType, uint lengthBytes)
    {
        // Cap at 32 bytes = 256 codes, plenty for the evdev ranges Rosella reads.
        var buffer = stackalloc byte[32];
        new Span<byte>(buffer, 32).Clear();
        var request = InputConstants.IoctlGetBits(eventType, lengthBytes);
        if (Libc.ioctl(fd, request, (nint)buffer) < 0)
        {
            return 0;
        }

        var bits = 0UL;
        for (var i = 0; i < 8; i++)
        {
            bits |= (ulong)buffer[i] << (8 * i);
        }

        return bits;
    }

    private static bool HasBit(ulong bits, int code) => code < 64 && (bits & (1UL << code)) != 0;

    private static int Open(string path, int flags)
    {
        var bytes = Encoding.UTF8.GetBytes(path + "\0");
        unsafe
        {
            fixed (byte* pointer = bytes)
            {
                return Libc.open((nint)pointer, flags);
            }
        }
    }
}
