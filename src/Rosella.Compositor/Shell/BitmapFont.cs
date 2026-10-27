using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>
/// Text drawing on the scanout span, one font, one size scaled by an integer
/// factor. Pixels are uint 0xFFRRGGBB like the cursor sprite. Glyphs are
/// 8x16, bit 7 of each row byte is the leftmost pixel.
/// </summary>
internal static partial class BitmapFont
{
    internal const int GlyphWidth = 8;

    internal const int GlyphHeight = 16;

    /// <summary>The pixel size of one line of text at the given scale.</summary>
    internal static (int Width, int Height) Measure(string text, int scale) =>
        (text.Length * GlyphWidth * scale, GlyphHeight * scale);

    /// <summary>
    /// Draws text with its top-left corner at (x, y), clipped to the output.
    /// '\n' advances to the next line at the starting x.
    /// </summary>
    internal static void DrawText(Span<byte> pixels, int width, int height, int stride, string text, int x, int y, uint colour, int scale)
    {
        var originX = x;
        foreach (var character in text)
        {
            if (character == '\n')
            {
                x = originX;
                y += GlyphHeight * scale;
                continue;
            }

            DrawGlyph(pixels, width, height, stride, character, x, y, colour, scale);
            x += GlyphWidth * scale;
        }
    }

    private static void DrawGlyph(Span<byte> pixels, int width, int height, int stride, char character, int x, int y, uint colour, int scale)
    {
        // Latin-1 in the single-byte font; anything else draws as '?'.
        var glyph = character < 256 ? character : '?';
        var rowPixels = MemoryMarshal.Cast<byte, uint>(pixels);
        for (var row = 0; row < GlyphHeight; row++)
        {
            var bits = Glyphs[(glyph * GlyphHeight) + row];
            for (var column = 0; column < GlyphWidth; column++)
            {
                if ((bits & (0x80 >> column)) == 0)
                {
                    continue;
                }

                for (var dy = 0; dy < scale; dy++)
                {
                    var targetY = y + (row * scale) + dy;
                    if (targetY < 0 || targetY >= height)
                    {
                        continue;
                    }

                    for (var dx = 0; dx < scale; dx++)
                    {
                        var targetX = x + (column * scale) + dx;
                        if (targetX < 0 || targetX >= width)
                        {
                            continue;
                        }

                        rowPixels[(targetY * (stride / 4)) + targetX] = colour;
                    }
                }
            }
        }
    }
}
