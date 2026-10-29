namespace Rosella.Compositor;

/// <summary>
/// The shell's text, at whatever size a place asks for. One real font,
/// rasterised through FreeType and drawn with anti-aliased, proportionally
/// spaced glyphs. Sizes are given as the font's em height in logical pixels
/// and scaled by the output scale at draw time, so text is crisp on a HiDPI
/// panel and correctly sized on a plain one.
///
/// A machine with neither FreeType nor a usable font keeps the old bitmap
/// font: the bar and the menu still show their words, blocky but legible.
/// There is no setting for any of this.
/// </summary>
internal static class ShellText
{
    /// <summary>Taskbar text: clock and window titles.</summary>
    internal const int BarEm = 17;

    /// <summary>Apple-menu text.</summary>
    internal const int MenuEm = 18;

    private static readonly TextRenderer? Renderer = TextRenderer.Load();

    /// <summary>The line height at this em, in logical pixels.</summary>
    internal static int LineHeight(int em) => Renderer?.LineHeight(em) ?? BitmapFont.GlyphHeight;

    /// <summary>The size of one line of <paramref name="text"/>, in logical pixels.</summary>
    internal static (int Width, int Height) Measure(string text, int em) =>
        Renderer?.Measure(text, em) ?? BitmapFont.Measure(text, 1);

    /// <summary>
    /// Draws text with the top of its line box at (x, y) — physical pixels,
    /// like every other shell blit — at <paramref name="em"/> logical pixels
    /// scaled by the output.
    /// </summary>
    internal static void DrawText(
        Span<byte> pixels,
        int clipWidth,
        int clipHeight,
        int stride,
        string text,
        int x,
        int y,
        uint colour,
        int em,
        int scale)
    {
        if (Renderer is not null)
        {
            Renderer.DrawText(pixels, clipWidth, clipHeight, stride, text, x, y, colour, em * scale);
            return;
        }

        // The bitmap font is a fixed 8x16 cell per unit of its own scale, so
        // the closest stand-in for an em is however many sixteenths it is.
        BitmapFont.DrawText(pixels, clipWidth, clipHeight, stride, text, x, y, colour, Math.Max(1, (em * scale) / BitmapFont.GlyphHeight));
    }
}
