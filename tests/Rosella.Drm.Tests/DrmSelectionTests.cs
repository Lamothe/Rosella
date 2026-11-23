namespace Rosella.Drm.Tests;

public class DrmSelectionTests
{
    private static DrmMode Mode(uint clock, ushort width, ushort height, uint type = 0, string name = "mode")
    {
        return new DrmMode(clock, width, 0, 0, 0, 0, height, 0, 0, 0, 0, 60, 0, type, name);
    }

    [Fact]
    public void SelectMode_PrefersPreferred()
    {
        var modes = new[]
        {
            Mode(1, 3840, 2160),
            Mode(2, 1920, 1080, DrmConstants.ModeTypePreferred),
        };

        Assert.Equal(2u, DrmSelection.SelectMode(modes).Clock);
    }

    [Fact]
    public void SelectMode_PrefersPreferredRegardlessOfSizeOrdering()
    {
        var preferredFirst = new[]
        {
            Mode(1, 1920, 1080, DrmConstants.ModeTypePreferred),
            Mode(2, 3840, 2160),
        };
        Assert.Equal(1u, DrmSelection.SelectMode(preferredFirst).Clock);

        var preferredMiddle = new[]
        {
            Mode(1, 3840, 2160),
            Mode(2, 1280, 720, DrmConstants.ModeTypePreferred),
            Mode(3, 7680, 4320),
        };
        Assert.Equal(2u, DrmSelection.SelectMode(preferredMiddle).Clock);
    }

    [Fact]
    public void SelectMode_FallsBackToLargest()
    {
        var modes = new[]
        {
            Mode(1, 1280, 720),
            Mode(2, 3840, 2160),
            Mode(3, 1920, 1080),
        };

        Assert.Equal(2u, DrmSelection.SelectMode(modes).Clock);
    }

    [Fact]
    public void SelectMode_TakesFirstWhenAllEqual()
    {
        var modes = new[] { Mode(1, 1920, 1080), Mode(2, 1920, 1080) };
        Assert.Equal(1u, DrmSelection.SelectMode(modes).Clock);
    }

    [Fact]
    public void SelectMode_RejectsEmpty()
    {
        Assert.Throws<ArgumentException>(() => DrmSelection.SelectMode([]));
    }

    [Fact]
    public void ConfiguredModeMatchesFractionalRefreshAndResolution()
    {
        var sixty = new DrmMode(148500, 1920, 0, 0, 2200, 0, 1080, 0, 0, 1125, 0, 60, 0, DrmConstants.ModeTypePreferred, "60");
        var fractional = sixty with { Clock = 148352, Type = 0, Name = "59.94" };
        var larger = Mode(1, 3840, 2160);
        Assert.Equal(fractional, DrmSelection.SelectMode([larger, sixty, fractional], 1920, 1080, 59.94));
        Assert.Equal(sixty, DrmSelection.SelectMode([larger, sixty, fractional], 1920, 1080, 75));
        Assert.Equal(sixty, DrmSelection.SelectMode([larger, sixty, fractional], 1280, 720, 60));
    }

    [Fact]
    public void RefreshIncludesInterlaceDoubleScanAndVScan()
    {
        var mode = new DrmMode(148500, 1920, 0, 0, 2200, 0, 1080, 0, 0, 1125, 0, 60, 0, 0, "mode");
        Assert.Equal(120, (mode with { Flags = 1 << 4 }).RefreshHz);
        Assert.Equal(30, (mode with { Flags = 1 << 5 }).RefreshHz);
        Assert.Equal(30, (mode with { VScan = 2 }).RefreshHz);
        var interlaced = mode with { Flags = 1 << 4, Name = "interlaced" };
        Assert.Equal(60, interlaced.GnomeRefreshHz);
        Assert.Equal(interlaced, DrmSelection.SelectMode([mode, interlaced], 1920, 1080, 60, true));
        Assert.Equal(mode, DrmSelection.SelectMode([interlaced, mode], 1920, 1080, 60, false));
    }

    [Fact]
    public void PreferredVideoRefreshKeepsNativeResolutionAndAcceptsFractionalRates()
    {
        var native = Mode(1, 3440, 1440, DrmConstants.ModeTypePreferred);
        var fractional = native with { Clock = 658860, HTotal = 3664, VTotal = 1500, VRefresh = 120, Type = 0 };
        // 120 Hz at a lower resolution or with a different scan type is not equivalent.
        var lowerResolution = fractional with { HDisplay = 2560 };
        var interlaced = fractional with { Flags = 1u << 4, Clock = fractional.Clock / 2 };
        var faster = native with { VRefresh = 144, Type = 0 };

        Assert.InRange(fractional.RefreshHz, 119.8, 120.2);
        Assert.Equal(fractional, DrmSelection.PreferRefreshRate([native, lowerResolution, interlaced, faster, fractional], native, 120));
        Assert.Equal(native, DrmSelection.PreferRefreshRate([native, lowerResolution, interlaced, faster], native, 120));
        Assert.Equal(faster, DrmSelection.PreferRefreshRate([fractional, faster], faster, 120));
    }

    [Fact]
    public void SelectCrtc_PrefersAttachedCrtc()
    {
        var encoder = new DrmEncoder(1, 0, CrtcId: 22, PossibleCrtcs: 0b01, PossibleClones: 0);
        Assert.Equal(22u, DrmSelection.SelectCrtc([11, 22], encoder));
    }

    [Fact]
    public void SelectCrtc_UsesMaskWhenUnattached()
    {
        var encoder = new DrmEncoder(1, 0, CrtcId: 0, PossibleCrtcs: 0b10, PossibleClones: 0);
        Assert.Equal(22u, DrmSelection.SelectCrtc([11, 22], encoder));
    }

    [Fact]
    public void SelectCrtc_RejectsIncompatibleEncoder()
    {
        var encoder = new DrmEncoder(1, 0, CrtcId: 0, PossibleCrtcs: 0b100, PossibleClones: 0);
        Assert.Throws<InvalidOperationException>(() => DrmSelection.SelectCrtc([11, 22], encoder));
    }

    [Fact]
    public void SelectCrtc_RejectsEmpty()
    {
        var encoder = new DrmEncoder(1, 0, CrtcId: 0, PossibleCrtcs: 1, PossibleClones: 0);
        Assert.Throws<ArgumentException>(() => DrmSelection.SelectCrtc([], encoder));
    }
}
