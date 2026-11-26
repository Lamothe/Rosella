using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Rosella.Protocol;
using Rosella.Server;

namespace Rosella.Compositor;

internal sealed class DrmSyncobjHandler(SafeFileHandle device) : IRequestHandler
{
    internal const string Interface = "wp_linux_drm_syncobj_manager_v1";

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        switch (HandlerSupport.Require(target, request).Name)
        {
            case "destroy":
                session.DestroyObject(target);
                break;
            case "get_surface":
                var surfaceId = request.Arguments[1].AsObject();
                if (!session.TryGetObject(surfaceId, out var resource) || resource.Handler is not SurfaceHandler surface || surface.Gone)
                {
                    throw new ProtocolErrorException(target.Id, ClientSession.ErrorInvalidObject, "invalid synchronization surface");
                }
                if (surface.DrmSyncobj is not null)
                {
                    throw new ProtocolErrorException(target.Id, 0, "the surface already has explicit synchronization");
                }
                var id = request.Arguments[0].AsNewId();
                var sync = new DrmSyncobjSurface(surface, id);
                HandlerSupport.CreateChild(session, target, "wp_linux_drm_syncobj_surface_v1", sync, id);
                surface.DrmSyncobj = sync;
                break;
            case "import_timeline":
                var timeline = DrmTimeline.Import(device, request.Arguments[1].AsFileDescriptor())
                    ?? throw new ProtocolErrorException(target.Id, 1, "could not import DRM synchronization timeline");
                try
                {
                    HandlerSupport.CreateChild(session, target, "wp_linux_drm_syncobj_timeline_v1", timeline, request.Arguments[0].AsNewId());
                }
                catch
                {
                    timeline.Dispose();
                    throw;
                }
                break;
        }
    }
}

internal sealed class DrmSyncobjSurface(SurfaceHandler surface, uint id) : IRequestHandler, IDisposable
{
    private DrmTimelinePoint? _acquire;
    private DrmTimelinePoint? _release;

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        var method = HandlerSupport.Require(target, request).Name;
        if (method == "destroy")
        {
            Dispose();
            session.DestroyObject(target);
            return;
        }
        if (surface.Gone)
        {
            throw new ProtocolErrorException(id, 1, "the synchronization surface has been destroyed");
        }
        var timelineId = request.Arguments[0].AsObject();
        if (!session.TryGetObject(timelineId, out var resource) || resource.Handler is not DrmTimeline timeline)
        {
            throw new ProtocolErrorException(id, ClientSession.ErrorInvalidObject, "invalid DRM synchronization timeline");
        }
        var value = ((ulong)request.Arguments[1].AsUInt32() << 32) | request.Arguments[2].AsUInt32();
        ref var pending = ref (method == "set_acquire_point" ? ref _acquire : ref _release);
        var point = new DrmTimelinePoint(timeline, value);
        pending?.Dispose();
        pending = point;
    }

    internal (DrmTimelinePoint? Acquire, DrmTimelinePoint? Release) Take(IClientBuffer? buffer)
    {
        if (buffer is null)
        {
            if (_acquire is not null || _release is not null)
            {
                throw new ProtocolErrorException(id, 3, "synchronization points require a new buffer");
            }
            return default;
        }
        if (buffer is not DmaBufBuffer)
        {
            throw new ProtocolErrorException(id, 2, "explicit synchronization requires a DMA-BUF buffer");
        }
        var acquire = _acquire ?? throw new ProtocolErrorException(id, 4, "missing acquire point");
        var release = _release ?? throw new ProtocolErrorException(id, 5, "missing release point");
        if (acquire.Timeline == release.Timeline && acquire.Value >= release.Value)
        {
            throw new ProtocolErrorException(id, 6, "the release point must follow the acquire point");
        }

        _acquire = null;
        _release = null;
        return (acquire, release);
    }

    public void Dispose()
    {
        if (surface.DrmSyncobj == this)
        {
            surface.DrmSyncobj = null;
        }
        _acquire?.Dispose();
        _release?.Dispose();
        _acquire = null;
        _release = null;
    }
}

internal sealed class DrmTimeline : SafeHandleZeroOrMinusOneIsInvalid, IRequestHandler
{
    private readonly SafeFileHandle _device;

    private DrmTimeline(SafeFileHandle device, uint timeline) : base(ownsHandle: true)
    {
        _device = device;
        SetHandle((nint)timeline);
    }

    internal uint Id => (uint)handle;
    internal int Device => (int)_device.DangerousGetHandle();

    internal static DrmTimeline? Import(SafeFileHandle device, int fd)
    {
        var retained = false;
        device.DangerousAddRef(ref retained);
        try
        {
            if (DrmSyncobjNative.drmSyncobjFDToHandle((int)device.DangerousGetHandle(), fd, out var handle) == 0 && handle != 0)
            {
                var timeline = new DrmTimeline(device, handle);
                retained = false;
                return timeline;
            }
            return null;
        }
        finally
        {
            if (retained)
            {
                device.DangerousRelease();
            }
        }
    }

    public void HandleRequest(ClientSession session, ServerObject target, DecodedMessage request)
    {
        HandlerSupport.Require(target, request);
        Dispose();
        session.DestroyObject(target);
    }

    protected override bool ReleaseHandle()
    {
        var result = DrmSyncobjNative.drmSyncobjDestroy(Device, Id);
        _device.DangerousRelease();
        return result == 0;
    }
}

internal sealed class DrmTimelinePoint : IDisposable
{
    private bool _retained;

    internal DrmTimelinePoint(DrmTimeline timeline, ulong value)
    {
        Timeline = timeline;
        Value = value;
        timeline.DangerousAddRef(ref _retained);
    }

    internal DrmTimeline Timeline { get; }
    internal ulong Value { get; }

    internal bool Wait()
    {
        const uint waitForSubmit = 2;
        // On Linux Stopwatch and the DRM wait both use CLOCK_MONOTONIC.
        // Bound the wait so a stalled producer does not wedge the X11 connection.
        var deadline = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 5;
        var handle = Timeline.Id;
        var point = Value;
        return DrmSyncobjNative.drmSyncobjTimelineWait(Timeline.Device, ref handle, ref point, 1, deadline, waitForSubmit, out _) == 0;
    }

    internal void Signal()
    {
        var handle = Timeline.Id;
        var point = Value;
        if (DrmSyncobjNative.drmSyncobjTimelineSignal(Timeline.Device, ref handle, ref point, 1) != 0)
        {
            Console.Error.WriteLine("rosella: failed to release a DRM synchronization point");
        }
    }

    public void Dispose()
    {
        if (_retained)
        {
            _retained = false;
            Timeline.DangerousRelease();
        }
    }
}

internal static class DrmSyncobjNative
{
    private const string Library = "libdrm.so.2";
    private const ulong TimelineCapability = 0x14;

    internal static bool SupportsTimelines(SafeFileHandle device) =>
        drmGetCap(device, TimelineCapability, out var enabled) == 0 && enabled != 0;

    [DllImport(Library)] private static extern int drmGetCap(SafeFileHandle device, ulong capability, out ulong value);
    [DllImport(Library)] internal static extern int drmSyncobjFDToHandle(int device, int fd, out uint syncobj);
    [DllImport(Library)] internal static extern int drmSyncobjDestroy(int device, uint syncobj);
    [DllImport(Library)] internal static extern int drmSyncobjTimelineWait(int device, ref uint handles, ref ulong points,
        uint count, long deadline, uint flags, out uint firstSignaled);
    [DllImport(Library)] internal static extern int drmSyncobjTimelineSignal(int device, ref uint handles, ref ulong points, uint count);
}
