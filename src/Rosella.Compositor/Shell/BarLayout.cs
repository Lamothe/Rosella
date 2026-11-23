namespace Rosella.Compositor;

/// <summary>A window button on the taskbar; minimised windows keep their button.</summary>
internal sealed record BarTask(string Label, string AppId, SurfaceHandler Surface, bool Minimized = false);

/// <summary>One pinned launcher button on the taskbar.</summary>
internal sealed record PinnedButton(Rect Rect, PinnedLauncher Launcher, bool Running);

internal sealed record BarLayout(Rect Bounds, Rect Clock, PinnedButton[] Pinned, (Rect Rect, BarTask Task)[] Tasks);
