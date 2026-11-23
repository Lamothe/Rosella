using System.Runtime.InteropServices;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The busy cursor: a click that spawns starts the animation until
/// the app's window appears, its process dies, or the timeout passes. The
/// clock and the liveness probe are injected, so the whole lifecycle runs
/// deterministically here.
/// </summary>
public class LaunchSpinnerTests
{
    private static uint PixelAt(byte[] buffer, int stride, int x, int y) =>
        MemoryMarshal.Read<uint>(buffer.AsSpan((y * stride) + (x * 4)));

    private static PinnedLauncher Pin(string id) =>
        new(id, $"{id}.desktop", [id], [id], null, null);

    private static LaunchSpinner MakeSpinner(Func<long> clock, Func<int, bool> alive)
    {
        var spinner = new LaunchSpinner { Now = clock, IsProcessAlive = alive };
        return spinner;
    }

    [Fact]
    public void Start_SpinsForASpawnedLaunch_ButNotForANullPid()
    {
        var spinner = MakeSpinner(() => 0, _ => true);
        spinner.Start(Pin("a"), null);
        Assert.False(spinner.IsActive, "a null pid means nothing was spawned");

        spinner.Start(Pin("a"), 4000);
        Assert.True(spinner.IsActive);
    }

    [Fact]
    public void Click_ReturnsThePidOnlyWhenItActuallySpawned()
    {
        var windows = new FakeRegistry { LaunchPid = 4242 };
        Assert.Equal(4242, Pin("a").Click(windows));

        var withWindow = new FakeRegistry { Match = new SurfaceHandler(new Scene()), LaunchPid = 1 };
        Assert.Null(Pin("a").Click(withWindow)); // focus wins over spawn
        Assert.Equal(0, withWindow.Launches);
    }
    [Fact]
    public void WindowAppearing_EndsTheSpin()
    {
        var launcher = Pin("firefox");
        var spinner = MakeSpinner(() => 0, _ => true);
        spinner.LiveAppIds = () => [];
        spinner.Start(launcher, 4242);
        Assert.True(spinner.IsSpinning(launcher));

        spinner.Advance();
        Assert.True(spinner.IsSpinning(launcher));

        spinner.LiveAppIds = () => ["firefox"];
        spinner.Advance();
        Assert.False(spinner.IsSpinning(launcher));
        Assert.False(spinner.IsActive);
    }

    [Fact]
    public void DeadProcess_EndsTheSpin()
    {
        var launcher = Pin("terminal");
        var alive = true;
        var spinner = MakeSpinner(() => 0, _ => alive);
        spinner.LiveAppIds = () => [];
        spinner.Start(launcher, 4242);

        alive = false;
        spinner.Advance();
        Assert.False(spinner.IsSpinning(launcher));
    }

    [Fact]
    public void Timeout_EndsAStuckLaunch()
    {
        var launcher = Pin("chrome");
        var now = 0L;
        var spinner = MakeSpinner(() => now, _ => true);
        spinner.LiveAppIds = () => [];
        spinner.Start(launcher, 4242);

        now = LaunchSpinner.MaximumLiveMilliseconds - 1;
        spinner.Advance();
        Assert.True(spinner.IsSpinning(launcher));

        now = LaunchSpinner.MaximumLiveMilliseconds;
        spinner.Advance();
        Assert.False(spinner.IsSpinning(launcher));
    }

    [Fact]
    public void ReClick_RearmsTheSameLauncher_WithoutDoublingIt()
    {
        var launcher = Pin("code");
        var now = 0L;
        var spinner = MakeSpinner(() => now, _ => true);
        spinner.LiveAppIds = () => [];
        spinner.Start(launcher, 111);
        spinner.Start(launcher, 222); // the second exec is the newest truth

        // Only the newest pid is tracked: killing it ends the spin.
        spinner.IsProcessAlive = pid => pid != 222;
        spinner.Advance();
        Assert.False(spinner.IsActive);
    }

