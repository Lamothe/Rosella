using System.Runtime.InteropServices;

namespace Rosella.Compositor.Tests;

/// <summary>
/// Icon decoding through gdk-pixbuf against the shipped fixtures: PNG
/// must decode into premultiplied ARGB with rounding, padded pixbuf rows
/// must not over-read the last row, and SVG depends on the host's pixbuf
/// SVG loader, so it may decode or fall back gracefully. These are decode
/// tests: they require libgdk_pixbuf and fail loudly without it, never
/// passing vacuously on a fallback branch.
/// </summary>
public class IconLoaderTests
{
    private const string PixbufRequired = "libgdk_pixbuf-2.0 required for icon decode tests";

    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    [Fact]
    public void PngFixture_DecodesToPremultipliedArgb()
    {
        Assert.True(IconLoader.Available, PixbufRequired);

        var icon = IconLoader.Load(Fixture("icon.png"), 4);

        Assert.NotNull(icon);
        Assert.Equal(4, icon.Width);
        Assert.Equal(4, icon.Height);

        // The fixture: opaque red everywhere, half-transparent white at
        // (3, 3) — straight RGBA in, premultiplied out. Bilinear scaling
        // at 1:1 should be exact; allow a hair of rounding anyway.
        AssertColour(0xFFFF0000u, icon.Pixels[0]);
        AssertColour(0x80808080u, icon.Pixels[(3 * 4) + 3]);
    }

    [Fact]
    public void SvgFixture_DecodesOrFallsBackGracefully()
    {
        Assert.True(IconLoader.Available, PixbufRequired);

        var icon = IconLoader.Load(Fixture("icon.svg"), 4);

        // With the SVG loader installed this decodes; without it the load
        // fails cleanly and the taskbar draws the fallback tile instead.
        if (icon is not null)
        {
            Assert.Equal(4, icon.Width);
            Assert.Equal(4, icon.Height);
            AssertColour(0xFFFF0000u, icon.Pixels[0]);
        }
    }

    [Fact]
    public void MissingFile_DecodesToNull()
    {
        Assert.True(IconLoader.Available, PixbufRequired);

        // With the library present the null here proves the missing-file
        // path, not a missing library.
        Assert.Null(IconLoader.Load(Path.Combine(AppContext.BaseDirectory, "Fixtures", "missing.png"), 4));
    }

    [Fact]
    public void PngWithPaddedRows_DecodesExactPixelsIncludingTheLastRow()
    {
        Assert.True(IconLoader.Available, PixbufRequired);

        // The 6x6 RGB fixture scaled to 3x3: the scaled pixbuf is 3 wide
        // with 3 channels, so gdk-pixbuf pads its rows to rowstride 12
        // while only 9 bytes per row are real data — and the last row is
        // not padded at all. Each 2x2 source block is one solid colour, so
        // every decoded pixel must be exactly its block colour, last row
        // included: no phantom pixels past the real data.
        var icon = IconLoader.Load(Fixture("icon-rgb-6x6.png"), 3);

        Assert.NotNull(icon);
        Assert.Equal(3, icon.Width);
        Assert.Equal(3, icon.Height);
        Assert.Equal(
            new uint[]
            {
                0xFF141E28u, 0xFF501E50u, 0xFF8C1E78u,
                0xFF145A50u, 0xFF505A78u, 0xFF8C5AA0u,
                0xFF149678u, 0xFF5096A0u, 0xFF8C96C8u,
            },
            icon.Pixels);

        // The last row, spelled out: exactly the three real pixels.
        Assert.Equal(0xFF149678u, icon.Pixels[6]);
        Assert.Equal(0xFF5096A0u, icon.Pixels[7]);
        Assert.Equal(0xFF8C96C8u, icon.Pixels[8]);
    }

    [Fact]
    public void PixelByteCount_TheLastRowIsNotPadded()
    {
        // A 3x3 3-channel pixbuf has rowstride 12 (rows padded to 4-byte
        // boundaries) but only (2 * 12) + (3 * 3) = 33 readable bytes: the
        // classic rowstride * height = 36 copy over-reads the heap by the
        // last row's padding (gdk_pixbuf_get_pixels documents this).
        Assert.Equal(33, IconLoader.PixelByteCount(12, 3, 3, 3));
    }

    [Fact]
    public void CopyRawPixels_PaddedRows_CopyExactlyTheReadableExtent()
    {
        // The same padded 3x3 layout:33 readable bytes, then nothing. The
        // destination is exactly the safe extent, so a rowstride * height
        // copy (36 bytes) cannot fit and the copy fails loudly instead of
        // over-reading the pixel buffer.
        var source = Marshal.AllocHGlobal(33);
        try
        {
            var pattern = Enumerable.Range(0, 33).Select(i => (byte)(i * 7)).ToArray();
            Marshal.Copy(pattern, 0, source, pattern.Length);

            var dest = new byte[33];
            IconLoader.CopyRawPixels(source, dest, 12, 3, 3, 3);

            Assert.Equal(pattern, dest);
        }
        finally
        {
            Marshal.FreeHGlobal(source);
        }
    }

    [Fact]
    public void PremultipliedPixels_RoundInsteadOfTruncating()
    {
        Assert.True(IconLoader.Available, PixbufRequired);

        // The 2x2 fixture's half-alpha pixels carry faint channels where
        // rounding and truncation disagree: (1, 2, 3, 128) premultiplies
        // to (1, 1, 2) with (c * a + 127) / 255 but (0, 1, 1) with
        // truncating c * a / 255. Values are exact: 1:1 decoding is a
        // straight copy.
        var icon = IconLoader.Load(Fixture("icon-rgba-2x2.png"), 2);

        Assert.NotNull(icon);
        Assert.Equal(2, icon.Width);
        Assert.Equal(2, icon.Height);
        Assert.Equal(0x80010102u, icon.Pixels[0]);
        Assert.Equal(0x80808080u, icon.Pixels[1]);
        Assert.Equal(0x80000000u, icon.Pixels[2]);
        Assert.Equal(0xFF020202u, icon.Pixels[3]);
    }

    private static void AssertColour(uint expected, uint actual)
    {
        static int Channel(uint colour, int shift) => (int)((colour >> shift) & 0xFF);
        Assert.True(
            Math.Abs(Channel(expected, 24) - Channel(actual, 24)) <= 4
            && Math.Abs(Channel(expected, 16) - Channel(actual, 16)) <= 4
            && Math.Abs(Channel(expected, 8) - Channel(actual, 8)) <= 4
            && Math.Abs(Channel(expected, 0) - Channel(actual, 0)) <= 4,
            $"expected 0x{expected:X8}, got 0x{actual:X8}");
    }
}
