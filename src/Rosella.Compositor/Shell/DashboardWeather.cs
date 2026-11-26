using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace Rosella.Compositor;

internal sealed class DashboardWeather(string? locationPath = null)
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly object _gate = new();
    private DateTimeOffset _refreshAfter;
    private volatile WeatherView _view = new(null, null, locationPath is null ? "Set a weather location" : "Loading weather…");

    internal Action? Dirty { get; set; }
    internal WeatherView View => _view;

    internal static string ConfigurationPath()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (!Path.IsPathFullyQualified(config ?? ""))
        {
            config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        }
        return Path.Combine(config!, "rosella", "dashboard-weather.json");
    }

    internal void Refresh()
    {
        if (locationPath is null)
        {
            return;
        }
        lock (_gate)
        {
            var now = DateTimeOffset.UtcNow;
            if (now < _refreshAfter)
            {
                return;
            }

            _refreshAfter = now.AddMinutes(15);
        }

        _ = Task.Run(UpdateAsync);
    }

    private async Task UpdateAsync()
    {
        WeatherLocation? location = null;
        try
        {
            location = File.Exists(locationPath) ? ReadLocation(File.ReadAllText(locationPath!)) : null;
            if (location is null)
            {
                _view = new(null, null, "Set a weather location");
                return;
            }

            var request = FormattableString.Invariant(
                $"https://api.open-meteo.com/v1/forecast?latitude={location.Latitude}&longitude={location.Longitude}&current=temperature_2m,apparent_temperature,weather_code,is_day&daily=temperature_2m_max,temperature_2m_min&forecast_days=1&timezone=auto");
            var forecast = ReadForecast(await Http.GetStringAsync(request).ConfigureAwait(false));
            _view = new(location.Name, forecast, "");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or HttpRequestException or
            OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or IndexOutOfRangeException)
        {
            var previous = _view;
            _view = new(location?.Name, location?.Name == previous.Location ? previous.Forecast : null, "Weather unavailable");
            lock (_gate)
            {
                _refreshAfter = DateTimeOffset.UtcNow.AddMinutes(2);
            }
        }
        finally
        {
            Dirty?.Invoke();
        }
    }

    internal static WeatherLocation? ReadLocation(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String || name.GetString()?.Trim() is not { Length: > 0 } label ||
            !root.TryGetProperty("latitude", out var latitude) || latitude.ValueKind != JsonValueKind.Number ||
            !root.TryGetProperty("longitude", out var longitude) || longitude.ValueKind != JsonValueKind.Number)
        {
            return null;
        }

        var lat = latitude.GetDouble();
        var lon = longitude.GetDouble();
        return lat is >= -90 and <= 90 && lon is >= -180 and <= 180 ? new(label, lat, lon) : null;
    }

    internal static WeatherForecast ReadForecast(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var current = root.GetProperty("current");
        var daily = root.GetProperty("daily");
        return new WeatherForecast(
            current.GetProperty("temperature_2m").GetDouble(),
            current.GetProperty("apparent_temperature").GetDouble(),
            daily.GetProperty("temperature_2m_max")[0].GetDouble(),
            daily.GetProperty("temperature_2m_min")[0].GetDouble(),
            current.GetProperty("weather_code").GetInt32(),
            current.GetProperty("is_day").GetInt32() == 1);
    }
}

internal sealed record WeatherLocation(string Name, double Latitude, double Longitude);
internal sealed record WeatherView(string? Location, WeatherForecast? Forecast, string Status);
internal sealed record WeatherForecast(double Temperature, double FeelsLike, double High, double Low, int Code, bool IsDay)
{
    internal static string Degrees(double value) => Math.Round(value).ToString(CultureInfo.CurrentCulture) + "°";

    internal string Conditions => Code switch
    {
        0 => IsDay ? "Sunny" : "Clear",
        1 or 2 => "Partly cloudy",
        3 => "Overcast",
        45 or 48 => "Fog",
        51 or 53 or 55 => "Drizzle",
        56 or 57 or 66 or 67 => "Freezing rain",
        61 or 63 or 65 => "Rain",
        71 or 73 or 75 or 77 or 85 or 86 => "Snow",
        80 or 81 or 82 => "Showers",
        95 or 96 or 99 => "Thunderstorms",
        _ => "Current conditions",
    };
}