    [Fact]
    public void ClockClicks_FireTheDirtyCallbackOncePerStep_AndNotTwicePerStep()
    {
        var now = 0L;
        var spinner = MakeSpinner(() => now, _ => true);
        spinner.LiveAppIds = () => [];
        var dirties = 0;
        spinner.Dirty = () => dirties++;
        spinner.Start(Pin("a"), 4242);
        Assert.Equal(1, dirties); // Start itself changes the picture

        // The first Advance after Start crosses into a new step boundary at
        // least, or the Start dirty covers it; either way a second Advance in
        // the same tick must not double-dirty.
        dirties = 0;
        spinner.Advance();
        var first = dirties;
        spinner.Advance();
        Assert.Equal(first, dirties);

        now += LaunchSpinner.TickMilliseconds;
        spinner.Advance();
        Assert.True(dirties > first, "the next rotation step must dirty the scene");
    }

    [Fact]
    public void IdleSpinner_NeverWakesTheScanoutLoop()
    {
        var spinner = MakeSpinner(() => 0, _ => true);
        Assert.Null(spinner.UntilNextTick());

        spinner.Start(Pin("a"), 4242);
        var wait = spinner.UntilNextTick();
        Assert.NotNull(wait);
        Assert.InRange(wait!.Value.TotalMilliseconds, 0, LaunchSpinner.TickMilliseconds);
    }

    [Fact]
    public void IdleWait_BoundedByTheSpinnerStep()
    {
        var now = new DateTime(2026, 11, 7, 12, 0, 30);
        var clockOnly = Scanout.IdleWait(now, null, null, null);
        Assert.True(Scanout.IdleWait(now, null, null, TimeSpan.FromSeconds(1)) < clockOnly);

        // Never shorter than the loop's floor.
        Assert.Equal(TimeSpan.FromMilliseconds(50), Scanout.IdleWait(now, null, null, TimeSpan.Zero));
    }

    [Fact]
    public void DotGeometry_HeadIsLargest_AndTailNeverVanishes()
    {
        Assert.Equal(5, LaunchSpinner.DotDiameter(0));
        for (var phase = 1; phase < 12; phase++)
        {
            Assert.True(LaunchSpinner.DotDiameter(phase) <= LaunchSpinner.DotDiameter(phase - 1));
            Assert.True(LaunchSpinner.DotDiameter(phase) >= 1);
        }

        Assert.Equal(12, LaunchSpinner.PhaseOffsets.Length);
    }

    [Fact]
    public void Draw_SpinsAtTheCursor()
    {
        const int width = 160;
        const int height = 100;
        const int stride = width * 4;
        var now = 0L;
        var spinner = MakeSpinner(() => now, _ => true);
        var scene = new Scene { Now = () => now, OutputSize = (width, height) };
        scene.Shell = new Shell(spinner: spinner);
        scene.Cursor = (80, 40);

        var idle = new byte[stride * height];
        scene.Render(idle, width, height, stride, 0);
        spinner.Start(Pin("test"), 4242);
        var busy = new byte[stride * height];
        scene.Render(busy, width, height, stride, 1);

        Assert.Equal(0xFFF0F1F5u, PixelAt(busy, stride, 80, 40 - LaunchSpinner.CursorRadius));
        Assert.Equal(PixelAt(idle, stride, 0, 0), PixelAt(busy, stride, 0, 0));

        now += LaunchSpinner.TickMilliseconds;
        var next = new byte[stride * height];
        scene.Render(next, width, height, stride, 2);
        Assert.False(busy.AsSpan().SequenceEqual(next));
    }

    private sealed class FakeRegistry : IWindowRegistry
    {
        public SurfaceHandler? Match { get; init; }

        public int? LaunchPid { get; init; } = 1;

        public int Launches { get; private set; }

        public SurfaceHandler? MostRecentMatching(IReadOnlyList<string> matchKeys) => Match;

        public void RaiseAndFocus(SurfaceHandler surface, IReadOnlyList<int> heldKeys)
        {
        }

        public int? Launch(IReadOnlyList<string> argv)
        {
            Launches++;
            return LaunchPid;
        }
    }
}
