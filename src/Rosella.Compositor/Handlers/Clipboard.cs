using Microsoft.Win32.SafeHandles;
using Rosella.Protocol;
using Rosella.Server;
using Rosella.Server.Handlers;

namespace Rosella.Compositor;

/// <summary>One seat's Wayland clipboard, including compositor-owned PNG captures.</summary>
internal sealed class Clipboard
{
    private readonly object _gate = new();
    private readonly List<(ClientSession Session, ServerObject Object)> _devices = [];
    private ClientSession? _focused;
    private ClipboardContent? _content;
    private long _generation;

    internal void Focus(ClientSession? session)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_focused, session))
            {
                return;
            }

            _focused = session;
            _generation++;
            NotifyFocused();
        }
    }

    internal void AddDevice(ClientSession session, ServerObject device)
    {
        lock (_gate)
        {
            _devices.Add((session, device));
            if (session == _focused)
            {
                SendSelection(session, device);
            }
        }
    }

    internal void RemoveDevice(ServerObject device)
    {
        lock (_gate)
        {
            _devices.RemoveAll(item => ReferenceEquals(item.Object, device));
        }
    }

    internal void CopyPng(byte[] png)
    {
        lock (_gate)
        {
            Replace(new ClipboardContent(["image/png"], (_, fd) => WriteImage(png, fd)));
        }
    }

    internal void SetSelection(ClientSession session, ClipboardSource? source)
    {
        lock (_gate)
        {
            if (session != _focused || (source is not null && source.Used))
            {
                return;
            }

            var content = source?.TakeContent();
            Replace(content);
        }
    }

    internal void RemoveSource(ClipboardContent content)
    {
        lock (_gate)
        {
            if (ReferenceEquals(content, _content))
            {
                _content = null;
                _generation++;
                NotifyFocused();
            }
        }
    }

    private void Replace(ClipboardContent? content)
    {
        _content?.Cancel?.Invoke();
        _content = content;
        _generation++;
        NotifyFocused();
    }

    private void NotifyFocused()
    {
        foreach (var (session, device) in _devices)
        {
            if (session == _focused)
            {
                SendSelection(session, device);
            }
        }
    }

    private void SendSelection(ClientSession session, ServerObject device)
    {
        if (_content is not { } content)
        {
            session.TrySendEvent(device, "selection", WireArgument.Object(0));
            return;
        }

        var handler = new ClipboardOffer(this, content, _generation);
        var type = session.Protocol.FindInterface("wl_data_offer")!;
        if (session.CreateServerObject(type, device.Version, handler) is not { } offer)
        {
            return;
        }

        if (!session.TrySendEvent(device, "data_offer", WireArgument.NewId(offer.Id)))
        {
            session.Retire(offer);
            return;
        }

        foreach (var mime in content.Types)
        {
            session.TrySendEvent(offer, "offer", WireArgument.String(mime));
        }

        session.TrySendEvent(device, "selection", WireArgument.Object(offer.Id));
    }

    internal void Receive(ClientSession session, ClipboardContent content, long generation, string mime, int fd)
    {
        lock (_gate)
        {
            if (session == _focused && generation == _generation &&
                ReferenceEquals(content, _content) && content.Types.Contains(mime, StringComparer.Ordinal))
            {
                content.Send(mime, fd);
            }
        }
    }

    private static readonly SemaphoreSlim ImageTransfers = new(4);

    private static void WriteImage(byte[] image, int fd)
    {
        if (!ImageTransfers.Wait(0))
        {
            return;
        }

        SafeFileHandle copy;
        try
        {
            copy = ShmNative.Duplicate(fd);
        }
        catch (ShmValidationException)
        {
            ImageTransfers.Release();
            return;
        }

        _ = Task.Run(() =>
        {
            using (copy)
            {
                try
                {
                    using var stream = new FileStream(copy, FileAccess.Write);
                    stream.Write(image);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    // The recipient can close or supply an unusable descriptor.
                }
                finally
                {
                    ImageTransfers.Release();
                }
            }
        });
    }
}

internal sealed record ClipboardContent(string[] Types, Action<string, int> Send, Action? Cancel = null);

internal sealed class ClipboardManager(SeatState seat) : IRequestHandler
{
    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        switch (HandlerSupport.Require(target, request).Name)
        {
            case "create_data_source":
            {
                var source = new ClipboardSource(seat);
                var obj = HandlerSupport.CreateChild(session, target, "wl_data_source", source, request.Arguments[0].AsNewId());
                source.Bind(session, obj);
                break;
            }

            case "get_data_device":
            {
                var device = new ClipboardDevice(seat);
                var obj = HandlerSupport.CreateChild(session, target, "wl_data_device", device, request.Arguments[0].AsNewId());
                device.Bind(session, obj);
                break;
            }

            default:
                StubHandler.Instance.HandleRequest(session, target, request);
                break;
        }
    }
}

