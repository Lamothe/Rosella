using Rosella.Input;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The clock-anchored settings panel: output and microphone sliders, power
/// actions and close behaviour.
/// </summary>
public class SettingsPanelTests
{
    [Fact]
    public void StartsAtNeutralLevelsUntilThePanelIsOpened()
    {
        var volume = new FakeVolumeControl { Output = 64, Microphone = 28 };
        var panel = CreatePanel(volume);
        Assert.Equal(50, panel.VolumePercent);
        Assert.Equal(50, panel.MicrophonePercent);
        panel.RefreshLevels();
        Assert.Equal(64, panel.VolumePercent);
        Assert.Equal(28, panel.MicrophonePercent);
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
        Assert.Equal(100, volume.LastOutputSet);
        panel.PointerMoved(0, clock, 1920, 1080);
        Assert.Equal(0, panel.VolumePercent);
        panel.PointerReleased();
        Assert.False(panel.IsDraggingSlider);
    }

    [Fact]
    public void MicrophoneSlider_MapsPointerToTheSourceAndLeavesTheOutputAlone()
    {
        var volume = new FakeVolumeControl { Output = 70, Microphone = 30 };
        var panel = CreatePanel(volume);
        panel.Toggle();
        panel.RefreshLevels();
        var clock = new Rect(1700, 1020, 210, 36);
        var bounds = panel.PanelRect(clock, 1920, 1080);
        var track = panel.MicrophoneTrackRect(bounds);
        Assert.Equal(30, panel.MicrophonePercent);

        Assert.True(panel.HandlePointer(track.X + (track.W / 2), track.Y, clock, 1920, 1080, pressed: true));
        Assert.InRange(panel.MicrophonePercent, 49, 51);
        Assert.Equal(70, panel.VolumePercent);
        Assert.Null(volume.LastOutputSet);

        panel.PointerMoved(0, clock, 1920, 1080);
        Assert.Equal(0, panel.MicrophonePercent);
        Assert.Equal(0, volume.LastMicrophoneSet);
        panel.PointerReleased();
        Assert.False(panel.IsDraggingSlider);
    }

    [Fact]
    public void AnInFlightRefresh_DoesNotOverwriteASliderMovedWhileItReads()
    {
        using var readStarted = new ManualResetEventSlim();
        using var continueReading = new ManualResetEventSlim();
        using var refreshed = new ManualResetEventSlim();
        var volume = new BlockingVolumeControl(readStarted, continueReading);
        var panel = CreatePanel(volume);
        panel.Dirty = refreshed.Set;

        panel.Toggle();
        Assert.True(readStarted.Wait(TimeSpan.FromSeconds(5)), "the refresh never started");

        var clock = new Rect(1700, 1020, 210, 36);
        var bounds = panel.PanelRect(clock, 1920, 1080);
        var track = panel.MicrophoneTrackRect(bounds);
        Assert.True(panel.HandlePointer(track.X + track.W - 1, track.Y, clock, 1920, 1080, pressed: true));
        Assert.Equal(100, panel.MicrophonePercent);

        continueReading.Set();
        Assert.True(refreshed.Wait(TimeSpan.FromSeconds(5)), "the refresh never completed");
        Assert.Equal(100, panel.MicrophonePercent);
    }

    private static SettingsPanel CreatePanel(IVolumeControl volume, ISystemActions? actions = null) =>
        new(actions ?? new FakeSystemActions(), volume);

    private sealed class FakeVolumeControl : IVolumeControl
    {
        public int? Output { get; set; }

        public int? Microphone { get; set; }

        public int? LastOutputSet { get; private set; }

        public int? LastMicrophoneSet { get; private set; }

        public int? ReadPercent(AudioEndpoint endpoint) =>
            endpoint == AudioEndpoint.Output ? Output : Microphone;

        public bool SetPercent(AudioEndpoint endpoint, int percent)
        {
            if (endpoint == AudioEndpoint.Output)
            {
                LastOutputSet = percent;
                Output = percent;
            }
            else
            {
                LastMicrophoneSet = percent;
                Microphone = percent;
            }

            return true;
        }
    }

    private sealed class BlockingVolumeControl(ManualResetEventSlim readStarted, ManualResetEventSlim continueReading) : IVolumeControl
    {
        public int? ReadPercent(AudioEndpoint endpoint)
        {
            if (endpoint == AudioEndpoint.Output)
            {
                readStarted.Set();
                continueReading.Wait(TimeSpan.FromSeconds(5));
                return 70;
            }

            return 30;
        }

        public bool SetPercent(AudioEndpoint endpoint, int percent) => true;
    }

    private sealed class FakeSystemActions : ISystemActions
    {
        public List<string> Calls { get; } = [];

        public void LogOut() => Calls.Add("LogOut");

        public void Restart() => Calls.Add("Restart");

        public void ShutDown() => Calls.Add("ShutDown");
    }
}
