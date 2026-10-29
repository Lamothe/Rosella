using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Rosella.Drm;

/// <summary>
/// An open DRM card: modesetting, dumb buffers, framebuffers and page flips.
/// </summary>
public sealed unsafe class DrmDevice : IDisposable
{
    private const int VersionStringCapacity = 256;
    private const int MaxPasses = 5;

    /// <summary>Offset of the name field in <see cref="DrmModeGetProperty"/>, pinned by layout tests.</summary>
    private const int PropNameFieldOffset = 24;

    private readonly SafeFileHandle _handle;
    private readonly int _fd;
    private bool _disposed;

    private DrmDevice(SafeFileHandle handle, string path)
    {
        _handle = handle;
        _fd = unchecked((int)handle.DangerousGetHandle());
        Path = path;
    }

    public string Path { get; }

    /// <summary>Card device nodes, sorted (card0, card1, …).</summary>
    public static IReadOnlyList<string> CardPaths()
    {
        if (!Directory.Exists("/dev/dri"))
        {
            return [];
        }

        return Directory.GetFiles("/dev/dri", "card*").Order(StringComparer.Ordinal).ToArray();
    }

    public static DrmDevice Open(string path)
    {
        var handle = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        return new DrmDevice(handle, path);
    }

    public DrmVersionInfo GetVersion()
    {
        var native = new DrmVersion();
        var name = Marshal.AllocHGlobal(VersionStringCapacity);
        var date = Marshal.AllocHGlobal(VersionStringCapacity);
        var description = Marshal.AllocHGlobal(VersionStringCapacity);
        try
        {
            native.NameLen = VersionStringCapacity;
            native.Name = name;
            native.DateLen = VersionStringCapacity;
            native.Date = date;
            native.DescLen = VersionStringCapacity;
            native.Desc = description;
            Ioctl(DrmConstants.IoctlVersion, ref native);
            return new DrmVersionInfo(
                native.VersionMajor,
                native.VersionMinor,
                native.VersionPatchlevel,
                ReadVersionString(name, native.NameLen),
                ReadVersionString(date, native.DateLen),
                ReadVersionString(description, native.DescLen));
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(date);
            Marshal.FreeHGlobal(description);
        }
    }

    /// <summary>
    /// The kernel reports the full string length while copying only what fits,
    /// so the returned length may exceed the buffer — clamp before reading, and
    /// trim at the terminator when the buffer was smaller than the string.
    /// </summary>
    internal static string ReadVersionString(nint buffer, ulong reportedLength)
    {
        var length = (int)Math.Min(reportedLength, VersionStringCapacity);
        var value = Marshal.PtrToStringUTF8(buffer, length) ?? string.Empty;
        var terminator = value.IndexOf('\0');
        return terminator < 0 ? value : value[..terminator];
    }

    /// <summary>Returns a DRM_CAP_* value.</summary>
    public ulong GetCap(ulong capability)
    {
        var native = new DrmGetCap { Capability = capability };
        Ioctl(DrmConstants.IoctlGetCap, ref native);
        return native.Value;
    }

    public DrmResources GetResources()
    {
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var native = new DrmModeCardRes();
            Ioctl(DrmConstants.IoctlModeGetResources, ref native);

            var crtcIds = new uint[native.CountCrtcs];
            var connectorIds = new uint[native.CountConnectors];
            var encoderIds = new uint[native.CountEncoders];
            fixed (uint* crtcs = crtcIds)
            fixed (uint* connectors = connectorIds)
            fixed (uint* encoders = encoderIds)
            {
                native.CrtcIdPtr = (ulong)crtcs;
                native.ConnectorIdPtr = (ulong)connectors;
                native.EncoderIdPtr = (ulong)encoders;
                Ioctl(DrmConstants.IoctlModeGetResources, ref native);
            }

            // The lists may have changed between the two calls.
            if (crtcIds.Length == native.CountCrtcs && connectorIds.Length == native.CountConnectors && encoderIds.Length == native.CountEncoders)
            {
                return new DrmResources(crtcIds, connectorIds, encoderIds);
            }
        }

