using System.Runtime.InteropServices;
using Rosella.Input;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The quick settings panel's audio controls and power buttons.
/// </summary>
public class QuickSettingsTests
{
    private const int OutputWidth = 1920;
    private const int OutputHeight = 1080;

    private static readonly Rect Clock = new(1700, 1020, 210, 36);

    [Fact]
    public void Buttons_AreOneHorizontalRowInsideThePanel()
    {
        var panel = OpenPanel().PanelRect(Clock, OutputWidth, OutputHeight);
        var buttons = Buttons(panel);

        foreach (var button in buttons)
        {
            Assert.Equal(buttons[0].Y, button.Y);
            Assert.InRange(Math.Abs(buttons[0].W - button.W), 0, 1);
            Assert.Equal(buttons[0].H, button.H);
            Assert.True(button.W > button.H);
            Assert.True(button.X >= panel.X && button.X + button.W <= panel.X + panel.W, $"{button} outside the panel");
            Assert.True(button.Y >= panel.Y && button.Y + button.H <= panel.Y + panel.H, $"{button} outside the panel");
        }

        // Left to right, with a real gap between neighbouring buttons.
        Assert.True(buttons[1].X > buttons[0].X + buttons[0].W, "the first two buttons overlap");
        Assert.True(buttons[2].X > buttons[1].X + buttons[1].W, "the last two buttons overlap");

        // Evenly spaced: equal centre-to-centre spacing, within a pixel of
        // integer rounding.
        var first = buttons[1].X - buttons[0].X;
        var second = buttons[2].X - buttons[1].X;
        Assert.InRange(Math.Abs(first - second), 0, 1);
    }

    [Theory]
    [InlineData(0, "LogOut")]
    [InlineData(1, "Restart")]
    [InlineData(2, "ShutDown")]
    public void ClickingACircleCentre_RunsItsActionAndClosesThePanel(int index, string expected)
    {
        var actions = new FakeSystemActions();
        var panel = new SettingsPanel(actions, new FakeVolumeControl());
        panel.Toggle();
        var button = SettingsPanel.ActionRect(panel.PanelRect(Clock, OutputWidth, OutputHeight), index);

        var handled = panel.HandlePointer(
            button.X + (button.W / 2), button.Y + (button.H / 2),
            Clock, OutputWidth, OutputHeight, pressed: true);

        Assert.True(handled);
        Assert.False(panel.IsOpen);
        Assert.Equal([expected], actions.Calls);
    }

    [Fact]
    public void MicrophoneSlider_SitsBetweenTheVolumeSliderAndTheButtons()
    {
        var panel = OpenPanel();
        var bounds = panel.PanelRect(Clock, OutputWidth, OutputHeight);
        var volume = panel.VolumeTrackRect(bounds);
        var microphone = panel.MicrophoneTrackRect(bounds);

        Assert.True(microphone.Y > volume.Y + volume.H, "the microphone slider overlaps the volume slider");
        Assert.True(SettingsPanel.ActionRect(bounds, 0).Y > microphone.Y + microphone.H, "the buttons overlap the microphone slider");
        Assert.True(microphone.X >= bounds.X && microphone.X + microphone.W <= bounds.X + bounds.W, "the microphone slider escapes the panel");
    }

    [Fact]
    public void AudioOutputPicker_SwitchesPlaybackAndRefreshesTheVolume()
    {
        var audio = new SelectableOutputs();
        var panel = new SettingsPanel(new FakeSystemActions(), audio);
        panel.Toggle();
        try
        {
            var main = panel.PanelRect(Clock, OutputWidth, OutputHeight);
            var outputRow = SettingsPanel.OutputRowRect(main);
            Assert.True(outputRow.Y > panel.MicrophoneTrackRect(main).Y);
            Assert.True(outputRow.Y + outputRow.H < SettingsPanel.ActionRect(main, 0).Y);
            Press(panel, outputRow);

            const int menuHeightForTwoOutputs = 132;
            var menu = CompositorSessionTests.WaitFor(() => panel.PanelRect(Clock, OutputWidth, OutputHeight),
                bounds => bounds.H == menuHeightForTwoOutputs);
            Press(panel, AudioOutputPicker.ChoiceRect(menu, 1));
            CompositorSessionTests.WaitFor(() => panel.VolumePercent == 73 &&
                panel.PanelRect(Clock, OutputWidth, OutputHeight).H == menu.H, ready => ready);
            Assert.Equal("tv", audio.Active);

            var chosen = AudioOutputPicker.ChoiceRect(menu, 1);
            Assert.Equal(0xFF273B54u, PixelAt(DrawPanel(panel, null), OutputWidth, chosen.X + 8, chosen.Y + chosen.H / 2));
            Assert.True(panel.HandleKey(InputConstants.KeyEsc));
            Assert.True(panel.IsOpen);
            Assert.Equal(main, panel.PanelRect(Clock, OutputWidth, OutputHeight));
        }
        finally
        {
            panel.Close();
        }
    }

