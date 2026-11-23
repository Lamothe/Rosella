using Rosella.Protocol;
using Rosella.Server;
using Rosella.Server.Handlers;

namespace Rosella.Compositor;

/// <summary>The seat's pointer drag and its Wayland data offers. Selection lives in Clipboard.</summary>
internal sealed class DragAndDrop
{
    internal const uint Copy = 1;
    internal const uint Move = 2;
    internal const uint Ask = 4;
    internal const uint AllActions = Copy | Move | Ask;

    private readonly object _gate = new();
    private readonly List<(ClientSession Session, ServerObject Object)> _devices = [];
    private Drag? _drag;
    private uint _modifierAction;

    internal SurfaceHandler? Icon => Volatile.Read(ref _drag)?.Icon;

    internal void AddDevice(ClientSession session, ServerObject device)
    {
        lock (_gate)
        {
            _devices.Add((session, device));
        }
    }

    internal void RemoveDevice(ServerObject device)
    {
        lock (_gate)
        {
            _devices.RemoveAll(entry => entry.Object == device);
            if (_drag is { Device: { } focused } drag && focused == device)
            {
                Leave(drag);
            }
        }
    }

    internal bool Start(ClientSession session, SurfaceHandler origin, ClipboardSource? source, SurfaceHandler? icon)
    {
        lock (_gate)
        {
            if (_drag is not null)
            {
                return false;
            }

            _drag = new Drag(session, origin, icon, source, source?.TakeContent());
            return true;
        }
    }

    internal void Motion(SurfaceHandler? target, int x, int y, uint time)
    {
        lock (_gate)
        {
            if (_drag is { } drag)
            {
                UpdateTarget(drag, target, x, y);
                if (drag.Device is { } device)
                {
                    drag.Target!.Session!.TrySendEvent(device, "motion", WireArgument.UInt32(time), Coordinate(x), Coordinate(y));
                }
            }
        }
    }

    internal void Drop(SurfaceHandler? target, int x, int y)
    {
        lock (_gate)
        {
            if (_drag is not { } drag)
            {
                return;
            }

            // Release can land after a window vanishes or the pointer is warped.
            UpdateTarget(drag, target, x, y);
            if (drag.Device is { } device && (drag.Source is null ||
                drag.Offer is { } offer && offer.Action != 0 && (device.Version < 3 || offer.Mime is not null)))
            {
                if (drag.Target!.Session!.TrySendEvent(device, "drop"))
                {
                    if (drag.Offer is { } accepted)
                    {
                        accepted.Dropped = true;
                        drag.Source!.SendSourceEvent("dnd_drop_performed", 3);
                    }

                    Leave(drag);
                    _drag = null;
                    return;
                }
            }

            CancelLocked(drag);
        }
    }

    internal bool Cancel()
    {
        lock (_gate)
        {
            if (_drag is not { } drag)
            {
                return false;
            }

            CancelLocked(drag);
            return true;
        }
    }

    internal void SourceDestroyed(ClipboardSource source)
    {
        SurfaceHandler? icon = null;
        lock (_gate)
        {
            if (_drag is { } drag && drag.Source == source)
            {
                icon = drag.Icon;
                Leave(drag);
                _drag = null;
            }
        }
        icon?.Scene.MarkDirty();
    }

    internal void SurfaceHidden(SurfaceHandler surface)
    {
        lock (_gate)
        {
            if (_drag is not { } drag)
            {
                return;
            }

            if (drag.Origin == surface || drag.Origin.MainSurface == surface)
            {
                CancelLocked(drag);
            }
            else if (drag.Target == surface || drag.Target?.MainSurface == surface)
            {
                Leave(drag);
            }
        }
    }

    internal void SetModifiers(bool shift, bool control)
    {
        lock (_gate)
        {
            _modifierAction = control ? Copy : shift ? Move : 0;
            if (_drag?.Offer is { } offer)
            {
                UpdateAction(offer);
            }
        }
    }

    private void UpdateTarget(Drag drag, SurfaceHandler? target, int x, int y)
    {
        if (target is not { IsInputVisible: true, Session: not null } ||
            (drag.Source is null && target.Session != drag.Session))
        {
            target = null;
        }

        if (target == drag.Target)
        {
            return;
        }

        Leave(drag);
        if (target?.Session is not { } session ||
            _devices.Find(entry => entry.Session == session).Object is not { } device)
        {
            return;
        }

        var serial = session.NextSerial();
        DragOffer? offer = null;
        if (drag.Source is { } source)
        {
            offer = new DragOffer(this, session, source, drag.Content!, serial);
            if (session.CreateServerObject(session.Protocol.FindInterface("wl_data_offer")!, device.Version, offer)
                is not { } offerObject)
            {
                return;
            }

            offer.Object = offerObject;
            if (!session.TrySendEvent(device, "data_offer", WireArgument.NewId(offerObject.Id)))
            {
                session.Retire(offerObject);
                return;
            }

            foreach (var mime in offer.Content.Types)
            {
                session.TrySendEvent(offerObject, "offer", WireArgument.String(mime));
            }
            if (device.Version >= 3)
            {
                session.TrySendEvent(offerObject, "source_actions", WireArgument.UInt32(source.DragActions));
            }
        }

        drag.Device = device;
        drag.Target = target;
        drag.Offer = offer;
        if (!session.TrySendEvent(device, "enter", WireArgument.UInt32(serial), WireArgument.Object(target.Id),
            Coordinate(x), Coordinate(y), WireArgument.Object(offer?.Object.Id ?? 0)))
        {
            Leave(drag);
            return;
        }

        if (offer is not null)
        {
            UpdateAction(offer);
        }
    }

