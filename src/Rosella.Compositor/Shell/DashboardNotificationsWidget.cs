#if ROSELLA_DASHBOARD_WIDGETS
using Rosella.Compositor;
using static Rosella.DashboardWidgets.DashboardWidgetChrome;
namespace Rosella.DashboardWidgets;
#else
using static Rosella.Compositor.DashboardWidgetChrome;
namespace Rosella.Compositor;
#endif

internal sealed class NotificationsWidget : IDashboardWidget
{
    public void Draw(Span<byte> pixels, in DashboardWidgetFrame frame)
    {
        var (_, _, stride, scale, damage, layout, _, _, _, _, _, _, _, _) = frame;
        var panel = layout.Notifications;
        Chrome.BlendRoundedRect(pixels, stride, panel, 18, CardGlass, scale, damage);
        Label(pixels, stride, scale, "Notifications", panel.X + 16, panel.Y + 10, 17, maximum: panel.W - 32);
        var visible = Dashboard.VisibleNotifications(panel);
        var records = frame.Notifications;
        if (records.Length == 0)
        {
            Label(pixels, stride, scale, "No recent notifications", panel.X + 16, panel.Y + 49, 13, Secondary, panel.W - 32);
            return;
        }

        var first = Math.Clamp(frame.NotificationOffset, 0, Math.Max(0, records.Length - visible));
        for (var index = 0; index < Math.Min(visible, records.Length - first); index++)
        {
            var item = records[first + index];
            var y = panel.Y + 39 + index * Dashboard.NotificationRowHeight;
            var local = item.Time.LocalDateTime;
            var time = local.Date == DateTime.Today ? local.ToString("HH:mm") : local.ToString("d MMM");
            var clockWidth = ShellText.Measure(time, 11).Width;
            var title = item.Title.Length > 0 ? item.Title : item.App;
            Label(pixels, stride, scale, title, panel.X + 16, y + 1, 14, maximum: panel.W - 48 - clockWidth);
            Label(pixels, stride, scale, time, panel.X + panel.W - 20 - clockWidth, y + 3, 11, Secondary);
            var detail = item.Body.Length > 0 ? item.Body : item.App;
            Label(pixels, stride, scale, detail, panel.X + 16, y + 22, 12, Secondary, panel.W - 36);
        }
        var track = new Rect(panel.X + panel.W - 9, panel.Y + 42, 3, panel.H - 54);
        DrawScrollBar(pixels, stride, scale, damage, track, visible, records.Length, first);
    }
}
