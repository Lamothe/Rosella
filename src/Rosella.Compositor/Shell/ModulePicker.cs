#if ROSELLA_DASHBOARD_WIDGETS
using Rosella.Compositor;

namespace Rosella.DashboardWidgets;
#elif ROSSELLA_TASKBAR
using Rosella.Compositor;

namespace Rosella.Taskbar;
#else
namespace Rosella.Compositor;
#endif

internal static class ModulePicker
{
    private const int SelectorHeight = 34;
    private const int Pad = 16;
    private const int Em = 13;
    private const uint Hover = 0xFF42424A;

    internal static Rect Back(Rect panel) => new(panel.X + Pad, panel.Y + 8, Math.Max(0, panel.W - 2 * Pad), 30);
    internal static int Height(bool choices) => 132 + (choices ? SelectorHeight : 0);
    internal static Rect Previous(Rect panel) => new(panel.X + Pad, panel.Y + 42, 30, 30);
    internal static Rect Next(Rect panel) => new(panel.X + panel.W - Pad - 30, panel.Y + 42, 30, 30);

    private static Rect ModuleRow(Rect panel, bool choices) =>
        new(panel.X + Pad, panel.Y + 44 + (choices ? SelectorHeight : 0), Math.Max(0, panel.W - 2 * Pad), 52);

    internal static Rect ReloadButton(Rect panel, bool choices = false)
    {
        var row = ModuleRow(panel, choices);
        var width = Math.Min(68, row.W / 2);
        return new Rect(row.X + row.W - width - 6, row.Y + 9, width, 34);
    }

    internal static void Draw(Span<byte> pixels, int width, int height, int stride, int scale, Rect panel,
        (int X, int Y)? pointer, TaskbarModuleControls modules)
    {
        var back = Back(panel);
        if (pointer is { } point && back.Contains(point.X, point.Y))
        {
            Chrome.FillRoundedRect(pixels, width, height, stride, back, 8, Hover, scale);
        }
        PowerIcons.Draw(pixels, width, height, stride, PowerGlyph.ChevronLeft, back.X + 12, back.Y + 15,
            Chrome.PanelText, scale, 16);
        DrawText(pixels, width, height, stride, scale, "Modules", back.X + 26, back.Y + 15,
            back.W - 26, Chrome.PanelText);

        if (modules.HasChoices)
        {
            DrawArrow(pixels, width, height, stride, scale, Previous(panel), pointer, PowerGlyph.ChevronLeft);
            DrawArrow(pixels, width, height, stride, scale, Next(panel), pointer, PowerGlyph.ChevronRight);
        }

        var selected = modules.Selected;
        var status = selected.ReadStatus();
        var row = ModuleRow(panel, modules.HasChoices);
        var reload = ReloadButton(panel, modules.HasChoices);
        Chrome.FillRoundedRect(pixels, width, height, stride, row, 10, Chrome.PanelControlBackground, scale);
        var hovered = pointer is { } position && reload.Contains(position.X, position.Y);
        Chrome.FillRoundedRect(pixels, width, height, stride, reload, 8, hovered ? Hover : 0xFF393B44, scale);

        var textWidth = reload.X - row.X - 20;
        DrawText(pixels, width, height, stride, scale, selected.Label, row.X + 12, row.Y + 17,
            textWidth, Chrome.PanelText);
        DrawText(pixels, width, height, stride, scale, status.Build, row.X + 12, row.Y + 35,
            textWidth, Chrome.PanelSecondaryText);
        var reloadLabelWidth = ShellText.Measure("Reload", Em).Width;
        DrawText(pixels, width, height, stride, scale, "Reload",
            reload.X + Math.Max(0, (reload.W - reloadLabelWidth) / 2), reload.Y + reload.H / 2,
            reload.W, Chrome.PanelText);

        if (status.Result is { } result)
        {
            DrawText(pixels, width, height, stride, scale, result, row.X + 4, row.Y + 69,
                row.W - 8, Chrome.PanelSecondaryText);
        }
    }

    private static void DrawArrow(Span<byte> pixels, int width, int height, int stride, int scale, Rect bounds,
        (int X, int Y)? pointer, PowerGlyph glyph)
    {
        var hovered = pointer is { } point && bounds.Contains(point.X, point.Y);
        Chrome.FillRoundedRect(pixels, width, height, stride, bounds, 8, hovered ? Hover : Chrome.PanelControlBackground, scale);
        PowerIcons.Draw(pixels, width, height, stride, glyph, bounds.X + bounds.W / 2, bounds.Y + bounds.H / 2,
            Chrome.PanelText, scale, 16);
    }

    private static void DrawText(Span<byte> pixels, int width, int height, int stride, int scale, string text,
        int x, int centerY, int available, uint color)
    {
        var fitted = ShellText.Fit(text, Em, Math.Max(0, available));
        ShellText.DrawText(pixels, width, height, stride, fitted, x * scale,
            (centerY - ShellText.LineHeight(Em) / 2) * scale, color, Em, scale);
    }
}