internal sealed class ClipboardDevice(SeatState seat) : IRequestHandler, IDisposable
{
    private ServerObject? _device;

    internal void Bind(ClientSession session, ServerObject device)
    {
        _device = device;
        seat.Clipboard.AddDevice(session, device);
        seat.Drag.AddDevice(session, device);
    }

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        switch (HandlerSupport.Require(target, request).Name)
        {
            case "set_selection":
            {
                if (!seat.IsValidInputSerial(session, request.Arguments[1].AsUInt32()))
                {
                    break;
                }

                var id = request.Arguments[0].AsObject();
                if (id == 0)
                {
                    seat.Clipboard.SetSelection(session, null);
                }
                else if (session.TryGetObject(id, out var obj) && obj.Handler is ClipboardSource source)
                {
                    if (source.HasDragActions)
                    {
                        throw new ProtocolErrorException(target.Id, 1, "a drag source cannot be used for clipboard selection");
                    }
                    seat.Clipboard.SetSelection(session, source);
                }
                break;
            }

            case "start_drag":
            {
                var sourceId = request.Arguments[0].AsObject();
                var source = sourceId == 0 ? null : ObjectHandler<ClipboardSource>(session, target, sourceId);
                var origin = ObjectHandler<SurfaceHandler>(session, target, request.Arguments[1].AsObject());
                var iconId = request.Arguments[2].AsObject();
                var icon = iconId == 0 ? null : ObjectHandler<SurfaceHandler>(session, target, iconId);
                seat.TryStartDrag(session, target, origin, source, icon, request.Arguments[3].AsUInt32());
                break;
            }

            case "release":
                Dispose();
                session.DestroyObject(target);
                break;

            default:
                StubHandler.Instance.HandleRequest(session, target, request);
                break;
        }
    }

    private static T ObjectHandler<T>(ClientSession session, ServerObject device, uint id) where T : class =>
        session.TryGetObject(id, out var obj) && obj.Handler is T handler
            ? handler
            : throw new ProtocolErrorException(device.Id, ClientSession.ErrorInvalidObject, $"invalid data device object {id}");

    public void Dispose()
    {
        if (_device is { } device)
        {
            seat.Clipboard.RemoveDevice(device);
            seat.Drag.RemoveDevice(device);
            _device = null;
        }
    }
}

internal sealed class ClipboardSource(SeatState seat) : IRequestHandler, IDisposable
{
    private ClientSession? _session;
    private ServerObject? _source;
    private ClipboardContent? _content;
    private readonly HashSet<string> _types = new(StringComparer.Ordinal);
    private uint? _dragActions;

    internal bool Used => _content is not null;
    internal bool HasDragActions => _dragActions.HasValue;
    internal uint DragActions => _dragActions ?? DragAndDrop.Copy;

    internal void Bind(ClientSession session, ServerObject source) => (_session, _source) = (session, source);

    internal void SendSourceEvent(string name, uint version, params WireArgument[] arguments)
    {
        if (_source is { } source && source.Version >= version)
        {
            _session!.TrySendEvent(source, name, arguments);
        }
    }

    internal ClipboardContent TakeContent()
    {
        _content = new ClipboardContent([.. _types],
            (mime, fd) => SendSourceEvent("send", 1, WireArgument.String(mime), WireArgument.FileDescriptor(fd)),
            () => SendSourceEvent("cancelled", 1));
        return _content;
    }

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        switch (HandlerSupport.Require(target, request).Name)
        {
            case "offer":
                if (!Used && _types.Count < 128)
                {
                    _types.Add(request.Arguments[0].AsString());
                }
                break;

            case "destroy":
                Dispose();
                session.DestroyObject(target);
                break;

            case "set_actions":
                var actions = request.Arguments[0].AsUInt32();
                if ((actions & ~DragAndDrop.AllActions) != 0)
                {
                    throw new ProtocolErrorException(target.Id, 0, "invalid drag action mask");
                }
                if (Used || HasDragActions)
                {
                    throw new ProtocolErrorException(target.Id, 1, "drag actions can only be set on an unused source");
                }
                _dragActions = actions;
                break;

            default:
                StubHandler.Instance.HandleRequest(session, target, request);
                break;
        }
    }

    public void Dispose()
    {
        _source = null;
        if (_content is { } content)
        {
            seat.Drag.SourceDestroyed(this);
            seat.Clipboard.RemoveSource(content);
        }
    }
}

internal sealed class ClipboardOffer(Clipboard clipboard, ClipboardContent content, long generation) : IRequestHandler
{
    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        if (HandlerSupport.Require(target, request).Name == "receive")
        {
            clipboard.Receive(session, content, generation,
                request.Arguments[0].AsString(), request.Arguments[1].AsFileDescriptor());
        }
        else
        {
            StubHandler.Instance.HandleRequest(session, target, request);
        }
    }
}
