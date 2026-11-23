using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Rosella.Compositor;

/// <summary>The libc calls shared-memory handling needs.</summary>
internal static partial class ShmNative
{
    private const string Library = "libc";

    [LibraryImport(Library, SetLastError = true)]
    private static partial int dup(int descriptor);

    [LibraryImport(Library, SetLastError = true)]
    internal static unsafe partial nint mmap(nint address, nuint length, int protection, int flags, int descriptor, nint offset);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial int munmap(nint address, nuint length);

    [LibraryImport(Library, SetLastError = true)]
    internal static partial nint lseek(int descriptor, nint offset, int whence);

    internal const int ProtRead = 0x1;
    internal const int MapShared = 0x1;
    internal const int MapFailedValue = -1;
    internal const int SeekEnd = 2;

    /// <summary>Duplicates a descriptor so it survives the session closing message fds.</summary>
    internal static SafeFileHandle Duplicate(int descriptor)
    {
        var copy = dup(descriptor);
        if (copy < 0)
        {
            throw new ShmValidationException(ShmErrors.InvalidFd, $"dup failed with errno {Marshal.GetLastPInvokeError()}");
        }

        return new SafeFileHandle((IntPtr)copy, ownsHandle: true);
    }
}
