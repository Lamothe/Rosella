using Rosella.Input;

namespace Rosella.Compositor;

/// <summary>
/// Clock-anchored settings panel: output and microphone volume sliders, then
/// the three power actions as GNOME Quick Settings style circular icon
/// buttons with quiet labels beneath them.
/// </summary>
internal sealed class SettingsPanel
{
    private const uint PanelBackground = 0xFF2E303A;
    private const uint PanelBorder = 0xFFB8BEC8;
    private const uint TextColour = 0xFFE8E8EC;
    private const uint RowSelected = 0xFFB8BEC8;
    private const uint RowSelectedText = 0xFF101216;
    private const uint TrackColour = 0xFF15161C;
    private const uint FillColour = 0xFF7B86E8;
    private const int PanelWidth = 300;
    private const int PanelPad = 10;
    private const int SliderSectionHeight = 76;
    private const int TrackTopOffset = 42;
    private const int TrackHeight = 8;
    private const int SliderCount = 2;
    private const int CornerRadius = 8;

    /// <summary>The action buttons' circle diameter, in logical pixels.</summary>
    private const int CircleSize = 50;

    /// <summary>The button labels: smaller than the panel's own text, and quiet.</summary>
    private const int LabelEm = 12;
    private const int LabelGap = 4;
    private const int ActionBottomPad = 8;

    private static readonly (string Label, PowerGlyph Glyph)[] Actions =
    [
        ("Log Out", PowerGlyph.LogOut),
        ("Restart", PowerGlyph.Restart),
        ("Shut Down", PowerGlyph.ShutDown),
    ];

    private readonly ISystemActions _systemActions;
    private readonly IVolumeControl _volume;
    private readonly object _volumeGate = new();
    private int _volumePercent;
    private int _microphonePercent;
    private long _levelGeneration;
    private AudioEndpoint? _dragging;

    internal Action? Dirty { get; set; }

    internal SettingsPanel(ISystemActions systemActions, IVolumeControl volume)
    {
        _systemActions = systemActions;
        _volume = volume;
        _volumePercent = 50;
        _microphonePercent = 50;
    }

    internal bool IsOpen { get; private set; }

    internal int VolumePercent
    {
        get
        {
            lock (_volumeGate)
            {
                return _volumePercent;
            }
        }
    }

    internal int MicrophonePercent
    {
        get
        {
            lock (_volumeGate)
            {
                return _microphonePercent;
            }
        }
    }

    internal bool IsDraggingSlider
    {
        get
        {
            lock (_volumeGate)
            {
                return _dragging is not null;
            }
        }
    }

    internal void RefreshLevels()
    {
        long generation;
        lock (_volumeGate)
        {
            // A reading requested now must not overwrite a slider the user
            // has moved while wpctl was running.
            generation = ++_levelGeneration;
        }

        var volume = _volume.ReadPercent(AudioEndpoint.Output);
        var microphone = _volume.ReadPercent(AudioEndpoint.Input);
        lock (_volumeGate)
        {
            if (generation != _levelGeneration)
            {
                return;
            }

            if (volume is { } output)
            {
                _volumePercent = Math.Clamp(output, 0, 150);
            }

            if (microphone is { } input)
            {
                _microphonePercent = Math.Clamp(input, 0, 150);
            }
        }
    }

    internal void Toggle()
    {
        IsOpen = !IsOpen;
        lock (_volumeGate)
        {
            _dragging = null;
        }

        if (IsOpen)
        {
            _ = Task.Run(() =>
            {
                RefreshLevels();
                Dirty?.Invoke();
            });
        }
    }

    internal void Close()
    {
        IsOpen = false;
        lock (_volumeGate)
        {
            _dragging = null;
        }
    }

    internal bool HandleKey(ushort key)
    {
        if (IsOpen && key == InputConstants.KeyEsc)
        {
            Close();
            return true;
        }

        return false;
    }

