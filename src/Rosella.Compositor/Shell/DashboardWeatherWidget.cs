#if ROSELLA_DASHBOARD_WIDGETS
using Rosella.Compositor;
using static Rosella.DashboardWidgets.DashboardWidgetChrome;
namespace Rosella.DashboardWidgets;
#else
using static Rosella.Compositor.DashboardWidgetChrome;
namespace Rosella.Compositor;
#endif

internal sealed class WeatherWidget : IDashboardWidget
{
    public void Draw(Span<byte> pixels, in DashboardWidgetFrame frame)
    {
        var (_, _, stride, scale, damage, layout, pointer, _, _, _, _, _, _, _) = frame;
        var box = layout.Weather;
        var card = box with { H = Math.Min(158, box.H) };
        Chrome.BlendRoundedRect(pixels, stride, card, 18, CardGlass, scale, damage);
        var view = frame.Weather;
        Label(pixels, stride, scale, view.Location ?? "Weather", card.X + 18, card.Y + 12, 18, maximum: card.W - 36);
        if (view.Forecast is not { } forecast)
        {
            Label(pixels, stride, scale, view.Status, card.X + 18, card.Y + 48, 15, Secondary, card.W - 36);
            return;
        }

        var temperature = WeatherForecast.Degrees(forecast.Temperature);
        Label(pixels, stride, scale, temperature, card.X + 17, card.Y + 38, 43, Accent);
        var detailX = card.X + Math.Max(108, ShellText.Measure(temperature, 43).Width + 34);
        var available = card.X + card.W - detailX - 16;
        Label(pixels, stride, scale, forecast.Conditions, detailX, card.Y + 49, 16, maximum: available);
        Label(pixels, stride, scale, "Feels like " + WeatherForecast.Degrees(forecast.FeelsLike), detailX,
            card.Y + 75, 13, Secondary, available);
        var extremes = "High " + WeatherForecast.Degrees(forecast.High) + "  Low " + WeatherForecast.Degrees(forecast.Low);
        Label(pixels, stride, scale, extremes, card.X + 18, card.Y + 104, 14, Secondary, card.W - 36);
        var credit = Dashboard.WeatherCredit(box);
        var active = pointer is { } mouse && credit.Contains(mouse.X, mouse.Y);
        var attribution = view.Status.Length == 0 ? "Open-Meteo" : "Open-Meteo · last available";
        Label(pixels, stride, scale, attribution, credit.X, credit.Y + 2, 11, active ? Accent : Secondary, credit.W);
    }
}
