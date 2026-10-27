using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>
/// The M3 test pattern: eight SMPTE-style colour bars with a colour-inverting
/// column that moves every frame, proving live page flips. Pure CPU rendering
/// into XRGB8888 pixels.
/// </summary>
internal static class TestPattern
{
    private static readonly uint[] Bars =
    [
        0xFFFFFFFF, // white
        0xFFFFFF00, // yellow
        0xFF00FFFF, // cyan
        0xFF00FF00, // green
        0xFFFF00FF, // magenta
        0xFFFF0000, // red
        0xFF0000FF, // blue
        0xFF000000, // black
    ];

    internal static void Render(Span<byte> pixels, int width, int height, int stride, long frame)
    {
        if (stride < width * 4 || (long)pixels.Length < (long)stride * height)
        {
            throw new ArgumentException("Pixel span is too small for the given geometry.");
        }

        for (var y = 0; y < height; y++)
        {
            var row = MemoryMarshal.Cast<byte, uint>(pixels.Slice(y * stride, width * 4));
            for (var x = 0; x < width; x++)
            {
                row[x] = Bars[(int)((long)x * Bars.Length / width)];
            }
        }

        // A column that inverts whatever bar it crosses, advancing each frame.
        var columnWidth = Math.Max(4, width / 64);
        var columnX = (int)((frame * columnWidth) % Math.Max(1, width));
        for (var y = 0; y < height; y++)
        {
            var row = MemoryMarshal.Cast<byte, uint>(pixels.Slice(y * stride, width * 4));
            for (var x = columnX; x < Math.Min(width, columnX + columnWidth); x++)
            {
                row[x] = ~row[x] | 0xFF000000;
            }
        }
    }
}
