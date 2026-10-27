using System.Runtime.InteropServices;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The embedded 8x16 font: glyph fidelity (the classic VGA 'A' is pinned so
/// an extraction mistake can never sneak in), scaling, clipping, and the
/// unknown-character fallback.
/// </summary>
public class BitmapFontTests
{
    // The classic VGA 8x16 'A': bit 7 is the leftmost pixel of each row.
    private static readonly byte[] ClassicA =
    [
        0x00, 0x00, 0x10, 0x38, 0x6C, 0xC6, 0xC6, 0xFE,
        0xC6, 0xC6, 0xC6, 0xC6, 0x00, 0x00, 0x00, 0x00,
    ];

    private static uint PixelAt(byte[] buffer, int stride, int x, int y) =>
        MemoryMarshal.Read<uint>(buffer.AsSpan((y * stride) + (x * 4)));

    [Fact]
    public void DrawText_RendersTheClassicA()
    {
        var buffer = new byte[8 * 16 * 4];
        BitmapFont.DrawText(buffer, 8, 16, 8 * 4, "A", 0, 0, 0xFFFFFFFF, scale: 1);

        for (var row = 0; row < 16; row++)
        {
            for (var column = 0; column < 8; column++)
            {
                var expected = (ClassicA[row] & (0x80 >> column)) != 0 ? 0xFFFFFFFFu : 0u;
                Assert.Equal(expected, PixelAt(buffer, 8 * 4, column, row));
            }
        }
    }

    [Fact]
    public void DrawText_ScaleTwoDrawsBlocks()
    {
        var buffer = new byte[16 * 32 * 4];
        BitmapFont.DrawText(buffer, 16, 32, 16 * 4, "A", 0, 0, 0xFFFFFFFF, scale: 2);

        // Row 2 of 'A' is 0x10 = pixel at column 3: a 2x2 block at (6..7, 4..5).
        Assert.Equal(0xFFFFFFFFu, PixelAt(buffer, 16 * 4, 6, 4));
        Assert.Equal(0xFFFFFFFFu, PixelAt(buffer, 16 * 4, 7, 5));
        Assert.Equal(0u, PixelAt(buffer, 16 * 4, 5, 4));
        Assert.Equal(0u, PixelAt(buffer, 16 * 4, 6, 3));
    }

    [Fact]
    public void DrawText_UnknownCharacterDrawsAsQuestionMark()
    {
        var unknown = new byte[8 * 16 * 4];
        var question = new byte[8 * 16 * 4];
        BitmapFont.DrawText(unknown, 8, 16, 8 * 4, "\u2603", 0, 0, 0xFFFFFFFF, scale: 1);
        BitmapFont.DrawText(question, 8, 16, 8 * 4, "?", 0, 0, 0xFFFFFFFF, scale: 1);

        Assert.Equal(question, unknown);
    }

    [Fact]
    public void DrawText_ClipsWithoutDrawingOutside()
    {
        var buffer = new byte[8 * 8 * 4];
        BitmapFont.DrawText(buffer, 8, 8, 8 * 4, "A", -3, -3, 0xFFFFFFFF, scale: 1);

        // Screen (x, y) shows glyph (y + 3, x + 3): columns 5..7 were pushed
        // off the span, every glyph row below 16 keeps drawing normally.
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++)
            {
                var inside = x < 5 && (ClassicA[y + 3] & (0x80 >> (x + 3))) != 0;
                Assert.Equal(inside ? 0xFFFFFFFFu : 0u, PixelAt(buffer, 8 * 4, x, y));
            }
        }
    }

    [Fact]
    public void Measure_UsesTheFixedGlyphBox()
    {
        Assert.Equal((24, 16), BitmapFont.Measure("abc", scale: 1));
        Assert.Equal((32, 32), BitmapFont.Measure("ab", scale: 2));
    }

    [Fact]
    public void Glyphs_TheWholeFontSurvived()
    {
        Assert.Equal(256 * 16, BitmapFont.Glyphs.Length);
    }
}
