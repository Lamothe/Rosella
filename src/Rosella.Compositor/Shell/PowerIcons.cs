using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>One of the three power glyphs the settings panel draws.</summary>
internal enum PowerGlyph
{
    LogOut,
    Restart,
    ShutDown,
}

/// <summary>
/// The settings panel's line-art power glyphs: a doorway with a departing
/// arrow, an open circular arrow, and the power symbol's broken ring and
/// stem. Each glyph is a handful of polylines authored on a 24x24 design
/// grid and drawn by stroking them — one round-capped capsule of coverage
/// per segment — into a coverage mask at the output scale, then blended
/// source-over. Rasterising at the physical resolution is what keeps the
/// strokes anti-aliased at 2x as well as 1x; the mask is cached per glyph
/// and pixel size, so a panel redraw costs one blend per glyph pixel, not a
/// distance field. Masks are immutable once built.
/// </summary>
internal static class PowerIcons
{
    /// <summary>The glyph's logical box, centred in the 50px circle that holds it.</summary>
    internal const int GlyphSize = 22;

    /// <summary>The design grid the strokes are authored on.</summary>
    private const double DesignSize = 24;

    /// <summary>Stroke thickness in design units — just over 2 logical pixels.</summary>
    private const double StrokeWidth = 2.2;

    /// <summary>Segments per arc; the design grid is small, so this is past smooth.</summary>
    private const int ArcSegments = 48;

    /// <summary>Arrowhead arm length in design units.</summary>
    private const double ArrowLength = 3.8;

    /// <summary>Half the arrowhead's opening angle.</summary>
    private const double ArrowSpread = 40 * Math.PI / 180;

    private static readonly ConcurrentDictionary<(PowerGlyph Glyph, int Pixels), Mask> Masks = new();

    /// <summary>
    /// Draws <paramref name="glyph"/> centred on a logical point, blended
    /// over whatever is already there. <paramref name="scale"/> is the
    /// output scale: the mask is rasterised in physical pixels, so the
    /// stroke stays anti-aliased on a HiDPI panel.
    /// </summary>
    internal static void Draw(
        Span<byte> pixels,
        int clipWidth,
        int clipHeight,
        int stride,
        PowerGlyph glyph,
        int centreX,
        int centreY,
        uint colour,
        int scale)
    {
        if (scale < 1)
        {
            return;
        }

        var mask = Masks.GetOrAdd((glyph, GlyphSize * scale), key => Rasterize(key.Glyph, key.Pixels));
        var rowPixels = MemoryMarshal.Cast<byte, uint>(pixels);
        var strideWords = stride / 4;
        var originX = (centreX * scale) - (mask.Size / 2);
        var originY = (centreY * scale) - (mask.Size / 2);

        var sourceRed = (colour >> 16) & 0xFF;
        var sourceGreen = (colour >> 8) & 0xFF;
        var sourceBlue = colour & 0xFF;

        for (var row = 0; row < mask.Size; row++)
        {
            var y = originY + row;
            if (y < 0 || y >= clipHeight)
            {
                continue;
            }

            for (var column = 0; column < mask.Size; column++)
            {
                var coverage = mask.Coverage[(row * mask.Size) + column];
                if (coverage == 0)
                {
                    continue;
                }

                var x = originX + column;
                if (x < 0 || x >= clipWidth)
                {
                    continue;
                }

                var index = (y * strideWords) + x;
                if (coverage == 255)
                {
                    rowPixels[index] = colour;
                    continue;
                }

                var destination = rowPixels[index];
                var inverse = 255u - coverage;
                var red = ((((destination >> 16) & 0xFF) * inverse) + (sourceRed * coverage)) / 255;
                var green = ((((destination >> 8) & 0xFF) * inverse) + (sourceGreen * coverage)) / 255;
                var blue = (((destination & 0xFF) * inverse) + (sourceBlue * coverage)) / 255;
                rowPixels[index] = 0xFF000000 | (red << 16) | (green << 8) | blue;
            }
        }
    }

    /// <summary>Rasterises a glyph's strokes into a physical-pixel coverage mask.</summary>
    private static Mask Rasterize(PowerGlyph glyph, int pixels)
    {
        var coverage = new byte[pixels * pixels];
        var unit = pixels / DesignSize;
        var halfWidth = (StrokeWidth * unit) / 2;
        foreach (var polyline in Strokes(glyph))
        {
            for (var index = 1; index < polyline.Length; index++)
            {
                Stamp(
                    coverage,
                    pixels,
                    polyline[index - 1].X * unit,
                    polyline[index - 1].Y * unit,
                    polyline[index].X * unit,
                    polyline[index].Y * unit,
                    halfWidth);
            }
        }

        return new Mask(pixels, coverage);
    }

