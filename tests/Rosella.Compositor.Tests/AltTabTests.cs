using System.Runtime.InteropServices;
using Rosella.Input;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The Alt+Tab window switcher: MRU order and wrapping, the panel's layout
/// maths, and the key policy — Tab swallowed with its release, Esc
/// cancelling without focus, and the Alt release committing the choice
/// without being swallowed itself.
/// </summary>
public class AltTabTests
{
    private static uint PixelAt(byte[] buffer, int stride, int x, int y) =>
        MemoryMarshal.Read<uint>(buffer.AsSpan((y * stride) + (x * 4)));

    /// <summary>The scene's window list, bottom-to-top like TaskWindows.</summary>
    private static BarTask[] Tasks(Scene scene, int count) =>
        [.. Enumerable.Range(0, count).Select(i => new BarTask($"Window {i}", $"app{i}", new SurfaceHandler(scene)))];

    // --- Selection and ordering -----------------------------------------

    [Fact]
    public void Open_SelectsThePreviousWindowInMruOrder()
    {
        var switcher = new TaskSwitcher();
        var tasks = Tasks(new Scene(), 4);

        // The scene lists bottom-to-top, so MRU is [3, 2, 1, 0]: the first
        // tap selects index 1 — the window that was focused before this one.
        switcher.Open(tasks);

        Assert.True(switcher.IsOpen);
        Assert.Equal(1, switcher.Index);
        Assert.Same(tasks[2].Surface, switcher.Commit());
        Assert.False(switcher.IsOpen);
        Assert.Equal(-1, switcher.Index);
    }

    [Fact]
    public void Next_StepsForwardThroughTheMruList()
    {
        var switcher = new TaskSwitcher();
        var tasks = Tasks(new Scene(), 4);
        switcher.Open(tasks);

        switcher.Next(backwards: false, tasks);

        Assert.Equal(2, switcher.Index);
        Assert.Same(tasks[1].Surface, switcher.Commit());
    }

    [Fact]
    public void Next_BackwardsStepsTowardsTheNewest()
    {
        var switcher = new TaskSwitcher();
        var tasks = Tasks(new Scene(), 4);
        switcher.Open(tasks);

        switcher.Next(backwards: true, tasks);

        Assert.Equal(0, switcher.Index);
        Assert.Same(tasks[3].Surface, switcher.Commit());
    }

    [Fact]
    public void Next_WrapsAtBothEnds()
    {
        var switcher = new TaskSwitcher();
        var tasks = Tasks(new Scene(), 4);
        switcher.Open(tasks);

        // From 1: 2, 3, then round to the newest; backwards from 0 lands on
        // the oldest.
        switcher.Next(backwards: false, tasks);
        switcher.Next(backwards: false, tasks);
        Assert.Equal(3, switcher.Index);
        switcher.Next(backwards: false, tasks);
        Assert.Equal(0, switcher.Index);
        switcher.Next(backwards: true, tasks);
        Assert.Equal(3, switcher.Index);
        Assert.Same(tasks[0].Surface, switcher.Commit());
    }

    [Fact]
    public void SingleWindow_StaysSelectedThroughStepsAndCommits()
    {
        var switcher = new TaskSwitcher();
        var tasks = Tasks(new Scene(), 1);
        switcher.Open(tasks);

        Assert.Equal(0, switcher.Index);
        switcher.Next(backwards: false, tasks);
        Assert.Equal(0, switcher.Index);
        switcher.Next(backwards: true, tasks);
        Assert.Equal(0, switcher.Index);
        Assert.Same(tasks[0].Surface, switcher.Commit());
    }

    [Fact]
    public void EmptyList_OpensWithoutCrashingAndCommitsNothing()
    {
        var switcher = new TaskSwitcher();
        switcher.Open([]);

        Assert.True(switcher.IsOpen);
        switcher.Next(backwards: false, []);
        switcher.Next(backwards: true, []);
        Assert.Null(switcher.Commit());
        Assert.False(switcher.IsOpen);
    }

    [Fact]
    public void Next_RefillsAnEmptySnapshotOnlyOnce()
    {
        var switcher = new TaskSwitcher();
        var tasks = Tasks(new Scene(), 4);
        switcher.Open([]);

        // A window appeared while the empty switch was up: the Tab press
        // takes the list again, then steps from the first row.
        switcher.Next(backwards: false, tasks);

        Assert.Equal(1, switcher.Index);
        Assert.Same(tasks[2].Surface, switcher.Commit());
    }

