using Rosella.Compositor;
using Xunit;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The blank latch: the screen goes dark exactly once per blank, on the
/// policy's word alone (hardware failure cannot stop it), and the wake
/// resets it so the next blank flips again.
/// </summary>
public sealed class BlankStateTests
{
    [Fact]
    public void BlankFlipsBlackExactlyOnce()
    {
        var state = new BlankState();

        Assert.True(state.NeedsBlackFlip(wantBlank: true));
        Assert.True(state.IsBlackUp);
        Assert.False(state.NeedsBlackFlip(wantBlank: true));
        Assert.False(state.NeedsBlackFlip(wantBlank: true));
    }

    [Fact]
    public void WakeResetsTheLatchSoTheNextBlankFlipsAgain()
    {
        var state = new BlankState();
        Assert.True(state.NeedsBlackFlip(wantBlank: true));

        Assert.False(state.NeedsBlackFlip(wantBlank: false));
        Assert.False(state.IsBlackUp);

        Assert.True(state.NeedsBlackFlip(wantBlank: true));
    }

    [Fact]
    public void WakeWhileAwakeIsANoOp()
    {
        var state = new BlankState();

        Assert.False(state.NeedsBlackFlip(wantBlank: false));
        Assert.False(state.IsBlackUp);
    }
}