    private static WireArgument Coordinate(int value) => WireArgument.FixedPoint(Fixed.FromRaw(value << 8));

    private static void Leave(Drag drag)
    {
        if (drag.Device is { } device)
        {
            drag.Target!.Session!.TrySendEvent(device, "leave");
        }
        if (drag.Offer is { Dropped: false } offer)
        {
            offer.Source.SendSourceEvent("target", 1, WireArgument.NullString());
            offer.Source.SendSourceEvent("action", 3, WireArgument.UInt32(0));
        }

        drag.Device = null;
        drag.Target = null;
        drag.Offer = null;
    }

    private void CancelLocked(Drag drag)
    {
        Leave(drag);
        drag.Source?.SendSourceEvent("cancelled", 3);
        _drag = null;
    }

    private void UpdateAction(DragOffer offer)
    {
        var available = offer.Source.DragActions & (offer.Object.Version < 3 ? Copy : offer.Actions);
        var preferred = offer.Dropped ? offer.Preferred : _modifierAction & available;
        if (preferred == 0)
        {
            preferred = offer.Preferred;
        }
        var action = (available & preferred) != 0 ? preferred : available & (~available + 1);
        if (offer.Action == action)
        {
            return;
        }

        offer.Action = action;
        if (offer.Object.Version >= 3)
        {
            offer.Session.TrySendEvent(offer.Object, "action", WireArgument.UInt32(action));
        }
        offer.Source.SendSourceEvent("action", 3, WireArgument.UInt32(action));
    }

    private void Request(DragOffer offer, DecodedMessage request)
    {
        lock (_gate)
        {
            var name = HandlerSupport.Require(offer.Object, request).Name;
            if (name == "destroy")
            {
                RemoveOffer(offer);
                offer.Session.DestroyObject(offer.Object);
                return;
            }

            if (!offer.Dropped && _drag?.Offer != offer)
            {
                return;
            }

            switch (name)
            {
                case "accept":
                    if (request.Arguments[0].AsUInt32() == offer.EnterSerial || offer.Dropped)
                    {
                        var mime = request.Arguments[1].AsNullableString();
                        offer.Mime = offer.Content.Types.Contains(mime, StringComparer.Ordinal) ? mime : null;
                        offer.Source.SendSourceEvent("target", 1, offer.Mime is { } accepted ? WireArgument.String(accepted) : WireArgument.NullString());
                    }
                    break;

                case "receive":
                    var type = request.Arguments[0].AsString();
                    if (offer.Content.Types.Contains(type, StringComparer.Ordinal))
                    {
                        offer.Content.Send(type, request.Arguments[1].AsFileDescriptor());
                    }
                    break;

                case "set_actions":
                    var actions = request.Arguments[0].AsUInt32();
                    var preferred = request.Arguments[1].AsUInt32();
                    if ((actions & ~AllActions) != 0)
                    {
                        throw new ProtocolErrorException(offer.Object.Id, 1, "invalid drag action mask");
                    }
                    if ((preferred & actions) != preferred || (preferred & (preferred - 1)) != 0)
                    {
                        throw new ProtocolErrorException(offer.Object.Id, 2, "invalid preferred drag action");
                    }
                    offer.Actions = actions;
                    offer.Preferred = preferred;
                    UpdateAction(offer);
                    break;

                case "finish":
                    if (!offer.Dropped || offer.Mime is null || offer.Action is 0 or Ask)
                    {
                        throw new ProtocolErrorException(offer.Object.Id, 0, "drag offer was not accepted and dropped");
                    }
                    offer.Dropped = false;
                    offer.Source.SendSourceEvent("dnd_finished", 3);
                    break;
            }
        }
    }

    private void RemoveOffer(DragOffer offer)
    {
        lock (_gate)
        {
            if (offer.Dropped)
            {
                offer.Dropped = false;
                offer.Source.SendSourceEvent(offer.Object.Version < 3 ? "dnd_finished" : "cancelled", 3);
            }
            else if (_drag?.Offer == offer)
            {
                _drag.Offer = null;
            }
        }
    }

    private sealed record Drag(ClientSession Session, SurfaceHandler Origin, SurfaceHandler? Icon,
        ClipboardSource? Source, ClipboardContent? Content)
    {
        internal SurfaceHandler? Target;
        internal ServerObject? Device;
        internal DragOffer? Offer;
    }

    private sealed class DragOffer(DragAndDrop owner, ClientSession session, ClipboardSource source,
        ClipboardContent content, uint serial) : IRequestHandler, IDisposable
    {
        internal ClientSession Session { get; } = session;
        internal ClipboardSource Source { get; } = source;
        internal ClipboardContent Content { get; } = content;
        internal uint EnterSerial { get; } = serial;
        internal ServerObject Object = null!;
        internal uint Actions;
        internal uint Preferred;
        internal uint Action = uint.MaxValue;
        internal string? Mime;
        internal bool Dropped;

        public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request) => owner.Request(this, request);

        public void Dispose() => owner.RemoveOffer(this);
    }
}
