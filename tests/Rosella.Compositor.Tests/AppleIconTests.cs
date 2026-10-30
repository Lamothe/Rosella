using System.Runtime.InteropServices;
using Rosella.Compositor;
using Xunit;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The Start button's apple. Every feature point below was read off a
/// rendered raster of the canonical silhouette (leaf tilted top-right,
/// bite out of the right flank, two bottom lobes with the dimple between
/// them) — these pins are what keeps the artwork from regressing into the
/// squashed blob again.
/// </summary>
public sealed class AppleIconTests
{
    [Fact]
    public void Body_IsSolidThroughItsCore()
    {
        Assert.True(AppleIcon.Inside(10, 13.3));
        Assert.True(AppleIcon.Inside(6.7, 10));
        Assert.True(AppleIcon.Inside(16, 6.7));
    }

    [Fact]
    public void Bite_ScoopsOutTheRightFlank()
    {
        // Above the bite the flank is solid; through it the edge recedes.
        Assert.True(AppleIcon.Inside(16, 6.7));
        Assert.False(AppleIcon.Inside(16.7, 10));
        Assert.False(AppleIcon.Inside(17.3, 10.7));
    }

    [Fact]
    public void Leaf_SitsTopRight_AndStandsApartFromTheBody()
    {
        Assert.True(AppleIcon.Inside(11, 2));
        Assert.True(AppleIcon.Inside(13, 0.33));

        // The gap between leaf tip and body, and the notch between the
        // body's shoulders, stay empty.
        Assert.False(AppleIcon.Inside(10.3, 4.67));
        Assert.False(AppleIcon.Inside(10, 5));
    }

    [Fact]
    public void Shoulders_RiseEitherSideOfTheNotch()
    {
        Assert.True(AppleIcon.Inside(13.3, 5));
    }

    [Fact]
    public void Bottom_HasTwoLobesAndTheDimpleBetweenThem()
    {
        Assert.True(AppleIcon.Inside(6.7, 19.3));
        Assert.True(AppleIcon.Inside(13.3, 19.3));
        Assert.False(AppleIcon.Inside(10, 19.3));
    }

    [Fact]
    public void Nothing_OutsideTheArt()
    {
        Assert.False(AppleIcon.Inside(0.2, 0.2));
        Assert.False(AppleIcon.Inside(19.5, 19.5));
    }

    [Fact]
    public void Fit_PreservesTheArtworkAspectRatio()
    {
        // The whole point of the rewrite: the art keeps its own shape
        // inside the square icon box instead of being stretched to fill it.
        // The silhouette is taller than wide and height-constrained, so it
        // spans the full box height and is centred with side margins.
        var (artWidth, artHeight) = AppleIcon.ArtSize;
        Assert.True(artWidth < artHeight, $"art {artWidth}×{artHeight} should be taller than wide");
        Assert.Equal(AppleIcon.Height, artHeight, 1e-9);

        var (inkX, _, inkW, inkH) = InkBounds(3);
        Assert.Equal((double)(AppleIcon.Height * 3), inkH, 2.0);
        Assert.Equal(artWidth * 3, inkW, 2.0);

        // Centred: left margin equals right margin (within AA fringe).
        Assert.Equal((double)inkX, (AppleIcon.Width * 3) - inkX - inkW, 2.0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Blend_RendersASolidCoreWithAntiAliasedEdges(int scale)
    {
        const uint Background = 0xFF16171E;
        var side = AppleIcon.Width * scale;
        var pixels = new byte[side * side * 4];
        Span<uint> words = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan());
        words.Fill(Background);
        AppleIcon.Blend(pixels, side, side, side * 4, 0, 0, scale, 0xFFE8E8ECu);

        // The core turns solid white…
        var core = words[(13 * scale * side) + (10 * scale)];
        Assert.Equal(0xFFE8E8ECu, core);

        // …while a pixel on the silhouette's edge blends part-way.
        var partial = 0;
        foreach (var word in words)
        {
            if (word is not (Background or 0xFFE8E8ECu))
            {
                partial++;
            }
        }

        Assert.True(partial > 0, "no anti-aliased edge pixels");
    }

    /// <summary>The bounding box of non-background ink at one output scale.</summary>
    private static (int X, int Y, int Width, int Height) InkBounds(int scale)
    {
        var side = AppleIcon.Width * scale;
        var pixels = new byte[side * side * 4];
        AppleIcon.Blend(pixels, side, side, side * 4, 0, 0, scale, 0xFFFFFFFFu);
        var minX = int.MaxValue;
        var minY = int.MaxValue;
        var maxX = int.MinValue;
        var maxY = int.MinValue;
        for (var y = 0; y < side; y++)
        {
            for (var x = 0; x < side; x++)
            {
                if (pixels[((y * side) + x) * 4] == 0)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x);
                maxY = Math.Max(maxY, y);
            }
        }

        return (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }
}