    /// <summary>
    /// Stamps one capsule: per pixel centre, the distance to the segment
    /// gives a one-pixel-wide coverage ramp across the stroke edge. Strokes
    /// merge by maximum coverage so overlapping joints never darken.
    /// </summary>
    private static void Stamp(byte[] coverage, int size, double x1, double y1, double x2, double y2, double halfWidth)
    {
        var minX = Math.Max(0, (int)Math.Floor(Math.Min(x1, x2) - halfWidth - 1));
        var maxX = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(x1, x2) + halfWidth + 1));
        var minY = Math.Max(0, (int)Math.Floor(Math.Min(y1, y2) - halfWidth - 1));
        var maxY = Math.Min(size - 1, (int)Math.Ceiling(Math.Max(y1, y2) + halfWidth + 1));
        var segmentX = x2 - x1;
        var segmentY = y2 - y1;
        var lengthSquared = (segmentX * segmentX) + (segmentY * segmentY);

        for (var y = minY; y <= maxY; y++)
        {
            var sampleY = y + 0.5;
            for (var x = minX; x <= maxX; x++)
            {
                var sampleX = x + 0.5;
                var t = lengthSquared <= 0
                    ? 0
                    : Math.Clamp((((sampleX - x1) * segmentX) + ((sampleY - y1) * segmentY)) / lengthSquared, 0, 1);
                var dx = sampleX - (x1 + (t * segmentX));
                var dy = sampleY - (y1 + (t * segmentY));
                var distance = Math.Sqrt((dx * dx) + (dy * dy));
                var alpha = (int)Math.Round(255 * Math.Clamp(halfWidth + 0.5 - distance, 0, 1));
                if (alpha <= 0)
                {
                    continue;
                }

                var index = (y * size) + x;
                if (alpha > coverage[index])
                {
                    coverage[index] = (byte)alpha;
                }
            }
        }
    }

    /// <summary>The glyph's polylines, in design units.</summary>
    private static (double X, double Y)[][] Strokes(PowerGlyph glyph) => glyph switch
    {
        PowerGlyph.LogOut =>
        [
            // Doorway: a frame open on the right.
            [(10.5, 4), (4, 4), (4, 20), (10.5, 20)],
            // The arrow leaves through the opening rather than beside it.
            [(8.5, 12), (19.8, 12)],
            .. ArrowHead((19.8, 12), (1, 0)),
        ],
        PowerGlyph.Restart =>
        [
            // Ring with a gap at the top right; the head rides the top,
            // pointing clockwise into the gap.
            Arc(12, 12.5, 7.2, 310, 320),
            .. ArrowHead(PointOnArc(12, 12.5, 7.2, 270), Tangent(270)),
        ],
        PowerGlyph.ShutDown =>
        [
            // Broken ring, gap at the top, and the stem through it to the centre.
            Arc(12, 14, 7.4, 305, 290),
            [(12, 3.6), (12, 13.6)],
        ],
        _ => [],
    };

    /// <summary>A circular polyline, clockwise in screen coordinates when the sweep is positive.</summary>
    private static (double X, double Y)[] Arc(double centreX, double centreY, double radius, double startDegrees, double sweepDegrees)
    {
        var points = new (double X, double Y)[ArcSegments + 1];
        for (var index = 0; index <= ArcSegments; index++)
        {
            points[index] = PointOnArc(centreX, centreY, radius, startDegrees + (sweepDegrees * index / ArcSegments));
        }

        return points;
    }

    /// <summary>One point on a circular arc, in design units; zero degrees is east.</summary>
    private static (double X, double Y) PointOnArc(double centreX, double centreY, double radius, double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return (centreX + (radius * Math.Cos(radians)), centreY + (radius * Math.Sin(radians)));
    }

    /// <summary>The direction of travel along an arc at an angle, in degrees.</summary>
    private static (double X, double Y) Tangent(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        return (-Math.Sin(radians), Math.Cos(radians));
    }

    /// <summary>Two chevron arms from a tip, trailing behind the travel direction.</summary>
    private static (double X, double Y)[][] ArrowHead((double X, double Y) tip, (double X, double Y) direction)
    {
        var length = Math.Sqrt((direction.X * direction.X) + (direction.Y * direction.Y));
        var backX = -direction.X / length * ArrowLength;
        var backY = -direction.Y / length * ArrowLength;
        var cos = Math.Cos(ArrowSpread);
        var sin = Math.Sin(ArrowSpread);
        var first = (X: tip.X + ((backX * cos) - (backY * sin)), Y: tip.Y + ((backX * sin) + (backY * cos)));
        var second = (X: tip.X + ((backX * cos) + (backY * sin)), Y: tip.Y + ((-backX * sin) + (backY * cos)));
        return [[tip, first], [tip, second]];
    }

    private readonly record struct Mask(int Size, byte[] Coverage);
}