    [Fact]
    public void PipeWireSnapshot_ListsOnlySinksAndUsesTheEffectiveDefault()
    {
        var outputs = WpctlVolumeControl.ParseOutputs("""
            [
              {"type":"PipeWire:Interface:Metadata","props":{"metadata.name":"default"},"metadata":[
                 {"key":"default.configured.audio.sink","value":{"name":"headset"}},
                 {"key":"default.audio.sink","value":{"name":"hdmi.3"}}
              ]},
              {"id":48,"type":"PipeWire:Interface:Node","info":{"props":{
                 "media.class":"Audio/Sink","node.name":"hdmi.3","node.nick":"Sony TV","node.description":"HDMI"
              }}},
              {"id":81,"type":"PipeWire:Interface:Node","info":{"props":{
                 "media.class":"Audio/Sink","node.name":"headset","node.description":"Headset"
              }}},
              {"id":80,"type":"PipeWire:Interface:Node","info":{"props":{
                 "media.class":"Audio/Source","node.name":"microphone"
              }}}
            ]
            """);

        Assert.Equal([new AudioOutput(81, "headset", "Headset", false), new AudioOutput(48, "hdmi.3", "Sony TV", true)], outputs);
    }

    private static void Press(SettingsPanel panel, Rect bounds) =>
        Assert.True(panel.HandlePointer(bounds.X + bounds.W / 2, bounds.Y + bounds.H / 2,
            Clock, OutputWidth, OutputHeight, pressed: true));

    [Fact]
    public void ClickingTheGapBetweenButtons_InvokesNothingAndLeavesThePanelOpen()
    {
        var actions = new FakeSystemActions();
        var panel = new SettingsPanel(actions, new FakeVolumeControl());
        panel.Toggle();
        var bounds = panel.PanelRect(Clock, OutputWidth, OutputHeight);
        var first = SettingsPanel.ActionRect(bounds, 0);
        var second = SettingsPanel.ActionRect(bounds, 1);

        var gapX = (first.X + first.W + second.X) / 2;
        var gapY = first.Y + (first.H / 2);
        Assert.False(first.Contains(gapX, gapY));
        Assert.False(second.Contains(gapX, gapY));

        var handled = panel.HandlePointer(gapX, gapY, Clock, OutputWidth, OutputHeight, pressed: true);

        Assert.True(handled);
        Assert.True(panel.IsOpen);
        Assert.Empty(actions.Calls);
    }

    [Fact]
    public void BothSliders_DrawTheirOwnLevel()
    {
        var panel = new SettingsPanel(new FakeSystemActions(), new FakeVolumeControl(output: 50, microphone: 25));
        panel.Toggle();
        panel.RefreshLevels();
        var bounds = panel.PanelRect(Clock, OutputWidth, OutputHeight);
        var pixels = DrawPanel(panel, pointer: null);
        var volume = panel.VolumeTrackRect(bounds);
        var microphone = panel.MicrophoneTrackRect(bounds);

        // At 40% the output slider is still filled but the 25% microphone
        // slider is not, so drawing one level in place of the other fails.
        Assert.Equal(0xFF3584E4, PixelAt(pixels, OutputWidth, volume.X + (volume.W * 2 / 5), volume.Y + (volume.H / 2)));
        Assert.Equal(0xFF54545D, PixelAt(pixels, OutputWidth, microphone.X + (microphone.W * 2 / 5), microphone.Y + (microphone.H / 2)));
        Assert.Equal(0xFF3584E4, PixelAt(pixels, OutputWidth, microphone.X + (microphone.W / 10), microphone.Y + (microphone.H / 2)));
    }

    [Fact]
    public void HoverChangesThePixels_ButNeverTheButtonGeometry()
    {
        var panel = OpenPanel();
        var bounds = panel.PanelRect(Clock, OutputWidth, OutputHeight);
        var before = Buttons(bounds);

        var calm = DrawPanel(panel, pointer: null);
        var first = before[0];
        var hover = DrawPanel(panel, pointer: (first.X + (first.W / 2), first.Y + (first.H / 2)));

        // The hit-test geometry is a pure function of the panel: hovering
        // cannot move, grow or shrink a button.
        Assert.Equal(before, Buttons(bounds));

        for (var y = bounds.Y; y < bounds.Y + bounds.H; y++)
        {
            for (var x = bounds.X; x < bounds.X + bounds.W; x++)
            {
                if (first.Contains(x, y))
                {
                    continue;
                }

                Assert.True(
                    PixelAt(calm, OutputWidth, x, y) == PixelAt(hover, OutputWidth, x, y),
                    $"hover changed the pixel at {x},{y}, outside the hovered button");
            }
        }

        // Inside, the fill recolours the resting panel background.
        Assert.NotEqual(
            PixelAt(calm, OutputWidth, first.X + 3, first.Y + (first.H / 2)),
            PixelAt(hover, OutputWidth, first.X + 3, first.Y + (first.H / 2)));
    }

