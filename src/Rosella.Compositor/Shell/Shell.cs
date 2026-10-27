namespace Rosella.Compositor;

/// <summary>
/// The desktop shell: a top bar (workspaces, clock, tasks), a launcher, and
/// four fixed workspaces. Everything is hardcoded; there is no configuration.
/// </summary>
internal sealed class Shell
{
    /// <summary>Bar height in output pixels (scale-2 text is 32px tall).</summary>
    internal const int BarHeight = 48;

    internal const int WorkspaceCount = 4;

    internal const int FontScale = 2;

    internal Launcher Launcher { get; } = new();

    /// <summary>The workspace shown on screen (1-based).</summary>
    internal int CurrentWorkspace { get; set; } = 1;

    /// <summary>
    /// The bar layout the scanout drew last frame: the input thread hit-tests
    /// exactly this, so drawn and clicked rectangles never disagree.
    /// </summary>
    internal BarLayout? LastBarLayout { get; private set; }

    /// <summary>Draws the bar and, when open, the launcher overlay.</summary>
    internal void Draw(Span<byte> pixels, int width, int height, int stride, string clock, IReadOnlyList<BarTask> tasks, SurfaceHandler? focused)
    {
        var layout = Bar.ComputeLayout(width, clock, tasks);
        LastBarLayout = layout;
        Bar.Draw(pixels, width, height, stride, layout, clock, CurrentWorkspace, focused);

        if (Launcher.IsOpen)
        {
            Launcher.Draw(pixels, width, height, stride);
        }
    }
}
