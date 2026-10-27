using Rosella.Drm;

namespace Rosella.Compositor.Tests;

public class ScanoutTests
{
    [Fact]
    public void IsTargetFlip_MatchesOnlyOurFlipCompletion()
    {
        Assert.True(Scanout.IsTargetFlip(new DrmFlipEvent(DrmConstants.EventFlipComplete, 42, 1), 42));
        Assert.False(Scanout.IsTargetFlip(new DrmFlipEvent(DrmConstants.EventFlipComplete, 41, 1), 42)); // stale flip
        Assert.False(Scanout.IsTargetFlip(new DrmFlipEvent(DrmConstants.EventVblank, 42, 1), 42)); // wrong event type
        Assert.False(Scanout.IsTargetFlip(new DrmFlipEvent(DrmConstants.EventFlipComplete, 0, 1), 42)); // no user data
    }
}
