using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The shell chrome geometry and painting: bar layout is shared between
/// drawing and hit-testing, the clock is fixed-format Australian, and the
/// launcher's fixed app list drives keyboard and mouse.
/// </summary>
public class ShellChromeTests
{
    private static uint PixelAt(byte[] buffer, int stride, int x, int y) =>
        MemoryMarshal.Read<uint>(buffer.AsSpan((y * stride) + (x * 4)));

    [Fact]
    public void BarLayout_PlacesChipsClockAndTasks()
    {
        var tasks = new List<BarTask> { new("Hello", null!) };
        var layout = Bar.ComputeLayout(1920, "Tue 27/10 13:37", tasks);

        Assert.Equal(new Rect(8, 4, 40, 40), layout.WorkspaceChips[0]);
        Assert.Equal(new Rect(140, 4, 40, 40), layout.WorkspaceChips[3]);

        // The clock is centred: 15 chars x 16px at scale 2.
        Assert.Equal(new Rect(840, 8, 240, 32), layout.Clock);

        // The task is right-aligned: 5 chars x 16px + 24px padding.
        Assert.Equal(new Rect(1808, 6, 104, 36), layout.Tasks[0].Rect);
    }

    [Fact]
    public void BarLayout_TruncatesLongLabelsAndDropsOverflowTasks()
    {
        var longLabel = new string('x', 30);
        var tasks = new List<BarTask> { new(longLabel, null!) };
        var layout = Bar.ComputeLayout(1920, "Tue 27/10 13:37", tasks);

        // 17 chars + "..." = 20 chars x 16px + 24px padding.
        Assert.Equal(344, layout.Tasks[0].Rect.W);

        // Two 20-char tasks cannot both fit between the clock and the edge
        // on a 1000px output: only the first is kept.
        var narrow = Bar.ComputeLayout(1000, "Tue 27/10 13:37", tasks.Concat(tasks).ToList());
        Assert.Single(narrow.Tasks);
    }

    [Fact]
    public void BarDraw_PaintsBackgroundActiveChipAndFocusedTask()
    {
        var surface = new SurfaceHandler(null!);
        var tasks = new List<BarTask> { new("Hello", surface) };
        var layout = Bar.ComputeLayout(640, "Tue 27/10 13:37", tasks);
        var buffer = new byte[640 * 200 * 4];

        Bar.Draw(buffer, 640, 200, 640 * 4, layout, "Tue 27/10 13:37", currentWorkspace: 1, focused: surface);

        Assert.Equal(0xFF24262Eu, PixelAt(buffer, 640 * 4, 0, 0));
        Assert.Equal(0xFFB8BEC8u, PixelAt(buffer, 640 * 4, layout.WorkspaceChips[0].X + 1, 5));
        Assert.Equal(0xFF3A3E4Au, PixelAt(buffer, 640 * 4, layout.Tasks[0].Rect.X + 1, 7));

        // The inactive chip shows the background, not the highlight.
        Assert.Equal(0xFF24262Eu, PixelAt(buffer, 640 * 4, layout.WorkspaceChips[1].X + 1, 5));
    }

    [Fact]
    public void ClockString_IsAustralianWithInvariantWeekday()
    {
        var clock = Bar.ClockString();
        Assert.Matches(new Regex("^[A-Z][a-z]{2} \\d{2}/\\d{2} \\d{2}:\\d{2}$"), clock);
    }

    [Fact]
    public void Launcher_KeyboardNavigatesClamped()
    {
        var launcher = new Launcher();
        launcher.Open();

        launcher.HandleKey(Rosella.Input.InputConstants.KeyUp);
        Assert.Equal(0, launcher.Selection);

        launcher.HandleKey(Rosella.Input.InputConstants.KeyDown);
        launcher.HandleKey(Rosella.Input.InputConstants.KeyDown);
        Assert.Equal(2, launcher.Selection);

        for (var i = 0; i < 10; i++)
        {
            launcher.HandleKey(Rosella.Input.InputConstants.KeyDown);
        }

        Assert.Equal(Launcher.Apps.Length - 1, launcher.Selection);
    }

    [Fact]
    public void Launcher_EnterSpawnsSelectedAppWithTheSocket()
    {
        var launcher = new Launcher { SocketName = "wayland-7" };
        var spawned = new List<(string Command, string? Socket)>();
        launcher.SpawnOverride = (command, socket) =>
        {
            spawned.Add((command, socket));
            return true;
        };

        launcher.Open();
        launcher.HandleKey(Rosella.Input.InputConstants.KeyDown);
        launcher.HandleKey(Rosella.Input.InputConstants.KeyEnter);

        Assert.Equal([(Launcher.Apps[1].Command, "wayland-7")], spawned);
        Assert.False(launcher.IsOpen);
    }

    [Fact]
    public void Launcher_EscClosesWithoutSpawning()
    {
        var launcher = new Launcher();
        launcher.SpawnOverride = (_, _) => throw new InvalidOperationException("must not spawn");
        launcher.Open();

        launcher.HandleKey(Rosella.Input.InputConstants.KeyEsc);

        Assert.False(launcher.IsOpen);
    }

    [Fact]
    public void Launcher_ClicksSelectLaunchOrDismiss()
    {
        var launcher = new Launcher();
        var spawned = new List<string>();
        launcher.SpawnOverride = (command, _) =>
        {
            spawned.Add(command);
            return true;
        };

        launcher.Open();
        var panel = launcher.PanelRect(1920, 1080);

        // Inside the panel but between rows: consumed, nothing launched.
        Assert.True(launcher.HandleClick(panel.X + 20, panel.Y + 2, 1920, 1080));
        Assert.Empty(spawned);
        Assert.True(launcher.IsOpen);

        // On row 2: launches that app and closes.
        Assert.True(launcher.HandleClick(panel.X + 20, panel.Y + 48 + 80 + 18, 1920, 1080));
        Assert.Equal([Launcher.Apps[2].Command], spawned);
        Assert.False(launcher.IsOpen);

        // Outside the panel: not consumed.
        launcher.Open();
        Assert.False(launcher.HandleClick(0, 0, 1920, 1080));
        Assert.True(launcher.IsOpen);
    }
}