    internal Rect PanelRect(Rect clock, int outputWidth, int outputHeight)
    {
        var width = Math.Min(PanelWidth, outputWidth);
        var height = (PanelPad * 2) + AudioSectionHeight + ActionSectionHeight;
        return new Rect(Math.Clamp(clock.X + clock.W - width, 0, Math.Max(0, outputWidth - width)),
            Math.Max(0, outputHeight - Shell.BarHeight - height), width, height);
    }

    /// <summary>One slider section: its label, track and the gap below it.</summary>
    private static int AudioSectionHeight => SliderSectionHeight * SliderCount;

    /// <summary>Circle, label and a little breathing room below it.</summary>
    private static int ActionSectionHeight =>
        CircleSize + LabelGap + ShellText.LineHeight(LabelEm) + ActionBottomPad;

    internal Rect VolumeTrackRect(Rect panel) => SliderTrackRect(panel, 0);

    internal Rect MicrophoneTrackRect(Rect panel) => SliderTrackRect(panel, 1);

    private static Rect SliderTrackRect(Rect panel, int index) =>
        new(panel.X + PanelPad + 8,
            panel.Y + PanelPad + (index * SliderSectionHeight) + TrackTopOffset,
            panel.W - (PanelPad * 2) - 16,
            TrackHeight);

    /// <summary>
    /// One action button's circle bounds, hit-tested as a box. The three
    /// circles are spread evenly across the panel's inner width, so their
    /// centres sit at the middle of equal slots.
    /// </summary>
    internal static Rect ActionRect(Rect panel, int index)
    {
        var innerWidth = Math.Max(0, panel.W - (PanelPad * 2));
        var centre = panel.X + PanelPad +
            (int)Math.Round(((index + 0.5) * innerWidth) / Actions.Length, MidpointRounding.AwayFromZero);
        return new Rect(centre - (CircleSize / 2), panel.Y + PanelPad + AudioSectionHeight, CircleSize, CircleSize);
    }

    /// <summary>Handles panel press and slider drag. True if the point was consumed.</summary>
    internal bool HandlePointer(int x, int y, Rect clock, int outputWidth, int outputHeight, bool pressed)
    {
        if (!IsOpen)
        {
            return false;
        }

        var panel = PanelRect(clock, outputWidth, outputHeight);
        if (!panel.Contains(x, y))
        {
            if (pressed && !IsDraggingSlider)
            {
                Close();
            }

            return false;
        }

        if (!pressed && IsDraggingSlider)
        {
            return true;
        }

        if (pressed && TryStartDrag(panel, AudioEndpoint.Output, x, y))
        {
            return true;
        }

        if (pressed && TryStartDrag(panel, AudioEndpoint.Input, x, y))
        {
            return true;
        }

        if (pressed)
        {
            PointerReleased();
            for (var index = 0; index < Actions.Length; index++)
            {
                if (ActionRect(panel, index).Contains(x, y))
                {
                    Activate(index);
                    Close();
                    return true;
                }
            }
        }

        return true;
    }

    internal void PointerReleased()
    {
        lock (_volumeGate)
        {
            _dragging = null;
        }
    }

    internal void PointerMoved(int x, Rect clock, int outputWidth, int outputHeight)
    {
        AudioEndpoint? dragging;
        lock (_volumeGate)
        {
            dragging = _dragging;
        }

        if (dragging is { } endpoint && IsOpen)
        {
            var panel = PanelRect(clock, outputWidth, outputHeight);
            SetSliderFromX(endpoint, x, SliderTrackRect(panel, SliderIndex(endpoint)));
        }
    }

