using Rosella.Compositor;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The release/acquire handshake between the VT switch signal and the
/// render loop. The contract: the loop parks only while a release is in
/// force, and an acquire that lands early leaves no latch behind for the
/// next release to race against.
/// </summary>
public sealed class SwitchGateTests
{
    [Fact]
    public void TryPark_WithoutAReleaseReturnsFalse()
    {
        var gate = new SwitchGate();
        Assert.False(gate.IsReleased);
        Assert.False(gate.TryPark());
    }

    [Fact]
    public async Task Release_CompletesWhenTheLoopParks()
    {
        var gate = new SwitchGate();
        var release = Task.Run(() => gate.BeginRelease(TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => gate.IsReleased, TimeSpan.FromSeconds(5)));
        Assert.True(gate.TryPark());
        Assert.True(await release);
    }

    [Fact]
    public void Release_TimesOutButStaysInForceUntilAcquire()
    {
        // A bounded wait, not an abandoned release: the loop parks at its
        // next opportunity and only an acquire clears the state. (A second
        // release without an acquire never happens: the VT protocol hands
        // over one switch at a time.)
        var gate = new SwitchGate();
        Assert.False(gate.BeginRelease(TimeSpan.FromMilliseconds(20)));
        Assert.True(gate.IsReleased);
        Assert.True(gate.TryPark());
        gate.EndRelease();
        Assert.False(gate.IsReleased);
        Assert.False(gate.TryPark());
    }

    [Fact]
    public async Task AcquireLandingEarly_LeavesNoStaleLatch()
    {
        var gate = new SwitchGate();
        var release = Task.Run(() => gate.BeginRelease(TimeSpan.FromMilliseconds(100)));
        Assert.True(SpinWait.SpinUntil(() => gate.IsReleased, TimeSpan.FromSeconds(5)));

        // The acquire lands before the loop ever parks: the release expires
        // unparked, and the late park attempt must latch nothing.
        gate.EndRelease();
        Assert.False(await release);
        Assert.False(gate.IsReleased);
        Assert.False(gate.TryPark());

        // A fresh cycle still works end to end.
        var next = Task.Run(() => gate.BeginRelease(TimeSpan.FromSeconds(5)));
        Assert.True(SpinWait.SpinUntil(() => gate.IsReleased, TimeSpan.FromSeconds(5)));
        Assert.True(gate.TryPark());
        Assert.True(await next);
    }
}
