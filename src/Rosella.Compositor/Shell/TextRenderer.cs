using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>
/// One rasterised glyph: its coverage map (0 = transparent, 255 = solid)
/// and the pen metrics that place it and space it from the next glyph.
/// <see cref="Advance26"/> is FreeType's own 26.6 pen move, kept at full
/// precision so a run of glyphs can be summed before rounding — rounding
/// each one separately makes measured text and drawn text disagree.
/// </summary>
internal sealed record Glyph(int Width, int Height, int Left, int Top, long Advance26, byte[] Coverage)
{
    /// <summary>The pen move in whole pixels, rounded to nearest.</summary>
    internal int Advance => (int)((Advance26 + 32) >> 6);
}

/// <summary>
/// Text as it should look: real glyphs from a real font, proportionally
/// spaced and anti-aliased, rasterised through FreeType — the same library
/// every toolkit on the machine uses. This replaces the old hand-drawn
/// 8×16 bitmap font, which was blocky and monospaced.
///
/// One font, no settings: the most Segoe-like face on the machine (Open
/// Sans leads), otherwise the first sans that loads. Glyphs are cached per
/// character and size, so drawing the clock every second costs nothing
/// after the first. A machine with no FreeType and no font keeps the old
/// bitmap font rather than losing its text.
/// </summary>
internal sealed class TextRenderer : IDisposable
{
    /// <summary>
    /// Faces to try, best first. All are ordinary desktop fonts; the first
    /// that loads is the one Rosella uses, and there is no setting. Open
    /// Sans leads because its humanist, Segoe-like proportions suit the
    /// Windows-style shell; Cantarell is only the fallback.
    /// </summary>
    private static readonly string[] PreferredFonts =
    [
        "OpenSans-Regular.ttf",
        "OpenSans-Regular.otf",
        "Inter-Regular.otf",
        "Inter.ttf",
        "LiberationSans-Regular.ttf",
        "Cantarell-Regular.otf",
        "Cantarell-VF.otf",
        "Cantarell.ttf",
        "DejaVuSans.ttf",
    ];

    private readonly nint _library;
    private readonly nint _face;

    /// <summary>
    /// Guards FreeType and the caches together. An FT_Face is single-threaded
    /// by contract, and the shell's text is reachable from the input readers
    /// as well as the render loop, so every entry point serialises here.
    /// </summary>
    private readonly object _gate = new();

    private readonly Dictionary<(char Character, int PixelSize), Glyph> _glyphs = new();
    private readonly Dictionary<int, int> _lineHeights = new();
    private readonly Dictionary<int, int> _ascents = new();
    private bool _disposed;

    private TextRenderer(nint library, nint face)
    {
        _library = library;
        _face = face;
    }

    /// <summary>
    /// Opens the preferred font that exists on this machine, or null when
    /// none of FreeType, a font file, or a scalable face is available.
    /// </summary>
    internal static TextRenderer? Load()
    {
        if (!OperatingSystem.IsLinux())
        {
            return null;
        }

        nint library = 0;
        try
        {
            if (FreeType.Init(out library) != 0 || library == 0)
            {
                return null;
            }

            foreach (var path in CandidateFonts())
            {
                if (FreeType.NewFace(library, path, 0, out var face) != 0 || face == 0)
                {
                    continue;
                }

                Console.WriteLine($"rosella: text from {path}");
                return new TextRenderer(library, face);
            }
        }
        catch (DllNotFoundException)
        {
            // No FreeType on this host; the caller falls back.
        }
        catch (EntryPointNotFoundException)
        {
            // A FreeType too old to have the entry points we need.
        }

        if (library != 0)
        {
            FreeType.Done(library);
        }

        return null;
    }

