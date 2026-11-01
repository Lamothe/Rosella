namespace Rosella.Compositor;

/// <summary>
/// The desktop shell: Windows 11 style — a bottom taskbar with a reserved
/// launcher position, centred pinned app launchers, window buttons and a
/// clock; clicking the clock opens the extensible settings panel. One
/// desktop; there are no workspaces.
/// Layout is in logical pixels; drawing multiplies by the output scale.
/// </summary>
internal sealed class Shell
{
    /// <summary>Taskbar height in logical pixels.</summary>
    internal const int BarHeight = 48;

    /// <summary>Menu text scale (the bitmap font's natural size is 8x16).</summary>
    internal const int FontScale = 2;

    internal Shell(ISystemActions? systemActions = null, IVolumeControl? volumeControl = null)
    {
        Settings = new SettingsPanel(systemActions ?? new SystemActions(() => { }), volumeControl ?? new WpctlVolumeControl());
        Switcher = new TaskSwitcher();
    }

    /// <summary>The clock-anchored system settings and power panel.</summary>
    internal SettingsPanel Settings { get; }

    /// <summary>
    /// The Alt+Tab window switcher: a centred overlay drawn over the whole
    /// desktop, above the taskbar and the settings panel.
    /// </summary>
    internal TaskSwitcher Switcher { get; }

    /// <summary>The pinned taskbar launchers that resolved at startup.</summary>
    internal PinnedLauncher[] Pinned { get; set; } = [];

    /// <summary>The socket name launched apps should connect to.</summary>
    internal string? SocketName { get; set; }

    /// <summary>The taskbar's window registry (focus-or-launch); wired by the entry point.</summary>
    internal IWindowRegistry? Windows { get; set; }

    /// <summary>The pinned button held down (for the press visual), or -1.</summary>
    internal int PressedPin { get; set; } = -1;

    /// <summary>
    /// The taskbar layout the scanout drew last frame: the input thread
    /// hit-tests exactly this, so drawn and clicked rectangles never
    /// disagree.
    /// </summary>
    internal BarLayout? LastBarLayout { get; private set; }

    /// <summary>
    /// Draws the taskbar and, when open, the settings panel. Width and
    /// height are logical pixels; the chrome is drawn scaled onto the
    /// physical scanout. The pointer drives hover highlighting.
    /// </summary>
    internal void Draw(Span<byte> pixels, int width, int height, int stride, int scale, string clock, IReadOnlyList<BarTask> tasks, SurfaceHandler? focused, (int X, int Y)? pointer = null)
    {
        var layout = Bar.ComputeLayout(width, height, clock, tasks, Pinned);
        LastBarLayout = layout;
        Bar.Draw(pixels, width, height, stride, scale, layout, clock, focused, pointer, PressedPin);

        if (Settings.IsOpen)
        {
            Settings.Draw(pixels, width, height, stride, scale, layout.Clock, pointer);
        }

        if (Switcher.IsOpen)
        {
            Switcher.Draw(pixels, width, height, stride, scale, tasks);
        }
    }
}
