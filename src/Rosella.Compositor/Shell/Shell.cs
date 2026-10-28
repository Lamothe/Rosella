namespace Rosella.Compositor;

/// <summary>
/// The desktop shell: Windows 11 style — a bottom taskbar with a Start
/// button and the clock, and a Start menu centred above it. One desktop;
/// there are no workspaces. Everything is hardcoded; there is no
/// configuration. Layout is in logical pixels; drawing multiplies by the
/// output scale.
/// </summary>
internal sealed class Shell
{
    /// <summary>Taskbar height in logical pixels.</summary>
    internal const int BarHeight = 48;

    /// <summary>Menu text scale (the bitmap font's natural size is 8x16).</summary>
    internal const int FontScale = 2;

    internal Launcher Launcher { get; } = new();

    /// <summary>
    /// The taskbar layout the scanout drew last frame: the input thread
    /// hit-tests exactly this, so drawn and clicked rectangles never
    /// disagree.
    /// </summary>
    internal BarLayout? LastBarLayout { get; private set; }

    /// <summary>
    /// Draws the taskbar and, when open, the Start menu overlay. Width and
    /// height are logical pixels; the chrome is drawn scaled onto the
    /// physical scanout.
    /// </summary>
    internal void Draw(Span<byte> pixels, int width, int height, int stride, int scale, string clock, IReadOnlyList<BarTask> tasks, SurfaceHandler? focused)
    {
        var layout = Bar.ComputeLayout(width, height, clock, tasks);
        LastBarLayout = layout;
        Bar.Draw(pixels, width, height, stride, scale, layout, clock, focused);

        if (Launcher.IsOpen)
        {
            Launcher.Draw(pixels, width, height, stride, scale);
        }
    }
}
