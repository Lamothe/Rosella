using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Rosella.Protocol;
using Rosella.Server;
using Rosella.Server.Handlers;

namespace Rosella.Compositor;

internal sealed class DmaBufHandler(DmaBufReader reader) : IRequestHandler, IBindEvents
{
    internal const string Interface = "zwp_linux_dmabuf_v1";
    private const string FeedbackInterface = "zwp_linux_dmabuf_feedback_v1";

    public void SendBindEvents(ClientSession session, ServerObject target)
    {
        if (target.Version >= 4)
        {
            return;
        }

        foreach (var format in reader.Formats)
        {
            if (target.Version == 3)
            {
                session.SendEvent(target, "modifier", WireArgument.UInt32(format.Fourcc),
                    WireArgument.UInt32((uint)(format.Modifier >> 32)), WireArgument.UInt32((uint)format.Modifier));
            }
            else if (format.Modifier == DmaBufReader.InvalidModifier)
            {
                session.SendEvent(target, "format", WireArgument.UInt32(format.Fourcc));
            }
        }
    }

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var method = HandlerSupport.Require(target, request).Name;
        switch (method)
        {
            case "destroy":
                session.DestroyObject(target);
                break;
            case "create_params":
                HandlerSupport.CreateChild(session, target, "zwp_linux_buffer_params_v1",
                    new DmaBufParamsHandler(reader), request.Arguments[0].AsNewId());
                break;
            case "get_default_feedback":
                SendFeedback(session, target, request.Arguments[0].AsNewId());
                break;
            case "get_surface_feedback":
                var id = request.Arguments[1].AsObject();
                if (!session.TryGetObject(id, out var surface) || surface.Handler is not SurfaceHandler)
                {
                    throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidObject, $"invalid surface {id}");
                }

                SendFeedback(session, target, request.Arguments[0].AsNewId());
                break;
            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{method}'");
        }
    }

    private void SendFeedback(ClientSession session, ServerObject target, uint id)
    {
        var feedback = HandlerSupport.CreateChild(session, target, FeedbackInterface, StubHandler.Instance, id);
        var table = new byte[reader.Formats.Count * 16];
        var indices = new byte[reader.Formats.Count * 2];
        for (var index = 0; index < reader.Formats.Count; index++)
        {
            var format = reader.Formats[index];
            BitConverter.TryWriteBytes(table.AsSpan(index * 16), format.Fourcc);
            BitConverter.TryWriteBytes(table.AsSpan(index * 16 + 8), format.Modifier);
            BitConverter.TryWriteBytes(indices.AsSpan(index * 2), (ushort)index);
        }

        using var handle = DmaBufNative.CreateFormatTable(table);
        session.SendEvent(feedback, "format_table", WireArgument.FileDescriptor((int)handle.DangerousGetHandle()),
            WireArgument.UInt32((uint)table.Length));
        session.SendEvent(feedback, "main_device", WireArgument.Array(reader.DeviceId));
        session.SendEvent(feedback, "tranche_target_device", WireArgument.Array(reader.DeviceId));
        session.SendEvent(feedback, "tranche_flags", WireArgument.UInt32(0));
        session.SendEvent(feedback, "tranche_formats", WireArgument.Array(indices));
        session.SendEvent(feedback, "tranche_done");
        session.SendEvent(feedback, "done");
    }
}

internal sealed class DmaBufParamsHandler(DmaBufReader reader) : IRequestHandler, IDisposable
{
    private const uint AlreadyUsed = 0;
    private const uint PlaneIndex = 1;
    private const uint PlaneSet = 2;
    private const uint Incomplete = 3;
    private const uint InvalidFormat = 4;
    private const uint InvalidDimensions = 5;
    private const uint OutOfBounds = 6;
    private const uint InvalidBuffer = 7;
    private const uint InvertY = 1;
    private const long MaxSnapshotBytes = 256L * 1024 * 1024;

    private SafeFileHandle? _fd;
    private DmaBufPlane _plane;
    private bool _used;

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var method = HandlerSupport.Require(target, request).Name;
        if (method == "destroy")
        {
            Dispose();
            session.DestroyObject(target);
            return;
        }

        if (_used)
        {
            throw new ProtocolErrorException(target.Id, AlreadyUsed, "DMA-BUF params already used");
        }

