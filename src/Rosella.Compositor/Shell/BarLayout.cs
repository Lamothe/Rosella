namespace Rosella.Compositor;

/// <summary>A window button on the taskbar; minimised windows keep their button.</summary>
internal sealed record BarTask(string Label, string AppId, SurfaceHandler Surface, bool Minimized = false);

/// <summary>One pinned launcher button on the taskbar.</summary>
internal sealed record PinnedButton(Rect Rect, PinnedLauncher Launcher, bool Running);

internal sealed record BarLayout(Rect Bounds, Rect Clock, PinnedButton[] Pinned, (Rect Rect, BarTask Task)[] Tasks)
{
    internal Rect HoverBounds((int X, int Y) pointer) => Clock.Contains(pointer.X, pointer.Y)
        ? Clock : HoveredIcon(pointer)?.Anchor ?? default;

    internal (Rect Anchor, string Name)? HoveredIcon((int X, int Y) pointer)
    {
        foreach (var button in Pinned)
        {
            if (button.Rect.Contains(pointer.X, pointer.Y))
            {
                return (button.Rect, button.Launcher.Name);
            }
        }
        foreach (var (rect, task) in Tasks)
        {
            if (rect.Contains(pointer.X, pointer.Y))
            {
                return (rect, task.Label);
            }
        }

        return null;
    }
}

internal readonly record struct DockLabel(string Text, Rect Bounds)
{
    private const int Em = 14;

    internal static DockLabel? Create(Rect anchor, string name, int outputWidth)
    {
        const int padding = 12;
        const int edge = 8;
        const int height = 28;
        const int gap = 8;
        var text = ShellText.Fit(name, Em, Math.Min(280, outputWidth - 2 * (padding + edge)));
        if (text.Length == 0 || anchor.Y < height + gap)
        {
            return null;
        }
        var width = ShellText.Measure(text, Em).Width + 2 * padding;
        var x = Math.Clamp(anchor.X + (anchor.W - width) / 2, edge, outputWidth - width - edge);
        return new DockLabel(text, new Rect(x, anchor.Y - height - gap, width, height));
    }

    internal void Draw(Span<byte> pixels, int stride, int scale, in DamageRect damage)
    {
        if (!damage.Intersects(Bounds.ToDamage(scale)))
        {
            return;
        }
        Chrome.BlendRoundedRect(pixels, stride, Bounds, 9, 0xFF505763, scale, damage);
        var inset = new Rect(Bounds.X + 1, Bounds.Y + 1, Bounds.W - 2, Bounds.H - 2);
        Chrome.BlendRoundedRect(pixels, stride, inset, 8, 0xFF252A32, scale, damage);
        var x = (Bounds.X + (Bounds.W - ShellText.Measure(Text, Em).Width) / 2) * scale;
        ShellText.DrawText(pixels, stride / 4, pixels.Length / stride, stride, Text, x,
            ShellText.InkCenteredY(Text, Em, Bounds, scale), Chrome.PanelText, Em, scale);
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
