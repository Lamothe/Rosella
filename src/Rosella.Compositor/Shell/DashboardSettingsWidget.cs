#if ROSELLA_DASHBOARD_WIDGETS
using Rosella.Compositor;
using static Rosella.DashboardWidgets.DashboardWidgetChrome;
namespace Rosella.DashboardWidgets;
#else
using static Rosella.Compositor.DashboardWidgetChrome;
namespace Rosella.Compositor;
#endif

internal sealed class SettingsWidget : IDashboardSettingsWidget
{
    private byte[] _settingsPixels = [];

    public ISettingsPanel CreateSettings(ISystemActions actions, IVolumeControl volume) => new SettingsPanel(actions, volume, fillWidth: true);

    public void Draw(Span<byte> pixels, in DashboardWidgetFrame frame)
    {
        var (width, height, stride, scale, damage, layout, pointer, _, _, _, _, _, settings, panel) = frame;
        var viewport = Dashboard.SettingsViewport(layout) with { X = panel.X, W = panel.W };
        if (viewport.W <= 0 || viewport.H <= 0)
        {
            return;
        }
        Chrome.BlendRoundedRect(pixels, stride, viewport, 18, SettingsCardGlass, scale, damage);
        if (panel.H <= viewport.H)
        {
            settings.DrawControls(pixels, stride, scale, panel, pointer);
            return;
        }

        // Keep the full settings controls available on short displays. Paint
        // only the visible slice over the stationary card background.
        var physical = viewport.Scaled(scale);
        var rowBytes = physical.W * 4;
        var length = rowBytes * physical.H;
        if (_settingsPixels.Length < length)
        {
            _settingsPixels = GC.AllocateUninitializedArray<byte>(length);
        }
        var visible = _settingsPixels.AsSpan(0, length);
        for (var row = 0; row < physical.H; row++)
        {
            pixels.Slice((physical.Y + row) * stride + physical.X * 4, rowBytes).CopyTo(visible.Slice(row * rowBytes, rowBytes));
        }
        (int X, int Y)? localPointer = pointer is { } point && viewport.Contains(point.X, point.Y)
            ? (point.X - viewport.X, point.Y - viewport.Y) : null;
        var localPanel = panel with { X = 0, Y = panel.Y - viewport.Y };
        settings.DrawControls(visible, rowBytes, scale, localPanel, localPointer);
        Blitter.Copy(pixels, width, height, stride, visible, physical.W, physical.H, physical.X, physical.Y, damage);
        var track = new Rect(viewport.X + viewport.W - 8, viewport.Y + 8, 3, viewport.H - 16);
        DrawScrollBar(pixels, stride, scale, damage, track, viewport.H, panel.H, viewport.Y - panel.Y);
    }
}
