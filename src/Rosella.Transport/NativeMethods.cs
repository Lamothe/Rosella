using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rosella.Transport;

/// <summary>
/// The libc surface Rosella needs for SCM_RIGHTS file-descriptor passing.
/// Managed sockets do not expose ancillary data, so the compositor speaks
/// recvmsg/sendmsg directly — the same calls libwayland uses.
/// </summary>
internal static partial class NativeMethods
{
    private const string Libc = "libc";

    internal const int SolSocket = 1;
    internal const int ScmRights = 1;

    /// <summary>Do not raise SIGPIPE on closed connections.</summary>
    internal const int MsgNoSignal = 0x4000;

    /// <summary>Set close-on-exec on received descriptors.</summary>
    internal const int MsgCmsgCloexec = 0x40000000;

    /// <summary>Control data was truncated; some descriptors were lost.</summary>
    internal const int MsgCtrunc = 0x08;

    internal const int Eintr = 4;
    internal const int Eagain = 11;

    internal static int CmsgHeaderSize
    {
        get { return Unsafe.SizeOf<ControlMessageHeader>(); }
    }

    [LibraryImport(Libc, EntryPoint = "recvmsg", SetLastError = true)]
    internal static partial int RecvMsg(int socket, ref MessageHeader message, int flags);

    [LibraryImport(Libc, EntryPoint = "sendmsg", SetLastError = true)]
    internal static partial int SendMsg(int socket, ref MessageHeader message, int flags);

    /// <summary>Round up to the alignment of a native word, as CMSG_ALIGN does.</summary>
    internal static int CmsgAlign(int length)
    {
        var alignment = IntPtr.Size;
        return (length + alignment - 1) & ~(alignment - 1);
    }

    /// <summary>CMSG_SPACE: space a control message with the given payload occupies.</summary>
    internal static int CmsgSpace(int payloadLength)
    {
        return CmsgAlign(payloadLength + CmsgHeaderSize);
    }

    /// <summary>CMSG_LEN: the cmsg_len value for a payload of the given size.</summary>
    internal static int CmsgLen(int payloadLength)
    {
        return payloadLength + CmsgHeaderSize;
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct Iovec
{
    internal IntPtr IovBase;
    internal nuint IovLen;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MessageHeader
{
    internal IntPtr MsgName;
    internal uint MsgNameLen;
    internal IntPtr MsgIov;
    internal nuint MsgIovlen;
    internal IntPtr MsgControl;
    internal nuint MsgControllen;
    internal int MsgFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct ControlMessageHeader
{
    internal nuint CmsgLen;
    internal int CmsgLevel;
    internal int CmsgType;
}
