using Rosella.Drm;

namespace Rosella.Compositor.Tests;

public class ScanoutTests
{
    [Theory]
    [InlineData(DrmConstants.ConnectorTypeDisplayPort, 4u, "DP-4")]
    [InlineData(DrmConstants.ConnectorTypeHdmiA, 2u, "HDMI-2")]
    [InlineData(DrmConstants.ConnectorTypeEdp, 1u, "eDP-1")]
    [InlineData(1u, 1u, "VGA-1")]
    [InlineData(2u, 1u, "DVI-I-1")]
    [InlineData(16u, 1u, "DSI-1")]
    public void ConnectorNamesMatchGnome(uint type, uint id, string expected)
    {
        var connector = new DrmConnector(1, type, id, DrmConstants.ConnectorConnected, 0, 0, 0, [], []);
        Assert.Equal(expected, Scanout.GnomeConnectorName(connector));
    }

    [Fact]
    public void IsTargetFlip_MatchesOnlyOurFlipCompletion()
    {
        Assert.True(Scanout.IsTargetFlip(new DrmFlipEvent(DrmConstants.EventFlipComplete, 42, 1), 42));
        Assert.False(Scanout.IsTargetFlip(new DrmFlipEvent(DrmConstants.EventFlipComplete, 41, 1), 42)); // stale flip
        Assert.False(Scanout.IsTargetFlip(new DrmFlipEvent(DrmConstants.EventVblank, 42, 1), 42)); // wrong event type
        Assert.False(Scanout.IsTargetFlip(new DrmFlipEvent(DrmConstants.EventFlipComplete, 0, 1), 42)); // no user data
    }
}
