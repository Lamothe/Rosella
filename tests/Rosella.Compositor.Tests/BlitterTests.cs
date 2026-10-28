namespace Rosella.Compositor.Tests;

public class BlitterTests
{
    [Fact]
    public void Copy_CopiesAtPosition()
    {
        var target = new byte[16 * 16 * 4];
        var source = new byte[4 * 4 * 4];
        for (var i = 0; i < source.Length; i += 4)
        {
            source[i] = 0x11;
        }

        Blitter.Copy(target, 16, 16, 16 * 4, source, 4, 4, x: 8, y: 8);

        Assert.Equal(0x11, target[(8 * 16 * 4) + (8 * 4)]);
        Assert.Equal(0x11, target[(11 * 16 * 4) + (11 * 4)]);
        Assert.Equal(0x00, target[(7 * 16 * 4) + (7 * 4)]);
    }

    [Fact]
    public void Copy_ClipsAtTargetEdges()
    {
        var target = new byte[8 * 8 * 4];
        var source = new byte[4 * 4 * 4];
        for (var i = 0; i < source.Length; i += 4)
        {
            source[i] = 0x22;
        }

        // Partially off the left and bottom edges.
        Blitter.Copy(target, 8, 8, 8 * 4, source, 4, 4, x: -2, y: 6);

        Assert.Equal(0x22, target[(6 * 8 * 4) + 0]); // column 0 came from source column 2
        Assert.Equal(0x22, target[(7 * 8 * 4) + (1 * 4)]);
        Assert.Equal(0x00, target[(5 * 8 * 4)]); // above the copy
    }

    [Fact]
    public void Copy_OffscreenDoesNothing()
    {
        var target = new byte[8 * 8 * 4];
        var source = new byte[4 * 4 * 4];
        source[0] = 0x33;

        Blitter.Copy(target, 8, 8, 8 * 4, source, 4, 4, x: 20, y: 20);
        Blitter.Copy(target, 8, 8, 8 * 4, source, 4, 4, x: -20, y: -20);
        Assert.All(target, value => Assert.Equal(0x00, value));
    }

    [Fact]
    public void Copy_LeavesTargetStridePaddingUntouched()
    {
        var stride = (4 * 4) + 8;
        var target = new byte[stride * 4];
        Array.Fill(target, (byte)0xCC);
        var source = new byte[4 * 4 * 4];

        Blitter.Copy(target, 4, 4, stride, source, 4, 4, x: 0, y: 0);

        for (var y = 0; y < 4; y++)
        {
            for (var i = 4 * 4; i < stride; i++)
            {
                Assert.Equal(0xCC, target[(y * stride) + i]);
            }
        }
    }

    // The scaled/blend path (column-map refactor) is pinned against the old
    // per-pixel reference formula: behaviour must be identical.

    private static uint ReferencePixel(uint[] source, int sourceWidth, int sourceHeight, uint format, int x, int y, int destinationWidth, int destinationHeight, uint target)
    {
        var sourceColumn = (int)(((long)x * sourceWidth) / destinationWidth);
        var sourceRow = (int)(((long)y * sourceHeight) / destinationHeight);
        var sourceColour = source[(sourceRow * sourceWidth) + sourceColumn];
        if (format == ShmFormats.Xrgb8888)
        {
            return sourceColour;
        }

        var alpha = sourceColour >> 24;
        if (alpha == 0)
        {
            return target;
        }

        if (alpha == 255)
        {
            return sourceColour;
        }

        var inverse = 255 - alpha;
        var red = ((sourceColour >> 16) & 0xFF) + ((((target >> 16) & 0xFF) * inverse) / 255);
        var green = ((sourceColour >> 8) & 0xFF) + ((((target >> 8) & 0xFF) * inverse) / 255);
        var blue = (sourceColour & 0xFF) + (((target & 0xFF) * inverse) / 255);
        return 0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue;
    }

    [Theory]
    [InlineData(ShmFormats.Xrgb8888)]
    [InlineData(ShmFormats.Argb8888)]
    public void Blit_Scaled_MatchesTheReferenceFormula(uint format)
    {
        const int sourceWidth = 3;
        const int sourceHeight = 2;
        const int targetWidth = 7;
        const int targetHeight = 5;
        var sourceColours = new uint[] { 0x80FF0000, 0xFF00FF00, 0x400000FF, 0xFFFFFFFF, 0x00000000, 0xC0123456 };
        var source = new byte[sourceWidth * sourceHeight * 4];
        sourceColours.AsSpan().CopyTo(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(source.AsSpan()));
        var target = new byte[targetWidth * targetHeight * 4];
        var targetColours = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(target.AsSpan());
        for (var i = 0; i < targetColours.Length; i++)
        {
            targetColours[i] = 0xFF303030;
        }

        // Odd size and a negative origin: scaling plus clipping in one go.
        Blitter.Blit(target, targetWidth, targetHeight, targetWidth * 4, source, sourceWidth, sourceHeight, sourceWidth * 4, format, -1, -1, 9, 7);

        for (var y = 0; y < targetHeight; y++)
        {
            for (var x = 0; x < targetWidth; x++)
            {
                // Same coordinate frame as the reference: origin at (-1, -1).
                var expected = ReferencePixel(sourceColours, sourceWidth, sourceHeight, format, x + 1, y + 1, 9, 7, 0xFF303030);
                Assert.True(expected == targetColours[(y * targetWidth) + x], $"pixel {x},{y}");
            }
        }
    }

    [Fact]
    public void Blit_ScaledXrgb_IsOpaqueEvenWithUndefinedAlpha()
    {
        var source = new byte[2 * 2 * 4];
        var colours = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(source.AsSpan());
        colours.Fill(0x00AABBCC); // alpha byte undefined for xrgb
        var target = new byte[4 * 4 * 4];

        Blitter.Blit(target, 4, 4, 4 * 4, source, 2, 2, 2 * 4, ShmFormats.Xrgb8888, 0, 0, 4, 4);

        var targetColours = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(target.AsSpan());
        Assert.Equal(0x00AABBCCu, targetColours[0]);
        Assert.Equal(0x00AABBCCu, targetColours[(3 * 4) + 3]);
    }
}
