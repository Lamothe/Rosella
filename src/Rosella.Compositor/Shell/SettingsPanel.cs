using Rosella.Input;

#if ROSELLA_DASHBOARD_WIDGETS
using Rosella.Compositor;

namespace Rosella.DashboardWidgets;
#elif ROSSELLA_TASKBAR
using Rosella.Compositor;

namespace Rosella.Taskbar;
#else
namespace Rosella.Compositor;
#endif

/// <summary>
/// Quick settings anchored to the taskbar clock.
/// </summary>
internal sealed class SettingsPanel : ISettingsPanel
{
    private const uint TextColour = Chrome.PanelText;
    private const uint SecondaryText = Chrome.PanelSecondaryText;
    private const uint ControlBackground = Chrome.PanelControlBackground;
    private const uint ControlHover = 0xFF42424A;
    private const uint TrackColour = 0xFF54545D;
    private const int PanelWidth = 328;
    private const int PanelPad = 16;
    private const int PanelGap = 8;
    private const int SliderSectionHeight = 36;
    private const int AudioIconSize = 18;
    private const int AudioButtonSize = 26;
    private const int AudioGap = 12;
    private const int TrackHeight = 6;
    private const int SliderCount = 2;
    private const int ActionGap = 6;
    private const int ActionHeight = 56;
    private const int LabelEm = 12;
    private const int SliderLabelEm = 13;

    private static readonly (string Label, PowerGlyph Glyph)[] Actions =
    [
        ("Log Out", PowerGlyph.LogOut),
        ("Restart", PowerGlyph.Restart),
        ("Shut Down", PowerGlyph.ShutDown),
    ];

    private readonly ISystemActions _systemActions;
    private readonly IVolumeControl _volume;
    private readonly AudioOutputPicker _outputs;
    private readonly bool _fillWidth;
    private readonly object _volumeGate = new();
    private readonly AudioLevel[] _levels = [new(50, false), new(50, false)];
    private readonly long[] _levelGenerations = new long[SliderCount];
    private AudioEndpoint? _dragging;
    private readonly Queue<(AudioEndpoint Endpoint, VolumeShortcut Shortcut)> _volumeShortcuts = new();
    private bool _processingVolumeShortcuts;

    public ResolutionPicker? Resolution { get; set; }

    public Action? Dirty { get; set; }
    public TaskbarModuleControls? Modules { get; set; }

    private volatile bool _modulesOpen;
    public bool ModulesOpen => _modulesOpen;

    internal SettingsPanel(ISystemActions systemActions, IVolumeControl volume, bool fillWidth = false)
    {
        _systemActions = systemActions;
        _volume = volume;
        _fillWidth = fillWidth;
        _outputs = new AudioOutputPicker(volume, RefreshOutputLevel, () => Dirty?.Invoke());
    }

    public bool IsOpen { get; private set; }

    internal int VolumePercent => Level(AudioEndpoint.Output).Percent;

    internal int MicrophonePercent => Level(AudioEndpoint.Input).Percent;

    internal AudioLevel Level(AudioEndpoint endpoint)
    {
        lock (_volumeGate)
        {
            return _levels[SliderIndex(endpoint)];
        }
    }

    public bool IsDraggingSlider
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
        long outputGeneration;
        long inputGeneration;
        lock (_volumeGate)
        {
            // A reading requested now must not overwrite a slider the user
            // has moved while wpctl was running.
            outputGeneration = _levelGenerations[0];
            inputGeneration = _levelGenerations[1];
        }

