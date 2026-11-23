using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Rosella.Compositor;

/// <summary>A clean snapshot and its precomputed dim layer, both in cached memory.</summary>
internal sealed record ScreenshotFrame(byte[] Pixels, byte[]? Dimmed, int Stride);

/// <summary>Print Screen's frozen frame, drag selection and pending export.</summary>
internal sealed class ScreenshotSelection
{
    [InlineArray(4)]
    private struct SelectionEdges
    {
        private DamageRect _first;
    }

    private readonly object _gate = new();
    private readonly DamageTracker _damage = new();
    private readonly RectList _difference = new();
    private long _generation;
    private bool _selecting;
    private ScreenshotFrame? _frame;
    private (int X, int Y)? _anchor;
    private Rect? _pending;

    internal bool IsSelecting
    {
        get { lock (_gate) { return _selecting; } }
    }

    internal void Start()
    {
        lock (_gate)
        {
            _generation++;
            _frame = null;
            _pending = null;
            _selecting = true;
            _anchor = null;
            _damage.MarkFull();
        }
    }

    internal bool Cancel()
    {
        lock (_gate)
        {
            if (!_selecting)
            {
                return false;
            }

            _generation++;
            _frame = null;
            _selecting = false;
            _anchor = null;
            return true;
        }
    }

    internal void BeginDrag((int X, int Y) point)
    {
        lock (_gate)
        {
            _anchor = point;
        }
    }

    internal void EndDrag((int X, int Y) point)
    {
        lock (_gate)
        {
            if (_anchor is not { } anchor)
            {
                return;
            }

            _anchor = null;
            if (point == anchor)
            {
                return;
            }

            _pending = Between(anchor, point);
            _selecting = false;
        }
    }

    internal void CaptureOutput(int width, int height)
    {
        lock (_gate)
        {
            if (!_selecting)
            {
                _generation++;
                _frame = null;
            }

            _selecting = false;
            _anchor = null;
            _pending = new Rect(0, 0, width, height);
        }
    }

    internal (long Generation, bool Dim)? SnapshotRequest()
    {
        lock (_gate)
        {
            return _frame is null && (_selecting || _pending is not null)
                ? (_generation, _selecting)
                : null;
        }
    }

    internal void SetSnapshot(long generation, byte[] pixels, byte[]? dimmed, int stride)
    {
        lock (_gate)
        {
            if (generation == _generation && (_selecting || _pending is not null))
            {
                _frame = new ScreenshotFrame(pixels, dimmed, stride);
            }
        }
    }

    internal (ScreenshotFrame Frame, Rect Region)? TakeCapture()
    {
        lock (_gate)
        {
            if (_frame is not { } frame || _pending is not { } region)
            {
                return null;
            }

            _frame = null;
            _pending = null;
            return (frame, region);
        }
    }

    internal int? TakeDamage(int width, int height, Span<DamageRect> destination)
    {
        lock (_gate)
        {
            return _selecting && _frame is not null ? _damage.Take(width, height, destination) : null;
        }
    }

    internal static byte[] Dim(byte[] pixels)
    {
        var dimmed = GC.AllocateUninitializedArray<byte>(pixels.Length);
        var source = MemoryMarshal.Cast<byte, uint>(pixels);
        var target = MemoryMarshal.Cast<byte, uint>(dimmed.AsSpan());
        for (var i = 0; i < source.Length; i++)
        {
            target[i] = 0xFF000000 | ((source[i] & 0x00FEFEFE) >> 1);
        }

        return dimmed;
    }

