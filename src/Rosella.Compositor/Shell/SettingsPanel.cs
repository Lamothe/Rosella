using Rosella.Input;

namespace Rosella.Compositor;

/// <summary>
/// Clock-anchored settings panel: a volume slider, then the three power
/// actions as GNOME Quick Settings style circular icon buttons with quiet
/// labels beneath them.
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
    private const int VolumeSectionHeight = 76;
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
    private bool _draggingVolume;

    internal Action? Dirty { get; set; }

    internal SettingsPanel(ISystemActions systemActions, IVolumeControl volume)
    {
        _systemActions = systemActions;
        _volume = volume;
        _volumePercent = 50;
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

    internal bool IsDraggingVolume
    {
        get
        {
            lock (_volumeGate)
            {
                return _draggingVolume;
            }
        }
    }

    internal void RefreshVolume()
    {
        var volume = _volume.ReadPercent();
        if (volume is { } percent)
        {
            lock (_volumeGate)
            {
                _volumePercent = Math.Clamp(percent, 0, 150);
            }
        }
    }

    internal void Toggle()
    {
        IsOpen = !IsOpen;
        lock (_volumeGate)
        {
            _draggingVolume = false;
        }

        if (IsOpen)
        {
            _ = Task.Run(() =>
            {
                RefreshVolume();
                Dirty?.Invoke();
            });
        }
    }

    internal void Close()
    {
        IsOpen = false;
        lock (_volumeGate)
        {
            _draggingVolume = false;
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
        var height = (PanelPad * 2) + VolumeSectionHeight + ActionSectionHeight;
        return new Rect(Math.Clamp(clock.X + clock.W - width, 0, Math.Max(0, outputWidth - width)),
            Math.Max(0, outputHeight - Shell.BarHeight - height), width, height);
    }

    /// <summary>Circle, label and a little breathing room below it.</summary>
    private static int ActionSectionHeight =>
        CircleSize + LabelGap + ShellText.LineHeight(LabelEm) + ActionBottomPad;

    internal Rect VolumeTrackRect(Rect panel) =>
        new(panel.X + PanelPad + 8, panel.Y + PanelPad + 42, panel.W - (PanelPad * 2) - 16, 8);

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
        return new Rect(centre - (CircleSize / 2), panel.Y + PanelPad + VolumeSectionHeight, CircleSize, CircleSize);
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
            if (pressed && !IsDraggingVolume)
            {
                Close();
            }

            return false;
        }

        if (!pressed && IsDraggingVolume)
        {
            return true;
        }

        var track = VolumeTrackRect(panel);
        var hitTrack = new Rect(track.X, track.Y - 8, track.W, track.H + 16);
        if (pressed && hitTrack.Contains(x, y))
        {
            lock (_volumeGate)
            {
                _draggingVolume = true;
            }

            SetVolumeFromX(x, track);
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
            _draggingVolume = false;
        }
    }

    internal void PointerMoved(int x, Rect clock, int outputWidth, int outputHeight)
    {
        if (IsDraggingVolume && IsOpen)
        {
            SetVolumeFromX(x, VolumeTrackRect(PanelRect(clock, outputWidth, outputHeight)));
        }
    }

    internal void Draw(Span<byte> pixels, int width, int height, int stride, int scale, Rect clock, (int X, int Y)? pointer)
    {
        var panel = PanelRect(clock, width, height);
        var clipW = stride / 4;
        var clipH = pixels.Length / stride;
        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, panel, CornerRadius, PanelBorder, scale);
        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, new Rect(panel.X + 1, panel.Y + 1, panel.W - 2, panel.H - 2), CornerRadius, PanelBackground, scale);

        ShellText.DrawText(pixels, clipW, clipH, stride, "Volume", (panel.X + PanelPad) * scale, (panel.Y + PanelPad) * scale, TextColour, ShellText.MenuEm, scale);
        var volumePercent = VolumePercent;
        var percentText = volumePercent > 100 ? "150%" : $"{volumePercent}%";
        var percentWidth = ShellText.Measure(percentText, ShellText.MenuEm).Width;
        ShellText.DrawText(pixels, clipW, clipH, stride, percentText, (panel.X + panel.W - PanelPad - percentWidth) * scale, (panel.Y + PanelPad) * scale, TextColour, ShellText.MenuEm, scale);
        var track = VolumeTrackRect(panel);
        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, track, 4, TrackColour, scale);
        var fillWidth = (track.W * Math.Clamp(volumePercent, 0, 100)) / 100;
        if (fillWidth > 0)
        {
            Chrome.FillRoundedRect(pixels, clipW, clipH, stride, new Rect(track.X, track.Y, fillWidth, track.H), 4, FillColour, scale);
        }

        var thumbX = track.X + (track.W * Math.Clamp(volumePercent, 0, 100) / 100);
        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, new Rect(thumbX - 6, track.Y - 4, 12, 16), 6, TextColour, scale);

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

    private void SetVolumeFromX(int x, Rect track)
    {
        var percent = Math.Clamp((int)Math.Round((x - track.X) * 100d / Math.Max(1, track.W)), 0, 100);
        lock (_volumeGate)
        {
            _volumePercent = percent;
        }

        _volume.SetPercent(percent);
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
