using System.Globalization;

namespace Rosella.Compositor;

internal sealed class DashboardCalendar
{
    private int _offset;

    internal void Reset() => Volatile.Write(ref _offset, 0);

    internal DateOnly Month(DateOnly today) => new DateOnly(today.Year, today.Month, 1).AddMonths(Volatile.Read(ref _offset));

    internal static int LeadingDays(DateOnly month)
    {
        var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        return ((int)month.DayOfWeek - (int)firstDay + 7) % 7;
    }

    internal static Rect Previous(Rect panel) => new(panel.X + 12, panel.Y + 9, 32, 30);
    internal static Rect Next(Rect panel) => new(panel.X + panel.W - 44, panel.Y + 9, 32, 30);
    internal static Rect MonthHeader(Rect panel) => new(panel.X + 48, panel.Y + 9, Math.Max(0, panel.W - 96), 30);

    internal bool Click(Rect panel, int x, int y)
    {
        if (Previous(panel).Contains(x, y))
        {
            _offset = Math.Max(_offset - 1, -120);
        }
        else if (Next(panel).Contains(x, y))
        {
            _offset = Math.Min(_offset + 1, 120);
        }
        else if (MonthHeader(panel).Contains(x, y))
        {
            Reset();
        }
        else
        {
            return false;
        }
        return true;
    }

    internal static int Hover(Rect panel, (int X, int Y) point) =>
        Previous(panel).Contains(point.X, point.Y) ? 1 :
        Next(panel).Contains(point.X, point.Y) ? 2 : MonthHeader(panel).Contains(point.X, point.Y) ? 3 : 0;
}
