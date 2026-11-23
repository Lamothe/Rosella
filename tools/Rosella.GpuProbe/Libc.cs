using System.Runtime.InteropServices;

namespace Rosella.GpuProbe;

/// <summary>
/// The handful of libc calls the probe needs. Nothing here allocates kernel
/// state: open, close, mmap, munmap.
/// </summary>
internal static unsafe class Libc
{
    internal const int O_RdWr = 2;
    internal const int OCloExec = 0x80000;
    internal const int ProtRead = 1;
    internal const int ProtWrite = 2;
    internal const int MapShared = 1;

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    internal static extern int Open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", EntryPoint = "close", SetLastError = true)]
    internal static extern int Close(int fd);

    [DllImport("libc", EntryPoint = "mmap", SetLastError = true)]
    internal static extern nint Mmap(nint address, nuint length, int prot, int flags, int fd, long offset);

    [DllImport("libc", EntryPoint = "munmap", SetLastError = true)]
    internal static extern int Munmap(nint address, nuint length);

    /// <summary>Maps an "mmap failed" into something readable; the errno is the point.</summary>
    internal static string ErrorName(int errno) => errno switch
    {
        0 => "SUCCESS",
        1 => "EPERM",
        2 => "ENOENT",
        5 => "EIO",
        6 => "ENXIO",
        9 => "EBADF",
        11 => "EAGAIN",
        12 => "ENOMEM",
        13 => "EACCES",
        16 => "EBUSY",
        19 => "ENODEV",
        22 => "EINVAL",
        25 => "ENOTTY",
        28 => "ENOSPC",
        38 => "ENOSYS",
        95 => "EOPNOTSUPP",
        110 => "ETIMEDOUT",
        _ => "errno " + errno.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}