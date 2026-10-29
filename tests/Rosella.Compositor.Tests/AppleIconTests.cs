using System.Runtime.InteropServices;
using Rosella.Compositor;
using Xunit;

namespace Rosella.Compositor.Tests;

public sealed class AppleIconTests
{
    [Fact]
    public void BodyIsSolidThroughItsCore()
    {
        Assert.True(AppleIcon.Inside(9.25, 12.7));
        Assert.True(AppleIcon.Inside(12, 8));
        Assert.True(AppleIcon.Inside(16, 13));
    }

    [Fact]
    public void BiteEmptiesTheUpperRightFlank()
    {
        Assert.False(AppleIcon.Inside(17.2, 9.4));
    }

    [Fact]
    public void DimpleEmptiesTheTopNotchAndTheStemRisesThroughIt()
    {
        // The dimple shows beside the stem's base; the stem itself rises
        // through the notch's right side.
        Assert.False(AppleIcon.Inside(8.3, 5.2));
        Assert.True(AppleIcon.Inside(10.3, 2.5));
    }

    [Fact]
    public void LeafSticksOutTopRight()
    {
        Assert.True(AppleIcon.Inside(13.2, 3.0));
    }

    [Fact]
    public void NothingOutsideTheArt()
    {
        Assert.False(AppleIcon.Inside(0.2, 0.2));
        Assert.False(AppleIcon.Inside(19.5, 19.5));
    }

    [Fact]
    public void StemFootStaysFusedToTheBody()
    {
        // The dimple's carve must not open a gap under the stem's foot: the
        // stem reaches past the carve, so the silhouette never shows a
        // detached stem (a hairline seam at HiDPI scales).
        Assert.True(AppleIcon.Inside(9.5, 6.35));
        Assert.True(AppleIcon.Inside(10.3, 5.8));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void BlendRendersASolidCoreWithAntiAliasedEdges(int scale)
    {
        const uint Background = 0xFF16171E;
        const uint Foreground = 0xFFE8E8EC;
        var width = AppleIcon.Width * scale;
        var height = AppleIcon.Height * scale;
        var stride = width * 4;
        var buffer = new byte[stride * height];
        MemoryMarshal.Cast<byte, uint>(buffer.AsSpan()).Fill(Background);

        AppleIcon.Blend(buffer, width, height, stride, 0, 0, scale, Foreground);

        var words = MemoryMarshal.Cast<byte, uint>(buffer);
        var solid = 0;
        var partial = 0;
        foreach (var word in words)
        {
            if (word == Foreground)
            {
                solid++;
            }
            else if (word != Background)
            {
                partial++;
            }
        }

        // A solid interior and genuinely anti-aliased edges at every scale.
        Assert.True(solid > 0, $"no solid pixels at scale {scale}");
        Assert.True(partial > 0, $"no anti-aliased edge pixels at scale {scale}");
    }
}
