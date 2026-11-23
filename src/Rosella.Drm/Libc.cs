using System.Runtime.InteropServices;

namespace Rosella.Drm;

/// <summary>The libc calls DRM interop needs.</summary>
internal static partial class Libc
{
    private const string Library = "libc";

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int ioctl(int fd, nuint request, nint argument);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int close(int fd);

    [LibraryImport(Library)]
    internal static partial nint ttyname(int fd);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial nint mmap(nint address, nuint length, int protection, int flags, int fd, nint offset);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int munmap(nint address, nuint length);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial nint read(int fd, nint buffer, nuint count);

    [LibraryImport(Library, SetLastError = true)]
    internal static unsafe partial int poll(PollFd* fds, nuint count, int timeoutMilliseconds);

    internal const int ProtRead = 0x1;
    internal const int ProtWrite = 0x2;
    internal const int MapShared = 0x1;
    internal const int MapFailedValue = -1;
    internal const int PollIn = 0x1;
}

[StructLayout(LayoutKind.Sequential)]
internal struct PollFd
{
    internal int Fd;
    internal short Events;
    internal short Revents;
}
