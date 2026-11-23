#if ROSSELLA_TASKBAR
using Rosella.Compositor;

namespace Rosella.Taskbar;
#else
namespace Rosella.Compositor;
#endif

internal static class ModulePicker
{
    internal const int Height = 132;
    private const int Pad = 16;
    private const int Em = 13;
    private const uint Hover = 0xFF42424A;

    internal static Rect Back(Rect panel) => new(panel.X + Pad, panel.Y + 8, Math.Max(0, panel.W - 2 * Pad), 30);

    private static Rect ModuleRow(Rect panel) =>
        new(panel.X + Pad, panel.Y + 44, Math.Max(0, panel.W - 2 * Pad), 52);

    internal static Rect ReloadButton(Rect panel)
    {
        var row = ModuleRow(panel);
        var width = Math.Min(68, row.W / 2);
        return new Rect(row.X + row.W - width - 6, row.Y + 9, width, 34);
    }

    internal static void Draw(Span<byte> pixels, int width, int height, int stride, int scale, Rect panel,
        (int X, int Y)? pointer, (string Build, string? Result) status)
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

        var row = ModuleRow(panel);
        var reload = ReloadButton(panel);
        Chrome.FillRoundedRect(pixels, width, height, stride, row, 10, Chrome.PanelControlBackground, scale);
        var hovered = pointer is { } position && reload.Contains(position.X, position.Y);
        Chrome.FillRoundedRect(pixels, width, height, stride, reload, 8, hovered ? Hover : 0xFF393B44, scale);

        var textWidth = reload.X - row.X - 20;
        DrawText(pixels, width, height, stride, scale, "Taskbar & Quick Settings", row.X + 12, row.Y + 17,
            textWidth, Chrome.PanelText);
        DrawText(pixels, width, height, stride, scale, status.Build, row.X + 12, row.Y + 35,
            textWidth, Chrome.PanelSecondaryText);
        var reloadLabelWidth = ShellText.Measure("Reload", Em).Width;
        DrawText(pixels, width, height, stride, scale, "Reload",
            reload.X + Math.Max(0, (reload.W - reloadLabelWidth) / 2), reload.Y + reload.H / 2,
            reload.W, Chrome.PanelText);

        if (status.Result is { } result)
        {
            DrawText(pixels, width, height, stride, scale, result, row.X + 4, panel.Y + 113,
                row.W - 8, Chrome.PanelSecondaryText);
        }
    }

    private static void DrawText(Span<byte> pixels, int width, int height, int stride, int scale, string text,
        int x, int centerY, int available, uint color)
    {
        var fitted = ShellText.Fit(text, Em, Math.Max(0, available));
        ShellText.DrawText(pixels, width, height, stride, fitted, x * scale,
            (centerY - ShellText.LineHeight(Em) / 2) * scale, color, Em, scale);
    }
}
