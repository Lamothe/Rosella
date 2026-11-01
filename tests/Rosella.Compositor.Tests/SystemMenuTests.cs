using Rosella.Input;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The clock-anchored settings panel: volume slider, power actions and close behaviour.
/// </summary>
public class SettingsPanelTests
{
    [Fact]
    public void StartsAtNeutralVolumeUntilThePanelIsOpened()
    {
        var volume = new FakeVolumeControl { Current = 64 };
        var panel = CreatePanel(volume);
        Assert.Equal(50, panel.VolumePercent);
        panel.RefreshVolume();
        Assert.Equal(64, panel.VolumePercent);
    }

    [Theory]
    [InlineData(0, "LogOut")]
    [InlineData(1, "Restart")]
    [InlineData(2, "ShutDown")]
    public void ClickOnAnItem_RunsItsActionAndCloses(int index, string action)
    {
        var actions = new FakeSystemActions();
        var volume = new FakeVolumeControl();
        var panel = new SettingsPanel(actions, volume);
        panel.Toggle();

        var bounds = panel.PanelRect(new Rect(1700, 1020, 210, 36), 1920, 1080);
        var row = SettingsPanel.ActionRect(bounds, index);
        var handled = panel.HandlePointer(row.X + (row.W / 2), row.Y + (row.H / 2), new Rect(1700, 1020, 210, 36), 1920, 1080, pressed: true);

        Assert.True(handled);
        Assert.False(panel.IsOpen);
        Assert.Equal([action], actions.Calls);
    }

    [Fact]
    public void ClickOutside_ClosesWithoutActing()
    {
        var actions = new FakeSystemActions();
        var menu = CreatePanel(new FakeVolumeControl(), actions);
        menu.Toggle();

        var clock = new Rect(1700, 1020, 210, 36);
        Assert.False(menu.HandlePointer(0, 200, clock, 1920, 1080, pressed: true));
        Assert.False(menu.IsOpen);
        Assert.Empty(actions.Calls);
    }

    [Fact]
    public void ClickInsideButOffARow_IsConsumedWithoutActing()
    {
        var actions = new FakeSystemActions();
        var menu = CreatePanel(new FakeVolumeControl(), actions);
        menu.Toggle();

        var clock = new Rect(1700, 1020, 210, 36);
        var panel = menu.PanelRect(clock, 1920, 1080);
        Assert.True(menu.HandlePointer(panel.X + 2, panel.Y + 2, clock, 1920, 1080, pressed: true));
        Assert.True(menu.IsOpen);
        Assert.Empty(actions.Calls);
    }

    [Fact]
    public void Esc_ClosesThePanelAndIsConsumed()
    {
        var menu = CreatePanel(new FakeVolumeControl());
        menu.Toggle();

        Assert.True(menu.HandleKey(InputConstants.KeyEsc));
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void OtherKeys_AreNotConsumed()
    {
        var menu = CreatePanel(new FakeVolumeControl());
        menu.Toggle();

        Assert.False(menu.HandleKey(InputConstants.KeyEnter));
        Assert.True(menu.IsOpen);
    }

    [Fact]
    public void Panel_AnchorsToClockAtRightAndFitsAboveTaskbar()
    {
        var menu = CreatePanel(new FakeVolumeControl());
        var clock = new Rect(1700, 1020, 210, 36);
        var panel = menu.PanelRect(clock, 1920, 1080);

        Assert.Equal(1910, panel.X + panel.W);
        Assert.Equal(1080 - Shell.BarHeight, panel.Y + panel.H);
        Assert.True(panel.H > 0 && panel.W > 0);
    }

    [Fact]
    public void LogOut_IsTheGracefulQuit()
    {
        // The production action wires Log Out to the same cancellation the
        // Super+Esc binding uses; the menu must reach it exactly once.
        var quit = 0;
        var menu = CreatePanel(new FakeVolumeControl(), new SystemActions(() => quit++));
        menu.Toggle();

        var clock = new Rect(1700, 1020, 210, 36);
        var panel = menu.PanelRect(clock, 1920, 1080);
        var row = SettingsPanel.ActionRect(panel, 0);
        menu.HandlePointer(row.X + (row.W / 2), row.Y + (row.H / 2), clock, 1920, 1080, pressed: true);

        Assert.Equal(1, quit);
        Assert.False(menu.IsOpen);
    }

    [Fact]
    public void VolumeSlider_MapsPointerToVolumeAndAllowsDragOutsidePanel()
    {
        var volume = new FakeVolumeControl();
        var panel = CreatePanel(volume);
        panel.Toggle();
        var clock = new Rect(1700, 1020, 210, 36);
        var bounds = panel.PanelRect(clock, 1920, 1080);
        var track = panel.VolumeTrackRect(bounds);

        Assert.True(panel.HandlePointer(track.X + track.W - 1, track.Y, clock, 1920, 1080, pressed: true));
        Assert.Equal(100, panel.VolumePercent);
        Assert.Equal(100, volume.LastSet);
        panel.PointerMoved(0, clock, 1920, 1080);
        Assert.Equal(0, panel.VolumePercent);
        panel.PointerReleased();
        Assert.False(panel.IsDraggingVolume);
    }

    private static SettingsPanel CreatePanel(FakeVolumeControl volume, ISystemActions? actions = null) =>
        new(actions ?? new FakeSystemActions(), volume);

    private sealed class FakeVolumeControl : IVolumeControl
    {
        public int? Current { get; set; }
        public int LastSet { get; private set; }
        public int? ReadPercent() => Current;
        public bool SetPercent(int percent)
        {
            LastSet = percent;
            Current = percent;
            return true;
        }
    }

    private sealed class FakeSystemActions : ISystemActions
    {
        public List<string> Calls { get; } = [];

        public void LogOut() => Calls.Add("LogOut");

        public void Restart() => Calls.Add("Restart");

        public void ShutDown() => Calls.Add("ShutDown");
    }
}
