#if ROSELLA_DASHBOARD_WIDGETS
using Rosella.Compositor;
using static Rosella.DashboardWidgets.DashboardWidgetChrome;
namespace Rosella.DashboardWidgets;
#else
using static Rosella.Compositor.DashboardWidgetChrome;
namespace Rosella.Compositor;
#endif

internal sealed class AppsWidget : IDashboardWidget
{
    private static void DrawAppName(Span<byte> pixels, int stride, int scale, string name, Rect box)
    {
        const int em = 14;
        var first = box with { H = 17 };
        if (ShellText.Measure(name, em).Width > box.W)
        {
            for (var split = name.LastIndexOf(' '); split > 0; split = name.LastIndexOf(' ', split - 1))
            {
                var prefix = name[..split];
                if (ShellText.Measure(prefix, em).Width <= box.W)
                {
                    CenteredLabel(pixels, stride, scale, prefix, first, em, shadow: true);
                    CenteredLabel(pixels, stride, scale, name[(split + 1)..], first with { Y = first.Y + 16 }, em, shadow: true);
                    return;
                }
            }
        }
        CenteredLabel(pixels, stride, scale, name, first, em, shadow: true);
    }

    public void Draw(Span<byte> pixels, in DashboardWidgetFrame frame)
    {
        var (width, height, stride, scale, damage, layout, pointer, _, _, _, _, _, _, _) = frame;
        var grid = layout.Applications;
        Chrome.BlendRoundedRect(pixels, stride, grid, 18, 0x20000000, scale, damage);
        var apps = frame.Apps.Items;
        if (apps is null || apps.Length == 0)
        {
            Label(pixels, stride, scale, apps is null ? "Loading apps…" : "No apps found",
                grid.X + 18, grid.Y + 18, 15, shadow: true);
            return;
        }

        var firstRow = Math.Clamp(frame.Apps.FirstRow, 0, layout.MaximumFirstRow(apps.Length));
        var first = firstRow * layout.Columns;
        var count = Math.Min(layout.PageSize, apps.Length - first);
        for (var index = 0; index < count; index++)
        {
            var tile = layout.Tile(index);
            if (!damage.Intersects(tile.ToDamage(scale)))
            {
                continue;
            }
            var app = apps[first + index];
            var hover = pointer is { } mouse && tile.Contains(mouse.X, mouse.Y);
            if (hover || first + index == frame.Apps.SelectedIndex)
            {
                Chrome.BlendRoundedRect(pixels, stride, tile, 13, hover ? 0x88414A57 : 0xAA264C72, scale, damage);
            }

            const int iconSize = 47;
            var contentTop = tile.Y + (tile.H - 96) / 2;
            var icon = new Rect(tile.X + (tile.W - iconSize) / 2, contentTop + 10, iconSize, iconSize);
            if (frame.Apps.Icon(app.Icon, iconSize * scale) is { } art)
            {
                art.Draw(pixels, width, height, stride, icon.Scaled(scale));
            }
            else
            {
                Chrome.FillRoundedRect(pixels, width, height, stride, icon, 11, Selected, scale);
                CenteredLabel(pixels, stride, scale, char.ToUpperInvariant(app.Name[0]).ToString(), icon, 20, Accent);
            }
            DrawAppName(pixels, stride, scale, app.Name, new Rect(tile.X + 5, contentTop + 61, tile.W - 10, 35));
        }

        var track = new Rect(grid.X + grid.W - 9, grid.Y + 8, 4, grid.H - 16);
        DrawScrollBar(pixels, stride, scale, damage, track, layout.Rows, layout.MaximumFirstRow(apps.Length) + layout.Rows, firstRow);

        if (frame.Apps.Menu is { } menu)
        {
            var box = menu.Bounds;
            var hovered = !menu.Pinned && pointer is { } mouse && box.Contains(mouse.X, mouse.Y);
            Chrome.FillRoundedRect(pixels, width, height, stride, box, 10, 0xFF67707D, scale);
            Chrome.FillRoundedRect(pixels, width, height, stride, new Rect(box.X + 1, box.Y + 1, box.W - 2, box.H - 2), 9,
                hovered ? Hover : Card, scale);
            var text = ShellText.Fit(menu.Pinned ? "On taskbar" : "Add to taskbar", 15, box.W - 28);
            ShellText.DrawText(pixels, width, height, stride, text, (box.X + 14) * scale,
                ShellText.InkCenteredY(text, 15, box, scale), menu.Pinned ? Secondary : Text, 15, scale);
        }
    }
}
