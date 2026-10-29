using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>A rectangle in logical pixels.</summary>
internal readonly record struct Rect(int X, int Y, int W, int H)
{
    internal bool Contains(int x, int y) => x >= X && x < X + W && y >= Y && y < Y + H;

    /// <summary>The rectangle in output pixels on a scaled scanout.</summary>
    internal Rect Scaled(int scale) => new(X * scale, Y * scale, W * scale, H * scale);
}

/// <summary>
/// Rectangle primitives for the shell chrome, all clipped. Rectangles are
/// logical pixels; <c>scale</c> maps them onto the physical scanout.
/// </summary>
internal static class Chrome
{
    internal static void FillRect(Span<byte> pixels, int width, int height, int stride, Rect rect, uint colour, int scale = 1)
    {
        var scaled = rect.Scaled(scale);
        var rowPixels = MemoryMarshal.Cast<byte, uint>(pixels);
        var strideWords = stride / 4;
        for (var y = Math.Max(0, scaled.Y); y < Math.Min(height, scaled.Y + scaled.H); y++)
        {
            for (var x = Math.Max(0, scaled.X); x < Math.Min(width, scaled.X + scaled.W); x++)
            {
                rowPixels[(y * strideWords) + x] = colour;
            }
        }
    }

    internal static void FrameRect(Span<byte> pixels, int width, int height, int stride, Rect rect, uint colour, int scale = 1)
    {
        // Each 1-logical-pixel stroke becomes a scale-pixel-thick band.
        FillRect(pixels, width, height, stride, new Rect(rect.X, rect.Y, rect.W, 1), colour, scale);
        FillRect(pixels, width, height, stride, new Rect(rect.X, (rect.Y + rect.H) - 1, rect.W, 1), colour, scale);
        FillRect(pixels, width, height, stride, new Rect(rect.X, rect.Y, 1, rect.H), colour, scale);
        FillRect(pixels, width, height, stride, new Rect((rect.X + rect.W) - 1, rect.Y, 1, rect.H), colour, scale);
    }

    /// <summary>
    /// Filled rounded rectangle: the union of the two full-width/height
    /// bands and one disc per corner — exact, clipped, and crisp at any
    /// output scale like every other primitive here.
    /// </summary>
    internal static void FillRoundedRect(Span<byte> pixels, int width, int height, int stride, Rect rect, int radius, uint colour, int scale = 1)
    {
        if (rect.W <= 0 || rect.H <= 0)
        {
            return;
        }

        var r = Math.Clamp(radius, 0, Math.Min(rect.W, rect.H) / 2);
        if (r == 0)
        {
            FillRect(pixels, width, height, stride, rect, colour, scale);
            return;
        }

        FillRect(pixels, width, height, stride, new Rect(rect.X + r, rect.Y, rect.W - (2 * r), rect.H), colour, scale);
        FillRect(pixels, width, height, stride, new Rect(rect.X, rect.Y + r, rect.W, rect.H - (2 * r)), colour, scale);
        FillEllipse(pixels, width, height, stride, rect.X + r, rect.Y + r, r, r, colour, scale);
        FillEllipse(pixels, width, height, stride, (rect.X + rect.W) - 1 - r, rect.Y + r, r, r, colour, scale);
        FillEllipse(pixels, width, height, stride, rect.X + r, (rect.Y + rect.H) - 1 - r, r, r, colour, scale);
        FillEllipse(pixels, width, height, stride, (rect.X + rect.W) - 1 - r, (rect.Y + rect.H) - 1 - r, r, r, colour, scale);
    }

    /// <summary>
    /// Filled ellipse by logical centre and radii, clipped. The shape is
    /// sampled per logical pixel and drawn as scale-sized blocks, so it
    /// stays crisp on any output scale.
    /// </summary>
    internal static void FillEllipse(Span<byte> pixels, int width, int height, int stride, int cx, int cy, int rx, int ry, uint colour, int scale = 1)
    {
        rx = Math.Max(1, rx);
        ry = Math.Max(1, ry);
        var rowPixels = MemoryMarshal.Cast<byte, uint>(pixels);
        var strideWords = stride / 4;
        for (var y = cy - ry; y <= cy + ry; y++)
        {
            var dy = y - cy;
            var dx = (int)Math.Floor(rx * Math.Sqrt(Math.Max(0, 1 - ((double)(dy * dy) / (ry * ry)))));
            for (var x = cx - dx; x <= cx + dx; x++)
            {
                for (var sy = 0; sy < scale; sy++)
                {
                    var row = (y * scale) + sy;
                    if (row < 0 || row >= height)
                    {
                        continue;
                    }

                    for (var sx = 0; sx < scale; sx++)
                    {
                        var column = (x * scale) + sx;
                        if (column < 0 || column >= width)
                        {
                            continue;
                        }

                        rowPixels[(row * strideWords) + column] = colour;
                    }
                }
            }
        }
    }
}
