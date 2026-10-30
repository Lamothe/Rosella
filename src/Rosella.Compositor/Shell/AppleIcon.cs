using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>
/// The Start button's apple: the canonical silhouette (the bitten apple
/// with its leaf, in the standard 24×24 icon-space outline) rendered from
/// real vector artwork instead of the hand-placed circles and ellipses
/// that read as "squashed". The path is fitted into the icon box with its
/// aspect ratio preserved, so the artwork keeps its own proportions at any
/// box or output scale. Coverage is supersampled, so edges anti-alias
/// cleanly and the interior is solid all the way through.
/// </summary>
internal static class AppleIcon
{
    internal const int Width = 20;
    internal const int Height = 20;

    /// <summary>Anti-aliasing subsamples per axis per output pixel.</summary>
    private const int Subsamples = 4;

    /// <summary>
    /// The bitten apple with leaf, as a single SVG path in a 24×24 icon
    /// space (the simple-icons "apple" outline, CC0): body with the bite
    /// carved from its right flank, the stem rising from the top notch and
    /// the leaf laid over it to the right.
    /// </summary>
    private const string Silhouette =
        "M12.152 6.896c-.948 0-2.415-1.078-3.96-1.04-2.04.027-3.91 1.183-4.961 3.014-2.117 3.675-.546 9.103 1.519 12.09 1.013 1.454 2.208 3.09 3.792 3.039 1.52-.065 2.09-.987 3.935-.987 1.831 0 2.35.987 3.96.948 1.637-.026 2.676-1.48 3.676-2.948 1.156-1.688 1.636-3.325 1.662-3.415-.039-.013-3.182-1.221-3.22-4.857-.026-3.04 2.48-4.494 2.597-4.559-1.429-2.09-3.623-2.324-4.39-2.376-2-.156-3.675 1.09-4.61 1.09zM15.53 3.83c.843-1.012 1.4-2.427 1.245-3.83-1.207.052-2.662.805-3.532 1.818-.78.896-1.454 2.338-1.273 3.714 1.338.104 2.715-.688 3.559-1.701";

    private static readonly SvgPath Path = SvgPath.Parse(Silhouette);

    /// <summary>Icon units per path unit, and where the art sits in the icon box.</summary>
    private static readonly (double Scale, double OriginX, double OriginY, double MinX, double MinY) Fit = ComputeFit();

    private static readonly object MaskGate = new();

    /// <summary>Coverage per output pixel for one output scale (rasterised once).</summary>
    private static (int Scale, byte[] Coverage)? _mask;

    /// <summary>Whether the icon-space point lies inside the silhouette.</summary>
    internal static bool Inside(double x, double y)
    {
        var (scale, originX, originY, minX, minY) = Fit;

        // Icon space → path space: the inverse of laying the art out.
        return Path.Inside(minX + ((x - originX) / scale), minY + ((y - originY) / scale));
    }

    /// <summary>The artwork's fitted size in icon units (tests pin the aspect ratio).</summary>
    internal static (double Width, double Height) ArtSize
    {
        get
        {
            var (minX, minY, maxX, maxY) = Path.Bounds;
            var (scale, _, _, _, _) = Fit;
            return ((maxX - minX) * scale, (maxY - minY) * scale);
        }
    }

    /// <summary>
    /// Blends the silhouette at (x0, y0) logical over the buffer: one
    /// source-over step per output pixel, with exact coverage from the
    /// cached mask — anti-aliased at any scale.
    /// </summary>
    internal static void Blend(Span<byte> pixels, int clipW, int clipH, int stride, int x0, int y0, int scale, uint colour)
    {
        if (scale <= 0)
        {
            return;
        }

        var words = MemoryMarshal.Cast<byte, uint>(pixels);
        var strideWords = stride / 4;
        var alpha = (byte)((colour >> 24) & 0xFF);
        var red = (byte)((colour >> 16) & 0xFF);
        var green = (byte)((colour >> 8) & 0xFF);
        var blue = (byte)(colour & 0xFF);
        var coverage = CoverageFor(scale);

        for (var py = Math.Max(0, y0 * scale); py < Math.Min(clipH, (y0 + Height) * scale); py++)
        {
            for (var px = Math.Max(0, x0 * scale); px < Math.Min(clipW, (x0 + Width) * scale); px++)
            {
                var sample = coverage[((py - (y0 * scale)) * Width * scale) + (px - (x0 * scale))];
                if (sample == 0)
                {
                    continue;
                }

                // sample is 0..Subsamples²; turn it into 0..255 coverage,
                // then fold in the requested colour's own alpha.
                var blendAlpha = (uint)(((sample * alpha) + 8) / (Subsamples * Subsamples));
                if (blendAlpha == 0)
                {
                    continue;
                }

                var index = (py * strideWords) + px;
                var destination = words[index];
                var inverse = 255 - blendAlpha;
                var outBlue = (byte)((((blue * blendAlpha) + ((destination & 0xFF) * inverse)) + 127) / 255);
                var outGreen = (byte)((((green * blendAlpha) + (((destination >> 8) & 0xFF) * inverse)) + 127) / 255);
                var outRed = (byte)((((red * blendAlpha) + (((destination >> 16) & 0xFF) * inverse)) + 127) / 255);
                words[index] = 0xFF000000 | ((uint)outRed << 16) | ((uint)outGreen << 8) | outBlue;
            }
        }
    }

    /// <summary>The coverage mask for one output scale: one byte (0..Subsamples²) per pixel.</summary>
    private static byte[] CoverageFor(int scale)
    {
        lock (MaskGate)
        {
            if (_mask is { } cached && cached.Scale == scale)
            {
                return cached.Coverage;
            }

            var side = Width * scale;
            var coverage = new byte[side * side];
            for (var py = 0; py < side; py++)
            {
                for (var px = 0; px < side; px++)
                {
                    var hits = 0;
                    for (var sy = 0; sy < Subsamples; sy++)
                    {
                        for (var sx = 0; sx < Subsamples; sx++)
                        {
                            var u = ((px * Subsamples) + sx + 0.5) / (Subsamples * scale);
                            var v = ((py * Subsamples) + sy + 0.5) / (Subsamples * scale);
                            if (Inside(u, v))
                            {
                                hits++;
                            }
                        }
                    }

                    coverage[(py * side) + px] = (byte)hits;
                }
            }

            _mask = (scale, coverage);
            return coverage;
        }
    }

    /// <summary>
    /// The fit from icon space to path space: the path's own bounding box
    /// scaled to sit inside the icon box with its aspect ratio untouched
    /// and the artwork centred — the fix for the squashed silhouette.
    /// </summary>
    private static (double Scale, double OriginX, double OriginY, double MinX, double MinY) ComputeFit()
    {
        var (minX, minY, maxX, maxY) = Path.Bounds;
        var artWidth = maxX - minX;
        var artHeight = maxY - minY;
        if (artWidth <= 0 || artHeight <= 0)
        {
            return (1, 0, 0, 0, 0);
        }

        // Icon units per path unit, chosen so the art just fits both ways.
        var scale = Math.Min(Width / artWidth, Height / artHeight);

        // Where the scaled art starts in icon space: centred in the box.
        var originX = (Width - (artWidth * scale)) / 2;
        var originY = (Height - (artHeight * scale)) / 2;
        return (scale, originX, originY, minX, minY);
    }
}
