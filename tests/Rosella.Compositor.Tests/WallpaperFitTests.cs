using Rosella.Compositor;
using Xunit;

namespace Rosella.Compositor.Tests;

/// <summary>
/// Cover-fitting the wallpaper: uniform scale, centre crop, bilinear
/// sampling. Geometry only — no files, no network.
/// </summary>
public sealed class WallpaperFitTests
{
    private const uint Red = 0xFFFF0000;
    private const uint Blue = 0xFF0000FF;

    [Fact]
    public void Scale_ExactFitCopiesTheSource()
    {
        var source = new uint[] { 1, 2, 3, 4 };
        var scaled = WallpaperFit.Scale(source, 2, 2, 2, 2);
        Assert.Equal(source, scaled);
    }

    [Fact]
    public void Scale_CoverCropsTheLongAxisCentred()
    {
        // 4×2 source (wide), 2×2 destination: scale=2, the crop window is
        // the centre two columns — the outer colours vanish entirely.
        var source = new uint[4 * 2];
        for (var y = 0; y < 2; y++)
        {
            source[(y * 4) + 0] = Red;
            source[(y * 4) + 1] = 2;
            source[(y * 4) + 2] = 3;
            source[(y * 4) + 3] = Blue;
        }

        var scaled = WallpaperFit.Scale(source, 4, 2, 2, 2);
        Assert.DoesNotContain(Red, scaled);
        Assert.DoesNotContain(Blue, scaled);
    }

    [Fact]
    public void Scale_CoverPicksTheCentreOfTheSource()
    {
        // 5×1 source into 1×1: scale=1, the window is one pixel wide and
        // lands exactly on the middle column.
        var source = new uint[] { 1, 2, 3, 4, 5 };
        var scaled = WallpaperFit.Scale(source, 5, 1, 1, 1);
        Assert.Equal(3u, scaled[0]);
    }

    [Fact]
    public void Scale_CoverCentresAnEvenCropBetweenColumns()
    {
        // 4×1 into 1×1: the window straddles columns 1 and 2 evenly, so the
        // sample lands exactly between values 2 and 3 (rounding may go
        // either way) — but never near 1 or 4.
        static uint Opaque(byte value) => 0xFF000000u | ((uint)value << 16) | ((uint)value << 8) | value;
        var scaled = WallpaperFit.Scale([Opaque(1), Opaque(2), Opaque(3), Opaque(4)], 4, 1, 1, 1);
        var red = (scaled[0] >> 16) & 0xFF;
        Assert.InRange(red, 2u, 3u);
    }

    [Fact]
    public void Scale_UpscaleInterpolatesBetweenNeighbours()
    {
        // 2×1 into 4×1: the middle destination pixels blend.
        var scaled = WallpaperFit.Scale(new uint[] { Red, Blue }, 2, 1, 4, 1);
        Assert.Equal(Red, scaled[0]);
        Assert.Equal(Blue, scaled[3]);

        // A middle pixel sits between the two endpoints on each channel.
        var middle = scaled[1];
        var red = (middle >> 16) & 0xFF;
        var blue = middle & 0xFF;
        Assert.InRange(red, 1u, 254u);
        Assert.InRange(blue, 1u, 254u);
    }

    [Fact]
    public void Scale_RejectsNonPositiveDimensions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WallpaperFit.Scale([1], 0, 1, 1, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => WallpaperFit.Scale([1], 1, 1, -1, 1));
    }
}