    [Fact]
    public void Next_SkipsAWindowThatVanishedMidSwitch()
    {
        var switcher = new TaskSwitcher();
        var tasks = Tasks(new Scene(), 4);
        switcher.Open(tasks);

        // MRU [3, 2, 1, 0]: the selection is on 2, and the next row (1)
        // closed before the Tab press. The step must land past it on 0.
        tasks[1].Surface.Gone = true;
        switcher.Next(backwards: false, tasks);

        Assert.Equal(3, switcher.Index);
        Assert.Same(tasks[0].Surface, switcher.Commit());
    }

    [Fact]
    public void Commit_SkipsAGoneSelection()
    {
        var switcher = new TaskSwitcher();
        var tasks = Tasks(new Scene(), 3);
        switcher.Open(tasks);

        // MRU [2, 1, 0]; the selected window (1) closed before Alt let go.
        tasks[1].Surface.Gone = true;

        Assert.Same(tasks[0].Surface, switcher.Commit());
    }

    // --- Layout maths ----------------------------------------------------

    [Fact]
    public void Layout_RowsNeverOverlapAndStayInsideThePanel()
    {
        const int rows = 8;
        var panel = TaskSwitcher.PanelRect(1920, 1080, rows);
        Assert.True(panel.X >= 0 && panel.Y >= 0);
        Assert.True(panel.X + panel.W <= 1920);
        Assert.True(panel.Y + panel.H <= 1080 - Shell.BarHeight);

        for (var slot = 0; slot < rows; slot++)
        {
            var row = TaskSwitcher.RowRect(panel, slot);
            Assert.True(row.H > 0 && row.H <= TaskSwitcher.RowHeight);
            Assert.True(row.X >= panel.X && row.X + row.W <= panel.X + panel.W);
            Assert.True(row.Y >= panel.Y && row.Y + row.H <= panel.Y + panel.H);
            if (slot > 0)
            {
                var above = TaskSwitcher.RowRect(panel, slot - 1);
                Assert.True(row.Y >= above.Y + above.H, $"row {slot} overlaps row {slot - 1}");
            }
        }
    }

    [Fact]
    public void Layout_SelectionStaysInsideTheDrawnWindowOfRows()
    {
        const int count = 30;
        var visible = TaskSwitcher.VisibleRows(count, 1080);
        Assert.True(visible > 0 && visible < count, $"visible rows {visible}");

        for (var selected = 0; selected < count; selected++)
        {
            var first = TaskSwitcher.FirstVisible(selected, count, visible);
            Assert.InRange(first, 0, count - visible);
            Assert.InRange(selected, first, first + visible - 1);
        }
    }

    [Fact]
    public void Layout_ShortOutputScrollsRowsInsteadOfShrinkingThem()
    {
        // 300 logical pixels of output leave four rows' worth of panel; the
        // row pitch itself never changes.
        Assert.Equal(4, TaskSwitcher.VisibleRows(10, 300));
        Assert.Equal(2, TaskSwitcher.VisibleRows(2, 300));
        Assert.Equal(0, TaskSwitcher.VisibleRows(0, 300));

        var panel = TaskSwitcher.PanelRect(400, 300, 4);
        Assert.True(panel.Y + panel.H <= 300 - Shell.BarHeight);
        Assert.Equal(
            TaskSwitcher.RowHeight,
            TaskSwitcher.RowRect(panel, 1).Y - TaskSwitcher.RowRect(panel, 0).Y);

        // A work area with no room for even one row draws nothing rather
        // than a panel overflowing the taskbar.
        Assert.Equal(0, TaskSwitcher.VisibleRows(10, Shell.BarHeight));
    }

    // --- Drawing ---------------------------------------------------------

    [Fact]
    public void Draw_PaintsThePanelAndInvertsTheSelectedRow()
    {
        var width = 640;
        var height = 480;
        var stride = width * 4;
        var tasks = Tasks(new Scene(), 3);
        var shell = new Shell();
        shell.Switcher.Open(tasks);
        var buffer = new byte[stride * height];

        shell.Draw(buffer, width, height, stride, 1, Bar.ClockString(), tasks, null);

        var panel = TaskSwitcher.PanelRect(width, height, tasks.Length);
        Assert.Equal(0xFF2E303Au, PixelAt(buffer, stride, panel.X + 4, panel.Y + (panel.H / 2)));

        // Selection index 1 (MRU [2, 1, 0]) wears the inverted row.
        var selected = TaskSwitcher.RowRect(panel, 1);
        Assert.Equal(0xFFB8BEC8u, PixelAt(buffer, stride, selected.X + 4, selected.Y + (selected.H / 2)));

        // The rest of the desktop is untouched.
        Assert.Equal(0u, PixelAt(buffer, stride, 0, 0));
    }

