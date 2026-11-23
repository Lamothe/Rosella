using System.Runtime.InteropServices;

namespace Rosella.Compositor;

// FreeType 2's C ABI, laid out exactly as x86_64 C lays it out. Only the
// handful of fields Rosella reads are declared, at their real offsets — the
// structs are explicit-layout precisely so a wrong offset cannot hide in
// automatic padding. Offsets are pinned by golden tests in
// Rosella.Compositor.Tests/FreeTypeLayoutTests.cs; if those fail, the ABI
// has been violated and every glyph drawn here is suspect.
//
// The library is loaded by name (libfreetype.so.6), which every Linux
// desktop already has because GTK, Qt and every toolkit's text goes through
// it. A host without it simply has no text: the shell falls back to its
// bitmap font rather than failing to start.

/// <summary>FT_FaceRec (freetype.h): only the fields Rosella reads.</summary>
[StructLayout(LayoutKind.Explicit, Size = FaceRecSize)]
internal struct FtFaceRec
{
    /// <summary>sizeof(FT_FaceRec) on LP64.</summary>
    internal const int FaceRecSize = 248;

    /// <summary>FT_GlyphSlot glyph: the one slot the face renders into.</summary>
    [FieldOffset(152)]
    internal nint Glyph;

    /// <summary>FT_Size size: holds the 26.6 metrics for the current ppem.</summary>
    [FieldOffset(160)]
    internal nint Size;
}

/// <summary>FT_GlyphSlotRec (freetype.h): the fields Rosella reads.</summary>
[StructLayout(LayoutKind.Explicit, Size = GlyphSlotRecSize)]
internal struct FtGlyphSlotRec
{
    /// <summary>sizeof(FT_GlyphSlotRec) on LP64.</summary>
    internal const int GlyphSlotRecSize = 304;

    /// <summary>FT_Vector advance.x: pen advance, 26.6 fixed point.</summary>
    [FieldOffset(128)]
    internal long AdvanceX;

    /// <summary>FT_Bitmap bitmap: the rendered coverage.</summary>
    [FieldOffset(152)]
    internal FtBitmap Bitmap;

    /// <summary>int bitmap_left: pen to left edge of the ink, in pixels.</summary>
    [FieldOffset(192)]
    internal int BitmapLeft;

    /// <summary>int bitmap_top: baseline to top edge of the ink, in pixels.</summary>
    [FieldOffset(196)]
    internal int BitmapTop;
}

/// <summary>FT_Bitmap (ftimage.h): a rendered glyph's coverage map.</summary>
[StructLayout(LayoutKind.Explicit, Size = BitmapSize)]
internal struct FtBitmap
{
    /// <summary>sizeof(FT_Bitmap) on LP64.</summary>
    internal const int BitmapSize = 40;

    [FieldOffset(0)]
    internal uint Rows;

    [FieldOffset(4)]
    internal uint Width;

    /// <summary>Bytes per row; negative means bottom-up rows.</summary>
    [FieldOffset(8)]
    internal int Pitch;

    /// <summary>The coverage bytes: one byte per pixel for a grey glyph.</summary>
    [FieldOffset(16)]
    internal nint Buffer;

    /// <summary>256 for a normal anti-aliased grey glyph.</summary>
    [FieldOffset(24)]
    internal ushort NumGrays;

    /// <summary>FT_PIXEL_MODE_GRAY (2) for anti-aliased, MONO (1) for bitmap.</summary>
    [FieldOffset(26)]
    internal byte PixelMode;
}

/// <summary>
/// The FreeType entry points Rosella uses. Every signature is the C
/// prototype with LP64 widths: FT_Long/FT_Pos are 8 bytes here, not 4.
/// </summary>
internal static partial class FreeType
{
    private const string Library = "libfreetype.so.6";

    // FT_LOAD_* (freetype.h). Hinting is off deliberately: hinted advances
    // are rounded at each pixel size, so a word measured at one size does not
    // match the same word drawn at another. Unhinted advances scale exactly
    // with the em, which is what keeps the bar's layout and its drawing in
    // step — and it is how macOS renders its text too.
    internal const int LoadNoHinting = 0x2;
    internal const int LoadRender = 0x4;

    /// <summary>The load flags for every glyph Rosella draws.</summary>
    internal const int LoadForText = LoadRender | LoadNoHinting;

    // FT_RENDER_MODE_* (freetype.h): note MONO is 2, not 1 — LIGHT is 1.
    internal const int RenderModeNormal = 0;

    // FT_PIXEL_MODE_* (ftimage.h): GRAY is 2, MONO is 1.
    internal const byte PixelModeGray = 2;

    [LibraryImport(Library, EntryPoint = "FT_Init_FreeType")]
    internal static partial int Init(out nint library);

    [LibraryImport(Library, EntryPoint = "FT_Done_FreeType")]
    internal static partial int Done(nint library);

    [LibraryImport(Library, EntryPoint = "FT_New_Face", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial int NewFace(nint library, string path, nint faceIndex, out nint face);

    [LibraryImport(Library, EntryPoint = "FT_Done_Face")]
    internal static partial int DoneFace(nint face);

    [LibraryImport(Library, EntryPoint = "FT_Set_Pixel_Sizes")]
    internal static partial int SetPixelSizes(nint face, uint pixelWidth, uint pixelHeight);

    [LibraryImport(Library, EntryPoint = "FT_Load_Char")]
    internal static partial int LoadChar(nint face, ulong charCode, int loadFlags);
}
