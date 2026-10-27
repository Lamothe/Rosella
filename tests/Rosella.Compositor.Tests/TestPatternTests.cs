using System.Buffers.Binary;

namespace Rosella.Compositor.Tests;

public class TestPatternTests
{
    private const int Width = 800;
    private const int Height = 600;

    private static uint PixelAt(byte[] pixels, int x, int y)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(pixels.AsSpan((y * Width * 4) + (x * 4), 4));
    }

    [Fact]
    public void Render_DrawsEightBars()
    {
        var pixels = new byte[Width * Height * 4];
        TestPattern.Render(pixels, Width, Height, Width * 4, frame: 1000);

        // Frame 1000's column sits at x = 800 exactly, i.e. wraps to 0.
        Assert.Equal(0xFF000000u, PixelAt(pixels, 0, 0)); // column inverts white
        Assert.Equal(0xFFFFFFFFu, PixelAt(pixels, 12, 0)); // bar 0: white
        Assert.Equal(0xFFFFFF00u, PixelAt(pixels, 100, 0)); // bar 1: yellow
        Assert.Equal(0xFF00FFFFu, PixelAt(pixels, 200, 0)); // bar 2: cyan
        Assert.Equal(0xFF00FF00u, PixelAt(pixels, 300, 0)); // bar 3: green
        Assert.Equal(0xFFFF00FFu, PixelAt(pixels, 400, 0)); // bar 4: magenta
        Assert.Equal(0xFFFF0000u, PixelAt(pixels, 500, 0)); // bar 5: red
        Assert.Equal(0xFF0000FFu, PixelAt(pixels, 600, 0)); // bar 6: blue
        Assert.Equal(0xFF000000u, PixelAt(pixels, 799, 0)); // bar 7: black
    }

    [Fact]
    public void Render_ColumnAdvancesEachFrame()
    {
        var pixels = new byte[Width * Height * 4];
        TestPattern.Render(pixels, Width, Height, Width * 4, frame: 1);

        // Column width is max(4, 800/64) = 12; frame 1 puts it at x 12..23.
        Assert.Equal(0xFFFFFFFFu, PixelAt(pixels, 0, 0)); // untouched bar
        Assert.Equal(0xFF000000u, PixelAt(pixels, 12, 0)); // inverted white bar
        Assert.Equal(0xFFFFFFFFu, PixelAt(pixels, 24, 0)); // just past the column
    }

    [Fact]
    public void Render_LeavesStridePaddingUntouched()
    {
        var stride = (Width * 4) + 16;
        var pixels = new byte[stride * Height];
        Array.Fill(pixels, (byte)0xCC);

        TestPattern.Render(pixels, Width, Height, stride, frame: 0);

        for (var y = 0; y < Height; y++)
        {
            for (var i = Width * 4; i < stride; i++)
            {
                Assert.Equal(0xCC, pixels[(y * stride) + i]);
            }
        }
    }

    [Fact]
    public void Render_RejectsTooSmallSpan()
    {
        var pixels = new byte[(Width * Height * 4) - 4];
        Assert.Throws<ArgumentException>(() => TestPattern.Render(pixels, Width, Height, Width * 4, frame: 0));
    }
}
