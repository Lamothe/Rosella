using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>Blurs the scene behind a rounded shell panel.</summary>
internal sealed class FrostedGlass(bool tinted = true)
{
    internal const int BlurRadius = 12;

    private ulong[] _horizontal = [];
    private ulong[] _columnSums = [];
    private uint[] _blurred = [];
    private DamageRect _area;

    internal bool HasDamage => !_area.IsEmpty;

    internal void Prepare(ReadOnlySpan<byte> backdrop, int width, int height, int stride, int radius, DamageRect area)
    {
        _area = area;
        if (area.IsEmpty)
        {
            return;
        }

        var source = MemoryMarshal.Cast<byte, uint>(backdrop);
        var strideWords = stride / 4;
        var sample = area.Inflate(radius).Intersect(DamageRect.For(width, height));
        var kernelWidth = (2 * radius) + 1;
        if (_horizontal.Length < area.W * sample.H)
        {
            _horizontal = new ulong[area.W * sample.H];
        }
        if (_columnSums.Length < area.W)
        {
            _columnSums = new ulong[area.W];
        }
        if (_blurred.Length < area.W * area.H)
        {
            _blurred = new uint[area.W * area.H];
        }

        // A running sum in each direction keeps the work independent of the
        // kernel width per pixel. Clamp samples at the physical output edge.
        for (var y = 0; y < sample.H; y++)
        {
            var row = source.Slice((sample.Y + y) * strideWords, width);
            var filtered = _horizontal.AsSpan(y * area.W, area.W);
            ulong sum = 0;
            for (var offset = -radius; offset <= radius; offset++)
            {
                sum += Expand(row[Math.Clamp(area.X + offset, 0, width - 1)]);
            }

            for (var x = 0; x < area.W; x++)
            {
                filtered[x] = Average(sum, kernelWidth);
                var center = area.X + x;
                if (x + 1 < area.W)
                {
                    sum -= Expand(row[Math.Max(0, center - radius)]);
                    sum += Expand(row[Math.Min(width - 1, center + radius + 1)]);
                }
            }
        }

        var sums = _columnSums.AsSpan(0, area.W);
        sums.Clear();
        for (var offset = -radius; offset <= radius; offset++)
        {
            var row = Math.Clamp(area.Y + offset, 0, height - 1) - sample.Y;
            for (var x = 0; x < area.W; x++)
            {
                sums[x] += _horizontal[(row * area.W) + x];
            }
        }

        for (var y = 0; y < area.H; y++)
        {
            var center = area.Y + y;
            var leaving = (Math.Max(0, center - radius) - sample.Y) * area.W;
            var entering = (Math.Min(height - 1, center + radius + 1) - sample.Y) * area.W;
            var lastRow = y == area.H - 1;
            for (var x = 0; x < area.W; x++)
            {
                var average = Average(sums[x], kernelWidth);
                _blurred[(y * area.W) + x] = tinted ? DarkTint(average) : Pack(average);
                if (!lastRow)
                {
                    sums[x] = sums[x] - _horizontal[leaving + x] + _horizontal[entering + x];
                }
            }
        }
    }

    internal void Draw(Span<byte> pixels, int stride, Rect panel, int cornerRadius, int scale)
        => Draw(pixels, stride, panel, cornerRadius, scale, _area);

    /// <summary>Copies the prepared blur into a tightly packed backdrop with neutral dimming.</summary>
    internal void CopyDimmedBlur(Span<byte> pixels, uint opacity)
    {
        var destination = MemoryMarshal.Cast<byte, uint>(pixels);
        for (var i = 0; i < destination.Length; i++)
        {
            var colour = _blurred[i];
            var red = Tint((colour >> 16) & 0xFF, 0, opacity);
            var green = Tint((colour >> 8) & 0xFF, 0, opacity);
            var blue = Tint(colour & 0xFF, 0, opacity);
            destination[i] = 0xFF000000u | (red << 16) | (green << 8) | blue;
        }
    }

    internal void LightenBlur(byte opacity)
    {
        var white = opacity * 0x01010101u;
        foreach (ref var pixel in _blurred.AsSpan(0, _area.W * _area.H))
        {
            Blitter.Blend(ref pixel, white);
        }
    }

    internal void Draw(Span<byte> pixels, int stride, Rect panel, int cornerRadius, int scale, in DamageRect damage)
        => Draw(pixels, stride, panel, cornerRadius, scale, damage, antialiasEdge: false);

    internal void Draw(Span<byte> pixels, int stride, Rect panel, int cornerRadius, int scale, in DamageRect damage, bool antialiasEdge)
    {
        var bounds = panel.Scaled(scale);
        var area = _area.Intersect(damage).Intersect(bounds.ToDamage(1));
        var radius = Math.Min(cornerRadius * scale, Math.Min(bounds.W, bounds.H) / 2);
        var destination = MemoryMarshal.Cast<byte, uint>(pixels);
        var strideWords = stride / 4;
        for (var y = area.Y; y < area.Y + area.H; y++)
        {
            var row = y - bounds.Y;
            if (antialiasEdge)
            {
                var edgeLine = destination.Slice(y * strideWords + area.X, area.W);
                var blurred = _blurred.AsSpan((y - _area.Y) * _area.W + area.X - _area.X, area.W);
                DrawAntialiasedRow(edgeLine, blurred, area.X - bounds.X, row, bounds.W, bounds.H, radius, scale);
                continue;
            }
            var inset = CornerInset(row, bounds.H, radius);
            var left = Math.Max(area.X, bounds.X + inset);
            var right = Math.Min(area.X + area.W, bounds.X + bounds.W - inset);
            if (right <= left)
            {
                continue;
            }

            var line = destination.Slice((y * strideWords) + left, right - left);
            _blurred.AsSpan(((y - _area.Y) * _area.W) + left - _area.X, line.Length).CopyTo(line);

            if (row < scale || row >= bounds.H - scale)
            {
                Highlight(line);
            }
            else
            {
                var innerRadius = Math.Max(0, radius - scale);
                var innerInset = scale + CornerInset(row - scale, bounds.H - (2 * scale), innerRadius);
                Highlight(line[..Math.Clamp(bounds.X + innerInset - left, 0, line.Length)]);
                Highlight(line[Math.Clamp(bounds.X + bounds.W - innerInset - left, 0, line.Length)..]);
            }
        }
    }