    [Fact]
    public void Labels_SitCentredInsideTheirButtons()
    {
        var panel = OpenPanel();
        var bounds = panel.PanelRect(Clock, OutputWidth, OutputHeight);
        var pixels = DrawPanel(panel, pointer: null);

        for (var index = 0; index < 3; index++)
        {
            var button = SettingsPanel.ActionRect(bounds, index);
            var labelTop = button.Y + 34;
            var left = -1;
            var right = -1;
            for (var y = labelTop; y < button.Y + button.H - 4; y++)
            {
                for (var x = button.X + 12; x < button.X + button.W - 12; x++)
                {
                    if (PixelAt(pixels, OutputWidth, x, y) == 0xFF303035u)
                    {
                        continue;
                    }

                    left = left < 0 ? x : Math.Min(left, x);
                    right = Math.Max(right, x);
                }
            }

            Assert.True(left >= 0, $"no label under button {index}");
            var labelCentre = (left + right + 1) / 2;
            var buttonCentre = button.X + (button.W / 2);
            Assert.InRange(Math.Abs(labelCentre - buttonCentre), 0, 2);
            Assert.True(left >= bounds.X && right < bounds.X + bounds.W, $"button {index} label escapes the panel");
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void EveryButtonDrawsAnAntiAliasedGlyph(int scale)
    {
        var panel = OpenPanel();
        var bounds = panel.PanelRect(Clock, OutputWidth, OutputHeight);
        var pixels = new byte[OutputWidth * scale * OutputHeight * scale * 4];
        panel.Draw(pixels, OutputWidth, OutputHeight, OutputWidth * scale * 4, scale, Clock, pointer: null);

        for (var index = 0; index < 3; index++)
        {
            var button = SettingsPanel.ActionRect(bounds, index);
            var glyph = new Rect(
                button.X + ((button.W - PowerIcons.GlyphSize) / 2),
                button.Y + 19 - (PowerIcons.GlyphSize / 2),
                PowerIcons.GlyphSize,
                PowerIcons.GlyphSize);
            const uint background = 0xFF303035u;
            var inked = 0;
            var partial = 0;
            for (var y = glyph.Y * scale; y < (glyph.Y + glyph.H) * scale; y++)
            {
                for (var x = glyph.X * scale; x < (glyph.X + glyph.W) * scale; x++)
                {
                    var pixel = PixelAt(pixels, OutputWidth * scale, x, y);
                    if (pixel == background)
                    {
                        continue;
                    }

                    inked++;
                    if (pixel != 0xFFF4F4F5u)
                    {
                        partial++;
                    }
                }
            }

            Assert.True(inked > 0, $"button {index} drew no glyph at scale {scale}");
            Assert.True(partial > 0, $"button {index} has no anti-aliased glyph edges at scale {scale}");
        }
    }

    private static SettingsPanel OpenPanel()
    {
        var panel = new SettingsPanel(new FakeSystemActions(), new FakeVolumeControl());
        var refreshes = 0;
        panel.Dirty = () => Interlocked.Increment(ref refreshes);
        panel.Toggle();
        CompositorSessionTests.WaitFor(() => Volatile.Read(ref refreshes), count => count >= 2);
        panel.Dirty = null;
        return panel;
    }

    private static Rect[] Buttons(Rect panel) =>
        [SettingsPanel.ActionRect(panel, 0), SettingsPanel.ActionRect(panel, 1), SettingsPanel.ActionRect(panel, 2)];

    private static byte[] DrawPanel(SettingsPanel panel, (int X, int Y)? pointer)
    {
        var pixels = new byte[OutputWidth * OutputHeight * 4];
        panel.Draw(pixels, OutputWidth, OutputHeight, OutputWidth * 4, 1, Clock, pointer);
        return pixels;
    }

    private static uint PixelAt(byte[] buffer, int strideWords, int x, int y) =>
        MemoryMarshal.Read<uint>(buffer.AsSpan(((y * strideWords) + x) * 4));

    private sealed class FakeVolumeControl(int output = 50, int microphone = 50) : IVolumeControl
    {
        public AudioLevel? ReadLevel(AudioEndpoint endpoint) =>
            new AudioLevel(endpoint == AudioEndpoint.Output ? output : microphone, false);

        public bool ApplyShortcut(AudioEndpoint endpoint, VolumeShortcut shortcut) => false;

        public bool SetPercent(AudioEndpoint endpoint, int percent) => true;
    }

    private sealed class SelectableOutputs : IVolumeControl
    {
        private volatile string _active = "headset";
        internal string Active => _active;

        public AudioLevel? ReadLevel(AudioEndpoint endpoint) =>
            new AudioLevel(endpoint == AudioEndpoint.Output && Active == "tv" ? 73 : 40, false);

        public AudioOutput[] ReadOutputs() =>
            [new AudioOutput(81, "headset", "Headset", Active == "headset"), new AudioOutput(48, "tv", "Sony TV", Active == "tv")];

        public bool SetDefaultOutput(string name)
        {
            _active = name;
            return true;
        }

        public bool ApplyShortcut(AudioEndpoint endpoint, VolumeShortcut shortcut) => false;

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