    [Fact]
    public void Draw_ScalesThePanelOntoThePhysicalScanout()
    {
        var width = 640;
        var height = 480;
        var scale = 2;
        var stride = width * scale * 4;
        var tasks = Tasks(new Scene(), 3);
        var shell = new Shell();
        shell.Switcher.Open(tasks);
        var buffer = new byte[stride * height * scale];

        shell.Draw(buffer, width, height, stride, scale, Bar.ClockString(), tasks, null);

        var panel = TaskSwitcher.PanelRect(width, height, tasks.Length);

        // The panel is interior geometry: its logical padding lands on the
        // physical scanout multiplied by the output scale.
        Assert.Equal(0xFF2E303Au, PixelAt(buffer, stride, (panel.X + 4) * scale, (panel.Y + (panel.H / 2)) * scale));
        Assert.Equal(0u, PixelAt(buffer, stride, 0, 0));
    }

    // --- The key policy --------------------------------------------------

    [Fact]
    public void AltTab_OpensEvenWithNoWindows()
    {
        var harness = new Harness();

        harness.Press(InputConstants.KeyLeftAlt);
        harness.Press(InputConstants.KeyTab);

        Assert.True(harness.Shell.Switcher.IsOpen);
        harness.Release(InputConstants.KeyTab);
        harness.Release(InputConstants.KeyLeftAlt);
        Assert.False(harness.Shell.Switcher.IsOpen);
        Assert.Empty(harness.Registry.Focused);
    }

    [Fact]
    public void AltTab_OpensAndTheAltReleaseFocusesThePreviousWindow()
    {
        var harness = new Harness();
        var tasks = Tasks(harness.Scene, 4);
        harness.Press(InputConstants.KeyLeftAlt);

        // No client is spawned in this test, so the scene cannot map a
        // window; inject the snapshot the Tab press would have taken.
        harness.Shell.Switcher.Open(tasks);
        Assert.True(harness.Shell.Switcher.IsOpen);

        harness.Release(InputConstants.KeyLeftAlt);

        Assert.False(harness.Shell.Switcher.IsOpen);
        var focused = Assert.Single(harness.Registry.Focused);
        Assert.Same(tasks[2].Surface, focused);
        Assert.Contains((int)InputConstants.KeyLeftAlt, harness.Registry.HeldKeys);
    }

    [Fact]
    public void Tab_IsSwallowedWhileAltIsHeld()
    {
        var harness = new Harness();
        var tasks = Tasks(harness.Scene, 4);
        harness.Press(InputConstants.KeyLeftAlt);
        harness.Shell.Switcher.Open(tasks);

        // Tab is still held when Alt lets go: if the press had joined the
        // pressed set, the commit's held keys would name it and the client
        // would have seen it.
        harness.Press(InputConstants.KeyTab);
        Assert.Equal(2, harness.Shell.Switcher.Index);
        harness.Release(InputConstants.KeyLeftAlt);

        Assert.Same(tasks[1].Surface, Assert.Single(harness.Registry.Focused));
        Assert.Contains((int)InputConstants.KeyLeftAlt, harness.Registry.HeldKeys);
        Assert.DoesNotContain((int)InputConstants.KeyTab, harness.Registry.HeldKeys);
        harness.Release(InputConstants.KeyTab);
    }

    [Fact]
    public void HeldTab_RepeatsThroughTheMruList()
    {
        var harness = new Harness();
        var tasks = Tasks(harness.Scene, 4);
        harness.Press(InputConstants.KeyLeftAlt);
        harness.Shell.Switcher.Open(tasks);
        Assert.Equal(1, harness.Shell.Switcher.Index);

        // Holding Tab walks the list; repeats are the switcher's alone and
        // still never reach a client.
        harness.Repeat(InputConstants.KeyTab);
        Assert.Equal(2, harness.Shell.Switcher.Index);
        harness.Repeat(InputConstants.KeyTab);
        Assert.Equal(3, harness.Shell.Switcher.Index);
    }

    [Fact]
    public void AltRelease_IsDeliveredSoTheChordEnds()
    {
        var harness = new Harness();
        var tasks = Tasks(harness.Scene, 4);
        harness.Press(InputConstants.KeyLeftAlt);
        harness.Shell.Switcher.Open(tasks);
        harness.Release(InputConstants.KeyLeftAlt);
        Assert.Single(harness.Registry.Focused);

        // Swallowing the Alt release would leave Alt in the pressed set, and
        // this bare Tab would open a new switch.
        harness.Press(InputConstants.KeyTab);
        Assert.False(harness.Shell.Switcher.IsOpen);
        harness.Release(InputConstants.KeyTab);
    }

