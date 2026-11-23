using System.Collections.Concurrent;
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
    private static readonly ConcurrentDictionary<(int Width, int Height), uint[]> Wallpapers = new();

    /// <summary>Installs the wallpaper (already scaled to the output size).</summary>
    internal static void SetWallpaper(uint[] pixels, int width, int height) =>
        Wallpapers[(width, height)] = pixels;

    /// <summary>Back to the bare gradient.</summary>
    internal static void ClearWallpaper() => Wallpapers.Clear();

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

    internal static void Render(Span<byte> pixels, int width, int height, int stride) =>
        RenderRegion(pixels, width, height, stride, DamageRect.For(width, height));

    /// <summary>
    /// Paints one region of the desktop (full-buffer coordinates; the
    /// gradient is a function of the row, so the region keeps its place in
    /// the fade instead of restarting at the region's top). Returns the
    /// pixels written, which is what damage tracking exists to shrink.
    /// </summary>
    internal static int RenderRegion(Span<byte> pixels, int width, int height, int stride, in DamageRect region)
    {
        var area = region.Intersect(DamageRect.For(width, height));
        if (area.IsEmpty)
        {
            return 0;
        }

        var rows = MemoryMarshal.Cast<byte, uint>(pixels);
        var strideWords = stride / 4;
        if (Wallpapers.TryGetValue((width, height), out var wallpaper))
        {
            // Scaled to the output already: one premultiplied row per row.
            for (var y = area.Y; y < area.Y + area.H; y++)
            {
                wallpaper.AsSpan((y * width) + area.X, area.W).CopyTo(rows.Slice((y * strideWords) + area.X, area.W));
            }

            return area.W * area.H;
        }

        for (var y = area.Y; y < area.Y + area.H; y++)
        {
            // The gradient is one solid colour per row: a span fill is the
            // whole job (JIT memset), not width scalar writes.
            rows.Slice((y * strideWords) + area.X, area.W).Fill(ColourAt(y, height));
        }

        return area.W * area.H;
    }
}
