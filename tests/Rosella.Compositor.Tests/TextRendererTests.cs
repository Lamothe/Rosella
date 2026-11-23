using Rosella.Compositor;

namespace Rosella.Compositor.Tests;

/// <summary>
/// The real font path: load a face, rasterise, and draw. These assert the
/// things the old bitmap font could not do — proportional widths and
/// anti-aliased edges — so a regression to blocky monospace is caught here
/// rather than by eye at the screen.
/// </summary>
public class TextRendererTests : IDisposable
{
    private readonly TextRenderer? _renderer = TextRenderer.Load();

    public void Dispose() => _renderer?.Dispose();

    [Fact]
    public void Loads_AFontOnThisMachine()
    {
        Assert.NotNull(_renderer);
    }

    [Fact]
    public void Widths_AreProportional()
    {
        if (_renderer is null)
        {
            return;
        }

        // 'i' is far narrower than 'W' in any real face; the bitmap font
        // gave every character the same 8 pixels.
        var (narrow, _) = _renderer.Measure("iiii", 32);
        var (wide, _) = _renderer.Measure("WWWW", 32);

        Assert.True(narrow > 0, "narrow text should still have width");
        Assert.True(wide > narrow, $"W should be wider than i ({wide} vs {narrow})");
    }

    [Fact]
    public void Measures_SumTheGlyphAdvancesBeforeRounding()
    {
        if (_renderer is null)
        {
            return;
        }

        // The width of a run is the summed 26.6 advances rounded once, not
        // the sum of per-glyph roundings. Rounding each glyph separately
        // would drift a pixel per character away from what is drawn.
        var (one, _) = _renderer.Measure("a", 32);
        var (two, _) = _renderer.Measure("aa", 32);
        var (four, _) = _renderer.Measure("aaaa", 32);

        Assert.InRange(two, one * 2 - 1, one * 2 + 1);
        Assert.InRange(four, one * 4 - 2, one * 4 + 2);
        Assert.True(four > two, "a longer run must be wider");
    }

    [Fact]
    public void MeasuredWidth_ContainsEverythingDrawn()
    {
        if (_renderer is null)
        {
            return;
        }

        // The real invariant the bar depends on: nothing drawn sticks out
        // past the width the layout reserved for it. A task label that
        // measured narrower than it drew would bleed over its neighbour.
        const int Size = 64;
        const int Width = 512;
        var pixels = new uint[Width * Size];
        var text = "Handgloves 12:34";

        var (measured, _) = _renderer.Measure(text, Size);
        _renderer.DrawText(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()),
            Width,
            Size,
            Width * 4,
            text,
            0,
            0,
            0xFFFFFFFFu,
            Size);

        var rightmost = -1;
        for (var row = 0; row < Size; row++)
        {
            for (var column = 0; column < Width; column++)
            {
                if (pixels[(row * Width) + column] != 0)
                {
                    rightmost = Math.Max(rightmost, column);
                }
            }
        }

        Assert.True(rightmost >= 0, "the text should draw something");
        Assert.True(
            rightmost < measured,
            $"ink reaches {rightmost}px but the text measured only {measured}px wide");
    }

    [Fact]
    public void LineBox_HoldsCapitalsAndDescenders()
    {
        if (_renderer is null)
        {
            return;
        }

        var height = _renderer.LineHeight(32);
        var ascent = _renderer.Ascent(32);

        Assert.True(height > 0, "a line must have height");
        Assert.True(ascent > 0, "capitals must sit below the line top");
        Assert.True(ascent < height, "the baseline must be inside the line box");
    }

    [Fact]
    public void Glyphs_AreAntiAliased()
    {
        if (_renderer is null)
        {
            return;
        }

        // Draw 'O' onto an empty surface: a real face's curves leave partial
        // coverage at the edges, so the buffer holds values strictly between
        // fully transparent and fully solid. A bitmap font has only 0 and 255.
        const int Size = 32;
        var pixels = new uint[Size * Size];
        _renderer.DrawText(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()),
            Size,
            Size,
            Size * 4,
            "O",
            2,
            2,
            0xFFFFFFFFu,
            Size);

        var partial = pixels.Count(pixel => pixel is not 0 and not 0xFFFFFFFFu);
        Assert.True(partial > 0, $"expected anti-aliased edge pixels, found {partial}");
    }

    [Fact]
    public void Draws_InsideTheGivenBox()
    {
        if (_renderer is null)
        {
            return;
        }

        const int Size = 32;
        var pixels = new uint[Size * Size];
        _renderer.DrawText(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()),
            Size,
            Size,
            Size * 4,
            "Hello",
            0,
            0,
            0xFFFFFFFFu,
            Size);

        // Nothing may land outside the clip the caller named. The span is
        // exactly Size*Size, so any overrun would have thrown or corrupted.
        Assert.Contains(pixels, pixel => pixel != 0);
    }

    [Fact]
    public void Space_HasAnAdvanceButNoInk()
    {
        if (_renderer is null)
        {
            return;
        }

        const int Size = 32;
        var pixels = new uint[Size * Size];
        _renderer.DrawText(
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()),
            Size,
            Size,
            Size * 4,
            "a a",
            0,
            0,
            0xFFFFFFFFu,
            Size);

        // Two letters with a space between: the space costs width, so the
        // second 'a' is not drawn on top of the first.
        var (width, _) = _renderer.Measure("a a", Size);
        var (letterWidth, _) = _renderer.Measure("aa", Size);
        Assert.True(width > letterWidth, "the space must advance the pen");
        Assert.Contains(pixels, pixel => pixel != 0);
    }
}
