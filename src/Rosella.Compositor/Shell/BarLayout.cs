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

internal sealed record DockPress(string? DesktopId, SurfaceHandler? Window, (int X, int Y) Origin, bool Dragging = false)
{
    internal static DockPress? At(BarLayout layout, (int X, int Y) point)
    {
        foreach (var pin in layout.Pinned)
        {
            if (pin.Rect.Contains(point.X, point.Y))
            {
                return new DockPress(pin.Launcher.DesktopId, null, point);
            }
        }
        foreach (var (rect, task) in layout.Tasks)
        {
            if (rect.Contains(point.X, point.Y))
            {
                return new DockPress(null, task.Surface, point);
            }
        }
        return null;
    }

    internal bool IsDrag((int X, int Y) point) =>
        Dragging || Math.Abs(point.X - Origin.X) >= 6 || Math.Abs(point.Y - Origin.Y) >= 6;

    internal int PinIndex(BarLayout layout) =>
        DesktopId is null ? -1 : Array.FindIndex(layout.Pinned, pin => pin.Launcher.DesktopId == DesktopId);

    internal DockDrop? DropAt(BarLayout layout, (int X, int Y) point)
    {
        var pinned = DesktopId is not null;
        var count = pinned ? layout.Pinned.Length : layout.Tasks.Length;
        var source = pinned ? PinIndex(layout) : Array.FindIndex(layout.Tasks, button => button.Task.Surface == Window);
        if (count < 2 || source < 0 || !layout.Bounds.Contains(point.X, point.Y))
        {
            return null;
        }

        Rect Cell(int index) => pinned ? layout.Pinned[index].Rect : layout.Tasks[index].Rect;
        var first = Cell(0);
        var last = Cell(count - 1);
        var gap = Bar.PinGap / 2;
        if (point.X < first.X - gap || point.X >= last.X + last.W + gap)
        {
            return null;
        }

        var slot = 0;
        while (slot < count)
        {
            var cell = Cell(slot);
            var middle = cell.X + cell.W / 2;
            if (point.X < middle || (point.X == middle && slot < source))
            {
                break;
            }
            slot++;
        }
        if (slot == source || slot == source + 1)
        {
            return null;
        }

        var after = slot > source;
        var anchor = after ? slot - 1 : slot;
        var rect = Cell(anchor);
        var marker = new Rect((after ? rect.X + rect.W + gap : rect.X - gap) - 1, rect.Y + 4, 2, rect.H - 8);
        return pinned
            ? new DockDrop(marker, layout.Pinned[anchor].Launcher.DesktopId, null, after)
            : new DockDrop(marker, null, layout.Tasks[anchor].Task.Surface, after);
    }
}

internal readonly record struct DockDrop(Rect Marker, string? DesktopId, SurfaceHandler? Window, bool After);
