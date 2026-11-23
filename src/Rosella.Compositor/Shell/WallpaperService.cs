using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Rosella.Compositor;

/// <summary>
/// The desktop picture: Bing's daily wallpaper for Australia, cached by
/// its archive date and painted cover-fit behind the
/// windows. The network never blocks startup — the calm gradient stands in
/// until the picture lands — and the cache means the wallpaper is there on
/// the next boot even offline. Opinionated: one picture a day, no settings.
/// </summary>
internal sealed class WallpaperService
{
    private readonly string _cacheDir;
    private readonly (int Width, int Height)[] _sizes;
    private readonly Action _changed;
    private readonly HttpClient _http;
    private readonly Action<string> _log;
    private string? _appliedPath;

    internal WallpaperService(
        string cacheDir,
        int width,
        int height,
        Action changed,
        HttpClient? http = null,
        Action<string>? log = null,
        (int Width, int Height)? secondarySize = null)
    {
        _cacheDir = cacheDir;
        _sizes = secondarySize is { } other && other != (width, height) ? [(width, height), other] : [(width, height)];
        _changed = changed;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _log = log ?? (message => Console.Error.WriteLine($"rosella: wallpaper: {message}"));
    }

    internal static string CachePath(string cacheDir, DateOnly day) =>
        Path.Combine(cacheDir, $"bing-au-{day.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.jpg");

    internal void Start()
    {
        var thread = new Thread(() =>
        {
            ApplyCached();
            while (true)
            {
                Refresh();
                Thread.Sleep(TimeSpan.FromMinutes(30));
            }
        })
        {
            IsBackground = true,
            Name = "rosella-wallpaper",
        };
        thread.Start();
    }

    internal void ApplyCached()
    {
        try
        {
            if (!Directory.Exists(_cacheDir))
            {
                return;
            }

            var candidates = Directory.EnumerateFiles(_cacheDir, "bing-au-*.jpg").OrderDescending(StringComparer.Ordinal)
                .Concat(Directory.EnumerateFiles(_cacheDir, "wallpaper-*.jpg").OrderDescending(StringComparer.Ordinal));
            foreach (var path in candidates)
            {
                if (Apply(path))
                {
                    _appliedPath = path;
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            _log($"could not load cached wallpaper: {exception.Message}");
        }
    }

    internal void Refresh()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BingArchive.RequestUrl());
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = _http.SendAsync(request).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            var image = BingArchive.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
            if (image is null || !DateOnly.TryParseExact(image.StartDate, "yyyyMMdd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var day))
            {
                _log("Bing did not return a dated wallpaper");
                return;
            }

            // Bing publishes on its own schedule. An archive response before
            // rollover can still be yesterday's image.
            var path = CachePath(_cacheDir, day);
            if (_appliedPath is { } current && Path.GetFileName(current).StartsWith("bing-au-", StringComparison.Ordinal) &&
                string.CompareOrdinal(path, current) <= 0)
            {
                return;
            }

            if (File.Exists(path) && Apply(path))
            {
                _appliedPath = path;
                return;
            }

            var bytes = _http.GetByteArrayAsync(BingArchive.ImageUrl(image.UrlBase)).GetAwaiter().GetResult();
            Directory.CreateDirectory(_cacheDir);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".part";
            try
            {
                File.WriteAllBytes(temporary, bytes);
                if (Apply(temporary))
                {
                    File.Move(temporary, path, overwrite: true);
                    _appliedPath = path;
                    _log($"applied Bing image {image.StartDate}: {image.Title}");
                }
                else
                {
                    _log("Bing returned an unusable image");
                }
            }
            finally
            {
                File.Delete(temporary);
            }
        }
        catch (Exception exception)
        {
            _log($"refresh failed: {exception.Message}");
        }
    }

    /// <summary>
    /// Decodes a cached picture, cover-fits it to the output and hands it
    /// to the background. False when the file is no usable image.
    /// </summary>
    internal bool Apply(string path)
    {
        var decoded = IconLoader.LoadNatural(path);
        if (decoded is null)
        {
            return false;
        }

        foreach (var (width, height) in _sizes)
        {
            var scaled = WallpaperFit.Scale(decoded.Pixels, decoded.Width, decoded.Height, width, height);
            Background.SetWallpaper(scaled, width, height);
        }
        _changed();
        return true;
    }
}
