using Rosella.Compositor;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The screen-blanking policy: idle blanks it, input wakes it, and the
/// control socket can force either. Pure logic against a fake clock — the
/// DPMS call itself is the render loop's business and is exercised live.
/// </summary>
public class ScreenPowerTests
{
    /// <summary>A clock the test drives by hand.</summary>
    private sealed class Clock
    {
        public long Now;

        public long Read() => Now;
    }

    private static (ScreenPower Power, Clock Clock) Create()
    {
        var clock = new Clock();
        return (new ScreenPower(clock.Read), clock);
    }

    [Fact]
    public void StartsAwake()
    {
        var (power, _) = Create();
        Assert.False(power.IsBlanked);
    }

    [Fact]
    public void IdleBeforeBlank_IsFiveMinutes()
    {
        // Fixed by design, not a setting: long enough never to interrupt
        // reading, short enough to save a panel left on overnight.
        Assert.Equal(TimeSpan.FromMinutes(5), ScreenPower.IdleBeforeBlank);
    }

    [Fact]
    public void Tick_AfterIdlePeriod_Blanks()
    {
        var (power, clock) = Create();
        clock.Now = (long)ScreenPower.IdleBeforeBlank.TotalMilliseconds;

        Assert.Equal(ScreenPowerAction.Blank, power.Tick());
        Assert.True(power.IsBlanked);
    }

    [Fact]
    public void Tick_JustBeforeIdlePeriod_LeavesTheScreenLit()
    {
        var (power, clock) = Create();
        clock.Now = (long)ScreenPower.IdleBeforeBlank.TotalMilliseconds - 1;

        Assert.Equal(ScreenPowerAction.None, power.Tick());
        Assert.False(power.IsBlanked);
    }

    [Fact]
    public void Tick_WhileBlanked_DoesNothing()
    {
        var (power, clock) = Create();
        clock.Now = (long)ScreenPower.IdleBeforeBlank.TotalMilliseconds;
        power.Tick();

        clock.Now += 60_000;
        Assert.Equal(ScreenPowerAction.None, power.Tick());
        Assert.True(power.IsBlanked);
    }

    [Fact]
    public void Activity_BeforeIdlePeriod_KeepsTheScreenLit()
    {
        var (power, clock) = Create();
        clock.Now = (long)ScreenPower.IdleBeforeBlank.TotalMilliseconds - 1;
        Assert.Equal(ScreenPowerAction.None, power.MarkActivity());

        // The activity reset the clock, so the idle period starts again.
        clock.Now += (long)ScreenPower.IdleBeforeBlank.TotalMilliseconds - 1;
        Assert.Equal(ScreenPowerAction.None, power.Tick());
        Assert.False(power.IsBlanked);
    }

    [Fact]
    public void Activity_WhileBlanked_WakesTheScreen()
    {
        var (power, clock) = Create();
        clock.Now = (long)ScreenPower.IdleBeforeBlank.TotalMilliseconds;
        power.Tick();

        Assert.Equal(ScreenPowerAction.Unblank, power.MarkActivity());
        Assert.False(power.IsBlanked);
    }

    [Fact]
    public void Force_CanBlankWithoutWaitingOutTheIdlePeriod()
    {
        var (power, _) = Create();
        Assert.Equal(ScreenPowerAction.Blank, power.Force(blank: true));
        Assert.True(power.IsBlanked);
    }

    [Fact]
    public void Force_CanWakeTheScreen()
    {
        var (power, _) = Create();
        power.Force(blank: true);

        Assert.Equal(ScreenPowerAction.Unblank, power.Force(blank: false));
        Assert.False(power.IsBlanked);
    }

    [Fact]
    public void Force_ToTheCurrentState_ChangesNothing()
    {
        var (power, _) = Create();
        Assert.Equal(ScreenPowerAction.None, power.Force(blank: false));

        power.Force(blank: true);
        Assert.Equal(ScreenPowerAction.None, power.Force(blank: true));
    }

    [Fact]
    public void ForcedWake_IsNotImmediatelyUndoneByTheIdleClock()
    {
        // The machine is still idle, so a naive Tick would re-blank at once
        // and the wake would never be seen. The idle clock restarts instead.
        var (power, clock) = Create();
        clock.Now = (long)ScreenPower.IdleBeforeBlank.TotalMilliseconds * 2;
        power.Tick();
        Assert.True(power.IsBlanked);

        Assert.Equal(ScreenPowerAction.Unblank, power.Force(blank: false));
        Assert.Equal(ScreenPowerAction.None, power.Tick());
        Assert.False(power.IsBlanked);

        // ...and it stays lit for a fresh idle period before blanking again.
        clock.Now += (long)ScreenPower.IdleBeforeBlank.TotalMilliseconds - 1;
        Assert.Equal(ScreenPowerAction.None, power.Tick());
        clock.Now += 1;
        Assert.Equal(ScreenPowerAction.Blank, power.Tick());
    }

    [Fact]
    public void ForcedBlank_IsStillWokenByActivity()
    {
        var (power, _) = Create();
        power.Force(blank: true);

        Assert.Equal(ScreenPowerAction.Unblank, power.MarkActivity());
        Assert.False(power.IsBlanked);
    }

    [Fact]
    public void Activity_WhileLit_DoesNotSignalAChange()
    {
        var (power, clock) = Create();
        clock.Now = 1000;

        Assert.Equal(ScreenPowerAction.None, power.MarkActivity());
        Assert.False(power.IsBlanked);
    }
}
