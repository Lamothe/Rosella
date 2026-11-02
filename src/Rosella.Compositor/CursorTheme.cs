namespace Rosella.Compositor;

/// <summary>A pointer image in physical pixels, ready to blit at 1:1.</summary>
internal sealed record CursorBitmap(int Width, int Height, int HotspotX, int HotspotY, uint[] Pixels);

/// <summary>
/// Rosella's own pointer, drawn from the Xcursor theme GTK clients use.
/// Without this the desktop shows a hardcoded arrow while every client shows
/// its themed bitmap — two designs on one screen.
/// <para>
/// Resolution follows libXcursor and wl_cursor_theme_load, so the image is
/// the one Chrome uploads for the same theme and size: the theme's
/// <c>Inherits=</c> chain is searched in order, the shape is a file under
/// <c>cursors/</c>, and the image used is the one whose nominal size is
/// nearest the request (the first in file order on a tie), blitted at its
/// native size — client cursor bitmaps are never rescaled either. The
/// request is the logical cursor size times the output scale, which is the
/// physical size Chrome asks for.
/// </para>
/// </summary>
internal sealed class CursorTheme
{
    private readonly object _gate = new();
    private readonly List<string> _directories = [];
    private readonly Dictionary<string, List<XCursorImage>?> _files = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Shape, int Pixels), CursorBitmap?> _bitmaps = [];

    private CursorTheme(int size)
    {
        Size = size;
    }

    /// <summary>The logical cursor size (before the output scale).</summary>
    internal int Size { get; }

    /// <summary>
    /// Opens a theme by name. Returns null when no directory with a
    /// <c>cursors/</c> subdirectory exists in the search path — the caller
    /// then tries the next candidate, and finally the built-in arrow.
    /// </summary>
    internal static CursorTheme? Open(string name, int size, IReadOnlyList<string>? searchPath = null)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name is "." or "..")
        {
            return null;
        }

        var roots = searchPath ?? SearchPath();
        var directory = FindTheme(name, roots);
        if (directory is null)
        {
            return null;
        }

        var theme = new CursorTheme(Math.Clamp(size, 1, 512));
        theme.AddChain(directory, roots, []);
        return theme._directories.Count > 0 ? theme : null;
    }

    /// <summary>The default pointer at an output scale, or null if the theme lacks it.</summary>
    internal CursorBitmap? Pointer(int scale)
    {
        var pixels = Size * Math.Max(1, scale);
        return Image("left_ptr", pixels) ?? Image("default", pixels);
    }

    /// <summary>
    /// One shape at a physical pixel size. Missing shapes fall through the
    /// inheritance chain; a theme that never defines <c>left_ptr</c> can
    /// still rely on <c>default</c>, which is why Pointer tries both.
    /// The lock makes the first load safe from the input and render threads
    /// alike; afterwards every call is a cache hit.
    /// </summary>
    internal CursorBitmap? Image(string shape, int pixels)
    {
        if (pixels <= 0)
        {
            return null;
        }

        lock (_gate)
        {
            if (_bitmaps.TryGetValue((shape, pixels), out var cached))
            {
                return cached;
            }

            var bitmap = Load(shape, pixels);
            _bitmaps[(shape, pixels)] = bitmap;
            return bitmap;
        }
    }

    private CursorBitmap? Load(string shape, int pixels)
    {
        foreach (var directory in _directories)
        {
            var path = Path.Combine(directory, "cursors", shape);
            if (!_files.TryGetValue(path, out var images))
            {
                images = ReadFile(path);
                _files[path] = images;
            }

            if (images is not null)
            {
                return Native(images, pixels);
            }
        }

        return null;
    }

    private static List<XCursorImage>? ReadFile(string path)
    {
        try
        {
            // File.Exists follows symlinks: theme aliases are usually links
            // to another cursor file, and a dangling one must not throw.
            if (!File.Exists(path))
            {
                return null;
            }

            // The parser's dimension caps apply after the read, so bound the
            // read itself: a user-writable theme must not hand us a huge file.
            if (new FileInfo(path).Length > 8L * 1024 * 1024)
            {
                return null;
            }

            return XCursorFile.TryParse(File.ReadAllBytes(path), out var images) ? images : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// libXcursor's rule: nearest nominal size by absolute distance, first
    /// TOC entry wins a tie. The chosen image is used at its native size;
    /// wl_cursor_theme_load does not rescale either, so a theme that ships no
    /// image near the request yields a small pointer, exactly as in Chrome.
    /// </summary>
    private static CursorBitmap Native(List<XCursorImage> images, int pixels)
    {
        var chosen = images[0];
        foreach (var image in images)
        {
            if (Math.Abs(image.Size - pixels) < Math.Abs(chosen.Size - pixels))
            {
                chosen = image;
            }
        }

        return new CursorBitmap(chosen.Width, chosen.Height, chosen.HotspotX, chosen.HotspotY, chosen.Pixels);
    }

    private void AddChain(string directory, IReadOnlyList<string> roots, HashSet<string> visited)
    {
        if (!visited.Add(directory))
        {
            return;
        }

        _directories.Add(directory);
        foreach (var inherited in Inherits(directory))
        {
            if (FindTheme(inherited, roots) is { } next)
            {
                AddChain(next, roots, visited);
            }
        }
    }

    /// <summary>The theme directories to search, Xcursor's own defaults.</summary>
    private static List<string> SearchPath()
    {
        if (Environment.GetEnvironmentVariable("XCURSOR_PATH") is { Length: > 0 } configured)
        {
            return configured
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Select(ExpandHome)
                .Where(Directory.Exists)
                .ToList();
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } configuredHome
            ? configuredHome
            : Path.Combine(home, ".local", "share");
        var dataDirs = (Environment.GetEnvironmentVariable("XDG_DATA_DIRS") ?? "/usr/local/share:/usr/share")
            .Split(':', StringSplitOptions.RemoveEmptyEntries);

        var roots = new List<string> { Path.Combine(dataHome, "icons"), Path.Combine(home, ".icons") };
        roots.AddRange(dataDirs.Select(dir => Path.Combine(dir, "icons")));
        roots.Add("/usr/share/pixmaps");
        roots.Add("/usr/share/cursors/xorg-x11");
        return roots.Where(Directory.Exists).ToList();
    }

    private static string ExpandHome(string path) =>
        path.StartsWith('~')
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path.TrimStart('~', '/'))
            : path;

    private static string? FindTheme(string name, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.GetFileName(name) != name || name is "." or "..")
        {
            return null;
        }

        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, name);
            if (Directory.Exists(Path.Combine(candidate, "cursors")))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>The <c>Inherits=</c> names from a theme's index.theme, in order.</summary>
    private static IEnumerable<string> Inherits(string directory)
    {
        var path = Path.Combine(directory, "index.theme");
        string[] lines;
        try
        {
            if (!File.Exists(path))
            {
                return [];
            }

            lines = File.ReadAllLines(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("Inherits", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator < 0)
            {
                continue;
            }

            return trimmed[(separator + 1)..]
                .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        }

        return [];
    }
}