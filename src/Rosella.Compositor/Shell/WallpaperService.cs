using System.Net.Http;

namespace Rosella.Compositor;

/// <summary>
/// The desktop picture: Bing's daily wallpaper for Australia, downloaded
/// once a day into the user's cache and painted cover-fit behind the
/// windows. The network never blocks startup — the calm gradient stands in
/// until the picture lands — and the cache means the wallpaper is there on
/// the next boot even offline. Opinionated: one picture a day, no settings.
/// </summary>
internal sealed class WallpaperService
{
    private readonly string _cacheDir;
    private readonly int _width;
    private readonly int _height;
    private readonly Action _changed;
    private readonly HttpClient _http;
    private readonly Action<string> _log;

    internal WallpaperService(
        string cacheDir,
        int width,
        int height,
        Action changed,
        HttpClient? http = null,
        Action<string>? log = null)
    {
        _cacheDir = cacheDir;
        _width = width;
        _height = height;
        _changed = changed;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _log = log ?? (message => Console.Error.WriteLine($"rosella: wallpaper: {message}"));
    }

    /// <summary>Where today's picture lives in the cache.</summary>
    internal static string CachePath(string cacheDir, DateTime day) =>
        Path.Combine(cacheDir, $"wallpaper-{day:yyyyMMdd}.jpg");

    /// <summary>
    /// Starts the background loop: today's picture (cache first, network
    /// after), then a fresh one whenever the date rolls over.
    /// </summary>
    internal void Start()
    {
        var thread = new Thread(() =>
        {
            // The loop must survive anything: a wallpaper bug is never
            // worth the session. (An unhandled exception on this thread
            // would take the whole process down.)
            try
            {
                var today = DateTime.Now.Date;
                EnsureDay(today);

                // Check for the date rolling over: the picture changes daily.
                while (true)
                {
                    Thread.Sleep(TimeSpan.FromMinutes(30));
                    var day = DateTime.Now.Date;
                    if (day != today)
                    {
                        today = day;
                        EnsureDay(day);
                    }
                }
            }
            catch (Exception exception)
            {
                _log($"wallpaper loop stopped: {exception.Message}");
            }
        })
        {
            IsBackground = true,
            Name = "rosella-wallpaper",
        };
        thread.Start();
    }

    /// <summary>Today's picture, downloading only what the cache lacks.</summary>
    internal void EnsureDay(DateTime day)
    {
        var path = CachePath(_cacheDir, day);
        if (!File.Exists(path) && !Download(day, path))
        {
            return;
        }

        if (Apply(path))
        {
            return;
        }

        // A truncated cache file (crash mid-write) decodes to nothing and
        // would suppress the picture for the whole day: clear it and fetch
        // a fresh copy.
        _log($"cache entry {Path.GetFileName(path)} is unusable; re-downloading");
        File.Delete(path);
        if (Download(day, path))
        {
            Apply(path);
        }
    }

    /// <summary>
    /// Downloads today's picture to <paramref name="path"/>. False on any
    /// failure — the gradient simply stays.
    /// </summary>
    internal bool Download(DateTime day, string path)
    {
        try
        {
            var json = _http.GetStringAsync(BingArchive.RequestUrl()).GetAwaiter().GetResult();
            var image = BingArchive.Parse(json);
            if (image is null)
            {
                _log("today's archive carried no image");
                return false;
            }

            var bytes = _http.GetByteArrayAsync(BingArchive.ImageUrl(image.UrlBase)).GetAwaiter().GetResult();
            if (bytes.Length == 0)
            {
                _log("today's picture came back empty");
                return false;
            }

            Directory.CreateDirectory(_cacheDir);

            // Write-then-move: a crash mid-write must leave no half-file
            // at the cache path (the next boot would trust its existence).
            var temporary = path + ".part";
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
            _log($"downloaded {image.Title} ({image.Copyright}) to {Path.GetFileName(path)}");
            return true;
        }
        catch (Exception exception)
        {
            // Any failure at all keeps the gradient; the wallpaper is a
            // luxury, never a reason to disturb the session.
            _log($"download failed: {exception.Message}");
            return false;
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

        var scaled = WallpaperFit.Scale(decoded.Pixels, decoded.Width, decoded.Height, _width, _height);
        Background.SetWallpaper(scaled, _width, _height);
        _changed();
        return true;
    }
}