        throw new DrmException("GETRESOURCES kept changing", 0);
    }

    public DrmConnector GetConnector(uint connectorId)
    {
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var native = new DrmModeGetConnector { ConnectorId = connectorId };
            Ioctl(DrmConstants.IoctlModeGetConnector, ref native);

            var modes = new DrmModeModeInfo[native.CountModes];
            var encoderIds = new uint[native.CountEncoders];
            var props = new uint[native.CountProps];
            var propValues = new ulong[native.CountProps];
            fixed (DrmModeModeInfo* modePointer = modes)
            fixed (uint* encoderPointer = encoderIds)
            fixed (uint* propPointer = props)
            fixed (ulong* propValuePointer = propValues)
            {
                native.ModesPtr = (ulong)modePointer;
                native.EncodersPtr = (ulong)encoderPointer;
                native.PropsPtr = (ulong)propPointer;
                native.PropValuesPtr = (ulong)propValuePointer;
                Ioctl(DrmConstants.IoctlModeGetConnector, ref native);
            }

            if (modes.Length != native.CountModes || encoderIds.Length != native.CountEncoders)
            {
                continue;
            }

            var managedModes = new DrmMode[modes.Length];
            for (var i = 0; i < modes.Length; i++)
            {
                managedModes[i] = DrmMode.FromNative(in modes[i]);
            }

            return new DrmConnector(
                native.ConnectorId,
                native.ConnectorType,
                native.ConnectorTypeId,
                native.Connection,
                native.MmWidth,
                native.MmHeight,
                native.EncoderId,
                encoderIds,
                managedModes);
        }

        throw new DrmException("GETCONNECTOR kept changing", 0);
    }

    public DrmEncoder GetEncoder(uint encoderId)
    {
        var native = new DrmModeGetEncoder { EncoderId = encoderId };
        Ioctl(DrmConstants.IoctlModeGetEncoder, ref native);
        return new DrmEncoder(native.EncoderId, native.EncoderType, native.CrtcId, native.PossibleCrtcs, native.PossibleClones);
    }

    public DrmCrtcState GetCrtc(uint crtcId)
    {
        var native = new DrmModeCrtc { CrtcId = crtcId };
        Ioctl(DrmConstants.IoctlModeGetCrtc, ref native);
        DrmMode? mode = native.ModeValid != 0 ? DrmMode.FromNative(in native.Mode) : null;
        return new DrmCrtcState(native.CrtcId, native.FbId, mode, native.ModeValid != 0);
    }

    /// <summary>Modesets a CRTC: full scanout of <paramref name="framebufferId"/> on the named connectors.</summary>
    public void SetCrtc(uint crtcId, uint framebufferId, IReadOnlyList<uint> connectorIds, DrmMode mode)
    {
        var connectors = connectorIds.ToArray();
        var native = new DrmModeCrtc
        {
            CrtcId = crtcId,
            FbId = framebufferId,
            ModeValid = 1,
            Mode = mode.ToNative(),
        };

        fixed (uint* connectorPointer = connectors)
        {
            native.SetConnectorsPtr = (ulong)connectorPointer;
            native.CountConnectors = (uint)connectors.Length;
            Ioctl(DrmConstants.IoctlModeSetCrtc, ref native);
        }
    }

    public DrmDumbBuffer CreateDumb(uint width, uint height, uint bpp)
    {
        var native = new DrmModeCreateDumb { Width = width, Height = height, Bpp = bpp };
        Ioctl(DrmConstants.IoctlModeCreateDumb, ref native);

        var map = new DrmModeMapDumb { Handle = native.Handle };
        nint address = 0;
        try
        {
            Ioctl(DrmConstants.IoctlModeMapDumb, ref map);
            address = Libc.mmap(0, checked((nuint)native.Size), Libc.ProtRead | Libc.ProtWrite, Libc.MapShared, _fd, unchecked((nint)map.Offset));
            if (address == (nint)Libc.MapFailedValue)
            {
                throw new DrmException("mmap of dumb buffer", Marshal.GetLastPInvokeError());
            }

            return new DrmDumbBuffer(this, native.Handle, native.Pitch, native.Size, address);
        }
        catch
        {
            if (address != 0 && address != (nint)Libc.MapFailedValue)
            {
                Libc.munmap(address, checked((nuint)native.Size));
            }

            DestroyDumbHandle(native.Handle);
            throw;
        }
    }

    public uint AddFramebuffer(uint width, uint height, uint pitch, uint bpp, uint depth, uint dumbHandle)
    {
        var native = new DrmModeFbCmd
        {
            Width = width,
            Height = height,
            Pitch = pitch,
            Bpp = bpp,
            Depth = depth,
            Handle = dumbHandle,
        };
        Ioctl(DrmConstants.IoctlModeAddFb, ref native);
        return native.FbId;
    }

    public void RemoveFramebuffer(uint framebufferId)
    {
        var id = framebufferId;
        Ioctl(DrmConstants.IoctlModeRemoveFb, ref id);
    }

    /// <summary>Schedules a page flip to <paramref name="framebufferId"/>; completion arrives from <see cref="ReadEvents"/>.</summary>
    public void PageFlip(uint crtcId, uint framebufferId, ulong userData)
    {
        var native = new DrmModeCrtcPageFlip
        {
            CrtcId = crtcId,
            FbId = framebufferId,
            Flags = DrmConstants.PageFlipEvent,
            UserData = userData,
        };
        Ioctl(DrmConstants.IoctlModePageFlip, ref native);
    }

    // Modeset base-object properties: how DPMS is read and written. The
    // property *id* is allocated per driver, so callers look it up by name.

    /// <summary>Reads the property ids and values attached to a modeset object.</summary>
    public (uint[] PropIds, ulong[] Values) GetObjectProperties(uint objectId, uint objectType)
    {
        for (var pass = 0; pass < MaxPasses; pass++)
        {
            var native = new DrmModeObjGetProperties { ObjId = objectId, ObjType = objectType };
            Ioctl(DrmConstants.IoctlModeObjGetProperties, ref native);

            var propIds = new uint[native.CountProps];
            var values = new ulong[native.CountProps];
            fixed (uint* propPointer = propIds)
            fixed (ulong* valuePointer = values)
            {
                native.PropsPtr = (ulong)propPointer;
                native.PropValuesPtr = (ulong)valuePointer;
                Ioctl(DrmConstants.IoctlModeObjGetProperties, ref native);
            }

            if (propIds.Length == native.CountProps)
            {
                return (propIds, values);
            }
        }

        throw new DrmException("OBJ_GETPROPERTIES kept changing", 0);
    }

    /// <summary>
    /// Reads a property's symbolic name (the key callers match on).
    /// </summary>
    public string GetPropertyName(uint propId)
    {
        var native = new DrmModeGetProperty { PropId = propId };
        Ioctl(DrmConstants.IoctlModeGetProperty, ref native);

        // Offset of the name field, pinned by layout tests.
        var copy = native;
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref copy, 1));
        var nameBytes = bytes.Slice(PropNameFieldOffset, DrmConstants.PropNameLength);
        var terminator = nameBytes.IndexOf((byte)0);
        return Encoding.UTF8.GetString(nameBytes[..(terminator < 0 ? nameBytes.Length : terminator)]);
    }

    /// <summary>Sets one property on a connector (e.g. DPMS).</summary>
    public void SetConnectorProperty(uint connectorId, uint propId, ulong value)
    {
        var native = new DrmModeConnectorSetProperty
        {
            Value = value,
            PropId = propId,
            ConnectorId = connectorId,
        };
        Ioctl(DrmConstants.IoctlModeSetProperty, ref native);
    }

    /// <summary>
    /// Turns a connector's DPMS on or off — a real power state, not black
    /// pixels: the panel stops being driven. The property id is looked up by
    /// name ("DPMS") because ids are allocated per driver; the lookup is
    /// cached, and a driver without the property simply reports false.
    /// </summary>
    public bool SetDpms(uint connectorId, bool on)
    {
        var propId = FindProperty(connectorId, "DPMS");
        if (propId is null)
        {
            return false;
        }

        SetConnectorProperty(connectorId, propId.Value, on ? DrmConstants.DpmsOn : DrmConstants.DpmsOff);
        return true;
    }

    /// <summary>Finds a named property on a connector, or null when absent.</summary>
    public uint? FindProperty(uint connectorId, string name)
    {
        var (propIds, _) = GetObjectProperties(connectorId, DrmConstants.ObjectConnector);
        foreach (var propId in propIds)
        {
            if (string.Equals(GetPropertyName(propId), name, StringComparison.Ordinal))
            {
                return propId;
            }
        }

        return null;
    }

    /// <summary>
    /// Waits for DRM events and returns them. Polls in short intervals so the
    /// token can interrupt; throws <see cref="OperationCanceledException"/> on
    /// cancellation.
    /// </summary>
    public IReadOnlyList<DrmFlipEvent> ReadEvents(CancellationToken cancellationToken = default)
    {
        var buffer = stackalloc byte[128];
        while (true)
        {
            WaitForReadable(cancellationToken);

            var received = Libc.read(_fd, (nint)buffer, 128);
            if (received >= 0)
            {
                return ParseEvents(new ReadOnlySpan<byte>(buffer, (int)received));
            }

            var error = Marshal.GetLastPInvokeError();
            if (error != 4 /* EINTR */)
            {
                throw new DrmException("read of DRM events", error);
            }
        }
    }

    /// <summary>Parses one read batch of DRM events. Defensive against corrupt lengths.</summary>
    internal static IReadOnlyList<DrmFlipEvent> ParseEvents(ReadOnlySpan<byte> data)
    {
        var headerSize = Marshal.SizeOf<DrmEvent>();
        var vblankSize = Marshal.SizeOf<DrmEventVblank>();
        var events = new List<DrmFlipEvent>();
        var offset = 0;
        while (offset + headerSize <= data.Length)
        {
            var header = MemoryMarshal.Read<DrmEvent>(data.Slice(offset, headerSize));
            if (header.Length < headerSize || header.Length > (uint)(data.Length - offset))
            {
                break;
            }

            if (header.Length >= vblankSize && header.Type is DrmConstants.EventVblank or DrmConstants.EventFlipComplete)
            {
                var vblank = MemoryMarshal.Read<DrmEventVblank>(data.Slice(offset, vblankSize));
                events.Add(new DrmFlipEvent(header.Type, vblank.UserData, vblank.Sequence));
            }
            else
            {
                events.Add(new DrmFlipEvent(header.Type, 0, 0));
            }

            offset += (int)header.Length;
        }

        return events;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    internal void DestroyDumbHandle(uint handle)
    {
        if (_disposed)
        {
            return;
        }

        var native = new DrmModeDestroyDumb { Handle = handle };
        Ioctl(DrmConstants.IoctlModeDestroyDumb, ref native);
    }

    private void WaitForReadable(CancellationToken cancellationToken)
    {
        var descriptor = stackalloc PollFd[1];
        descriptor->Fd = _fd;
        descriptor->Events = Libc.PollIn;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            descriptor->Revents = 0;
            var ready = Libc.poll(descriptor, 1, 250);
            if (ready > 0)
            {
                return;
            }

            if (ready < 0)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != 4 /* EINTR */)
                {
                    throw new DrmException("poll of DRM events", error);
                }
            }
        }
    }

    private void Ioctl<T>(uint request, ref T value)
        where T : unmanaged
    {
        fixed (T* pointer = &value)
        {
            if (Libc.ioctl(_fd, request, (nint)pointer) < 0)
            {
                throw new DrmException($"ioctl 0x{request:X8}", Marshal.GetLastPInvokeError());
            }
        }
    }
}