    internal bool Draw(Span<byte> pixels, int stride, int scale, in DamageRect clip, (int X, int Y) cursor)
    {
        (int X, int Y)? anchor;
        ScreenshotFrame frame;
        lock (_gate)
        {
            if (!_selecting || _frame is not { } snapshot)
            {
                return false;
            }

            frame = snapshot;
            anchor = _anchor;
        }

        var bounds = anchor is { } start ? Between(start, cursor).Scaled(scale) : default;
        var selection = new DamageRect(bounds.X, bounds.Y, bounds.W, bounds.H);
        if (!selection.Contains(clip))
        {
            CopyRegion(frame.Dimmed ?? frame.Pixels, frame.Stride, pixels, stride, clip);
        }

        CopyRegion(frame.Pixels, frame.Stride, pixels, stride, selection.Intersect(clip));

        if (anchor is not null)
        {
            SelectionEdges edges = default;
            Outline(bounds, scale, edges);
            foreach (var edge in edges)
            {
                var part = edge.Intersect(clip);
                Chrome.FillRect(pixels, stride / 4, pixels.Length / stride, stride,
                    new Rect(part.X, part.Y, part.W, part.H), Chrome.AccentBlue);
            }
        }

        var viewport = pixels.Slice((clip.Y * stride) + (clip.X * 4));
        var xHot = (cursor.X * scale) - clip.X;
        var yHot = (cursor.Y * scale) - clip.Y;
        Chrome.FillRect(viewport, clip.W, clip.H, stride,
            new Rect(xHot - 8 * scale, yHot - scale, 17 * scale, 3 * scale), 0xFF111111);
        Chrome.FillRect(viewport, clip.W, clip.H, stride,
            new Rect(xHot - scale, yHot - 8 * scale, 3 * scale, 17 * scale), 0xFF111111);
        Chrome.FillRect(viewport, clip.W, clip.H, stride,
            new Rect(xHot - 7 * scale, yHot, 15 * scale, scale), 0xFFFFFFFF);
        Chrome.FillRect(viewport, clip.W, clip.H, stride,
            new Rect(xHot, yHot - 7 * scale, scale, 15 * scale), 0xFFFFFFFF);
        return true;
    }

    internal bool PointerMoved((int X, int Y) before, (int X, int Y) after, int scale)
    {
        lock (_gate)
        {
            if (!_selecting)
            {
                return false;
            }

            AddCrosshairDamage(before, scale);
            AddCrosshairDamage(after, scale);
            if (_anchor is { } anchor)
            {
                var oldBounds = Between(anchor, before).Scaled(scale);
                var newBounds = Between(anchor, after).Scaled(scale);
                var oldArea = new DamageRect(oldBounds.X, oldBounds.Y, oldBounds.W, oldBounds.H);
                var newArea = new DamageRect(newBounds.X, newBounds.Y, newBounds.W, newBounds.H);
                AddDifference(oldArea, newArea);
                AddDifference(newArea, oldArea);

                SelectionEdges oldEdges = default;
                SelectionEdges newEdges = default;
                Outline(oldBounds, scale, oldEdges);
                Outline(newBounds, scale, newEdges);
                for (var i = 0; i < 4; i++)
                {
                    if (oldEdges[i] != newEdges[i])
                    {
                        _damage.Add(oldEdges[i].Intersect(newArea));
                        _damage.Add(newEdges[i].Intersect(oldArea));
                    }
                }
            }

            return true;
        }
    }

    private void AddCrosshairDamage((int X, int Y) point, int scale) =>
        _damage.Add(new DamageRect((point.X - 8) * scale, (point.Y - 8) * scale, 17 * scale, 17 * scale));

    private void AddDifference(in DamageRect area, in DamageRect other)
    {
        _difference.Reset(area);
        _difference.Subtract(other);
        for (var i = 0; i < _difference.Count; i++)
        {
            _damage.Add(_difference[i]);
        }
    }

    private static void CopyRegion(ReadOnlySpan<byte> source, int sourceStride, Span<byte> target, int targetStride, in DamageRect region)
    {
        var x = region.X * 4;
        var bytes = region.W * 4;
        for (var y = region.Y; y < region.Y + region.H; y++)
        {
            source.Slice(y * sourceStride + x, bytes).CopyTo(target.Slice(y * targetStride + x, bytes));
        }
    }

    private static void Outline(Rect bounds, int thickness, Span<DamageRect> edges)
    {
        edges[0] = new DamageRect(bounds.X, bounds.Y, bounds.W, thickness);
        edges[1] = new DamageRect(bounds.X, bounds.Y + bounds.H - thickness, bounds.W, thickness);
        edges[2] = new DamageRect(bounds.X, bounds.Y, thickness, bounds.H);
        edges[3] = new DamageRect(bounds.X + bounds.W - thickness, bounds.Y, thickness, bounds.H);
    }

    private static Rect Between((int X, int Y) first, (int X, int Y) last) =>
        new(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y),
            Math.Abs(first.X - last.X) + 1, Math.Abs(first.Y - last.Y) + 1);
}
