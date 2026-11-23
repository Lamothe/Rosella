#if ROSSELLA_TASKBAR
using Rosella.Compositor;

namespace Rosella.Taskbar;
#else
namespace Rosella.Compositor;
#endif

internal sealed class TaskbarImplementation : ITaskbarModule
{
    private readonly TaskbarIcons _icons = new();

    public string ClockString() => Bar.ClockString();

    public BarLayout ComputeLayout(int width, int height, string clock, IReadOnlyList<BarTask> tasks,
        IReadOnlyList<PinnedLauncher> pinned) => Bar.ComputeLayout(width, height, clock, tasks, pinned);

    public void Draw(Span<byte> pixels, int width, int height, int stride, int scale, BarLayout layout,
        string clock, SurfaceHandler? focused, (int X, int Y)? pointer, int pressedPin, FrostedGlass glass) =>
        Bar.Draw(pixels, width, height, stride, scale, layout, clock, focused, pointer, pressedPin, glass, _icons);

    public ISettingsPanel CreateSettings(ISystemActions systemActions, IVolumeControl volume) =>
        new SettingsPanel(systemActions, volume);
}