    internal void Draw(Span<byte> pixels, int width, int height, int stride, int scale, Rect clock, (int X, int Y)? pointer)
    {
        var clipW = stride / 4;
        var clipH = pixels.Length / stride;
        var panel = PanelRect(clock, width, height);
        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, panel, CornerRadius, PanelBorder, scale);
        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, new Rect(panel.X + 1, panel.Y + 1, panel.W - 2, panel.H - 2), CornerRadius, PanelBackground, scale);

        DrawSlider(pixels, clipW, clipH, stride, scale, panel, 0, "Volume", VolumePercent);
        DrawSlider(pixels, clipW, clipH, stride, scale, panel, 1, "Mic volume", MicrophonePercent);

        for (var index = 0; index < Actions.Length; index++)
        {
            var button = ActionRect(panel, index);
            var hovered = pointer is { } point && button.Contains(point.X, point.Y);
            if (hovered)
            {
                // A disc inset a pixel keeps the hover fill inside the
                // circle's own bounding box, so nothing moves or spills.
                Chrome.FillEllipse(pixels, clipW, clipH, stride,
                    button.X + (button.W / 2), button.Y + (button.H / 2),
                    (button.W / 2) - 1, (button.H / 2) - 1, RowSelected, scale);
            }

            var colour = hovered ? RowSelectedText : TextColour;
            PowerIcons.Draw(pixels, clipW, clipH, stride, Actions[index].Glyph,
                button.X + (button.W / 2), button.Y + (button.H / 2), colour, scale);

            var label = Actions[index].Label;
            var labelWidth = ShellText.Measure(label, LabelEm).Width;
            var labelX = button.X + ((button.W - labelWidth) / 2);
            var labelY = button.Y + button.H + LabelGap;
            ShellText.DrawText(pixels, clipW, clipH, stride, label, labelX * scale, labelY * scale, TextColour, LabelEm, scale);
        }
    }

    private static int SliderIndex(AudioEndpoint endpoint) =>
        endpoint == AudioEndpoint.Output ? 0 : 1;

    private void DrawSlider(Span<byte> pixels, int clipW, int clipH, int stride, int scale,
        Rect panel, int index, string label, int percent)
    {
        var top = panel.Y + PanelPad + (index * SliderSectionHeight);
        ShellText.DrawText(pixels, clipW, clipH, stride, label,
            (panel.X + PanelPad) * scale, top * scale, TextColour, ShellText.MenuEm, scale);
        var percentText = $"{percent}%";
        var percentWidth = ShellText.Measure(percentText, ShellText.MenuEm).Width;
        ShellText.DrawText(pixels, clipW, clipH, stride, percentText,
            (panel.X + panel.W - PanelPad - percentWidth) * scale, top * scale, TextColour, ShellText.MenuEm, scale);

        var track = SliderTrackRect(panel, index);
        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, track, 4, TrackColour, scale);
        var fillWidth = (track.W * Math.Clamp(percent, 0, 100)) / 100;
        if (fillWidth > 0)
        {
            Chrome.FillRoundedRect(pixels, clipW, clipH, stride, new Rect(track.X, track.Y, fillWidth, track.H), 4, FillColour, scale);
        }

        var thumbX = track.X + (track.W * Math.Clamp(percent, 0, 100) / 100);
        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, new Rect(thumbX - 6, track.Y - 4, 12, 16), 6, TextColour, scale);
    }

    private bool TryStartDrag(Rect panel, AudioEndpoint endpoint, int x, int y)
    {
        var track = SliderTrackRect(panel, SliderIndex(endpoint));
        var hitTrack = new Rect(track.X, track.Y - 8, track.W, track.H + 16);
        if (!hitTrack.Contains(x, y))
        {
            return false;
        }

        lock (_volumeGate)
        {
            _dragging = endpoint;
        }

        SetSliderFromX(endpoint, x, track);
        return true;
    }

    private void SetSliderFromX(AudioEndpoint endpoint, int x, Rect track)
    {
        var percent = Math.Clamp((int)Math.Round((x - track.X) * 100d / Math.Max(1, track.W)), 0, 100);
        lock (_volumeGate)
        {
            _levelGeneration++;
            if (endpoint == AudioEndpoint.Output)
            {
                _volumePercent = percent;
            }
            else
            {
                _microphonePercent = percent;
            }
        }

        _volume.SetPercent(endpoint, percent);
    }

    private void Activate(int index)
    {
        switch (index)
        {
            case 0:
                _systemActions.LogOut();
                break;
            case 1:
                _systemActions.Restart();
                break;
            case 2:
                _systemActions.ShutDown();
                break;
        }
    }
}