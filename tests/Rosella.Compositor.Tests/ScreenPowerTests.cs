using Rosella.Compositor;
using Rosella.Input;

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
    public void DesktopSelectionOnlyResetsTheDestinationTimer()
    {
        var clock = new Clock();
        var sony = new ScreenPower(clock.Read);
        var dell = new ScreenPower(clock.Read);
        var seat = new SeatState();
        var secondary = new InputRouter(new Scene(), seat, () => { }, screenPower: dell);
        using var input = new InputThread(new Scene(), seat, () => { }, screenPower: sony, secondary: (secondary, seat));
        var delay = (long)ScreenPower.IdleBeforeBlank.TotalMilliseconds;

        clock.Now = delay - 1;
        secondary.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeX, 3));
        input.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeX, 3));
        clock.Now++;
        Assert.Equal(ScreenPowerAction.Blank, dell.Tick());
        Assert.Equal(ScreenPowerAction.None, sony.Tick());
        input.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.ButtonBack, InputConstants.KeyPressed));
        Assert.False(dell.IsBlanked);

        clock.Now = 2 * delay - 1;
        input.PrimaryRouter.ProcessEvent(new InputEvent(0, 0, InputConstants.EventSyn, 0, 0));
        Assert.Equal(ScreenPowerAction.Blank, sony.Tick());
        Assert.Equal(ScreenPowerAction.None, dell.Tick());
        input.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, InputConstants.ButtonForward, InputConstants.KeyPressed));
        Assert.False(sony.IsBlanked);

        clock.Now++;
        Assert.Equal(ScreenPowerAction.Blank, dell.Tick());
        input.ProcessEvent(new InputEvent(0, 0, InputConstants.EventSyn, 0, 0));
        Assert.True(dell.IsBlanked);
    }

    [Fact]
    public void SynchronizationPacketsDoNotWakeTheSelectedDisplay()
    {
        var (power, _) = Create();
        var router = new InputRouter(new Scene(), new SeatState(), () => { }, screenPower: power);
        power.Force(blank: true);

        router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventSyn, 0, 0));
        Assert.True(power.IsBlanked);
        router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventRelative, InputConstants.RelativeX, 1));
        Assert.False(power.IsBlanked);
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
