#if ROSSELLA_TASKBAR
using Rosella.Compositor;

namespace Rosella.Taskbar;
#else
namespace Rosella.Compositor;
#endif

/// <summary>The summary row for a Quick Settings picker.</summary>
internal static class SettingsSelector
{
    internal const int Height = 42;

    internal static void Draw(Span<byte> pixels, int width, int height, int stride, int scale,
        Rect row, (int X, int Y)? pointer, string title, string subtitle)
    {
        var hovered = pointer is { } point && row.Contains(point.X, point.Y);
        Chrome.FillRoundedRect(pixels, width, height, stride, row, 10,
            hovered ? 0xFF42424A : Chrome.PanelControlBackground, scale);
        DrawText(pixels, width, height, stride, scale, row, title, 12, Chrome.PanelText);
        DrawText(pixels, width, height, stride, scale, row, subtitle, 29, Chrome.PanelSecondaryText);
        PowerIcons.Draw(pixels, width, height, stride, PowerGlyph.ChevronRight,
            row.X + row.W - 16, row.Y + row.H / 2, Chrome.PanelSecondaryText, scale, 16);
    }

    private static void DrawText(Span<byte> pixels, int width, int height, int stride, int scale, Rect row,
        string text, int centerOffset, uint colour)
    {
        const int em = 13;
        var fitted = ShellText.Fit(text, em, Math.Max(0, row.W - 42));
        ShellText.DrawText(pixels, width, height, stride, fitted, (row.X + 12) * scale,
            (row.Y + centerOffset - ShellText.LineHeight(em) / 2) * scale, colour, em, scale);
    }
}
