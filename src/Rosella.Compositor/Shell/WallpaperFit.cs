namespace Rosella.Compositor;

/// <summary>
/// Fitting a photo to the screen: scale so it covers the whole output with
/// its aspect ratio untouched, and crop whatever hangs over the edge. Pure
/// geometry and pure pixels — bilinear sampling, premultiplied ARGB.
/// </summary>
internal static class WallpaperFit
{
    /// <summary>
    /// Scales <paramref name="source"/> (sw×sh) to exactly dw×dh the cover
    /// way: uniform scale = max(dw/sw, dh/sh), centre-cropped.
    /// </summary>
    internal static uint[] Scale(uint[] source, int sw, int sh, int dw, int dh)
    {
        if (sw <= 0 || sh <= 0 || dw <= 0 || dh <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(source), "wallpaper dimensions must be positive");
        }

        var destination = new uint[dw * dh];

        // The window of source the destination shows: the scaled source is
        // bigger than the destination on exactly one axis; centre the crop.
        var scale = Math.Max(dw / (double)sw, dh / (double)sh);
        var windowWidth = dw / scale;
        var windowHeight = dh / scale;
        var left = (sw - windowWidth) / 2;
        var top = (sh - windowHeight) / 2;

        for (var y = 0; y < dh; y++)
        {
            // Bilinear: sample at the centre of each destination pixel.
            var sourceY = top + (((y + 0.5) * windowHeight) / dh) - 0.5;
            var y0 = Clamp((int)Math.Floor(sourceY), 0, sh - 1);
            var y1 = Clamp(y0 + 1, 0, sh - 1);
            var ty = Clamp01(sourceY - y0);

            for (var x = 0; x < dw; x++)
            {
                var sourceX = left + (((x + 0.5) * windowWidth) / dw) - 0.5;
                var x0 = Clamp((int)Math.Floor(sourceX), 0, sw - 1);
                var x1 = Clamp(x0 + 1, 0, sw - 1);
                var tx = Clamp01(sourceX - x0);

                var top0 = Blend(source[(y0 * sw) + x0], source[(y0 * sw) + x1], tx);
                var bottom0 = Blend(source[(y1 * sw) + x0], source[(y1 * sw) + x1], tx);
                destination[(y * dw) + x] = Blend(top0, bottom0, ty);
            }
        }

        return destination;
    }

    private static int Clamp(int value, int low, int high) => Math.Max(low, Math.Min(high, value));

    private static double Clamp01(double value) => Math.Max(0, Math.Min(1, value));

    /// <summary>One bilinear step between two premultiplied pixels.</summary>
    private static uint Blend(uint from, uint to, double t)
    {
        if (t <= 0)
        {
            return from;
        }

        if (t >= 1)
        {
            return to;
        }

        var f = 1 - t;
        var a = (byte)((((from >> 24) & 0xFF) * f) + (((to >> 24) & 0xFF) * t));
        var r = (byte)((((from >> 16) & 0xFF) * f) + (((to >> 16) & 0xFF) * t));
        var g = (byte)((((from >> 8) & 0xFF) * f) + (((to >> 8) & 0xFF) * t));
        var b = (byte)(((from & 0xFF) * f) + ((to & 0xFF) * t));
        return ((uint)a << 24) | ((uint)r << 16) | ((uint)g << 8) | b;
    }
}
