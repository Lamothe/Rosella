using System.Runtime.InteropServices;

namespace Rosella.Compositor.Tests;

/// <summary>
/// Pins FreeType 2's struct layouts to the x86_64 Linux ABI, the same way
/// the DRM structs are pinned. Rosella reads fields at these offsets by
/// hand, so a wrong offset here draws garbage instead of glyphs and the
/// layout tests are the only thing that would say so.
///
/// Values are from FreeType 2's public headers (include/freetype/freetype.h
/// and ftimage.h) on LP64: FT_Long/FT_Pos/FT_Fixed are 8 bytes, not 4.
/// </summary>
public class FreeTypeLayoutTests
{
    [Theory]

    // Sizes as literals, not the constants under test: a golden test that
    // asserts a constant against itself can never fail.
    [InlineData(typeof(FtFaceRec), 248)]
    [InlineData(typeof(FtGlyphSlotRec), 304)]
    [InlineData(typeof(FtBitmap), 40)]
    public void StructSizes_MatchFreeTypeAbi(Type type, int expectedSize)
    {
        Assert.Equal(expectedSize, Marshal.SizeOf(type));
    }

    [Theory]

    // FT_FaceRec: five FT_Longs, two pointers, an FT_Int, a pointer, an
    // FT_Int, a pointer, FT_Generic (16), FT_BBox (32), eight FT_Shorts —
    // then 2 bytes of padding to align the glyph slot pointer to 8.
    [InlineData(typeof(FtFaceRec), "Glyph", 152)]
    [InlineData(typeof(FtFaceRec), "Size", 160)]

    // FT_GlyphSlotRec: advance is FT_Vector at 128 (x is its first FT_Pos),
    // the bitmap follows at 152, then the two ink offsets.
    [InlineData(typeof(FtGlyphSlotRec), "AdvanceX", 128)]
    [InlineData(typeof(FtGlyphSlotRec), "Bitmap", 152)]
    [InlineData(typeof(FtGlyphSlotRec), "BitmapLeft", 192)]
    [InlineData(typeof(FtGlyphSlotRec), "BitmapTop", 196)]

    // FT_Bitmap: buffer is a pointer at 16 after the three width fields,
    // pixel_mode is a byte at 26 inside the trailing 2/1/1 group.
    [InlineData(typeof(FtBitmap), "Rows", 0)]
    [InlineData(typeof(FtBitmap), "Width", 4)]
    [InlineData(typeof(FtBitmap), "Pitch", 8)]
    [InlineData(typeof(FtBitmap), "Buffer", 16)]
    [InlineData(typeof(FtBitmap), "NumGrays", 24)]
    [InlineData(typeof(FtBitmap), "PixelMode", 26)]
    public void FieldOffsets_MatchFreeTypeAbi(Type type, string field, int expectedOffset)
    {
        Assert.Equal(expectedOffset, Marshal.OffsetOf(type, field).ToInt32());
    }

    [Fact]
    public void LoadFlagsAndPixelModes_MatchFreeTypeAbi()
    {
        // FT_RENDER_MODE_MONO is 2, not 1 — LIGHT sits at 1, and a wrong
        // value here would silently produce the wrong kind of strike.
        Assert.Equal(0x4, FreeType.LoadRender);
        Assert.Equal(0, FreeType.RenderModeNormal);
        Assert.Equal(2, FreeType.PixelModeGray);
    }
}
