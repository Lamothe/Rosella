using System.Net;
using System.Text;
using Rosella.Compositor;
using Xunit;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The wallpaper service: daily cache, download on miss, decode and hand
/// to the background. The network is a fake; the decode is the same
/// gdk-pixbuf path the icons use. Disposes by clearing the static
/// background so the gradient tests never see leftover wallpaper.
/// </summary>
[Collection("Background")]
public sealed class WallpaperServiceTests : IDisposable
{
    [Fact]
    public void CachePath_EncodesTheDay()
    {
        var path = WallpaperService.CachePath("/cache/rosella", new DateTime(2026, 10, 30));
        Assert.Equal(Path.Combine("/cache/rosella", "wallpaper-20261030.jpg"), path);
    }

    [Fact]
    public void Download_WritesThePictureToTheCache()
    {
        var handler = new FakeHandler
        {
            Archive = """{"images":[{"urlbase":"/th?id=OHR.X","title":"Stack"}]}""",
            Picture = "picture-bytes"u8.ToArray(),
        };
        var directory = NewCacheDir();
        var service = NewService(directory, handler, out _);

        var path = WallpaperService.CachePath(directory, DateTime.Now.Date);
        Assert.True(service.Download(DateTime.Now.Date, path));
        Assert.Equal("picture-bytes"u8.ToArray(), File.ReadAllBytes(path));
        Assert.Contains("mkt=en-AU", handler.Requested[0]);
    }

    [Fact]
    public void Download_FailureLeavesNoFile()
    {
        var handler = new FakeHandler { Fail = true };
        var directory = NewCacheDir();
        var service = NewService(directory, handler, out _);

        var path = WallpaperService.CachePath(directory, DateTime.Now.Date);
        Assert.False(service.Download(DateTime.Now.Date, path));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void EnsureDay_UsesTheCacheWithoutTouchingTheNetwork()
    {
        var handler = new FakeHandler { Fail = true };
        var directory = NewCacheDir();
        var service = NewService(directory, handler, out var probe);
        var path = WallpaperService.CachePath(directory, DateTime.Now.Date);
        File.WriteAllBytes(path, File.ReadAllBytes(Fixture("icon.png")));

        service.EnsureDay(DateTime.Now.Date);

        Assert.Empty(handler.Requested);
        Assert.True(probe.Changed);
    }

    [Fact]
    public void Apply_DecodesScalesAndPaintsBehindTheWindows()
    {
        var directory = NewCacheDir();
        var service = NewService(directory, new FakeHandler(), out _);

        service.Apply(Fixture("icon.png"));

        // The background now paints the wallpaper, not the gradient.
        var pixels = new byte[8 * 8 * 4];
        Background.Render(pixels, 8, 8, 8 * 4);
        Assert.NotEqual(Background.ColourAt(0, 8), BitConverter.ToUInt32(pixels, 0));
    }

    [Fact]
    public void Apply_LeavesTheGradientWhenTheFileIsNotAnImage()
    {
        var directory = NewCacheDir();
        var service = NewService(directory, new FakeHandler(), out _);
        var garbage = Path.Combine(directory, "garbage.jpg");
        File.WriteAllText(garbage, "definitely not an image");

        service.Apply(garbage);

        var pixels = new byte[8 * 8 * 4];
        Background.Render(pixels, 8, 8, 8 * 4);
        Assert.Equal(Background.ColourAt(0, 8), BitConverter.ToUInt32(pixels, 0));
    }

    public void Dispose() => Background.ClearWallpaper();

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    private static string NewCacheDir()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"rosella-wallpaper-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static WallpaperService NewService(string directory, FakeHandler handler, out ChangedProbe probe)
    {
        var captured = new ChangedProbe();
        probe = captured;
        return new WallpaperService(
            directory,
            8,
            8,
            () => captured.Changed = true,
            new HttpClient(handler),
            _ => { });
    }

    private sealed class ChangedProbe
    {
        public bool Changed { get; set; }
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        public string Archive { get; set; } = """{"images":[]}""";

        public byte[] Picture { get; set; } = [];

        public bool Fail { get; set; }

        public List<string> Requested { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!.ToString());
            if (Fail)
            {
                throw new HttpRequestException("network is down");
            }

            var body = request.RequestUri!.ToString().Contains("HPImageArchive", StringComparison.Ordinal)
                ? Encoding.UTF8.GetBytes(Archive)
                : Picture;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            });
        }
    }
}
