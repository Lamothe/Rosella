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