    [Fact]
    public void StaleSwitchAfterAnInputReset_IsCancelledNotCommitted()
    {
        var harness = new Harness();
        var tasks = Tasks(harness.Scene, 4);
        harness.Press(InputConstants.KeyLeftAlt);
        harness.Shell.Switcher.Open(tasks);

        // A VT switch drops the pressed set; the Alt release happens in the
        // other session. A later Alt chord must not commit the old snapshot.
        harness.Router.ResetInputState();
        harness.Press(InputConstants.KeyLeftAlt);
        harness.Release(InputConstants.KeyLeftAlt);

        Assert.False(harness.Shell.Switcher.IsOpen);
        Assert.Empty(harness.Registry.Focused);
    }

    [Fact]
    public void ShiftTab_StepsBackwardsThroughTheMruList()
    {
        var harness = new Harness();
        var tasks = Tasks(harness.Scene, 4);
        harness.Press(InputConstants.KeyLeftAlt);
        harness.Press(InputConstants.KeyLeftShift);
        harness.Shell.Switcher.Open(tasks);

        harness.Press(InputConstants.KeyTab);
        Assert.Equal(0, harness.Shell.Switcher.Index);
        harness.Release(InputConstants.KeyLeftAlt);

        Assert.Same(tasks[3].Surface, Assert.Single(harness.Registry.Focused));
        harness.Release(InputConstants.KeyLeftShift);
    }

    [Fact]
    public void Esc_CancelsWithoutFocusing()
    {
        var harness = new Harness();
        var tasks = Tasks(harness.Scene, 3);
        harness.Press(InputConstants.KeyLeftAlt);
        harness.Shell.Switcher.Open(tasks);

        harness.Press(InputConstants.KeyEsc);

        Assert.False(harness.Shell.Switcher.IsOpen);
        harness.Release(InputConstants.KeyLeftAlt);
        Assert.Empty(harness.Registry.Focused);
        harness.Release(InputConstants.KeyEsc);
    }

    [Fact]
    public void Esc_IsSwallowedEvenWhileStillHeldAtCommit()
    {
        var harness = new Harness();
        var tasks = Tasks(harness.Scene, 4);
        harness.Press(InputConstants.KeyLeftAlt);
        harness.Shell.Switcher.Open(tasks);
        harness.Press(InputConstants.KeyEsc);
        Assert.False(harness.Shell.Switcher.IsOpen);

        // A second switch while Esc is still down: had the Esc press joined
        // the pressed set, the commit below would carry it to the new focus.
        harness.Shell.Switcher.Open(tasks);
        harness.Press(InputConstants.KeyTab);
        harness.Release(InputConstants.KeyLeftAlt);

        Assert.Single(harness.Registry.Focused);
        Assert.DoesNotContain((int)InputConstants.KeyEsc, harness.Registry.HeldKeys);
        harness.Release(InputConstants.KeyEsc);
    }

    [Fact]
    public void BareTabWithoutAlt_DoesNotOpenTheSwitcher()
    {
        var harness = new Harness();

        harness.Press(InputConstants.KeyTab);
        harness.Release(InputConstants.KeyTab);

        Assert.False(harness.Shell.Switcher.IsOpen);
        Assert.Empty(harness.Registry.Focused);
    }

    private sealed class Harness
    {
        public Scene Scene { get; } = new();

        public SeatState Seat { get; } = new();

        public Shell Shell { get; } = new();

        public FakeWindowRegistry Registry { get; } = new();

        public InputRouter Router { get; }

        public Harness()
        {
            Shell.Windows = Registry;
            Router = new InputRouter(Scene, Seat, () => { }, Shell);
        }

        public void Press(ushort code) =>
            Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, code, InputConstants.KeyPressed));

        public void Release(ushort code) =>
            Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, code, InputConstants.KeyReleased));

        public void Repeat(ushort code) =>
            Router.ProcessEvent(new InputEvent(0, 0, InputConstants.EventKey, code, InputConstants.KeyRepeated));
    }

    /// <summary>Records what the switcher asks the session to focus, and with which keys held.</summary>
    private sealed class FakeWindowRegistry : IWindowRegistry
    {
        public List<SurfaceHandler> Focused { get; } = [];

        public List<int> HeldKeys { get; } = [];

        public SurfaceHandler? MostRecentMatching(IReadOnlyList<string> matchKeys) => null;

        public void RaiseAndFocus(SurfaceHandler surface, IReadOnlyList<int> heldKeys)
        {
            Focused.Add(surface);
            HeldKeys.AddRange(heldKeys);
        }

        public void Launch(IReadOnlyList<string> argv)
        {
        }
    }
}