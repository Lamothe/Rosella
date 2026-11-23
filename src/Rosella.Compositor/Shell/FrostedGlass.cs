using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>Blurs the scene behind a rounded shell panel, then applies a dark tint.</summary>
internal sealed class FrostedGlass
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
                _blurred[(y * area.W) + x] = DarkTint(average);
                if (!lastRow)
                {
                    sums[x] = sums[x] - _horizontal[leaving + x] + _horizontal[entering + x];
                }
            }
        }
    }

    internal void Draw(Span<byte> pixels, int stride, Rect panel, int cornerRadius, int scale)
    {
        var area = _area;
        var bounds = panel.Scaled(scale);
        var radius = Math.Min(cornerRadius * scale, Math.Min(bounds.W, bounds.H) / 2);
        var destination = MemoryMarshal.Cast<byte, uint>(pixels);
        var strideWords = stride / 4;
        for (var y = area.Y; y < area.Y + area.H; y++)
        {
            var row = y - bounds.Y;
            var inset = CornerInset(row, bounds.H, radius);
            var left = Math.Max(area.X, bounds.X + inset);
            var right = Math.Min(area.X + area.W, bounds.X + bounds.W - inset);
            if (right <= left)
            {
                continue;
            }

            var line = destination.Slice((y * strideWords) + left, right - left);
            _blurred.AsSpan(((y - area.Y) * area.W) + left - area.X, line.Length).CopyTo(line);

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
