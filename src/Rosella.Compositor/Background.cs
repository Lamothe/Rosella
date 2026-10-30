using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>
/// The desktop background: one calm dark blue-grey gradient, replaced by
/// the wallpaper when one is loaded (cover-fitted to the output). No test
/// pattern, no movement — the desktop is not a test rig.
/// </summary>
internal static class Background
{
    /// <summary>The wallpaper in output pixels, if one is loaded.</summary>
    private static volatile WallpaperImage? _wallpaper;

    /// <summary>Installs the wallpaper (already scaled to the output size).</summary>
    internal static void SetWallpaper(uint[] pixels, int width, int height) =>
        _wallpaper = new WallpaperImage(pixels, width, height);

    /// <summary>Back to the bare gradient.</summary>
    internal static void ClearWallpaper() => _wallpaper = null;

    private sealed record WallpaperImage(uint[] Pixels, int Width, int Height);

    /// <summary>The gradient colour at a row: shared by drawing and tests.</summary>
    internal static uint ColourAt(int y, int height)
    {
        // Top (34, 44, 58) fading to (24, 30, 40): a quiet dark desktop.
        var t = height <= 1 ? 0 : y / (double)(height - 1);
        var r = (byte)(34 - (10 * t));
        var g = (byte)(44 - (14 * t));
        var b = (byte)(58 - (18 * t));
        return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | b;
    }

    internal static void Render(Span<byte> pixels, int width, int height, int stride)
    {
        if (_wallpaper is { } wallpaper && wallpaper.Width == width && wallpaper.Height == height)
        {
            // Scaled to the output already: one premultiplied row per row.
            var rows = MemoryMarshal.Cast<byte, uint>(pixels);
            var strideWords = stride / 4;
            for (var y = 0; y < height; y++)
            {
                wallpaper.Pixels.AsSpan(y * width, width).CopyTo(rows.Slice(y * strideWords, width));
            }

            return;
        }

        var rowPixels = MemoryMarshal.Cast<byte, uint>(pixels);
        var strideWords2 = stride / 4;
        for (var y = 0; y < height; y++)
        {
            // The gradient is one solid colour per row: a span fill is the
            // whole job (JIT memset), not width scalar writes.
            rowPixels.Slice(y * strideWords2, width).Fill(ColourAt(y, height));
        }
    }
}