        switch (method)
        {
            case "add":
                AddPlane(target, request.Arguments);
                break;
            case "create":
                CreateBuffer(session, target, request.Arguments, immediate: false);
                break;
            case "create_immed":
                CreateBuffer(session, target, request.Arguments, immediate: true);
                break;
            default:
                throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidMethod, $"unhandled request '{method}'");
        }
    }

    private void AddPlane(ServerObject target, IReadOnlyList<WireArgument> args)
    {
        if (args[1].AsUInt32() != 0)
        {
            throw new ProtocolErrorException(target.Id, PlaneIndex, "DMA-BUF format requires only plane zero");
        }

        if (_fd is not null)
        {
            throw new ProtocolErrorException(target.Id, PlaneSet, "DMA-BUF plane zero already set");
        }

        _fd = DmaBufNative.Duplicate(args[0].AsFileDescriptor());
        var modifier = ((ulong)args[4].AsUInt32() << 32) | args[5].AsUInt32();
        _plane = new DmaBufPlane((int)_fd.DangerousGetHandle(), args[2].AsUInt32(), args[3].AsUInt32(), modifier);
    }

    private void CreateBuffer(ClientSession session, ServerObject target, IReadOnlyList<WireArgument> args, bool immediate)
    {
        _used = true;
        var first = immediate ? 1 : 0;
        var width = args[first].AsInt32();
        var height = args[first + 1].AsInt32();
        var fourcc = args[first + 2].AsUInt32();
        var flags = args[first + 3].AsUInt32();

        if (_fd is not { } fd)
        {
            throw new ProtocolErrorException(target.Id, Incomplete, "missing DMA-BUF plane zero");
        }

        if (width <= 0 || height <= 0 || (long)width * height > MaxSnapshotBytes / 4)
        {
            throw new ProtocolErrorException(target.Id, InvalidDimensions, $"unsupported DMA-BUF size {width}x{height}");
        }

        if (!reader.Formats.Contains(new DmaBufFormat(fourcc, _plane.Modifier)))
        {
            throw new ProtocolErrorException(target.Id, InvalidFormat,
                $"unsupported DMA-BUF format 0x{fourcc:X8} and modifier 0x{_plane.Modifier:X16}");
        }

        var rowBytes = width * 4L;
        var length = ShmNative.lseek(_plane.Fd, 0, ShmNative.SeekEnd);
        if (_plane.Offset > int.MaxValue || _plane.Stride > int.MaxValue || _plane.Stride < rowBytes ||
            (length >= 0 && _plane.Offset + (height - 1L) * _plane.Stride + rowBytes > length))
        {
            throw new ProtocolErrorException(target.Id, OutOfBounds, "DMA-BUF plane is out of bounds");
        }

        var image = (flags & ~InvertY) == 0 ? reader.Import(width, height, fourcc, _plane) : 0;
        if (image == 0)
        {
            Dispose();
            if (immediate)
            {
                throw new ProtocolErrorException(target.Id, InvalidBuffer, "could not import DMA-BUF");
            }

            session.SendEvent(target, "failed");
            return;
        }

        var buffer = new DmaBufBuffer(reader, fd, image, width, height, fourcc, (flags & InvertY) != 0);
        _fd = null;
        var handler = new BufferHandler(buffer);
        try
        {
            var bufferType = session.Protocol.FindInterface("wl_buffer")!;
            var child = immediate
                ? session.CreateClientObject(args[0].AsNewId(), bufferType, 1, handler, target)
                : session.CreateServerObject(bufferType, 1, handler);
            if (child is null)
            {
                handler.Dispose();
                session.SendEvent(target, "failed");
                return;
            }

            handler.Bind(session, child);
            if (!immediate)
            {
                session.SendEvent(target, "created", WireArgument.NewId(child.Id));
            }
        }
        catch
        {
            handler.Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        _fd?.Dispose();
        _fd = null;
    }
}

internal sealed class DmaBufBuffer(
    DmaBufReader reader, SafeFileHandle fd, nint image, int width, int height, uint fourcc, bool invertY) : IClientBuffer
{
    public int Width => width;
    public int Height => height;
    public uint Format => fourcc == DmaBufReader.Argb8888 ? ShmFormats.Argb8888 : ShmFormats.Xrgb8888;

    public byte[]? ReadPixels(byte[]? reusable)
    {
        if (DmaBufNative.WaitForProducer((int)fd.DangerousGetHandle()) &&
            reader.Read(image, width, height, invertY, reusable) is { } pixels)
        {
            return pixels;
        }

        reader.ReportReadError(width, height);
        return null;
    }

    public void Release()
    {
        reader.Release(image);
        fd.Dispose();
    }
}

internal static class DmaBufNative
{
    public static SafeFileHandle CreateFormatTable(byte[] table)
    {
        const uint closeOnExec = 1;
        const uint allowSealing = 2;
        const int addSeals = 1033;
        const int allWriteSeals = 0xF;
        using var handle = Owned(memfd_create("rosella-dmabuf-formats", closeOnExec | allowSealing));
        RandomAccess.Write(handle, table, 0);
        if (fcntl((int)handle.DangerousGetHandle(), addSeals, allWriteSeals) < 0)
        {
            throw new IOException($"could not seal the DMA-BUF format table (errno {Marshal.GetLastPInvokeError()})");
        }

        return Duplicate((int)handle.DangerousGetHandle());
    }

    public static SafeFileHandle Duplicate(int fd)
    {
        const int duplicateCloseOnExec = 1030;
        return Owned(fcntl(fd, duplicateCloseOnExec, 0));
    }

    public static bool WaitForProducer(int fd)
    {
        // POLLIN on a DMA-BUF waits for implicitly attached write fences.
        const short pollIn = 1;
        const int interrupted = 4;
        var item = new PollFd { Fd = fd, Events = pollIn };
        int result;
        do
        {
            result = poll(ref item, 1, 250);
        }
        while (result < 0 && Marshal.GetLastPInvokeError() == interrupted);

        return result > 0 && (item.ReturnedEvents & pollIn) != 0;
    }

    private static SafeFileHandle Owned(int fd) => fd >= 0
        ? new SafeFileHandle(fd, ownsHandle: true)
        : throw new IOException($"could not acquire DMA-BUF descriptor (errno {Marshal.GetLastPInvokeError()})");

    [StructLayout(LayoutKind.Sequential)]
    private struct PollFd
    {
        public int Fd;
        public short Events;
        public short ReturnedEvents;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int memfd_create([MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int fcntl(int fd, int operation, int argument);

    [DllImport("libc", SetLastError = true)]
    private static extern int poll(ref PollFd fds, nuint count, int milliseconds);
}
