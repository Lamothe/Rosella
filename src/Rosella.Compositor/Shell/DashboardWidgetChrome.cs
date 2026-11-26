#if ROSELLA_DASHBOARD_WIDGETS
using Rosella.Compositor;
namespace Rosella.DashboardWidgets;
#else
namespace Rosella.Compositor;
#endif

internal static class DashboardWidgetChrome
{
    internal const uint Card = 0xFF242931;
    internal const uint CardGlass = 0x9416202B;
    internal const uint SettingsCardGlass = 0x7016202B;
    internal const uint Hover = 0xFF363F4C;
    internal const uint Selected = 0xFF334D66;
    internal const uint Text = 0xFFF2F4F7;
    internal const uint Secondary = 0xFFBBC4CF;
    internal const uint Accent = 0xFFA6D0F9;

    internal static void DrawScrollBar(Span<byte> pixels, int stride, int scale, in DamageRect damage, Rect track,
        int visible, int total, int offset)
    {
        if (visible <= 0 || total <= visible || track.H <= 0)
        {
            return;
        }
        var thumb = Math.Clamp(track.H * visible / total, Math.Min(18, track.H), track.H);
        var y = track.Y + offset * (track.H - thumb) / (total - visible);
        Chrome.BlendRoundedRect(pixels, stride, track, 2, 0x66202834, scale, damage);
        Chrome.BlendRoundedRect(pixels, stride, track with { Y = y, H = thumb }, 2, 0xDDEDF4FA, scale, damage);
    }

    internal static void CenteredLabel(Span<byte> pixels, int stride, int scale, string text, Rect box, int em,
        uint colour = Text, bool shadow = false)
    {
        text = ShellText.Fit(text, em, box.W);
        var x = (box.X + (box.W - ShellText.Measure(text, em).Width) / 2) * scale;
        DrawLabel(pixels, stride, scale, text, x, ShellText.InkCenteredY(text, em, box, scale), em, colour, shadow);
    }

    internal static void Label(Span<byte> pixels, int stride, int scale, string text, int x, int y, int em,
        uint colour = Text, int maximum = int.MaxValue, bool shadow = false) =>
        DrawLabel(pixels, stride, scale, ShellText.Fit(text, em, maximum), x * scale, y * scale, em, colour, shadow);

    private static void DrawLabel(Span<byte> pixels, int stride, int scale, string text, int x, int y, int em, uint colour, bool shadow)
    {
        var width = stride / 4;
        var height = pixels.Length / stride;
        if (shadow)
        {
            ShellText.DrawText(pixels, width, height, stride, text, x - scale, y + scale, 0xFF151A22, em, scale);
            ShellText.DrawText(pixels, width, height, stride, text, x + scale, y + scale, 0xFF151A22, em, scale);
        }
        ShellText.DrawText(pixels, width, height, stride, text, x, y, colour, em, scale);
    }

}
