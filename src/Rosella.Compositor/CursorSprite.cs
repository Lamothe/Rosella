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

    /// <summary>
    /// Draws the arrow with its hotspot at (x, y), clipped to the output.
    /// Each art pixel becomes a scale × scale block on the scanout.
    /// </summary>
    internal static void Draw(Span<byte> pixels, int width, int height, int stride, int x, int y, int scale = 1)
    {
        for (var row = 0; row < Art.Length; row++)
        {
            for (var column = 0; column < Art[row].Length; column++)
        {
                var colour = Art[row][column] switch
                {
                    'K' => 0xFF000000u,
                    'W' => 0xFFFFFFFFu,
                    _ => 0u,
                };
                if (colour == 0)
                {
                    continue;
                }

                var blockX = x + (column * scale);
                var blockY = y + (row * scale);
                Chrome.FillRect(pixels, width, height, stride, new Rect(blockX, blockY, scale, scale), colour);
            }
        }
    }
}
