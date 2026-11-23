using System.Runtime.InteropServices;
using System.Text;

namespace Rosella.Drm;

/// <summary>A display mode (modeline) as the kernel reports it.</summary>
public readonly record struct DrmMode(
    uint Clock,
    ushort HDisplay,
    ushort HSyncStart,
    ushort HSyncEnd,
    ushort HTotal,
    ushort HSkew,
    ushort VDisplay,
    ushort VSyncStart,
    ushort VSyncEnd,
    ushort VTotal,
    ushort VScan,
    uint VRefresh,
    uint Flags,
    uint Type,
    string Name)
{
    public bool IsPreferred => (Type & DrmConstants.ModeTypePreferred) != 0;

    public long Area => (long)HDisplay * VDisplay;

    /// <summary>Mutter's saved mode rate, before interlace and doublescan adjustments.</summary>
    public double GnomeRefreshHz => Clock == 0 || HTotal == 0 || VTotal == 0
        ? VRefresh
        : Clock * 1000.0 / HTotal / VTotal / (VScan > 1 ? VScan : 1);

    public bool IsInterlaced => (Flags & (1u << 4)) != 0;

    /// <summary>Refresh in Hz from the modeline, including interlace, doublescan and vscan.</summary>
    public double RefreshHz
    {
        get
        {
            if (Clock == 0 || HTotal == 0 || VTotal == 0)
            {
                return VRefresh;
            }

            var refresh = Clock * 1000.0 / HTotal / VTotal;
            if ((Flags & (1u << 4)) != 0) // DRM_MODE_FLAG_INTERLACE
            {
                refresh *= 2;
            }
            if ((Flags & (1u << 5)) != 0) // DRM_MODE_FLAG_DBLSCAN
            {
                refresh /= 2;
            }
            if (VScan > 1)
            {
                refresh /= VScan;
            }
            return refresh;
        }
    }

    /// <summary>Offset of the name field in <see cref="DrmModeModeInfo"/>, pinned by layout tests.</summary>
    private const int NameFieldOffset = 36;

    internal static DrmMode FromNative(in DrmModeModeInfo native)
    {
        var copy = native;
        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref copy, 1));
        var nameBytes = bytes.Slice(NameFieldOffset, DrmConstants.DisplayModeNameLength);
        var terminator = nameBytes.IndexOf((byte)0);
        var name = Encoding.ASCII.GetString(nameBytes[..(terminator < 0 ? nameBytes.Length : terminator)]);
        return new DrmMode(
            native.Clock,
            native.HDisplay,
            native.HSyncStart,
            native.HSyncEnd,
            native.HTotal,
            native.HSkew,
            native.VDisplay,
            native.VSyncStart,
            native.VSyncEnd,
            native.VTotal,
            native.VScan,
            native.VRefresh,
            native.Flags,
            native.Type,
            name);
    }

    internal DrmModeModeInfo ToNative()
    {
        var native = new DrmModeModeInfo
        {
            Clock = Clock,
            HDisplay = HDisplay,
            HSyncStart = HSyncStart,
            HSyncEnd = HSyncEnd,
            HTotal = HTotal,
            HSkew = HSkew,
            VDisplay = VDisplay,
            VSyncStart = VSyncStart,
            VSyncEnd = VSyncEnd,
            VTotal = VTotal,
            VScan = VScan,
            VRefresh = VRefresh,
            Flags = Flags,
            Type = Type,
        };

        var bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref native, 1));
        var encoded = Encoding.ASCII.GetBytes(Name);
        encoded.AsSpan(0, Math.Min(encoded.Length, DrmConstants.DisplayModeNameLength))
            .CopyTo(bytes.Slice(NameFieldOffset, DrmConstants.DisplayModeNameLength));
        return native;
    }

    public override string ToString() => Name;
}

/// <summary>A display connector (output).</summary>
public sealed record DrmConnector(
    uint Id,
    uint Type,
    uint TypeId,
    uint Status,
    uint MmWidth,
    uint MmHeight,
    uint EncoderId,
    IReadOnlyList<uint> EncoderIds,
    IReadOnlyList<DrmMode> Modes)
{
    public bool IsConnected => Status == DrmConstants.ConnectorConnected;

    public string TypeName => Type switch
    {
        DrmConstants.ConnectorTypeDisplayPort => "DisplayPort",
        DrmConstants.ConnectorTypeHdmiA => "HDMI-A",
        DrmConstants.ConnectorTypeHdmiB => "HDMI-B",
        DrmConstants.ConnectorTypeDviD => "DVI-D",
        DrmConstants.ConnectorTypeEdp => "eDP",
        DrmConstants.ConnectorTypeLvds => "LVDS",
        _ => $"type-{Type}",
    };

    public override string ToString() => $"{TypeName}-{TypeId} ({(IsConnected ? "connected" : "disconnected")})";
}

/// <summary>An encoder as the kernel reports it.</summary>
public sealed record DrmEncoder(uint Id, uint Type, uint CrtcId, uint PossibleCrtcs, uint PossibleClones);

/// <summary>A CRTC's current state.</summary>
public sealed record DrmCrtcState(uint Id, uint FramebufferId, DrmMode? Mode, bool ModeValid, uint X = 0, uint Y = 0);

/// <summary>The object id spaces of a card.</summary>
public sealed record DrmResources(IReadOnlyList<uint> CrtcIds, IReadOnlyList<uint> ConnectorIds, IReadOnlyList<uint> EncoderIds);

/// <summary>Identity of the kernel driver behind a card.</summary>
public sealed record DrmVersionInfo(int Major, int Minor, int Patch, string Name, string Date, string Description);

/// <summary>Chosen output: a connector, its CRTC, and the mode to drive.</summary>
public sealed record DrmOutput(DrmConnector Connector, uint CrtcId, DrmMode Mode);
