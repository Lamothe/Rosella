using System.Globalization;
#if ROSELLA_DASHBOARD_WIDGETS
using Rosella.Compositor;
using static Rosella.DashboardWidgets.DashboardWidgetChrome;
namespace Rosella.DashboardWidgets;
#else
using static Rosella.Compositor.DashboardWidgetChrome;
namespace Rosella.Compositor;
#endif

internal sealed class CalendarWidget : IDashboardWidget
{
    public void Draw(Span<byte> pixels, in DashboardWidgetFrame frame)
    {
        var (_, _, stride, scale, damage, layout, pointer, _, _, _, _, _, _, _) = frame;
        var bounds = layout.Calendar;
        var panel = Dashboard.CalendarPanel(bounds);
        Chrome.BlendRoundedRect(pixels, stride, panel, 18, CardGlass, scale, damage);
        var today = DateOnly.FromDateTime(DateTime.Today);
        var month = frame.Calendar.Month(today);
        var previous = DashboardCalendar.Previous(panel);
        var next = DashboardCalendar.Next(panel);
        var hovered = pointer is { } mouse ? DashboardCalendar.Hover(panel, mouse) : 0;
        if (hovered is 1 or 2)
        {
            Chrome.BlendRoundedRect(pixels, stride, hovered == 1 ? previous : next, 8, 0x485A7086, scale, damage);
        }
        CenteredLabel(pixels, stride, scale, "‹", previous, 24);
        CenteredLabel(pixels, stride, scale, "›", next, 24);
        CenteredLabel(pixels, stride, scale, month.ToString("MMMM yyyy"), DashboardCalendar.MonthHeader(panel), 16,
            hovered == 3 ? Accent : Text);

        var cellWidth = (panel.W - 24) / 7;
        var dayHeight = Math.Min(26, (panel.H - 58) / 6);
        if (cellWidth <= 0 || dayHeight < 14)
        {
            return;
        }

        var culture = CultureInfo.CurrentCulture.DateTimeFormat;
        var start = (int)culture.FirstDayOfWeek;
        var left = panel.X + (panel.W - cellWidth * 7) / 2;
        for (var column = 0; column < 7; column++)
        {
            var name = culture.GetAbbreviatedDayName((DayOfWeek)((start + column) % 7));
            CenteredLabel(pixels, stride, scale, name, new Rect(left + column * cellWidth, panel.Y + 39, cellWidth, 21), 11, Secondary);
        }

        var leading = DashboardCalendar.LeadingDays(month);
        var total = DateTime.DaysInMonth(month.Year, month.Month);
        for (var day = 1; day <= total; day++)
        {
            var index = leading + day - 1;
            var cell = new Rect(left + index % 7 * cellWidth, panel.Y + 61 + index / 7 * dayHeight, cellWidth, dayHeight);
            var current = today.Year == month.Year && today.Month == month.Month && today.Day == day;
            if (current)
            {
                var marker = new Rect(cell.X + (cell.W - 27) / 2, cell.Y + (cell.H - 24) / 2, 27, 24);
                Chrome.BlendRoundedRect(pixels, stride, marker, 8, 0xEF284E75, scale, damage);
            }
            CenteredLabel(pixels, stride, scale, day.ToString(CultureInfo.CurrentCulture), cell, 13, current ? Accent : Text);
        }
    }
}
