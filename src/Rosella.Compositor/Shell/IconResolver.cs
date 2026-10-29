namespace Rosella.Compositor;

/// <summary>
/// Icon= name to file: absolute paths pass through, bare names are looked
/// up in the icon themes (Adwaita then hicolor, size directories in a
/// documented priority order, PNG before SVG) under each XDG data root,
/// then /usr/share/pixmaps. An Icon= that already ends in an image
/// extension resolves as that file name — never with a second extension
/// appended.
/// </summary>
internal static class IconResolver
{
    private static readonly string[] Themes = ["Adwaita", "hicolor"];

    /// <summary>
    /// Size directories in priority order: 256x256 first (the best raster
    /// fit for the bar's icon decode), then the remaining sizes largest to
    /// smallest; scalable last, since vectors fit any box but raster PNGs
    /// decode cheaper and more predictably.
    /// </summary>
    private static readonly string[] Sizes =
        ["256x256", "512x512", "128x128", "96x96", "64x64", "48x48", "32x32", "22x22", "16x16", "scalable"];

    private static readonly string[] Extensions = ["png", "svg", "svgz", "xpm"];
    private static readonly string[] PixmapsExtensions = ["png", "xpm", "svg"];

    /// <summary>Resolves an icon name to a file path, or null when nothing matches.</summary>
    internal static string? Resolve(string? iconName, IReadOnlyList<string> dataRoots, string pixmapsRoot = "/usr/share/pixmaps")
    {
        if (string.IsNullOrWhiteSpace(iconName))
        {
            return null;
        }

        if (iconName.StartsWith('/'))
        {
            return File.Exists(iconName) ? iconName : null;
        }

        // An Icon= that already names a file resolves as that exact file
        // name in the theme directories (then the pixmaps fallback) — the
        // extension is never appended twice.
        if (Extensions.Any(extension => iconName.EndsWith($".{extension}", StringComparison.OrdinalIgnoreCase)))
        {
            foreach (var root in dataRoots)
            {
                foreach (var theme in Themes)
                {
                    foreach (var size in Sizes)
                    {
                        var path = Path.Combine(root, "icons", theme, size, "apps", iconName);
                        if (File.Exists(path))
                        {
                            return path;
                        }
                    }
                }
            }

            var pixmap = Path.Combine(pixmapsRoot, iconName);
            return File.Exists(pixmap) ? pixmap : null;
        }

        foreach (var root in dataRoots)
        {
            foreach (var theme in Themes)
            {
                foreach (var size in Sizes)
                {
                    foreach (var extension in Extensions)
                    {
                        var path = Path.Combine(root, "icons", theme, size, "apps", $"{iconName}.{extension}");
                        if (File.Exists(path))
                        {
                            return path;
                        }
                    }
                }
            }
        }

        foreach (var extension in PixmapsExtensions)
        {
            var path = Path.Combine(pixmapsRoot, $"{iconName}.{extension}");
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }
}
