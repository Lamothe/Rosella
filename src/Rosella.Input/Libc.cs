using System.Runtime.InteropServices;

namespace Rosella.Input;

/// <summary>The libc calls evdev interop needs.</summary>
internal static partial class Libc
{
    private const string Library = "libc";

    internal const int OpenReadOnly = 0x0;
    internal const int OpenReadWrite = 0x2;
    internal const int SeekSet = 0;

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int open(nint path, int flags);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int close(int fd);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial nint read(int fd, nint buffer, nuint count);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial nint write(int fd, nint buffer, nuint count);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial nint lseek(int fd, nint offset, int whence);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int ioctl(int fd, nuint request, nint argument);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int memfd_create(nint name, uint flags);

    [LibraryImport(Library, SetLastError = true)]
    internal static unsafe partial int poll(PollFd* fds, nuint count, int timeoutMilliseconds);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int eventfd(uint initialValue, int flags);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial void free(nint pointer);

    internal const int ProtRead = 0x1;
    internal const int ProtWrite = 0x2;
    internal const int MapShared = 0x1;
    internal const int MapFailedValue = -1;
    internal const int PollIn = 0x1;
    internal const int EfdNonblock = 0x800;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PollFd
{
    internal int Fd;
    internal short Events;
    internal short Revents;
}