        var output = _volume.ReadLevel(AudioEndpoint.Output);
        var input = _volume.ReadLevel(AudioEndpoint.Input);
        lock (_volumeGate)
        {
            if (outputGeneration == _levelGenerations[0] && output is { } currentOutput)
            {
                _levels[0] = currentOutput;
            }

            if (inputGeneration == _levelGenerations[1] && input is { } currentInput)
            {
                _levels[1] = currentInput;
            }
        }
    }

    public void HandleVolumeKey(ushort code, int value)
    {
        if (value == InputConstants.KeyReleased || (code == InputConstants.KeyMute && value != InputConstants.KeyPressed))
        {
            return;
        }

        var shortcut = code switch
        {
            InputConstants.KeyVolumeUp => VolumeShortcut.Raise,
            InputConstants.KeyVolumeDown => VolumeShortcut.Lower,
            _ => VolumeShortcut.ToggleMute,
        };

        QueueVolumeShortcut(AudioEndpoint.Output, shortcut, value == InputConstants.KeyRepeated);
    }

    private void QueueVolumeShortcut(AudioEndpoint endpoint, VolumeShortcut shortcut, bool repeat = false)
    {
        lock (_volumeGate)
        {
            // Bound the repeats if PipeWire is slow or unavailable.
            if (repeat && _volumeShortcuts.Count >= 16)
            {
                return;
            }

            _volumeShortcuts.Enqueue((endpoint, shortcut));
            if (_processingVolumeShortcuts)
            {
                return;
            }

            _processingVolumeShortcuts = true;
        }

        _ = Task.Run(ApplyVolumeShortcuts);
    }

    private void ApplyVolumeShortcuts()
    {
        while (true)
        {
            (AudioEndpoint Endpoint, VolumeShortcut Shortcut) command;
            int index;
            long generation;
            lock (_volumeGate)
            {
                if (!_volumeShortcuts.TryDequeue(out command))
                {
                    _processingVolumeShortcuts = false;
                    return;
                }

                index = SliderIndex(command.Endpoint);
                generation = _levelGenerations[index];
            }

            if (!_volume.ApplyShortcut(command.Endpoint, command.Shortcut) ||
                _volume.ReadLevel(command.Endpoint) is not { } level)
            {
                continue;
            }

            lock (_volumeGate)
            {
                var percent = generation != _levelGenerations[index]
                    ? _levels[index].Percent
                    : level.Percent;
                _levels[index] = level with { Percent = percent };
                _levelGenerations[index]++;
            }

            if (IsOpen)
            {
                Dirty?.Invoke();
            }
        }
    }

    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
            return;
        }

        IsOpen = true;
        _outputs.SetActive(true);
        lock (_volumeGate)
        {
            _dragging = null;
        }

        _ = Task.Run(() =>
        {
            RefreshLevels();
            Dirty?.Invoke();
        });
    }

    public void Close()
    {
        IsOpen = false;
        _modulesOpen = false;
        _outputs.SetActive(false);
        if (Resolution is { } resolution) { resolution.IsOpen = false; }
        lock (_volumeGate)
        {
            _dragging = null;
        }
    }

    public void OpenModules()
    {
        if (!IsOpen)
        {
            Toggle();
        }
        _modulesOpen = true;
    }

    public bool HandleKey(ushort key)
    {
        if (IsOpen && key == InputConstants.KeyEsc)
        {
            if (_modulesOpen)
            {
                _modulesOpen = false;
            }
            else if (Resolution is { IsOpen: true } resolution)
            {
                resolution.IsOpen = false;
            }
            else if (!_outputs.Back())
            {
                Close();
            }
            Dirty?.Invoke();
            return true;
        }

        return false;
    }

    public Rect PanelRect(Rect clock, int outputWidth, int outputHeight)
    {
        var width = _fillWidth && clock == default ? outputWidth : Math.Min(PanelWidth, outputWidth);
        var detailHeight = _modulesOpen ? ModulePicker.Height(Modules?.HasChoices == true) :
            Resolution is { IsOpen: true } resolution ? resolution.Height : _outputs.PanelHeight;
        var selectors = 1 + (Resolution is null ? 0 : 1) + (Modules is null ? 0 : 1);
        var height = detailHeight ?? (PanelPad * 2) + AudioSectionHeight + PanelGap +
            selectors * (SettingsSelector.Height + PanelGap) + ActionHeight;
        return new Rect(Math.Clamp(clock.X + clock.W - width, 0, Math.Max(0, outputWidth - width)),
            Math.Max(0, outputHeight - Shell.BarHeight - PanelGap - height), width, height);
    }

    /// <summary>One slider section: its label, track and the gap below it.</summary>
    private static int AudioSectionHeight => SliderSectionHeight * SliderCount;

    internal Rect VolumeTrackRect(Rect panel) => SliderTrackRect(panel, 0);

    internal Rect MicrophoneTrackRect(Rect panel) => SliderTrackRect(panel, 1);

    internal static Rect OutputRowRect(Rect panel) => new(panel.X + PanelPad,
        panel.Y + PanelPad + AudioSectionHeight + PanelGap, Math.Max(0, panel.W - (2 * PanelPad)), SettingsSelector.Height);

    internal static Rect AudioButtonRect(Rect panel, AudioEndpoint endpoint)
    {
        var index = SliderIndex(endpoint);
        return new Rect(panel.X + PanelPad + (AudioIconSize - AudioButtonSize) / 2,
            panel.Y + PanelPad + index * SliderSectionHeight + (SliderSectionHeight - AudioButtonSize) / 2,
            AudioButtonSize, AudioButtonSize);
    }

    private static Rect SliderTrackRect(Rect panel, int index)
    {
        var left = PanelPad + AudioIconSize + AudioGap;
        var right = PanelPad + ShellText.Measure("100%", SliderLabelEm).Width + AudioGap;
        return new Rect(panel.X + left,
            panel.Y + PanelPad + (index * SliderSectionHeight) + (SliderSectionHeight - TrackHeight) / 2,
            Math.Max(0, panel.W - left - right), TrackHeight);
    }

    internal static Rect ActionRect(Rect panel, int index, bool showResolution = false, bool showModules = false)
    {
        var innerWidth = Math.Max(0, panel.W - (2 * PanelPad) - (2 * ActionGap));
        var left = innerWidth * index / Actions.Length;
        var right = innerWidth * (index + 1) / Actions.Length;
        var lastSelector = showModules ? ModulesRow(panel, showResolution) :
            showResolution ? ResolutionRow(panel) : OutputRowRect(panel);
        return new Rect(panel.X + PanelPad + left + (index * ActionGap),
            lastSelector.Y + lastSelector.H + PanelGap, right - left, ActionHeight);
    }

    private static Rect ResolutionRow(Rect panel) => NextSelector(OutputRowRect(panel));

    internal static Rect ModulesRow(Rect panel, bool showResolution) =>
        NextSelector(showResolution ? ResolutionRow(panel) : OutputRowRect(panel));

    private static Rect NextSelector(Rect row) => row with { Y = row.Y + row.H + PanelGap };

    /// <summary>Handles panel press and slider drag. True if the point was consumed.</summary>
    public bool HandlePointer(int x, int y, Rect clock, int outputWidth, int outputHeight, bool pressed)
        => HandlePointer(x, y, PanelRect(clock, outputWidth, outputHeight), pressed);

    public bool HandlePointer(int x, int y, Rect panel, bool pressed)
    {
        if (!IsOpen)
        {
            return false;
        }

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

        if (_modulesOpen && Modules is { } modules)
        {
            if (pressed && ModulePicker.Back(panel).Contains(x, y))
            {
                _modulesOpen = false;
                Dirty?.Invoke();
            }
            else if (pressed && modules.HasChoices && ModulePicker.Previous(panel).Contains(x, y))
            {
                modules.Step(-1);
                Dirty?.Invoke();
            }
            else if (pressed && modules.HasChoices && ModulePicker.Next(panel).Contains(x, y))
            {
                modules.Step(1);
                Dirty?.Invoke();
            }
            else if (pressed && ModulePicker.ReloadButton(panel, modules.HasChoices).Contains(x, y))
            {
                modules.Selected.Reload();
            }
            return true;
        }

        if (Resolution is { IsOpen: true } picker)
        {
            if (pressed) { picker.Press(panel, y); Dirty?.Invoke(); }
            return true;
        }

        if (pressed && Resolution is { } modes && !_outputs.PanelHeight.HasValue && ResolutionRow(panel).Contains(x, y))
        {
            modes.IsOpen = true;
            Dirty?.Invoke();
            return true;
        }

        if (pressed && _outputs.HandlePress(panel, x, y))
        {
            return true;
        }

        if (pressed && Modules is not null && ModulesRow(panel, Resolution is not null).Contains(x, y))
        {
            PointerReleased();
            OpenModules();
            Dirty?.Invoke();
            return true;
        }

        if (pressed && OutputRowRect(panel).Contains(x, y))
        {
            PointerReleased();
            _outputs.Show();
            return true;
        }

        if (pressed && (TryToggleMute(panel, AudioEndpoint.Output, x, y) ||
            TryToggleMute(panel, AudioEndpoint.Input, x, y)))
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
                if (ActionRect(panel, index, Resolution is not null, Modules is not null).Contains(x, y))
                {
                    Activate(index);
                    Close();
                    return true;
                }
            }
        }

        return true;
    }

    public void PointerReleased()
    {
        lock (_volumeGate)
        {
            _dragging = null;
        }
    }

    public void PointerMoved(int x, Rect clock, int outputWidth, int outputHeight)
        => PointerMoved(x, PanelRect(clock, outputWidth, outputHeight));

    public void PointerMoved(int x, Rect panel)
    {
        AudioEndpoint? dragging;
        lock (_volumeGate)
        {
            dragging = _dragging;
        }

        if (dragging is { } endpoint && IsOpen)
        {
            SetSliderFromX(endpoint, x, SliderTrackRect(panel, SliderIndex(endpoint)));
        }
    }

    internal void Draw(Span<byte> pixels, int width, int height, int stride, int scale, Rect clock, (int X, int Y)? pointer)
        => Draw(pixels, stride, scale, PanelRect(clock, width, height), pointer, glass: null);

    public void Draw(Span<byte> pixels, int stride, int scale, Rect panel, (int X, int Y)? pointer, FrostedGlass? glass)
    {
        var clipW = stride / 4;
        var clipH = pixels.Length / stride;
        if (glass is not null)
        {
            glass.Draw(pixels, stride, panel, Chrome.PanelRadius, scale);
        }
        else
        {
            Chrome.DrawPanel(pixels, clipW, clipH, stride, panel, scale);
        }

        DrawControls(pixels, stride, scale, panel, pointer);
    }

    public void DrawControls(Span<byte> pixels, int stride, int scale, Rect panel, (int X, int Y)? pointer)
    {
        var clipW = stride / 4;
        var clipH = pixels.Length / stride;
        if (_modulesOpen && Modules is { } modules)
        {
            ModulePicker.Draw(pixels, clipW, clipH, stride, scale, panel, pointer, modules);
            return;
        }

        if (Resolution is { IsOpen: true } picker)
        {
            picker.Draw(pixels, clipW, clipH, stride, scale, panel);
            return;
        }

        if (_outputs.PanelHeight.HasValue)
        {
            _outputs.DrawChoices(pixels, clipW, clipH, stride, scale, panel, pointer);
            return;
        }

        DrawSlider(pixels, clipW, clipH, stride, scale, panel, AudioEndpoint.Output, pointer);
        DrawSlider(pixels, clipW, clipH, stride, scale, panel, AudioEndpoint.Input, pointer);
        _outputs.DrawSummary(pixels, clipW, clipH, stride, scale, OutputRowRect(panel), pointer);
        if (Resolution is { } modes)
        {
            SettingsSelector.Draw(pixels, clipW, clipH, stride, scale, ResolutionRow(panel), pointer,
                "Resolution", modes.Summary);
        }
        if (Modules is not null)
        {
            SettingsSelector.Draw(pixels, clipW, clipH, stride, scale, ModulesRow(panel, Resolution is not null), pointer,
                "Modules", Modules.HasChoices ? "Taskbar and dashboard" : Modules.Selected.Label);
        }

        for (var index = 0; index < Actions.Length; index++)
        {
            var button = ActionRect(panel, index, Resolution is not null, Modules is not null);
            var hovered = pointer is { } point && button.Contains(point.X, point.Y);
            Chrome.FillRoundedRect(pixels, clipW, clipH, stride, button, 12,
                hovered ? ControlHover : ControlBackground, scale);
            PowerIcons.Draw(pixels, clipW, clipH, stride, Actions[index].Glyph,
                button.X + (button.W / 2), button.Y + 19, TextColour, scale);

            var label = Actions[index].Label;
            var labelWidth = ShellText.Measure(label, LabelEm).Width;
            var labelX = button.X + ((button.W - labelWidth) / 2);
            var labelY = button.Y + 34;
            ShellText.DrawText(pixels, clipW, clipH, stride, label, labelX * scale, labelY * scale, TextColour, LabelEm, scale);
        }

    }

    private static int SliderIndex(AudioEndpoint endpoint) =>
        endpoint == AudioEndpoint.Output ? 0 : 1;

    private void RefreshOutputLevel()
    {
        lock (_volumeGate)
        {
            _levelGenerations[0]++;
        }

        RefreshLevels();
    }

    private bool TryToggleMute(Rect panel, AudioEndpoint endpoint, int x, int y)
    {
        if (!AudioButtonRect(panel, endpoint).Contains(x, y))
        {
            return false;
        }

        PointerReleased();
        QueueVolumeShortcut(endpoint, VolumeShortcut.ToggleMute);
        return true;
    }

    private void DrawSlider(Span<byte> pixels, int clipW, int clipH, int stride, int scale,
        Rect panel, AudioEndpoint endpoint, (int X, int Y)? pointer)
    {
        var (percent, muted) = Level(endpoint);
        var track = SliderTrackRect(panel, SliderIndex(endpoint));
        var button = AudioButtonRect(panel, endpoint);
        var centreY = track.Y + (track.H / 2);
        var glyph = endpoint == AudioEndpoint.Output
            ? muted ? PowerGlyph.SpeakerMuted : PowerGlyph.Speaker
            : muted ? PowerGlyph.MicrophoneMuted : PowerGlyph.Microphone;
        var hovered = pointer is { } point && button.Contains(point.X, point.Y);
        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, button, 8, hovered ? ControlHover : ControlBackground, scale);
        PowerIcons.Draw(pixels, clipW, clipH, stride, glyph,
            button.X + button.W / 2, centreY, muted ? SecondaryText : TextColour, scale, AudioIconSize);

        var percentText = $"{percent}%";
        var percentWidth = ShellText.Measure(percentText, SliderLabelEm).Width;
        var textY = centreY - (ShellText.LineHeight(SliderLabelEm) / 2);
        ShellText.DrawText(pixels, clipW, clipH, stride, percentText,
            (panel.X + panel.W - PanelPad - percentWidth) * scale, textY * scale, SecondaryText, SliderLabelEm, scale);

        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, track, 3, TrackColour, scale);
        var fillWidth = (track.W * Math.Clamp(percent, 0, 100)) / 100;
        if (fillWidth > 0)
        {
            Chrome.FillRoundedRect(pixels, clipW, clipH, stride, new Rect(track.X, track.Y, fillWidth, track.H),
                3, muted ? SecondaryText : Chrome.AccentBlue, scale);
        }

        var thumbX = track.X + (track.W * Math.Clamp(percent, 0, 100) / 100);
        Chrome.FillRoundedRect(pixels, clipW, clipH, stride, new Rect(thumbX - 6, track.Y - 3, 12, 12), 6, TextColour, scale);
    }

    private bool TryStartDrag(Rect panel, AudioEndpoint endpoint, int x, int y)
    {
        var track = SliderTrackRect(panel, SliderIndex(endpoint));
        var hitTrack = new Rect(track.X - 8, track.Y - 8, track.W + 16, track.H + 16);
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
            var index = SliderIndex(endpoint);
            _levelGenerations[index]++;
            _levels[index] = _levels[index] with { Percent = percent };
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
