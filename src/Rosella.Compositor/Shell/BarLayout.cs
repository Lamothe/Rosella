namespace Rosella.Compositor;

/// <summary>A window button on the taskbar; minimised windows keep their button.</summary>
internal sealed record BarTask(string Label, string AppId, SurfaceHandler Surface, bool Minimized = false);

/// <summary>One pinned launcher button on the taskbar.</summary>
internal sealed record PinnedButton(Rect Rect, PinnedLauncher Launcher, bool Running);

internal sealed record BarLayout(Rect Bounds, Rect Clock, PinnedButton[] Pinned, (Rect Rect, BarTask Task)[] Tasks)
{
    internal Rect HoverBounds((int X, int Y) pointer)
    {
        if (Clock.Contains(pointer.X, pointer.Y))
        {
            return Clock;
        }

        foreach (var button in Pinned)
        {
            if (button.Rect.Contains(pointer.X, pointer.Y))
            {
                return button.Rect;
            }
        }

        return default;
    }
}
