using System.Net.Sockets;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Rosella.Protocol;

namespace Rosella.Transport;

/// <summary>
/// A connected Wayland socket: sends and receives wire-format data together
/// with out-of-band file descriptors (SCM_RIGHTS).
/// </summary>
public sealed class WireConnection : IDisposable
{
    private const int ControlBufferSize = 256;

    /// <summary>The most descriptors one receive can carry (control buffer capacity).</summary>
    public const int MaxDescriptorsPerReceive = (ControlBufferSize - 16) / sizeof(int);

    /// <summary>The most descriptors one send may carry (the kernel's SCM_MAX_FD).</summary>
    public const int MaxDescriptorsPerSend = 253;

    private readonly int _descriptor;
    private bool _disposed;

    public WireConnection(Socket socket)
    {
        Socket = socket ?? throw new ArgumentNullException(nameof(socket));
        _descriptor = unchecked((int)socket.Handle);
    }

    /// <summary>The underlying connected socket.</summary>
    public Socket Socket { get; }

    /// <summary>
    /// Sends all of <paramref name="data"/>. Descriptors are attached to the
    /// first write only — later partial writes carry no control data.
    /// </summary>
    public unsafe void Send(ReadOnlySpan<byte> data, ReadOnlySpan<int> descriptors)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (data.IsEmpty && !descriptors.IsEmpty)
        {
            throw new ArgumentException("File descriptors cannot be sent with an empty payload.", nameof(data));
        }

        if (descriptors.Length > MaxDescriptorsPerSend)
        {
            throw new ArgumentException(
                $"At most {MaxDescriptorsPerSend} file descriptors can be sent at once, got {descriptors.Length}.",
                nameof(descriptors));
        }

        var controlSize = descriptors.Length > 0 ? NativeMethods.CmsgSpace(descriptors.Length * sizeof(int)) : 0;
        var control = stackalloc byte[controlSize == 0 ? 1 : controlSize];
        var offset = 0;
        var descriptorsPending = descriptors.Length > 0;
        while (offset < data.Length)
        {
            var chunk = data.Slice(offset);
            fixed (byte* dataPointer = chunk)
            fixed (int* descriptorsPointer = descriptors)
            {
                var iovec = new Iovec { IovBase = (IntPtr)dataPointer, IovLen = (nuint)chunk.Length };

                var header = new MessageHeader
                {
                    MsgIov = (IntPtr)(&iovec),
                    MsgIovlen = 1,
                    MsgControl = descriptorsPending ? (IntPtr)control : IntPtr.Zero,
                    MsgControllen = descriptorsPending ? (nuint)controlSize : 0,
                };

                if (descriptorsPending)
                {
                    var controlHeader = (ControlMessageHeader*)control;
                    controlHeader->CmsgLen = (nuint)NativeMethods.CmsgLen(descriptors.Length * sizeof(int));
                    controlHeader->CmsgLevel = NativeMethods.SolSocket;
                    controlHeader->CmsgType = NativeMethods.ScmRights;
                    var payload = (int*)(control + NativeMethods.CmsgHeaderSize);
                    for (var i = 0; i < descriptors.Length; i++)
                    {
                        payload[i] = descriptorsPointer[i];
                    }
                }

                var sent = NativeMethods.SendMsg(_descriptor, ref header, NativeMethods.MsgNoSignal);
                if (sent < 0)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == NativeMethods.Eintr)
                    {
                        continue;
                    }

                    if (error == NativeMethods.Eagain)
                    {
                        // Non-blocking socket with a full buffer (or send
                        // timeout): wait for room and retry.
                        Thread.Sleep(1);
                        continue;
                    }

                    throw new SocketException(error);
                }

                if (sent == 0)
                {
                    // Nothing was queued, so the descriptors were not sent
                    // either: fail loudly instead of looping and dropping them.
                    throw new IOException("sendmsg reported no progress.");
                }

                offset += sent;
                descriptorsPending = false;
            }
        }
    }

    /// <summary>Sends an encoded message and its out-of-band descriptors.</summary>
    public void Send(EncodedMessage message)
    {
        Send(message.Bytes, message.FileDescriptors.AsSpan());
    }

    /// <summary>
    /// Blocking receive into <paramref name="data"/> and
    /// <paramref name="descriptors"/>. Returns the byte count, 0 on orderly
    /// close, or -1 when the receive timeout expired with no data.
    /// </summary>
    public unsafe int Receive(Span<byte> data, Span<int> descriptors, out int descriptorCount)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        descriptorCount = 0;

        fixed (byte* dataPointer = data)
        fixed (int* descriptorsPointer = descriptors)
        {
            var control = stackalloc byte[ControlBufferSize];
            var iovec = new Iovec { IovBase = (IntPtr)dataPointer, IovLen = (nuint)data.Length };
            var header = new MessageHeader
            {
                MsgIov = (IntPtr)(&iovec),
                MsgIovlen = 1,
                MsgControl = (IntPtr)control,
                MsgControllen = ControlBufferSize,
            };

            var received = NativeMethods.RecvMsg(_descriptor, ref header, NativeMethods.MsgCmsgCloexec);
            if (received < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error == NativeMethods.Eagain || error == NativeMethods.Eintr)
                {
                    return -1;
                }

                throw new SocketException(error);
            }

            if (received == 0)
            {
                return 0;
            }

            descriptorCount = CollectDescriptors(header, control, descriptors);
            return received;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Socket.Dispose();
    }

    /// <summary>
    /// Copies received SCM_RIGHTS descriptors to <paramref name="descriptors"/>.
    /// Every descriptor the kernel delivered is closed before throwing, so
    /// nothing leaks on failure.
    /// </summary>
    private static unsafe int CollectDescriptors(MessageHeader header, byte* control, Span<int> descriptors)
    {
        var scratch = stackalloc int[MaxDescriptorsPerReceive];
        var count = 0;
        var offset = 0;
        while ((nuint)offset + (nuint)NativeMethods.CmsgHeaderSize <= header.MsgControllen)
        {
            var controlHeader = (ControlMessageHeader*)(control + offset);
            var payloadLength = (int)(controlHeader->CmsgLen - (nuint)NativeMethods.CmsgHeaderSize);
            if (payloadLength < 0 || (nuint)(offset + NativeMethods.CmsgHeaderSize + payloadLength) > header.MsgControllen)
            {
                CloseDescriptors(scratch, count);
                throw new WireProtocolException("Received a malformed control message.");
            }

            if (controlHeader->CmsgLevel == NativeMethods.SolSocket && controlHeader->CmsgType == NativeMethods.ScmRights)
            {
                var payload = (int*)(control + offset + NativeMethods.CmsgHeaderSize);
                var received = payloadLength / sizeof(int);
                for (var i = 0; i < received; i++)
                {
                    scratch[count] = payload[i];
                    count++;
                }
            }

            offset += NativeMethods.CmsgSpace(payloadLength);
        }

        if ((header.MsgFlags & NativeMethods.MsgCtrunc) != 0)
        {
            CloseDescriptors(scratch, count);
            throw new WireProtocolException("Control data was truncated; file descriptors were lost.");
        }

        if (count > descriptors.Length)
        {
            CloseDescriptors(scratch, count);
            throw new WireProtocolException("Peer sent more file descriptors than the caller can accept.");
        }

        for (var i = 0; i < count; i++)
        {
            descriptors[i] = scratch[i];
        }

        return count;
    }

    private static unsafe void CloseDescriptors(int* descriptors, int count)
    {
        for (var i = 0; i < count; i++)
        {
            using var handle = new SafeFileHandle((IntPtr)descriptors[i], ownsHandle: true);
        }
    }
}