    /// <summary>Existing font files in preference order, wherever they live.</summary>
    private static IEnumerable<string> CandidateFonts()
    {
        string[] roots = ["/usr/share/fonts", "/usr/local/share/fonts"];
        var found = new List<string>();
        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                found.AddRange(Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories));
            }
            catch (Exception exception) when (exception is UnauthorizedAccessException or IOException)
            {
                // A directory we cannot read simply contributes nothing.
            }
        }

        foreach (var preferred in PreferredFonts)
        {
            foreach (var path in found)
            {
                if (string.Equals(Path.GetFileName(path), preferred, StringComparison.OrdinalIgnoreCase))
                {
                    yield return path;
                }
            }
        }
    }

    /// <summary>
    /// The baseline offset from the top of a line at this size: where the pen
    /// sits so capitals land inside the caller's box. Derived from real
    /// glyphs rather than the raw ppem, so it matches what is drawn.
    /// </summary>
    internal int Ascent(int pixelSize)
    {
        lock (_gate)
        {
            return AscentLocked(pixelSize);
        }
    }

    /// <summary><see cref="Ascent"/>, for callers already holding the gate.</summary>
    private int AscentLocked(int pixelSize)
    {
        LineHeightLocked(pixelSize);
        _ascents.TryGetValue(pixelSize, out var ascent);
        return ascent;
    }

    /// <summary>
    /// The pixel height of one line at this size: the ascent of capitals plus
    /// the descent of a lowercase descender, so callers can centre text in a
    /// rect and advance to the next line without clipping either.
    /// </summary>
    internal int LineHeight(int pixelSize)
    {
        lock (_gate)
        {
            return LineHeightLocked(pixelSize);
        }
    }

    /// <summary><see cref="LineHeight"/>, for callers already holding the gate.</summary>
    private int LineHeightLocked(int pixelSize)
    {
        if (_lineHeights.TryGetValue(pixelSize, out var cached))
        {
            return cached;
        }

        var ascent = GlyphFor('M', pixelSize)?.Top ?? pixelSize;
        var descentGlyph = GlyphFor('g', pixelSize);
        var descent = descentGlyph is null ? 0 : Math.Max(0, descentGlyph.Height - descentGlyph.Top);
        var height = Math.Max(pixelSize, ascent + descent);

        _ascents[pixelSize] = ascent;
        _lineHeights[pixelSize] = height;
        return height;
    }

    /// <summary>The pixel size of <paramref name="text"/> drawn at this size.</summary>
    internal (int Width, int Height) Measure(string text, int pixelSize)
    {
        lock (_gate)
        {
            // Sum at full precision and round once: rounding each glyph's
            // advance separately would make the measured width disagree with
            // the drawn width by up to a pixel per character.
            long advance26 = 0;
            foreach (var character in text)
            {
                if (character == '\n')
                {
                    continue;
                }

                advance26 += GlyphFor(character, pixelSize)?.Advance26 ?? (pixelSize / 2L) << 6;
            }

            return ((int)((advance26 + 32) >> 6), LineHeightLocked(pixelSize));
        }
    }

    /// <summary>
    /// Draws text with the top of its line box at (x, y), clipped to the
    /// output — the same corner the old bitmap font used, so callers need no
    /// baseline arithmetic. '\n' advances to the next line at the starting x.
    /// The glyphs blend by coverage, so edges are smooth rather than stepped.
    /// </summary>
    internal void DrawText(Span<byte> pixels, int width, int height, int stride, string text, int x, int y, uint colour, int pixelSize)
    {
        var rowPixels = MemoryMarshal.Cast<byte, uint>(pixels);
        var stridePixels = stride / 4;

        lock (_gate)
        {
            // The pen runs along the baseline, which sits Ascent below the top
            // of the line box the caller named.
            var baseline = y + AscentLocked(pixelSize);
            var originX = x;

            // The pen accumulates at 26.6 and is rounded per glyph, exactly
            // as Measure rounds the total: what is measured is what is drawn.
            long advance26 = 0;
            foreach (var character in text)
            {
                if (character == '\n')
                {
                    advance26 = 0;
                    baseline += LineHeightLocked(pixelSize);
                    continue;
                }

                var glyph = GlyphFor(character, pixelSize);
                if (glyph is not null)
                {
                    var penX = originX + (int)((advance26 + 32) >> 6);
                    BlendGlyph(rowPixels, stridePixels, width, height, glyph, penX, baseline, colour);
                }

                advance26 += glyph?.Advance26 ?? ((pixelSize / 2L) << 6);
            }
        }
    }

    /// <summary>Source-over of one glyph's coverage map, clipped to the output.</summary>
    private static void BlendGlyph(
        Span<uint> rowPixels,
        int stridePixels,
        int clipWidth,
        int clipHeight,
        Glyph glyph,
        int penX,
        int penY,
        uint colour)
    {
        // FreeType's bitmap is described by the pen: ink starts Left pixels
        // right of the pen and Top pixels above the baseline.
        var originX = penX + glyph.Left;
        var originY = penY - glyph.Top;

        var sourceRed = (colour >> 16) & 0xFF;
        var sourceGreen = (colour >> 8) & 0xFF;
        var sourceBlue = colour & 0xFF;

        for (var row = 0; row < glyph.Height; row++)
        {
            var targetY = originY + row;
            if (targetY < 0 || targetY >= clipHeight)
            {
                continue;
            }

            for (var column = 0; column < glyph.Width; column++)
            {
                var targetX = originX + column;
                if (targetX < 0 || targetX >= clipWidth)
                {
                    continue;
                }

                var coverage = glyph.Coverage[(row * glyph.Width) + column];
                if (coverage == 0)
                {
                    continue;
                }

                var index = (targetY * stridePixels) + targetX;
                if (coverage == 255)
                {
                    rowPixels[index] = colour;
                    continue;
                }

                var destination = rowPixels[index];
                var destinationRed = (destination >> 16) & 0xFF;
                var destinationGreen = (destination >> 8) & 0xFF;
                var destinationBlue = destination & 0xFF;

                var inverse = 255 - coverage;
                var red = ((sourceRed * coverage) + (destinationRed * inverse)) / 255;
                var green = ((sourceGreen * coverage) + (destinationGreen * inverse)) / 255;
                var blue = ((sourceBlue * coverage) + (destinationBlue * inverse)) / 255;
                rowPixels[index] = 0xFF000000 | ((uint)red << 16) | ((uint)green << 8) | (uint)blue;
            }
        }
    }

    /// <summary>
    /// The glyph for a character at a size, rasterised once and cached. Null
    /// only when the character has no outline (e.g. a space, handled by its
    /// advance) or the size cannot be set.
    /// </summary>
    private Glyph? GlyphFor(char character, int pixelSize)
    {
        var key = (character, pixelSize);
        if (_glyphs.TryGetValue(key, out var cached))
        {
            return cached;
        }

        if (_disposed)
        {
            return null;
        }

        Glyph? glyph = null;
        if (FreeType.SetPixelSizes(_face, 0, (uint)pixelSize) == 0 && FreeType.LoadChar(_face, character, FreeType.LoadForText) == 0)
        {
            glyph = ReadSlot();
        }

        // Cache misses too: a failed character must not be retried forever.
        _glyphs[key] = glyph!;
        return glyph;
    }

    /// <summary>Copies the slot's bitmap and metrics into a managed glyph.</summary>
    private unsafe Glyph? ReadSlot()
    {
        // FT_Face -> FT_GlyphSlot, at offsets pinned by the layout tests.
        var facePointer = (byte*)_face;
        var slotPointer = *(nint*)(facePointer + 152);
        if (slotPointer == 0)
        {
            return null;
        }

        ref var slot = ref Unsafe.AsRef<FtGlyphSlotRec>((void*)slotPointer);
        ref var bitmap = ref slot.Bitmap;
        if (bitmap.Width == 0 || bitmap.Rows == 0)
        {
            // A space: no ink, but the pen still advances.
            return new Glyph(0, 0, 0, 0, slot.AdvanceX, []);
        }

        if (bitmap.PixelMode != FreeType.PixelModeGray)
        {
            // Only grey coverage is drawn; a mono strike would need its own
            // unpacker and no preferred font produces one here.
            return null;
        }

        var coverage = new byte[checked((int)(bitmap.Width * bitmap.Rows))];
        var pitch = Math.Abs(bitmap.Pitch);
        for (var row = 0; row < bitmap.Rows; row++)
        {
            var sourceRow = bitmap.Pitch >= 0 ? row : (bitmap.Rows - 1) - row;
            var source = (byte*)bitmap.Buffer + (sourceRow * pitch);
            new ReadOnlySpan<byte>(source, (int)bitmap.Width).CopyTo(coverage.AsSpan((int)(row * bitmap.Width)));
        }

        return new Glyph(
            (int)bitmap.Width,
            (int)bitmap.Rows,
            slot.BitmapLeft,
            slot.BitmapTop,
            slot.AdvanceX,
            coverage);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_face != 0)
        {
            FreeType.DoneFace(_face);
        }

        if (_library != 0)
        {
            FreeType.Done(_library);
        }
    }
}
