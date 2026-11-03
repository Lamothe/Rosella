using System.Runtime.InteropServices;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The settings panel's power actions as GNOME Quick Settings style circular
/// icon buttons: one horizontal row, hit-tested as circle bounding boxes,
/// with hover inverting the glyph and never moving anything.
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
            Assert.Equal(buttons[0].W, button.W);
            Assert.Equal(buttons[0].H, button.H);
            Assert.InRange(button.W, 46, 56);
            Assert.Equal(button.W, button.H);
            Assert.True(button.X >= panel.X && button.X + button.W <= panel.X + panel.W, $"{button} outside the panel");
            Assert.True(button.Y >= panel.Y && button.Y + button.H <= panel.Y + panel.H, $"{button} outside the panel");
        }

        // Left to right, with a real gap between neighbouring circles.
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
    public void ClickingTheGapBetweenCircles_InvokesNothingAndLeavesThePanelOpen()
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
        Assert.Equal(0xFF7B86E8, PixelAt(pixels, OutputWidth, volume.X + (volume.W * 2 / 5), volume.Y + (volume.H / 2)));
        Assert.Equal(0xFF15161C, PixelAt(pixels, OutputWidth, microphone.X + (microphone.W * 2 / 5), microphone.Y + (microphone.H / 2)));
        Assert.Equal(0xFF7B86E8, PixelAt(pixels, OutputWidth, microphone.X + (microphone.W / 10), microphone.Y + (microphone.H / 2)));
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
                    $"hover changed the pixel at {x},{y}, outside the hovered circle");
            }
        }

        // Inside, the fill recolours the resting panel background.
        Assert.NotEqual(
            PixelAt(calm, OutputWidth, first.X + 3, first.Y + (first.H / 2)),
            PixelAt(hover, OutputWidth, first.X + 3, first.Y + (first.H / 2)));

        // And the glyph inverts: the resting light ink on the dark panel
        // becomes dark ink on the light disc. Comparing the circle's lightest
        // and darkest pixels proves the swap without pinning glyph geometry.
        var (calmDark, calmLight) = LuminanceRange(calm, first);
        var (hoverDark, hoverLight) = LuminanceRange(hover, first);
        Assert.True(hoverDark < calmDark, $"hovered darkest {hoverDark:x} is not darker than resting {calmDark:x}");
        Assert.True(hoverLight < calmLight, $"hovered lightest {hoverLight:x} is not darker than resting {calmLight:x}");
    }

    [Fact]
    public void Labels_SitCentredBeneathTheirCircles()
    {
        var panel = OpenPanel();
        var bounds = panel.PanelRect(Clock, OutputWidth, OutputHeight);
        var pixels = DrawPanel(panel, pointer: null);

        for (var index = 0; index < 3; index++)
        {
            var button = SettingsPanel.ActionRect(bounds, index);
            var labelTop = button.Y + button.H;
            var background = PixelAt(pixels, OutputWidth, button.X, labelTop);
            var margins = 20;
            var left = -1;
            var right = -1;
            for (var y = labelTop; y < bounds.Y + bounds.H - 2; y++)
            {
                for (var x = Math.Max(bounds.X, button.X - margins); x < Math.Min(bounds.X + bounds.W, button.X + button.W + margins); x++)
                {
                    if (PixelAt(pixels, OutputWidth, x, y) == background)
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
    public void EveryCircleDrawsAnAntiAliasedGlyph(int scale)
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
                button.Y + ((button.H - PowerIcons.GlyphSize) / 2),
                PowerIcons.GlyphSize,
                PowerIcons.GlyphSize);
            var background = PixelAt(pixels, OutputWidth * scale, button.X * scale, button.Y * scale);
            var inked = 0;
            var partial = 0;
            for (var y = button.Y * scale; y < (button.Y + button.H) * scale; y++)
            {
                for (var x = button.X * scale; x < (button.X + button.W) * scale; x++)
                {
                    var pixel = PixelAt(pixels, OutputWidth * scale, x, y);
                    var inGlyph = x >= glyph.X * scale && x < (glyph.X + glyph.W) * scale &&
                        y >= glyph.Y * scale && y < (glyph.Y + glyph.H) * scale;
                    if (!inGlyph)
                    {
                        // At rest the button is invisible: ink lives only in
                        // the glyph box, never in the circle around it.
                        Assert.True(pixel == background, $"button {index} has ink at {x},{y} outside its glyph box");
                        continue;
                    }

                    if (pixel == background)
                    {
                        continue;
                    }

                    inked++;
                    if (pixel != 0xFFE8E8EC)
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
        panel.Toggle();
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

    private static (int Darkest, int Lightest) LuminanceRange(byte[] pixels, Rect button)
    {
        var darkest = int.MaxValue;
        var lightest = int.MinValue;
        for (var y = button.Y; y < button.Y + button.H; y++)
        {
            for (var x = button.X; x < button.X + button.W; x++)
            {
                var pixel = PixelAt(pixels, OutputWidth, x, y);
                var luminance = (int)(((pixel >> 16) & 0xFF) + ((pixel >> 8) & 0xFF) + (pixel & 0xFF));
                darkest = Math.Min(darkest, luminance);
                lightest = Math.Max(lightest, luminance);
            }
        }

        return (darkest, lightest);
    }

    private sealed class FakeVolumeControl(int output = 50, int microphone = 50) : IVolumeControl
    {
        public int? ReadPercent(AudioEndpoint endpoint) =>
            endpoint == AudioEndpoint.Output ? output : microphone;

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