    private static void DrawAntialiasedRow(Span<uint> line, ReadOnlySpan<uint> blurred, int x, int y,
        int width, int height, int radius, int scale)
    {
        const uint rim = 0x24242424;
        var distanceY = Math.Min(y, height - 1 - y);
        var band = distanceY < radius ? Math.Max(radius, scale) : scale;
        var middleStart = Math.Clamp(band - x, 0, line.Length);
        var middleEnd = Math.Clamp(width - band - x, middleStart, line.Length);
        blurred[middleStart..middleEnd].CopyTo(line[middleStart..middleEnd]);
        if (distanceY < scale)
        {
            foreach (ref var pixel in line[middleStart..middleEnd])
            {
                Blitter.Blend(ref pixel, rim);
            }
        }

        for (var i = 0; i < middleStart; i++)
        {
            DrawEdgePixel(ref line[i], blurred[i], x + i, distanceY, radius, scale, rim >> 24);
        }
        for (var i = middleEnd; i < line.Length; i++)
        {
            DrawEdgePixel(ref line[i], blurred[i], width - 1 - x - i, distanceY, radius, scale, rim >> 24);
        }
    }

    private static void DrawEdgePixel(ref uint pixel, uint glass, int x, int y, int radius, int scale, uint rimAlpha)
    {
        var dx = radius - (x + 0.5);
        var dy = radius - (y + 0.5);
        // Signed distance to the outer edge, sampled at the pixel center.
        var distance = dx > 0 && dy > 0 ? Math.Sqrt(dx * dx + dy * dy) - radius : -Math.Min(x, y) - 0.5;
        var coverage = Math.Clamp(0.5 - distance, 0, 1);
        if (coverage == 0)
        {
            return;
        }

        var border = Math.Clamp(distance + scale + 0.5, 0, 1);
        Blitter.Blend(ref glass, (uint)Math.Round(border * rimAlpha) * 0x01010101);
        var alpha = (uint)Math.Round(coverage * 256);
        var red = Tint((pixel >> 16) & 0xFF, (glass >> 16) & 0xFF, alpha);
        var green = Tint((pixel >> 8) & 0xFF, (glass >> 8) & 0xFF, alpha);
        var blue = Tint(pixel & 0xFF, glass & 0xFF, alpha);
        pixel = 0xFF000000 | (red << 16) | (green << 8) | blue;
    }

    private static int CornerInset(int row, int height, int radius)
    {
        var distance = radius - Math.Min(row, height - row - 1);
        return distance <= 0 ? 0 : radius - (int)Math.Sqrt((radius * radius) - (distance * distance));
    }

    // Each channel has a 16-bit lane, so the rolling sums never carry into a neighbour.
    private static ulong Expand(uint colour) =>
        (colour & 0xFFu) | ((colour & 0xFF00u) << 8) | ((ulong)(colour & 0xFF0000u) << 16);

    private static ulong Average(ulong sum, int count)
    {
        var divisor = (uint)count;
        var blue = ((uint)sum & 0xFFFF) / divisor;
        var green = ((uint)(sum >> 16) & 0xFFFF) / divisor;
        var red = (uint)(sum >> 32) / divisor;
        return blue | (green << 16) | ((ulong)red << 32);
    }

    private static uint DarkTint(ulong colour)
    {
        const uint tint = 190;
        var blue = Tint((uint)colour & 0xFF, 33, tint);
        var green = Tint((uint)(colour >> 16) & 0xFF, 24, tint);
        var red = Tint((uint)(colour >> 32), 21, tint);
        return 0xFF000000u | (red << 16) | (green << 8) | blue;
    }

    private static uint Pack(ulong colour) =>
        0xFF000000u | ((uint)(colour >> 16) & 0xFF0000u) | ((uint)(colour >> 8) & 0xFF00u) | ((uint)colour & 0xFFu);

    private static uint Tint(uint channel, uint tint, uint opacity) =>
        ((channel * (256 - opacity)) + (tint * opacity)) >> 8;

    private static void Highlight(Span<uint> line)
    {
        for (var pixel = 0; pixel < line.Length; pixel++)
        {
            var colour = line[pixel];
            var blue = Tint(colour & 0xFF, 132, 79);
            var green = Tint((colour >> 8) & 0xFF, 121, 79);
            var red = Tint((colour >> 16) & 0xFF, 112, 79);
            line[pixel] = 0xFF000000u | (red << 16) | (green << 8) | blue;
        }
    }
}
