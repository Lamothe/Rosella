using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>
/// The one cursor: a classic arrow, hand-authored as pixel art. Opacity is
/// 1-bit (' ' transparent), the palette is black outline over white fill —
/// deliberately not configurable.
/// </summary>
internal static class CursorSprite
{
    private static readonly string[] Art =
    [
        "K               ",
        "KK              ",
        "KWK             ",
        "KWWK            ",
        "KWWWK           ",
        "KWWWWK          ",
        "KWWWWWK         ",
        "KWWWWWWK        ",
        "KWWWWWWWK       ",
        "KWWWWWWWWK      ",
        "KWWWWWKKKKK     ",
        "KWWKWWK         ",
        "KWK.KWWK        ",
        "KK..KWWK        ",
        "K....KWWK       ",
        ".....KWWK       ",
        "......KK        ",
    ];

    internal static int Width => Art[0].Length;

    internal static int Height => Art.Length;

    /// <summary>Draws the arrow with its hotspot at (x, y), clipped to the output.</summary>
    internal static void Draw(Span<byte> pixels, int width, int height, int stride, int x, int y)
    {
        for (var row = 0; row < Art.Length; row++)
        {
            var targetY = y + row;
            if (targetY < 0 || targetY >= height)
            {
                continue;
            }

            var rowPixels = MemoryMarshal.Cast<byte, uint>(pixels.Slice(targetY * stride, width * 4));
            for (var column = 0; column < Art[row].Length; column++)
            {
                var targetX = x + column;
                if (targetX < 0 || targetX >= width)
                {
                    continue;
                }

                rowPixels[targetX] = Art[row][column] switch
                {
                    'K' => 0xFF000000,
                    'W' => 0xFFFFFFFF,
                    _ => rowPixels[targetX],
                };
            }
        }
    }
}
