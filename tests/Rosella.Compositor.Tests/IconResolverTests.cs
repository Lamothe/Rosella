namespace Rosella.Compositor.Tests;

/// <summary>
/// Icon= name → file resolution against fixture directories: theme, size
/// and extension order, data-root priority, pixmaps fallback and
/// absolute paths.
/// </summary>
public class IconResolverTests : IDisposable
{
    private readonly string _rootA;
    private readonly string _rootB;
    private readonly string _pixmaps;

    public IconResolverTests()
    {
        _rootA = Path.Combine(Path.GetTempPath(), $"rosella-icons-a-{Guid.NewGuid():N}");
        _rootB = Path.Combine(Path.GetTempPath(), $"rosella-icons-b-{Guid.NewGuid():N}");
        _pixmaps = Path.Combine(Path.GetTempPath(), $"rosella-icons-p-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootA);
        Directory.CreateDirectory(_rootB);
        Directory.CreateDirectory(_pixmaps);
    }

    public void Dispose()
    {
        Directory.Delete(_rootA, recursive: true);
        Directory.Delete(_rootB, recursive: true);
        Directory.Delete(_pixmaps, recursive: true);
    }

    private string Plant(string root, params string[] parts)
    {
        var path = Path.Combine([root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "fixture");
        return path;
    }

    [Fact]
    public void AbsolutePath_PassesThroughWhenItExists()
    {
        var path = Plant(_rootA, "icon.png");
        Assert.Equal(path, IconResolver.Resolve(path, [_rootA], _pixmaps));
        Assert.Null(IconResolver.Resolve(Path.Combine(_rootA, "missing.png"), [_rootA], _pixmaps));
    }

    [Fact]
    public void FindsIconUnderThemeSizeAndAppsDirectory()
    {
        var path = Plant(_rootA, "icons", "hicolor", "48x48", "apps", "foo.png");
        Assert.Equal(path, IconResolver.Resolve("foo", [_rootA], _pixmaps));
    }

    [Fact]
    public void AdwaitaBeatsHicolorAndBiggerSizesComeFirst()
    {
        var adwaita = Plant(_rootA, "icons", "Adwaita", "48x48", "apps", "foo.png");
        Plant(_rootA, "icons", "hicolor", "256x256", "apps", "foo.png");
        Assert.Equal(adwaita, IconResolver.Resolve("foo", [_rootA], _pixmaps));

        var big = Plant(_rootB, "icons", "hicolor", "256x256", "apps", "bar.png");
        Plant(_rootB, "icons", "hicolor", "48x48", "apps", "bar.png");
        Assert.Equal(big, IconResolver.Resolve("bar", [_rootB], _pixmaps));
    }

    [Fact]
    public void PngBeatsSvgInOneDirectory()
    {
        var png = Plant(_rootA, "icons", "hicolor", "scalable", "apps", "foo.png");
        Plant(_rootA, "icons", "hicolor", "scalable", "apps", "foo.svg");
        Assert.Equal(png, IconResolver.Resolve("foo", [_rootA], _pixmaps));
    }

    [Fact]
    public void EarlierDataRootWins()
    {
        Plant(_rootA, "icons", "hicolor", "48x48", "apps", "foo.png");
        var second = Plant(_rootB, "icons", "hicolor", "48x48", "apps", "foo.png");

        var resolved = IconResolver.Resolve("foo", [_rootA, _rootB], _pixmaps);
        Assert.NotEqual(second, resolved);
        Assert.StartsWith(_rootA, resolved);
    }

    [Fact]
    public void FallsBackToPixmaps()
    {
        var path = Plant(_pixmaps, "foo.png");
        Assert.Equal(path, IconResolver.Resolve("foo", [_rootA], _pixmaps));
    }

    [Fact]
    public void FindsIconInEveryKnownSizeDirectory()
    {
        foreach (var size in new[] { "16x16", "22x22", "32x32", "96x96", "512x512" })
        {
            var name = $"i{size.Replace("x", string.Empty)}";
            var path = Plant(_rootA, "icons", "hicolor", size, "apps", $"{name}.png");
            Assert.Equal(path, IconResolver.Resolve(name, [_rootA], _pixmaps));
        }
    }

    [Fact]
    public void SizeDirectoriesSearch256FirstThenLargestToSmallest()
    {
        // The documented order: 256x256, 512x512, 128x128, 96x96, ...,
        // 16x16, scalable.
        var huge = Plant(_rootA, "icons", "hicolor", "512x512", "apps", "foo.png");
        Plant(_rootA, "icons", "hicolor", "96x96", "apps", "foo.png");
        Assert.Equal(huge, IconResolver.Resolve("foo", [_rootA], _pixmaps));

        var preferred = Plant(_rootA, "icons", "hicolor", "256x256", "apps", "bar.png");
        Plant(_rootA, "icons", "hicolor", "512x512", "apps", "bar.png");
        Assert.Equal(preferred, IconResolver.Resolve("bar", [_rootA], _pixmaps));

        var small = Plant(_rootA, "icons", "hicolor", "32x32", "apps", "baz.png");
        Plant(_rootA, "icons", "hicolor", "22x22", "apps", "baz.png");
        Assert.Equal(small, IconResolver.Resolve("baz", [_rootA], _pixmaps));
    }

    [Fact]
    public void IconNameEndingInAnExtension_ResolvesAsAFileName()
    {
        var path = Plant(_rootA, "icons", "hicolor", "48x48", "apps", "foo.png");
        Assert.Equal(path, IconResolver.Resolve("foo.png", [_rootA], _pixmaps));

        var svg = Plant(_rootA, "icons", "hicolor", "scalable", "apps", "bar.svg");
        Assert.Equal(svg, IconResolver.Resolve("bar.svg", [_rootA], _pixmaps));
    }

    [Fact]
    public void IconNameEndingInAnExtension_NeverGetsASecondExtension()
    {
        // foo.png.png sits in the first-priority spot, but Icon=foo.png
        // resolves to the literal foo.png file — never the double-extension
        // artefact.
        Plant(_rootA, "icons", "Adwaita", "256x256", "apps", "foo.png.png");
        var literal = Plant(_rootA, "icons", "hicolor", "48x48", "apps", "foo.png");
        Assert.Equal(literal, IconResolver.Resolve("foo.png", [_rootA], _pixmaps));
    }

    [Fact]
    public void IconNameEndingInAnExtension_FallsBackToPixmapsAsAFileName()
    {
        var path = Plant(_pixmaps, "foo.png");
        Assert.Equal(path, IconResolver.Resolve("foo.png", [_rootA], _pixmaps));
    }

    [Fact]
    public void MissingIconResolvesToNull()
    {
        Assert.Null(IconResolver.Resolve("nothing", [_rootA], _pixmaps));
        Assert.Null(IconResolver.Resolve("", [_rootA], _pixmaps));
        Assert.Null(IconResolver.Resolve(null, [_rootA], _pixmaps));
    }
}
